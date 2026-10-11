using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Exchange.MoySklad;

public sealed record ImportRunDto(
    long Id, ImportRunStatus Status, bool Overwrite, ItemType DefaultItemType, DateTime RequestedAtUtc, DateTime? StartedAtUtc, DateTime? FinishedAtUtc,
    string? Stage, int Created, int Updated, int Skipped, int Errors, string? Message, string RequestedBy);

public sealed record MoySkladStatusDto(bool Connected, string? Account, string? TokenHint, DateTime? ConnectedAtUtc, IReadOnlyList<ImportRunDto> Runs);

/// <summary>Предпросмотр по виду объектов: сколько будет создано, обновлено, без изменений и пропущено.</summary>
public sealed record PreviewLineDto(string Entity, int Total, int ToCreate, int ToUpdate, int Unchanged, int Skipped);

public sealed record MoySkladPreviewDto(IReadOnlyList<PreviewLineDto> Lines, IReadOnlyList<string> Conflicts, bool Truncated);

public sealed record ImportIssueDto(string Entity, string? Code, string? Name, bool IsError, string Text);

/// <summary>
/// Загрузка справочников из МойСклад (D310, ТЗ каталога, этап 4): подключение по токену, предпросмотр, фоновая загрузка страницами
/// с продолжением, отчёт. Только Владелец или Администратор (право integration.moysklad.manage), не через API.
/// Повторная загрузка не дублирует: совпадение по связи external_links, затем по externalCode, затем по коду или артикулу; изменённое
/// в МойСклад обновляется, изменённое вручную в knitERP не затирается без флага «перезаписать».
/// </summary>
public sealed class MoySkladImportService(
    IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, IMoySkladClient client, ISpreadsheetFormat spreadsheet)
{
    public const int PageSize = 500;
    public const int BundlePageSize = 100;
    public const int PreviewLimit = 10_000;
    public const int MaxConflicts = 200;
    private const ExternalSystem Sys = ExternalSystem.MoySklad;

    /// <summary>Этапы загрузки по порядку: позже идут те, что ссылаются на раньше загруженное.</summary>
    public static readonly IReadOnlyList<string> Stages = ["pricetype", "folder", "product", "service", "variant", "bundle"];

    public static string StageName(string? stage) => stage switch
    {
        "pricetype" => "Виды цен",
        "folder" => "Группы",
        "product" => "Товары",
        "service" => "Услуги",
        "variant" => "Модификации",
        "bundle" => "Комплекты",
        _ => "—",
    };

    // ---------- Подключение ----------

    public async Task<MoySkladStatusDto> StatusAsync(CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        var connection = await db.ExternalConnections.AsNoTracking().SingleOrDefaultAsync(c => c.OrganizationId == ctx.OrganizationId && c.System == Sys, ct);
        var runs = await (from r in db.ExternalImportRuns.AsNoTracking()
                          join u in db.Users.AsNoTracking() on r.RequestedByUserId equals u.Id
                          where r.OrganizationId == ctx.OrganizationId && r.System == Sys
                          orderby r.Id descending
                          select new ImportRunDto(r.Id, r.Status, r.Overwrite, r.DefaultItemType, r.RequestedAtUtc, r.StartedAtUtc, r.FinishedAtUtc, r.Stage,
                              r.Created, r.Updated, r.Skipped, r.Errors, r.Message, u.DisplayName)).Take(20).ToListAsync(ct);
        return new MoySkladStatusDto(connection is not null, connection?.AccountName, connection?.TokenHint, connection?.ConnectedAtUtc, runs);
    }

    /// <summary>Подключение: токен проверяется запросом к МойСклад (GET entity/organization) и хранится зашифрованным.</summary>
    public async Task ConnectAsync(string? token, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        var normalized = ExternalConnection.NormalizeToken(token);
        string? account;
        try
        {
            account = await client.CheckAsync(normalized, ct);
        }
        catch (MoySkladException ex)
        {
            throw new BusinessRuleException("exchange.moysklad.check", ex.Status is 401 or 403
                ? "МойСклад не принял токен: проверьте, что он скопирован целиком и у его владельца есть доступ к товарам."
                : $"МойСклад не ответил: {ex.Message}");
        }

        var connection = await db.ExternalConnections.SingleOrDefaultAsync(c => c.OrganizationId == ctx.OrganizationId && c.System == Sys, ct);
        if (connection is null)
        {
            db.ExternalConnections.Add(ExternalConnection.Create(ctx.OrganizationId, Sys, normalized, account, ctx.UserId, clock.UtcNow));
        }
        else
        {
            connection.Replace(normalized, account, ctx.UserId, clock.UtcNow);
        }

        // В журнал — только последние символы токена, сам токен никогда.
        Audit(ctx.OrganizationId, ctx.UserId, "Подключение МойСклад", null, $"{account ?? "аккаунт"}, токен …{normalized[^4..]}");
        await db.SaveOrConflictAsync(ct);
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        var connection = await db.ExternalConnections.SingleOrDefaultAsync(c => c.OrganizationId == ctx.OrganizationId && c.System == Sys, ct)
                         ?? throw new NotFoundException("Подключение МойСклад");
        if (await db.ExternalImportRuns.AnyAsync(r => r.OrganizationId == ctx.OrganizationId && r.System == Sys
                                                      && (r.Status == ImportRunStatus.Queued || r.Status == ImportRunStatus.Running), ct))
        {
            throw new BusinessRuleException("exchange.run.active", "Идёт загрузка — дождитесь её окончания.");
        }

        db.ExternalConnections.Remove(connection);
        Audit(ctx.OrganizationId, ctx.UserId, "Подключение МойСклад", connection.AccountName, "отключено");
        await db.SaveChangesAsync(ct);
    }

    // ---------- Предпросмотр и запуск ----------

    /// <summary>Предпросмотр без записи: по первым <see cref="PreviewLimit"/> строкам каждого вида — создать, обновить, без изменений, пропустить.</summary>
    public async Task<MoySkladPreviewDto> PreviewAsync(ItemType defaultItemType = ItemType.Finished, bool overwrite = false, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        var token = await TokenAsync(ctx.OrganizationId, ct);
        var engine = new Engine(db, client, clock, ctx.OrganizationId, token, ExternalImportRun.Queue(ctx.OrganizationId, Sys, overwrite, defaultItemType, ctx.UserId,
            clock.UtcNow), dryRun: true);
        try
        {
            return await engine.PreviewAsync(ct);
        }
        catch (MoySkladException ex)
        {
            throw new BusinessRuleException("exchange.moysklad.check", $"МойСклад не ответил: {ex.Message}");
        }
    }

    /// <summary>Запуск загрузки в фоне. Одна активная загрузка на организацию.</summary>
    public async Task<long> StartAsync(ItemType defaultItemType = ItemType.Finished, bool overwrite = false, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        await TokenAsync(ctx.OrganizationId, ct);
        if (await db.ExternalImportRuns.AnyAsync(r => r.OrganizationId == ctx.OrganizationId && r.System == Sys
                                                      && (r.Status == ImportRunStatus.Queued || r.Status == ImportRunStatus.Running), ct))
        {
            throw new BusinessRuleException("exchange.run.active", "Загрузка уже идёт — дождитесь её окончания.");
        }

        var run = ExternalImportRun.Queue(ctx.OrganizationId, Sys, overwrite, defaultItemType, ctx.UserId, clock.UtcNow);
        db.ExternalImportRuns.Add(run);
        await db.SaveChangesAsync(ct);
        Audit(ctx.OrganizationId, ctx.UserId, "Загрузка из МойСклад", null, $"№ {run.Id} поставлена в очередь{(overwrite ? ", с перезаписью" : "")}");
        await db.SaveChangesAsync(ct);
        return run.Id;
    }

    /// <summary>Продолжить прерванную загрузку с последней записанной страницы.</summary>
    public async Task ResumeAsync(long runId, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        var run = await db.ExternalImportRuns.SingleOrDefaultAsync(r => r.Id == runId && r.OrganizationId == ctx.OrganizationId, ct)
                  ?? throw new NotFoundException("Загрузка");
        run.Resume(clock.UtcNow);
        Audit(ctx.OrganizationId, ctx.UserId, "Загрузка из МойСклад", null, $"№ {run.Id} продолжена с этапа «{StageName(run.Stage)}»");
        await db.SaveOrConflictAsync(ct);
    }

    public async Task<IReadOnlyList<ImportIssueDto>> IssuesAsync(long runId, int take = 500, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        await RunAsync(ctx.OrganizationId, runId, ct);
        return await db.ExternalImportIssues.AsNoTracking().Where(i => i.OrganizationId == ctx.OrganizationId && i.RunId == runId)
            .OrderByDescending(i => i.IsError).ThenBy(i => i.Id).Take(Math.Clamp(take, 1, 5000))
            .Select(i => new ImportIssueDto(i.Entity, i.Code, i.Name, i.IsError, i.Text)).ToListAsync(ct);
    }

    /// <summary>Отчёт загрузки в Excel: итоги и все замечания по строкам.</summary>
    public async Task<byte[]> ReportAsync(long runId, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        var run = await RunAsync(ctx.OrganizationId, runId, ct);
        var issues = await db.ExternalImportIssues.AsNoTracking().Where(i => i.OrganizationId == ctx.OrganizationId && i.RunId == runId).OrderBy(i => i.Id)
            .ToListAsync(ct);
        return spreadsheet.Write(
        [
            new SheetData("Итоги", ["Показатель", "Значение"],
            [
                ["Загрузка", $"№ {run.Id}"], ["Состояние", run.Status.ToString()], ["Создано", run.Created.ToString(CultureInfo.InvariantCulture)],
                ["Обновлено", run.Updated.ToString(CultureInfo.InvariantCulture)], ["Без изменений или пропущено", run.Skipped.ToString(CultureInfo.InvariantCulture)],
                ["Ошибок", run.Errors.ToString(CultureInfo.InvariantCulture)], ["Сообщение", run.Message ?? ""],
            ]),
            new SheetData("Замечания", ["Вид", "Код", "Наименование", "Ошибка", "Текст", "Id в МойСклад"],
                issues.Select(i => (IReadOnlyList<string>)[i.Entity, i.Code ?? "", i.Name ?? "", i.IsError ? "да" : "нет", i.Text, i.ExternalId ?? ""]).ToList()),
        ]);
    }

    // ---------- Фоновая обработка ----------

    /// <summary>
    /// Обработать самую раннюю загрузку из очереди (любой организации). Вызывается фоновым процессом без пользователя: организация и автор —
    /// из записи загрузки. Возвращает false, если очередь пуста.
    /// </summary>
    public async Task<bool> ProcessNextAsync(CancellationToken ct = default)
    {
        var run = await db.ExternalImportRuns.Where(r => r.Status == ImportRunStatus.Queued && r.System == Sys).OrderBy(r => r.RequestedAtUtc).FirstOrDefaultAsync(ct);
        if (run is null)
        {
            return false;
        }

        run.Start(clock.UtcNow);
        await db.SaveChangesAsync(ct);
        var connection = await db.ExternalConnections.AsNoTracking().SingleOrDefaultAsync(c => c.OrganizationId == run.OrganizationId && c.System == Sys, ct);
        try
        {
            if (connection is null)
            {
                throw new BusinessRuleException("exchange.not_connected", "МойСклад отключён — подключите его заново.");
            }

            await new Engine(db, client, clock, run.OrganizationId, connection.Token, run, dryRun: false).RunAsync(ct);
            // Запись загрузки — заново: отказ строки сбрасывает отслеживание (DiscardChanges).
            run = await db.ExternalImportRuns.SingleAsync(r => r.Id == run.Id, ct);
            run.Complete(clock.UtcNow);
            Audit(run.OrganizationId, run.RequestedByUserId, "Загрузка из МойСклад", null,
                $"№ {run.Id}: создано {run.Created}, обновлено {run.Updated}, без изменений или пропущено {run.Skipped}, ошибок {run.Errors}");
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Любой сбой переводит загрузку в «прервана», иначе она навсегда осталась бы «идёт» и закрыла новые запуски.
            // Текст неизвестных исключений пользователю не показывается — он может содержать внутренние подробности.
            db.DiscardChanges();
            var failed = await db.ExternalImportRuns.SingleAsync(r => r.Id == run.Id, ct);
            failed.Fail(ex switch
            {
                MoySkladException or BusinessRuleException => ex.Message,
                DbUpdateException => "Ошибка записи в базу — загрузку можно продолжить.",
                HttpRequestException or TaskCanceledException => "МойСклад не ответил вовремя — загрузку можно продолжить.",
                _ => "Внутренняя ошибка загрузки — её можно продолжить; если повторяется, сообщите администратору.",
            }, clock.UtcNow);
            Audit(run.OrganizationId, run.RequestedByUserId, "Загрузка из МойСклад", null, $"№ {run.Id} прервана: {failed.Message}");
            await db.SaveChangesAsync(ct);
        }

        return true;
    }

    /// <summary>
    /// При запуске приложения: загрузки, оставшиеся «идёт» после остановки процесса, переводятся в «прервана» — их можно продолжить
    /// с последней записанной страницы. Вызывается фоновым процессом один раз до обработки очереди (один экземпляр приложения, ADR-0001).
    /// </summary>
    public async Task<int> RecoverInterruptedAsync(CancellationToken ct = default)
    {
        var stale = await db.ExternalImportRuns.Where(r => r.Status == ImportRunStatus.Running && r.System == Sys).ToListAsync(ct);
        foreach (var run in stale)
        {
            run.Fail("Загрузка прервана перезапуском приложения — её можно продолжить.", clock.UtcNow);
        }

        await db.SaveChangesAsync(ct);
        return stale.Count;
    }

    private async Task<AccessContext> DemandAsync(CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.MoySkladManage, ct);
        if (currentUser.ApiScope is not null)
        {
            throw new BusinessRuleException("exchange.via_api", "Подключение и загрузка МойСклад — только в интерфейсе, не через API.");
        }

        return ctx;
    }

    private async Task<string> TokenAsync(long org, CancellationToken ct) =>
        (await db.ExternalConnections.AsNoTracking().Where(c => c.OrganizationId == org && c.System == Sys).Select(c => c.Token).SingleOrDefaultAsync(ct))
        ?? throw new BusinessRuleException("exchange.not_connected", "Сначала подключите МойСклад: введите токен.");

    private async Task<ExternalImportRun> RunAsync(long org, long runId, CancellationToken ct) =>
        await db.ExternalImportRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId && r.OrganizationId == org, ct) ?? throw new NotFoundException("Загрузка");

    private void Audit(long org, long userId, string reason, string? before, string after) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, org, userId, AuditActions.CatalogImported, "MoySklad", null, before, after, reason,
            currentUser.CorrelationId));

    // ---------- Движок загрузки ----------

    private enum Decision
    {
        Create,
        Update,
        Unchanged,
        Skip,
    }

    /// <summary>
    /// Один проход загрузки или предпросмотра. Справочники — словари id без отслеживания; каждая строка записывается своим SaveChanges внутри
    /// транзакции страницы: при ошибке строки EF откатывает только её (точка сохранения), страница коммитится целиком.
    /// </summary>
    private sealed class Engine(IKnitErpDbContext db, IMoySkladClient client, IClock clock, long org, string token, ExternalImportRun run, bool dryRun)
    {
        private readonly Dictionary<(ExternalObjectType, string), ExternalLink> _links = [];
        private readonly Dictionary<string, MsRow> _uoms = [];
        private readonly Dictionary<string, MsRow> _countries = [];
        private readonly List<string> _conflicts = [];
        private Dictionary<string, (long Id, bool Linked)> _codes = new(StringComparer.Ordinal);
        private Dictionary<string, long> _articles = new(StringComparer.OrdinalIgnoreCase);
        private int _created, _updated, _skipped, _errors;
        private List<UnitOfMeasure> _units = [];
        private List<VatRate> _rates = [];

        public async Task<MoySkladPreviewDto> PreviewAsync(CancellationToken ct)
        {
            await LoadAsync(ct);
            var lines = new List<PreviewLineDto>();
            var truncated = false;
            foreach (var (stage, entity) in new[] { ("folder", MoySkladEntities.Folder), ("product", MoySkladEntities.Product), ("service", MoySkladEntities.Service),
                         ("variant", MoySkladEntities.Variant), ("bundle", MoySkladEntities.Bundle) })
            {
                int create = 0, update = 0, unchanged = 0, skip = 0, total = 0;
                for (var offset = 0; offset < PreviewLimit; offset += 1000)
                {
                    var page = await client.ListAsync(token, entity, offset, 1000, null, ct);
                    var rows = page.Rows ?? [];
                    foreach (var row in rows)
                    {
                        total++;
                        var decision = stage == "folder" ? DecideFolder(row) : await DecideItemAsync(stage, row, ct);
                        switch (decision)
                        {
                            case Decision.Create: create++; break;
                            case Decision.Update: update++; break;
                            case Decision.Unchanged: unchanged++; break;
                            default: skip++; break;
                        }
                    }

                    if (rows.Count < 1000)
                    {
                        break;
                    }

                    truncated |= offset + 1000 >= PreviewLimit;
                }

                lines.Add(new PreviewLineDto(StageName(stage), total, create, update, unchanged, skip));
            }

            return new MoySkladPreviewDto(lines, _conflicts.Take(MaxConflicts).ToList(), truncated);
        }

        public async Task RunAsync(CancellationToken ct)
        {
            await LoadAsync(ct);
            var startIndex = run.Stage is { } s ? Math.Max(0, Stages.ToList().IndexOf(s)) : 0;
            for (var i = startIndex; i < Stages.Count; i++)
            {
                var stage = Stages[i];
                var offset = i == startIndex && run.Stage == stage ? run.StageOffset : 0;
                switch (stage)
                {
                    case "pricetype":
                        await PriceTypesAsync(ct);
                        await CheckpointAsync(stage, 0, ct);
                        break;
                    case "folder":
                        await FoldersAsync(ct);
                        await CheckpointAsync(stage, 0, ct);
                        break;
                    default:
                        await ItemsAsync(stage, offset, ct);
                        break;
                }
            }
        }

        private async Task CheckpointAsync(string stage, int nextOffset, CancellationToken ct)
        {
            // Запись загрузки — из базы: после отказа строки (DiscardChanges) прежний объект не отслеживается.
            var tracked = await db.ExternalImportRuns.SingleAsync(r => r.Id == run.Id, ct);
            tracked.Checkpoint(stage, nextOffset, _created, _updated, _skipped, _errors);
            (_created, _updated, _skipped, _errors) = (0, 0, 0, 0);
            await db.SaveChangesAsync(ct);
        }

        // ---------- Справочники в памяти ----------

        private async Task LoadAsync(CancellationToken ct)
        {
            foreach (var link in await db.ExternalLinks.AsNoTracking().Where(l => l.OrganizationId == org && l.System == Sys).ToListAsync(ct))
            {
                _links[(link.ObjectType, link.ExternalId)] = link;
            }

            var linkedItems = _links.Values.Where(l => l.ObjectType == ExternalObjectType.Item).Select(l => l.LocalId).ToHashSet();
            var items = await db.Items.AsNoTracking().Where(i => i.OrganizationId == org).Select(i => new { i.Id, i.Code, i.Article }).ToListAsync(ct);
            _codes = items.ToDictionary(i => i.Code, i => (i.Id, linkedItems.Contains(i.Id)), StringComparer.Ordinal);
            _articles = items.Where(i => i.Article != null).GroupBy(i => i.Article!, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().Id, StringComparer.OrdinalIgnoreCase);
            _units = await db.Units.AsNoTracking().Where(u => u.OrganizationId == org).ToListAsync(ct);
            _rates = await db.VatRates.AsNoTracking().Include(r => r.Periods).Where(r => r.OrganizationId == org && !r.IsArchived).ToListAsync(ct);
            await LoadAllAsync(MoySkladEntities.Uom, _uoms, ct);
            await LoadAllAsync(MoySkladEntities.Country, _countries, ct);
        }

        private async Task LoadAllAsync(string entity, Dictionary<string, MsRow> target, CancellationToken ct)
        {
            for (var offset = 0; ; offset += 1000)
            {
                var rows = (await client.ListAsync(token, entity, offset, 1000, null, ct)).Rows ?? [];
                foreach (var r in rows)
                {
                    target[r.Id] = r;
                }

                if (rows.Count < 1000)
                {
                    return;
                }
            }
        }

        private ExternalLink? Link(ExternalObjectType type, string? externalId) =>
            externalId is not null && _links.TryGetValue((type, externalId), out var l) ? l : null;

        private ExternalLink? LinkByCode(ExternalObjectType type, string? externalCode) =>
            string.IsNullOrWhiteSpace(externalCode) ? null : _links.Values.FirstOrDefault(l => l.ObjectType == type && l.ExternalCode == externalCode.Trim());

        private async Task SaveLinkAsync(ExternalObjectType type, long localId, MsRow row, string? hash, CancellationToken ct)
        {
            var existing = await db.ExternalLinks.SingleOrDefaultAsync(l => l.OrganizationId == org && l.System == Sys && l.ObjectType == type
                                                                           && (l.ExternalId == row.Id || l.LocalId == localId), ct);
            if (existing is not null && existing.ExternalId != row.Id)
            {
                // Объект пересоздан в МойСклад (новый id, тот же externalCode или код): связь переносится на новый id.
                db.ExternalLinks.Remove(existing);
                await db.SaveChangesAsync(ct);
                existing = null;
            }

            if (existing is null)
            {
                existing = ExternalLink.Create(org, Sys, type, localId, row.Id, row.ExternalCode, row.Updated, hash, clock.UtcNow);
                db.ExternalLinks.Add(existing);
            }
            else
            {
                existing.Touch(row.ExternalCode, row.Updated, hash, clock.UtcNow);
            }

            await db.SaveChangesAsync(ct);
            _links[(type, row.Id)] = existing;
        }

        private async Task IssueAsync(string entity, MsRow row, bool isError, string text, CancellationToken ct)
        {
            if (isError)
            {
                _errors++;
            }
            else
            {
                _skipped++;
            }

            if (dryRun)
            {
                return;
            }

            db.ExternalImportIssues.Add(ExternalImportIssue.Create(org, run.Id, entity, row.Id, row.Code, row.Name, isError, text));
            await db.SaveChangesAsync(ct);
        }

        // ---------- Виды цен и группы ----------

        private async Task PriceTypesAsync(CancellationToken ct)
        {
            var types = await db.PriceTypes.Where(t => t.OrganizationId == org).ToListAsync(ct);
            foreach (var pt in await client.PriceTypesAsync(token, ct))
            {
                if (Link(ExternalObjectType.PriceType, pt.Id) is not null)
                {
                    continue;
                }

                var name = (pt.Name ?? "").Trim();
                var local = types.FirstOrDefault(t => !t.IsArchived && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
                if (local is null)
                {
                    if (name.Length == 0 || types.Count(t => !t.IsArchived) >= PriceType.MaxTypes)
                    {
                        await IssueAsync("Вид цены", new MsRow { Id = pt.Id, Name = name }, false, "Вид цены не создан: пустое название или уже 20 видов цен.", ct);
                        continue;
                    }

                    local = PriceType.Create(org, name, true, false, types.Count + 1);
                    db.PriceTypes.Add(local);
                    await db.SaveChangesAsync(ct);
                    types.Add(local);
                    _created++;
                }

                await SaveLinkAsync(ExternalObjectType.PriceType, local.Id, new MsRow { Id = pt.Id, ExternalCode = pt.ExternalCode, Name = name }, null, ct);
            }
        }

        private Decision DecideFolder(MsRow row) =>
            Link(ExternalObjectType.ItemGroup, row.Id) is not null ? Decision.Unchanged : row.Archived ? Decision.Skip : Decision.Create;

        private async Task FoldersAsync(CancellationToken ct)
        {
            var all = new Dictionary<string, MsRow>();
            await LoadAllAsync(MoySkladEntities.Folder, all, ct);
            var groups = await db.ItemGroups.Where(g => g.OrganizationId == org).ToListAsync(ct);
            // Родители раньше детей: по глубине пути.
            foreach (var row in all.Values.Where(r => !r.Archived).OrderBy(r => (r.PathName ?? "").Count(c => c == '/') + (string.IsNullOrEmpty(r.PathName) ? 0 : 1))
                         .ThenBy(r => r.Name))
            {
                var parentId = row.ProductFolder?.RefId is { } p ? Link(ExternalObjectType.ItemGroup, p)?.LocalId : null;
                var name = (row.Name ?? "").Trim();
                try
                {
                    var link = Link(ExternalObjectType.ItemGroup, row.Id);
                    var group = link is not null ? groups.FirstOrDefault(g => g.Id == link.LocalId) : null;
                    if (group is null)
                    {
                        group = groups.FirstOrDefault(g => !g.IsArchived && g.ParentId == parentId && string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
                        if (group is null)
                        {
                            group = ItemGroup.Create(org, parentId, name);
                            db.ItemGroups.Add(group);
                            await db.SaveChangesAsync(ct);
                            groups.Add(group);
                            _created++;
                        }
                    }
                    else if (!group.IsArchived && group.Name != name)
                    {
                        group.Rename(name);
                        await db.SaveChangesAsync(ct);
                        _updated++;
                    }

                    await SaveLinkAsync(ExternalObjectType.ItemGroup, group.Id, row, null, ct);
                }
                catch (Exception ex) when (ex is BusinessRuleException or DbUpdateException)
                {
                    db.DiscardChanges();
                    groups = await db.ItemGroups.Where(g => g.OrganizationId == org).ToListAsync(ct);
                    await IssueAsync("Группа", row, true, Message(ex), ct);
                }
            }
        }

        // ---------- Товары, услуги, модификации, комплекты ----------

        private async Task ItemsAsync(string stage, int offset, CancellationToken ct)
        {
            var (entity, limit, expand) = stage switch
            {
                "service" => (MoySkladEntities.Service, PageSize, (string?)null),
                "variant" => (MoySkladEntities.Variant, PageSize, null),
                "bundle" => (MoySkladEntities.Bundle, BundlePageSize, "components.assortment"),
                _ => (MoySkladEntities.Product, PageSize, null),
            };
            while (true)
            {
                var rows = (await client.ListAsync(token, entity, offset, limit, expand, ct)).Rows ?? [];
                await using (var tx = await db.BeginTransactionAsync(ct))
                {
                    foreach (var row in rows)
                    {
                        await ItemAsync(stage, row, ct);
                    }

                    await CheckpointAsync(stage, offset + rows.Count, ct);
                    await tx.CommitAsync(ct);
                }

                if (rows.Count < limit)
                {
                    return;
                }

                offset += rows.Count;
            }
        }

        private ItemType TypeOf(string stage) => stage switch
        {
            "service" => ItemType.Service,
            "bundle" => ItemType.Bundle,
            _ => run.DefaultItemType,
        };

        /// <summary>Найти позицию: связь по id, связь по externalCode, затем код или артикул (только ещё не связанные позиции).</summary>
        private (long? LocalId, ExternalLink? Link) Match(MsRow row)
        {
            var link = Link(ExternalObjectType.Item, row.Id) ?? LinkByCode(ExternalObjectType.Item, row.ExternalCode);
            if (link is not null)
            {
                return (link.LocalId, link);
            }

            var code = Item.NormalizeCode(row.Code);
            if (code.Length > 0 && _codes.TryGetValue(code, out var byCode) && !byCode.Linked)
            {
                return (byCode.Id, null);
            }

            return !string.IsNullOrWhiteSpace(row.Article) && _articles.TryGetValue(row.Article.Trim(), out var byArticle)
                                                         && !_codes.Values.Any(c => c.Id == byArticle && c.Linked)
                ? (byArticle, null)
                : (null, null);
        }

        private async Task<Decision> DecideItemAsync(string stage, MsRow row, CancellationToken ct)
        {
            var (localId, link) = Match(row);
            if (localId is null)
            {
                if (row.Archived)
                {
                    return Decision.Skip;
                }

                var code = Item.NormalizeCode(row.Code);
                if (code.Length > 0 && _codes.ContainsKey(code))
                {
                    _conflicts.Add($"{StageName(stage)}: код {code} («{row.Name}») уже занят в knitERP — будет создан с суффиксом.");
                }

                return Decision.Create;
            }

            if (link is null)
            {
                return run.Overwrite ? Decision.Update : Decision.Skip;
            }

            if (link.ExternalVersion == row.Updated && !run.Overwrite)
            {
                return Decision.Unchanged;
            }

            var item = await db.Items.AsNoTracking().SingleAsync(i => i.Id == localId, ct);
            return link.ContentHash == Hash(item) || run.Overwrite ? Decision.Update : Decision.Skip;
        }

        private async Task ItemAsync(string stage, MsRow row, CancellationToken ct)
        {
            var entity = StageName(stage);
            try
            {
                var decision = await DecideItemAsync(stage, row, ct);
                var (localId, link) = Match(row);
                switch (decision)
                {
                    case Decision.Unchanged:
                        _skipped++;
                        return;
                    case Decision.Skip when localId is null:
                        await IssueAsync(entity, row, false, "В архиве МойСклад — не загружено.", ct);
                        return;
                    case Decision.Skip when link is null:
                        // Позиция knitERP с тем же кодом или артикулом: связываем, значения knitERP не трогаем.
                        var local = await db.Items.AsNoTracking().SingleAsync(i => i.Id == localId, ct);
                        await SaveLinkAsync(ExternalObjectType.Item, local.Id, row, Hash(local), ct);
                        MarkLinked(local);
                        await IssueAsync(entity, row, false, $"Связано с позицией {local.Code} по коду или артикулу; значения knitERP сохранены (включите «перезаписать», чтобы обновить).", ct);
                        return;
                    case Decision.Skip:
                        await IssueAsync(entity, row, false, "Позиция изменена в knitERP после прошлой загрузки — не перезаписана.", ct);
                        return;
                }

                var item = localId is { } id ? await db.Items.SingleAsync(i => i.Id == id, ct) : null;
                if (item is not null && item.Type != TypeOf(stage) && (stage != "product" || !ItemTypes.IsStocked(item.Type)))
                {
                    await IssueAsync(entity, row, true, $"В knitERP это «{ItemTypes.Name(item.Type)}», а в МойСклад — «{entity}»: тип не меняется загрузкой.", ct);
                    return;
                }

                item = stage == "variant" ? await VariantAsync(row, item, ct) : await ProductAsync(stage, row, item, ct);
                if (item is null)
                {
                    return;
                }

                await db.SaveChangesAsync(ct);
                await CommonAsync(row, item, ct);
                if (stage == "bundle")
                {
                    await ComponentsAsync(row, item, ct);
                }

                await db.SaveChangesAsync(ct);
                await SaveLinkAsync(ExternalObjectType.Item, item.Id, row, Hash(item), ct);
                MarkLinked(item);
                if (decision == Decision.Create)
                {
                    _created++;
                }
                else
                {
                    _updated++;
                }
            }
            catch (Exception ex) when (ex is BusinessRuleException or DbUpdateException or KeyNotFoundException)
            {
                db.DiscardChanges();
                await IssueAsync(entity, row, true, Message(ex), ct);
            }
        }

        private void MarkLinked(Item item) => _codes[item.Code] = (item.Id, true);

        /// <summary>Товар, услуга или комплект: создать или обновить карточку (без цен, штрихкодов и упаковок — они в CommonAsync).</summary>
        private async Task<Item?> ProductAsync(string stage, MsRow row, Item? item, CancellationToken ct)
        {
            var type = item?.Type ?? TypeOf(stage);
            var unitId = await UnitAsync(row.Uom?.RefId, ct);
            var name = (row.Name ?? "").Trim();
            if (item is null)
            {
                item = Item.Create(org, await FreeCodeAsync(row, ct), name, type, unitId, null, clock.UtcNow);
                db.Items.Add(item);
            }
            else
            {
                if (item.IsArchived)
                {
                    item.Restore(); // в МойСклад позиция действующая — возвращаем из архива (в архив снова — если и там в архиве)
                }

                if (unitId != item.UnitId && await db.ItemPacks.AnyAsync(p => p.ItemId == item.Id, ct))
                {
                    unitId = item.UnitId; // у позиции упаковки в основной единице — единицу не меняем
                }

                item.Update(item.Code, name, type, unitId, item.Description);
            }

            item.SetVatRate(Vat(row, type));
            var groupId = row.ProductFolder?.RefId is { } f ? Link(ExternalObjectType.ItemGroup, f)?.LocalId : null;
            var country = row.Country?.RefId is { } c && _countries.TryGetValue(c, out var cr) ? cr : null;
            var countryCode = country?.Code is { Length: 3 } cc && cc.All(char.IsAsciiDigit) ? cc : null;
            var service = type == ItemType.Service;
            item.SetDetails(item.Details with
            {
                GroupId = groupId ?? item.GroupId,
                Article = Clean(row.Article, ItemDetailRules.ArticleMaxLength),
                OriginCountryCode = countryCode ?? item.OriginCountryCode,
                OriginCountryName = countryCode is null ? item.OriginCountryName : Clean(country!.Name, ItemDetailRules.CountryNameMaxLength),
                TnVedCode = Digits(row.Tnved) is { Length: 4 or 6 or 8 or 10 } tn ? tn : item.TnVedCode,
                WeightKg = service ? null : Round(row.Weight, 6) is > 0 and var w ? w : item.WeightKg,
                VolumeM3 = service ? null : Round(row.Volume, 6) is > 0 and var v ? v : item.VolumeM3,
                MinStock = service || type == ItemType.Bundle ? null : item.MinStock,
                PurchasePrice = Kopecks(row.BuyPrice?.Value) ?? item.PurchasePrice,
            });
            return item;
        }

        /// <summary>Модификация: основная позиция — по связи товара МойСклад; характеристики — по названию (недостающие создаются).</summary>
        private async Task<Item?> VariantAsync(MsRow row, Item? item, CancellationToken ct)
        {
            var parentLink = Link(ExternalObjectType.Item, row.Product?.RefId);
            if (parentLink is null)
            {
                await IssueAsync("Модификации", row, true, "Основной товар не загружен — модификация пропущена.", ct);
                return null;
            }

            var parent = await db.Items.SingleAsync(i => i.Id == parentLink.LocalId, ct);
            var values = new Dictionary<long, string>();
            var characteristics = await db.Characteristics.Where(c => c.OrganizationId == org).ToListAsync(ct);
            foreach (var ch in row.Characteristics ?? [])
            {
                var chName = (ch.Name ?? "").Trim();
                var local = characteristics.FirstOrDefault(c => !c.IsArchived && string.Equals(c.Name, chName, StringComparison.OrdinalIgnoreCase));
                if (local is null)
                {
                    local = Characteristic.Create(org, chName, characteristics.Count + 1);
                    db.Characteristics.Add(local);
                    await db.SaveChangesAsync(ct);
                    characteristics.Add(local);
                }

                values[local.Id] = Variants.CleanValue(ch.Value);
            }

            var key = Variants.Key(values);
            item ??= await db.Items.SingleOrDefaultAsync(i => i.OrganizationId == org && i.ParentItemId == parent.Id && i.VariantKey == key, ct);
            var name = (row.Name ?? "").Trim();
            if (item is null)
            {
                item = Item.CreateModification(parent, await FreeCodeAsync(row, ct, parent.Code), name, key, clock.UtcNow);
                db.Items.Add(item);
                await db.SaveChangesAsync(ct);
            }
            else
            {
                item.Update(item.Code, name, item.Type, item.UnitId, item.Description);
                if (item.VariantKey != key)
                {
                    item.SetVariantKey(key);
                }

                db.ItemCharacteristicValues.RemoveRange(await db.ItemCharacteristicValues.Where(v => v.ItemId == item.Id).ToListAsync(ct));
                await db.SaveChangesAsync(ct);
            }

            foreach (var (characteristicId, value) in values)
            {
                db.ItemCharacteristicValues.Add(ItemCharacteristicValue.Create(org, item.Id, characteristicId, value));
            }

            return item;
        }

        /// <summary>Цены по видам, минимальная цена, маркировка, предмет расчёта, штрихкоды, упаковки, архив.</summary>
        private async Task CommonAsync(MsRow row, Item item, CancellationToken ct)
        {
            var barcodes = Barcodes(row.Barcodes).ToList();
            var gtin = barcodes.Where(b => b.Type == BarcodeType.Gtin).Select(b => b.Code).FirstOrDefault();
            var tracking = Marking.FromMoySklad(row.TrackingType);
            var isService = item.Type == ItemType.Service;
            var subject = isService ? PaymentSubject.Service : row.PaymentItemType == "EXCISABLE_GOOD" ? PaymentSubject.ExcisableGoods : PaymentSubject.Goods;
            if (gtin is not null && await db.Items.AnyAsync(i => i.OrganizationId == org && i.Gtin == gtin && i.Id != item.Id, ct))
            {
                gtin = null;
            }

            item.SetTrade(new ItemTradeDetails(subject, !isService && tracking != TrackingType.None, isService ? TrackingType.None : tracking,
                gtin ?? item.Gtin, Kopecks(row.MinPrice?.Value) ?? item.MinPrice));

            // Цены по видам: значение МойСклад — в копейках; изменение пишется в историю цен как «внешняя система».
            var prices = await db.ItemPrices.Where(p => p.ItemId == item.Id).ToDictionaryAsync(p => p.PriceTypeId, ct);
            foreach (var sp in row.SalePrices ?? [])
            {
                if (Link(ExternalObjectType.PriceType, sp.PriceType?.RefId) is not { } typeLink || Kopecks(sp.Value) is not { } value)
                {
                    continue;
                }

                var before = prices.TryGetValue(typeLink.LocalId, out var current) ? current.Price : (decimal?)null;
                if (before == value || (before is null && value == 0))
                {
                    continue;
                }

                if (current is null)
                {
                    db.ItemPrices.Add(ItemPrice.Create(org, item.Id, typeLink.LocalId, value));
                }
                else
                {
                    current.Set(value);
                }

                db.ItemPriceHistory.Add(ItemPriceHistory.Create(org, item.Id, typeLink.LocalId, before, value, clock.UtcNow, run.RequestedByUserId,
                    PriceChangeSource.External));
            }

            // Штрихкоды: добавляются недостающие; занятые другой позицией — замечание. Свои штрихкоды knitERP не удаляются.
            var own = await db.ItemBarcodes.Where(b => b.ItemId == item.Id).Select(b => b.Code).ToListAsync(ct);
            foreach (var (type, code) in barcodes.Where(b => b.Type != BarcodeType.Gtin || item.Gtin != b.Code).DistinctBy(b => b.Code))
            {
                if (own.Contains(code) || own.Count >= ItemBarcode.MaxPerItem)
                {
                    continue;
                }

                if (await db.ItemBarcodes.AnyAsync(b => b.OrganizationId == org && b.Code == code, ct)
                    || await db.ItemPacks.AnyAsync(p => p.OrganizationId == org && p.Barcode == code, ct))
                {
                    await IssueAsync(StageName("product"), row, false, $"Штрихкод {code} уже у другой позиции — не добавлен.", ct);
                    continue;
                }

                db.ItemBarcodes.Add(ItemBarcode.Create(org, item.Id, type, code));
                own.Add(code);
            }

            // Упаковки: по единице; коэффициент — в основной единице позиции.
            if (ItemTypes.IsStocked(item.Type) && row.Packs is { Count: > 0 } packs)
            {
                var precision = await db.Units.AsNoTracking().Where(u => u.Id == item.UnitId).Select(u => u.Precision).SingleAsync(ct);
                var current = await db.ItemPacks.Where(p => p.ItemId == item.Id).ToListAsync(ct);
                foreach (var pack in packs.Take(ItemPack.MaxPerItem))
                {
                    var unitId = await UnitAsync(pack.Uom?.RefId, ct);
                    if (unitId == item.UnitId || pack.Quantity is not > 0 || decimal.Round(pack.Quantity.Value, precision) != pack.Quantity.Value)
                    {
                        await IssueAsync(StageName("product"), row, false, "Упаковка пропущена: та же единица или дробное количество для штучной единицы.", ct);
                        continue;
                    }

                    string? barcode = null;
                    foreach (var candidate in Barcodes(pack.Barcodes).Select(b => b.Code))
                    {
                        if (!await db.ItemBarcodes.AnyAsync(x => x.OrganizationId == org && x.Code == candidate, ct)
                            && !await db.ItemPacks.AnyAsync(x => x.OrganizationId == org && x.Barcode == candidate && x.ItemId != item.Id, ct))
                        {
                            barcode = candidate;
                            break;
                        }
                    }

                    if (current.FirstOrDefault(p => p.UnitId == unitId) is { } existing)
                    {
                        existing.Set(pack.Quantity.Value, precision, barcode ?? existing.Barcode);
                    }
                    else if (current.Count < ItemPack.MaxPerItem)
                    {
                        var created = ItemPack.Create(org, item, unitId, pack.Quantity.Value, precision, barcode);
                        db.ItemPacks.Add(created);
                        current.Add(created);
                    }
                }
            }

            if (row.Archived && !item.IsArchived)
            {
                item.Archive(clock.UtcNow);
            }
        }

        /// <summary>Состав комплекта по компонентам (товары и модификации МойСклад); не загруженный компонент — замечание, состав не меняется.</summary>
        private async Task ComponentsAsync(MsRow row, Item bundle, CancellationToken ct)
        {
            var wanted = new List<(long ItemId, decimal Quantity)>();
            foreach (var c in row.Components?.Rows ?? [])
            {
                if (Link(ExternalObjectType.Item, c.Assortment?.RefId) is not { } link || c.Quantity is not > 0)
                {
                    await IssueAsync("Комплекты", row, true, "Компонент не загружен (услуга, архив или ошибка) — состав комплекта не изменён.", ct);
                    return;
                }

                wanted.Add((link.LocalId, c.Quantity.Value));
            }

            var current = await db.BundleComponents.Where(b => b.BundleItemId == bundle.Id).OrderBy(b => b.SortOrder).ThenBy(b => b.Id).ToListAsync(ct);
            if (current.Select(x => (x.ComponentItemId, x.Quantity)).SequenceEqual(wanted))
            {
                return;
            }

            db.BundleComponents.RemoveRange(current);
            await db.SaveChangesAsync(ct);
            var ids = wanted.Select(w => w.ItemId).ToList();
            var precision = await db.Items.AsNoTracking().Where(i => ids.Contains(i.Id)).Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new { i.Id, u.Precision })
                .ToDictionaryAsync(x => x.Id, x => x.Precision, ct);
            for (var i = 0; i < wanted.Count; i++)
            {
                db.BundleComponents.Add(BundleComponent.Create(org, bundle.Id, wanted[i].ItemId, Round(wanted[i].Quantity, precision[wanted[i].ItemId])!.Value,
                    precision[wanted[i].ItemId], i));
            }
        }

        // ---------- Сопоставление полей ----------

        /// <summary>Единица по связи или коду ОКЕИ, иначе создаётся (точность 0 для штучных кодов, иначе 3). Нет единицы — «шт».</summary>
        private async Task<long> UnitAsync(string? uomId, CancellationToken ct)
        {
            var units = _units;
            if (uomId is null || !_uoms.TryGetValue(uomId, out var uom))
            {
                return units.FirstOrDefault(u => u.Code == "796")?.Id ?? units.First().Id;
            }

            if (Link(ExternalObjectType.UnitOfMeasure, uomId) is { } link && units.Any(u => u.Id == link.LocalId))
            {
                return link.LocalId;
            }

            var code = Clean(uom.Code, UnitOfMeasure.CodeMaxLength);
            var symbol = Clean(uom.Name, UnitOfMeasure.SymbolMaxLength) ?? code ?? "ед";
            var local = units.FirstOrDefault(u => code is not null && u.Code == code)
                        ?? units.FirstOrDefault(u => string.Equals(u.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
            if (local is null)
            {
                var newCode = code ?? ("MS" + (units.Count + 1)).PadLeft(3, '0');
                while (units.Any(u => u.Code == newCode))
                {
                    newCode = ("MS" + Random.Shared.Next(100, 9999)).ToString(CultureInfo.InvariantCulture);
                }

                local = UnitOfMeasure.Create(org, newCode, Clean(uom.Description, UnitOfMeasure.NameMaxLength) ?? symbol, symbol,
                    code is "796" or "715" or "778" or "876" ? (byte)0 : (byte)3);
                db.Units.Add(local);
                await db.SaveChangesAsync(ct);
                _units.Add(local);
            }

            await SaveLinkAsync(ExternalObjectType.UnitOfMeasure, local.Id, uom, null, ct);
            return local.Id;
        }

        /// <summary>Ставка НДС: «без НДС» → вид «Без НДС»; иначе вид с тем же процентом на сегодня; у услуги без совпадения — основная ставка.</summary>
        private long? Vat(MsRow row, ItemType type)
        {
            var rates = _rates;
            var today = DateOnly.FromDateTime(clock.UtcNow);
            VatRate? rate = row.VatEnabled == false
                ? rates.FirstOrDefault(r => r.Kind == VatRateKind.Exempt)
                : (row.EffectiveVat ?? row.Vat) is { } percent ? rates.FirstOrDefault(r => r.PercentOn(today) == percent) : null;
            rate ??= type == ItemType.Service ? rates.FirstOrDefault(r => r.Kind == VatRateKind.Standard) : null;
            return rate?.Id;
        }

        /// <summary>Код из МойСклад; пустой — из начала id; занятый другой позицией — с суффиксом «-MS2», «-MS3»… (замечание в отчёте).</summary>
        private async Task<string> FreeCodeAsync(MsRow row, CancellationToken ct, string? parentCode = null)
        {
            var baseCode = Item.NormalizeCode(row.Code?.Replace(' ', '-'));
            if (baseCode.Length == 0)
            {
                baseCode = parentCode is null ? "MS-" + row.Id.Replace("-", "")[..8].ToUpperInvariant() : parentCode + "-" + row.Id.Replace("-", "")[..4].ToUpperInvariant();
            }

            baseCode = baseCode.Length > Item.CodeMaxLength - 5 ? baseCode[..(Item.CodeMaxLength - 5)] : baseCode;
            var code = baseCode;
            for (var n = 2; _codes.ContainsKey(code) || await db.Items.AnyAsync(i => i.OrganizationId == org && i.Code == code, ct); n++)
            {
                code = $"{baseCode}-MS{n}";
            }

            if (code != baseCode)
            {
                await IssueAsync("Код", row, false, $"Код {baseCode} уже занят в knitERP — позиция создана с кодом {code}.", ct);
                _skipped--; // замечание, а не пропуск
            }

            _codes[code] = (0, true);
            return code;
        }

        private static IEnumerable<(BarcodeType Type, string Code)> Barcodes(IReadOnlyList<Dictionary<string, string>>? list)
        {
            foreach (var entry in list ?? [])
            {
                foreach (var (kind, value) in entry)
                {
                    var type = kind switch { "ean13" => BarcodeType.Ean13, "ean8" => BarcodeType.Ean8, "gtin" => BarcodeType.Gtin, _ => BarcodeType.Code128 };
                    string? code;
                    try
                    {
                        code = type == BarcodeType.Gtin ? Marking.NormalizeGtin(value) : ItemBarcode.Normalize(type, value);
                    }
                    catch (BusinessRuleException)
                    {
                        code = null;
                    }

                    if (code is not null)
                    {
                        yield return (type, code);
                    }
                }
            }
        }

        /// <summary>
        /// Отпечаток значений карточки, записанных загрузкой: по нему видно, правили ли позицию вручную после прошлой загрузки.
        /// Цены, штрихкоды и упаковки в отпечаток не входят — они только дополняются.
        /// </summary>
        private static string Hash(Item i)
        {
            var text = string.Join('|', i.Name, i.Type, i.UnitId, i.VatRateId, i.GroupId, i.Article, i.OriginCountryCode, i.TnVedCode,
                F(i.WeightKg), F(i.VolumeM3), F(i.PurchasePrice), F(i.MinPrice), i.IsMarked, i.TrackingType, i.Gtin, i.PaymentSubject, i.IsArchived, i.VariantKey);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

            static string F(decimal? v) => v?.ToString("0.######", CultureInfo.InvariantCulture) ?? "";
        }

        private static decimal? Kopecks(decimal? value) => value is { } v && v >= 0 ? decimal.Round(v / 100m, 4) : null;

        private static decimal? Round(decimal? value, int scale) => value is { } v ? decimal.Round(v, scale) : null;

        private static string? Clean(string? value, int max) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim() is var t && t.Length > max ? t[..max] : value.Trim();

        private static string? Digits(string? value) => value is null ? null : new string(value.Where(char.IsAsciiDigit).ToArray());

        private static string Message(Exception ex) => ex switch
        {
            BusinessRuleException b => b.Message,
            DbUpdateException => "Не записано: нарушено ограничение базы (повтор кода, штрихкода или GTIN).",
            _ => "Не записано: нет связанного объекта.",
        };
    }
}
