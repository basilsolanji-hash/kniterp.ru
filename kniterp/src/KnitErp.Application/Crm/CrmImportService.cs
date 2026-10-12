using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Crm;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Crm;

public enum CrmImportKind : byte
{
    Leads = 1,
    Contacts = 2,
    Deals = 3,
}

public enum CrmImportAction : byte
{
    Create = 1,
    Update = 2,
    Skip = 3,
    Error = 4,
}

/// <summary>Строка протокола: значения по полям CRM, действие, ошибки и подсказки (дубликат и т. п.).</summary>
public sealed record CrmImportRow(int RowNumber, IReadOnlyDictionary<string, string> Values, CrmImportAction Action, IReadOnlyList<string> Messages, string? ExternalId);

/// <param name="System">Распознанный формат: «knitERP», «amoCRM», «Битрикс24».</param>
/// <param name="Mapping">Поле CRM → заголовок столбца файла (что с чем сопоставлено).</param>
public sealed record CrmImportPlan(CrmImportKind Kind, string System, IReadOnlyDictionary<string, string> Mapping, IReadOnlyList<CrmImportRow> Rows)
{
    public int ToCreate => Rows.Count(r => r.Action == CrmImportAction.Create);
    public int ToUpdate => Rows.Count(r => r.Action == CrmImportAction.Update);
    public int Skipped => Rows.Count(r => r.Action == CrmImportAction.Skip);
    public int Errors => Rows.Count(r => r.Action == CrmImportAction.Error);
    public bool CanApply => Errors == 0 && ToCreate + ToUpdate > 0;
}

/// <summary>
/// Импорт лидов, контактов и сделок (D645–D647, предложение А5): Excel из amoCRM, Битрикс24 или по шаблону knitERP. Столбцы
/// сопоставляются по названиям (синонимы каждой системы), этапы сделок — по названию этапа воронки (финальные: «Успешно реализовано»,
/// «Сделка успешна» → выигрыш; «Закрыто и не реализовано», «Сделка провалена» → проигрыш). Внешний id строки сохраняется
/// (`crm_external_links`): повторная загрузка обновляет, а не дублирует. Предпросмотр с протоколом ошибок; применение — всё или ничего.
/// Ответственный — по имени сотрудника, иначе правило распределения. Права: crm.edit и crm.all (массовая загрузка в организацию).
/// </summary>
public sealed class CrmImportService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, ISpreadsheetFormat spreadsheet)
{
    public const int MaxRows = 5000;

    private static readonly Dictionary<string, string[]> Synonyms = new(StringComparer.Ordinal)
    {
        ["id"] = ["id", "внешний id", "ид"],
        ["title"] = ["название", "название сделки", "название лида", "сделка", "лид", "наименование"],
        ["name"] = ["фио", "контакт", "контактное лицо", "имя", "полное имя", "основной контакт", "контакт: фио"],
        ["position"] = ["должность", "контакт: должность"],
        ["phone"] = ["телефон", "рабочий телефон", "мобильный телефон", "телефон контакта", "контакт: телефон", "рабочий телефон (контакт)"],
        ["email"] = ["почта", "email", "e-mail", "рабочий email", "рабочий e-mail", "email контакта", "контакт: email"],
        ["company"] = ["компания", "название компании", "покупатель", "контрагент", "компания: название"],
        ["inn"] = ["инн", "инн компании", "компания: инн"],
        ["source"] = ["источник", "источник сделки", "источник лида"],
        ["amount"] = ["сумма", "бюджет", "ожидаемая сумма", "сумма сделки", "возможная сделка"],
        ["stage"] = ["этап", "этап сделки", "стадия сделки", "стадия", "статус"],
        ["responsible"] = ["ответственный", "ответственный пользователь"],
        ["close"] = ["дата закрытия", "предполагаемая дата закрытия", "плановое закрытие"],
        ["need"] = ["потребность", "комментарий", "примечание", "описание"],
    };

    private static readonly string[] WonNames = ["успешно реализовано", "сделка успешна", "выиграна", "успешна"];
    private static readonly string[] LostNames = ["закрыто и не реализовано", "сделка провалена", "проиграна", "провалена"];

    /// <summary>Шаблон knitERP для вида объектов.</summary>
    public async Task<byte[]> TemplateAsync(CrmImportKind kind, CancellationToken ct = default)
    {
        await guard.DemandAsync(Permissions.CrmEdit, ct);
        IReadOnlyList<string> header = kind switch
        {
            CrmImportKind.Leads => ["ID", "Название", "Контакт", "Телефон", "Почта", "Компания", "ИНН", "Источник", "Сумма", "Ответственный", "Потребность"],
            CrmImportKind.Contacts => ["ID", "ФИО", "Должность", "Телефон", "Почта", "Компания", "ИНН", "Ответственный"],
            _ => ["ID", "Название", "Компания", "ИНН", "Контакт", "Телефон", "Почта", "Сумма", "Этап", "Ответственный", "Дата закрытия", "Источник"],
        };
        return spreadsheet.Write([new SheetData("Импорт CRM", header, [])]);
    }

    public async Task<CrmImportPlan> PreviewAsync(CrmImportKind kind, Stream file, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        await CrmSettingsService.EnsureDefaultsAsync(db, ctx.OrganizationId, ct);
        var sheet = spreadsheet.ReadFirstSheet(file, MaxRows + 1);
        if (sheet.Count < 2)
        {
            throw new BusinessRuleException("import.file_empty", "В файле нет строк с данными.");
        }

        if (sheet.Count > MaxRows + 1)
        {
            throw new BusinessRuleException("crm.import.too_many", $"В файле больше {MaxRows} строк — разбейте его на части.");
        }

        var header = sheet[0].Select(h => h.Trim()).ToList();
        var lower = header.Select(h => h.ToLowerInvariant()).ToList();
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (field, names) in Synonyms)
        {
            var i = lower.FindIndex(h => names.Contains(h));
            if (i >= 0)
            {
                index[field] = i;
                mapping[field] = header[i];
            }
        }

        var system = lower.Any(h => h is "этап сделки" or "основной контакт" or "бюджет") ? "amoCRM"
            : lower.Any(h => h is "стадия сделки" or "стадия" or "рабочий телефон" or "рабочий e-mail") ? "Битрикс24" : "knitERP";
        var required = kind == CrmImportKind.Contacts ? "name" : "title";
        if (!index.ContainsKey(required))
        {
            throw new BusinessRuleException("crm.import.header", kind == CrmImportKind.Contacts
                ? "В файле нет столбца «ФИО» (или «Имя», «Контакт»). Скачайте шаблон."
                : "В файле нет столбца «Название» (или «Название сделки»). Скачайте шаблон.");
        }

        var users = await UsersAsync(ctx, ct);
        var stages = await StagesAsync(ctx, ct);
        var links = await db.CrmExternalLinks.AsNoTracking().Where(l => l.OrganizationId == ctx.OrganizationId && l.System == system && l.Entity == Entity(kind))
            .ToDictionaryAsync(l => l.ExternalId, l => l.EntityId, ct);
        var rows = new List<CrmImportRow>();
        var seenIds = new HashSet<string>();
        for (var r = 1; r < sheet.Count; r++)
        {
            var cells = sheet[r];
            if (cells.All(c => string.IsNullOrWhiteSpace(c)))
            {
                continue;
            }

            var values = index.ToDictionary(x => x.Key, x => x.Value < cells.Count ? cells[x.Value].Trim() : "", StringComparer.Ordinal);
            var msgs = new List<string>();
            var ext = values.GetValueOrDefault("id") is { Length: > 0 } e ? e : null;
            var action = CrmImportAction.Create;
            try
            {
                Validate(kind, values, users, stages, msgs);
            }
            catch (BusinessRuleException ex)
            {
                msgs.Add(ex.Message);
            }

            if (ext is not null && !seenIds.Add(ext))
            {
                msgs.Add($"ID «{ext}» повторяется в файле.");
            }

            if (msgs.Count > 0)
            {
                action = CrmImportAction.Error;
            }
            else if (ext is not null && links.ContainsKey(ext))
            {
                action = CrmImportAction.Update;
            }
            else if (kind != CrmImportKind.Deals && await DuplicateAsync(ctx, values, ct))
            {
                action = CrmImportAction.Skip;
                msgs.Add("Уже есть в CRM с тем же телефоном или почтой — строка пропущена.");
            }

            rows.Add(new CrmImportRow(r + 1, values, action, msgs, ext));
        }

        return new CrmImportPlan(kind, system, mapping, rows);
    }

    public async Task<(int Created, int Updated)> ApplyAsync(CrmImportPlan plan, CancellationToken ct = default)
    {
        var ctx = await DemandAsync(ct);
        if (plan.Errors > 0)
        {
            throw new BusinessRuleException("import.has_errors", "В файле есть ошибки — ничего не загружено. Исправьте строки и загрузите снова.");
        }

        if (plan.Rows.Count > MaxRows)
        {
            throw new BusinessRuleException("crm.import.too_many", $"В файле больше {MaxRows} строк — разбейте его на части.");
        }

        var users = await UsersAsync(ctx, ct);
        var stages = await StagesAsync(ctx, ct);
        var currency = await CrmCommon.CurrencyAsync(db, ctx, ct);
        var lostReason = await LostReasonAsync(ctx, ct);
        var created = 0;
        var updated = 0;
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.LockAsync($"kniterp.crm.import.{ctx.OrganizationId}", ct);
        foreach (var row in plan.Rows.Where(r => r.Action is CrmImportAction.Create or CrmImportAction.Update))
        {
            var v = row.Values;
            // Проверки повторяются: план мог прийти не из PreviewAsync.
            var msgs = new List<string>();
            Validate(plan.Kind, v, users, stages, msgs);
            if (msgs.Count > 0)
            {
                throw new BusinessRuleException("import.has_errors", $"Строка {row.RowNumber}: {msgs[0]}");
            }

            var link = row.ExternalId is { } ext
                ? await db.CrmExternalLinks.SingleOrDefaultAsync(l => l.OrganizationId == ctx.OrganizationId && l.System == plan.System && l.Entity == Entity(plan.Kind)
                                                                      && l.ExternalId == ext, ct)
                : null;
            var responsible = Responsible(v, users) ?? await CrmEvents.AssigneeAsync(db, ctx.OrganizationId, ctx.UserId, ct);
            long id;
            switch (plan.Kind)
            {
                case CrmImportKind.Leads:
                {
                    var values = new CrmLeadValues(v["title"], await SourceAsync(ctx, v.GetValueOrDefault("source"), ct), null, responsible, Opt(v, "name"), Opt(v, "phone"),
                        Opt(v, "email"), null, Opt(v, "company"), Opt(v, "inn"), Opt(v, "need"), Amount(v));
                    var lead = link is null ? null : await db.CrmLeads.SingleOrDefaultAsync(l => l.Id == link.EntityId && l.OrganizationId == ctx.OrganizationId, ct);
                    if (lead is not null)
                    {
                        if (!lead.IsClosed)
                        {
                            lead.Update(values);
                            updated++;
                        }

                        id = lead.Id;
                        break;
                    }

                    var number = await DocumentNumbers.NextAsync(db, ctx.OrganizationId, CrmLead.NumberPrefix, ct);
                    lead = CrmLead.Create(ctx.OrganizationId, number, values, currency, ctx.UserId, clock.UtcNow);
                    db.CrmLeads.Add(lead);
                    await db.SaveChangesAsync(ct);
                    id = lead.Id;
                    created++;
                    break;
                }

                case CrmImportKind.Contacts:
                {
                    var company = await CounterpartyAsync(ctx, Opt(v, "company"), Opt(v, "inn"), false, ct);
                    var values = new CrmContactValues(v["name"], Opt(v, "position"), company, Opt(v, "phone"), Opt(v, "email"), null, null, responsible);
                    var contact = link is null ? null : await db.CrmContacts.Include(c => c.Channels)
                        .SingleOrDefaultAsync(c => c.Id == link.EntityId && c.OrganizationId == ctx.OrganizationId, ct);
                    if (contact is not null)
                    {
                        if (!contact.IsArchived)
                        {
                            contact.Update(values);
                            updated++;
                        }

                        id = contact.Id;
                        break;
                    }

                    contact = CrmContact.Create(ctx.OrganizationId, values, ctx.UserId, clock.UtcNow);
                    db.CrmContacts.Add(contact);
                    await db.SaveChangesAsync(ct);
                    id = contact.Id;
                    created++;
                    break;
                }

                default:
                {
                    var company = await CounterpartyAsync(ctx, Opt(v, "company") ?? Opt(v, "name") ?? v["title"], Opt(v, "inn"), true, ct)!;
                    long? contactId = Opt(v, "name") is { } contactName
                        ? await ContactAsync(ctx, contactName, Opt(v, "phone"), Opt(v, "email"), company!.Value, responsible, ct)
                        : null;
                    var values = new CrmDealValues(v["title"], company!.Value, contactId, Amount(v) ?? 0, null, Date(v), responsible, null, null,
                        await SourceAsync(ctx, v.GetValueOrDefault("source"), ct));
                    var deal = link is null ? null : await db.CrmDeals.Include(d => d.Lines).Include(d => d.Participants)
                        .SingleOrDefaultAsync(d => d.Id == link.EntityId && d.OrganizationId == ctx.OrganizationId, ct);
                    var stage = StageFor(v, stages);
                    if (deal is null)
                    {
                        var first = stages.First(s => s.Kind == CrmStageKind.Open);
                        var number = await DocumentNumbers.NextAsync(db, ctx.OrganizationId, CrmDeal.NumberPrefix, ct);
                        (deal, var entry) = CrmDeal.Create(ctx.OrganizationId, number, values, first, currency, ctx.UserId, clock.UtcNow);
                        db.CrmDeals.Add(deal);
                        await db.SaveChangesAsync(ct);
                        entry.AttachTo(deal);
                        db.CrmDealStageHistory.Add(entry);
                        created++;
                    }
                    else if (deal.Outcome == CrmDealOutcome.Open)
                    {
                        deal.Update(values);
                        updated++;
                    }

                    if (stage is not null && stage.Id != deal.StageId)
                    {
                        db.CrmDealStageHistory.Add(deal.MoveTo(stage, ctx.UserId, clock.UtcNow, stage.Kind == CrmStageKind.Lost ? lostReason : null,
                            stage.Kind == CrmStageKind.Lost ? "Перенесено из другой CRM" : null));
                    }

                    await db.SaveChangesAsync(ct);
                    id = deal.Id;
                    break;
                }
            }

            if (row.ExternalId is { } externalId && link is null)
            {
                db.CrmExternalLinks.Add(CrmExternalLink.Create(ctx.OrganizationId, plan.System, Entity(plan.Kind), externalId, id));
            }
        }

        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CrmImported, "CrmImport", null, null,
            $"{KindName(plan.Kind)} из {plan.System}: создано {created}, обновлено {updated}", null, currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (created, updated);
    }

    public static string KindName(CrmImportKind kind) => kind switch
    {
        CrmImportKind.Leads => "Лиды",
        CrmImportKind.Contacts => "Контакты",
        _ => "Сделки",
    };

    private static void Validate(CrmImportKind kind, IReadOnlyDictionary<string, string> v, IReadOnlyDictionary<string, long> users, IReadOnlyList<CrmStage> stages,
        List<string> msgs)
    {
        if (kind == CrmImportKind.Contacts ? Opt(v, "name") is null : Opt(v, "title") is null)
        {
            msgs.Add(kind == CrmImportKind.Contacts ? "Не указано ФИО." : "Не указано название.");
        }

        CrmText.NormalizePhone(Opt(v, "phone"));
        CrmText.NormalizeEmail(Opt(v, "email"));
        CrmText.NormalizeInn(Opt(v, "inn"));
        Amount(v);
        if (Opt(v, "responsible") is { } r && !users.ContainsKey(r.ToLowerInvariant()))
        {
            msgs.Add($"Сотрудник «{r}» не найден — оставьте поле пустым, и ответственный назначится по правилу.");
        }

        if (kind == CrmImportKind.Deals && Opt(v, "stage") is { } st && StageFor(v, stages) is null)
        {
            msgs.Add($"Этап «{st}» не найден в первой воронке — переименуйте этап в файле или в «Настройках CRM».");
        }

        if (Opt(v, "close") is { } c && TableImport.ParseDate(c) is null)
        {
            msgs.Add($"Дата «{c}» не распознана.");
        }
    }

    private static CrmStage? StageFor(IReadOnlyDictionary<string, string> v, IReadOnlyList<CrmStage> stages)
    {
        if (Opt(v, "stage") is not { } name)
        {
            return null;
        }

        var n = name.ToLowerInvariant();
        return stages.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
               ?? (WonNames.Contains(n) ? stages.FirstOrDefault(s => s.Kind == CrmStageKind.Won) : null)
               ?? (LostNames.Contains(n) ? stages.FirstOrDefault(s => s.Kind == CrmStageKind.Lost) : null);
    }

    private static string? Opt(IReadOnlyDictionary<string, string> v, string key) => v.TryGetValue(key, out var x) && x.Length > 0 ? x : null;

    private static decimal? Amount(IReadOnlyDictionary<string, string> v) =>
        CrmCurrency.Amount(Opt(v, "amount") is { } a ? Decimals.Parse(a, "Сумма") is { } d ? Math.Round(d, 2) : null : null);

    private static DateOnly? Date(IReadOnlyDictionary<string, string> v) => Opt(v, "close") is { } c ? TableImport.ParseDate(c) : null;

    private static long? Responsible(IReadOnlyDictionary<string, string> v, IReadOnlyDictionary<string, long> users) =>
        Opt(v, "responsible") is { } r && users.TryGetValue(r.ToLowerInvariant(), out var id) ? id : null;

    private static CrmExternalEntity Entity(CrmImportKind kind) => kind switch
    {
        CrmImportKind.Leads => CrmExternalEntity.Lead,
        CrmImportKind.Contacts => CrmExternalEntity.Contact,
        _ => CrmExternalEntity.Deal,
    };

    private async Task<bool> DuplicateAsync(AccessContext ctx, IReadOnlyDictionary<string, string> v, CancellationToken ct)
    {
        var p = CrmText.NormalizePhone(Opt(v, "phone"));
        var e = CrmText.NormalizeEmail(Opt(v, "email"));
        return (p is not null || e is not null) && (await CrmCommon.DuplicatesAsync(db, ctx, p, e, null, null, null, ct))
            .Any(d => d.Kind is CrmDuplicateKind.Lead or CrmDuplicateKind.Contact);
    }

    private async Task<Dictionary<string, long>> UsersAsync(AccessContext ctx, CancellationToken ct) =>
        (await db.OrganizationMembers.AsNoTracking().Where(m => m.OrganizationId == ctx.OrganizationId && m.Status == MembershipStatus.Active)
            .Join(db.Users.AsNoTracking(), m => m.UserId, u => u.Id, (m, u) => new { u.Id, u.DisplayName }).ToListAsync(ct))
        .GroupBy(u => u.DisplayName.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().Id);

    private async Task<List<CrmStage>> StagesAsync(AccessContext ctx, CancellationToken ct)
    {
        var pipeline = await db.CrmPipelines.AsNoTracking().Where(p => p.OrganizationId == ctx.OrganizationId && !p.IsArchived)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Id).Select(p => p.Id).FirstAsync(ct);
        return CrmSettingsService.Ordered(await db.CrmStages.AsNoTracking().Where(s => s.PipelineId == pipeline && !s.IsArchived).ToListAsync(ct)).ToList();
    }

    private async Task<long> LostReasonAsync(AccessContext ctx, CancellationToken ct)
    {
        const string name = "Перенесено из другой CRM";
        var id = await db.CrmReferences.Where(r => r.OrganizationId == ctx.OrganizationId && r.Kind == CrmReferenceKind.LossReason && r.Name == name)
            .Select(r => (long?)r.Id).FirstOrDefaultAsync(ct);
        if (id is { } existing)
        {
            return existing;
        }

        var item = CrmReference.Create(ctx.OrganizationId, CrmReferenceKind.LossReason, name, 2000);
        db.CrmReferences.Add(item);
        await db.SaveChangesAsync(ct);
        return item.Id;
    }

    private async Task<long?> SourceAsync(AccessContext ctx, string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var n = name.Trim();
        var id = await db.CrmReferences.Where(r => r.OrganizationId == ctx.OrganizationId && r.Kind == CrmReferenceKind.Source && !r.IsArchived && r.Name == n)
            .Select(r => (long?)r.Id).FirstOrDefaultAsync(ct);
        if (id is not null)
        {
            return id;
        }

        var item = CrmReference.Create(ctx.OrganizationId, CrmReferenceKind.Source, n, 1000);
        db.CrmReferences.Add(item);
        await db.SaveChangesAsync(ct);
        return item.Id;
    }

    /// <summary>Контрагент по ИНН или точному названию; required — для сделки создаётся новый покупатель.</summary>
    private async Task<long?> CounterpartyAsync(AccessContext ctx, string? name, string? inn, bool required, CancellationToken ct)
    {
        var i = CrmText.NormalizeInn(inn);
        var found = i is not null
            ? await db.Counterparties.Where(c => c.OrganizationId == ctx.OrganizationId && c.Inn == i).Select(c => (long?)c.Id).FirstOrDefaultAsync(ct)
            : null;
        if (found is null && name is not null)
        {
            found = await db.Counterparties.Where(c => c.OrganizationId == ctx.OrganizationId && !c.IsArchived && c.Name == name)
                .Select(c => (long?)c.Id).FirstOrDefaultAsync(ct);
        }

        if (found is not null || !required || name is null)
        {
            return found;
        }

        var country = await db.Organizations.AsNoTracking().Where(o => o.Id == ctx.OrganizationId).Select(o => o.CountryCode).SingleAsync(ct);
        var created = Counterparty.Create(ctx.OrganizationId, name, i, null, false, true, "Импорт CRM", country);
        db.Counterparties.Add(created);
        await db.SaveChangesAsync(ct);
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CatalogCreated, nameof(Counterparty), created.Id.ToString(),
            null, created.Name, "импорт CRM", currentUser.CorrelationId));
        return created.Id;
    }

    private async Task<long> ContactAsync(AccessContext ctx, string name, string? phone, string? email, long counterpartyId, long responsible, CancellationToken ct)
    {
        var p = CrmText.NormalizePhone(phone);
        var e = CrmText.NormalizeEmail(email);
        var existing = await db.CrmContacts.Where(c => c.OrganizationId == ctx.OrganizationId && c.ErasedAtUtc == null
                                                       && ((p != null && c.Phone == p) || (e != null && c.Email == e) || (c.FullName == name && c.CounterpartyId == counterpartyId)))
            .Select(c => (long?)c.Id).FirstOrDefaultAsync(ct);
        if (existing is { } id)
        {
            return id;
        }

        var contact = CrmContact.Create(ctx.OrganizationId, new CrmContactValues(name, null, counterpartyId, p, e, null, null, responsible), ctx.UserId, clock.UtcNow);
        db.CrmContacts.Add(contact);
        await db.SaveChangesAsync(ct);
        return contact.Id;
    }

    private async Task<AccessContext> DemandAsync(CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmEdit, ct);
        await guard.DemandAsync(Permissions.CrmAll, ct);
        return ctx;
    }
}
