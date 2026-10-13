using System.Globalization;
using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Sales;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Sales;

/// <summary>Канал продаж со свойствами (D1107).</summary>
public sealed record SalesChannelDto(long Id, string Name, MarketplaceKind? Marketplace, FulfillmentScheme? Scheme, long? CounterpartyId, string? Counterparty,
    byte[]? RowVersion);

/// <summary>Столбец файла маркетплейса: поле, заголовок по умолчанию и действующий (своё сопоставление или по умолчанию).</summary>
public sealed record MarketplaceColumnDto(MarketplaceField Field, string Default, string Header);

/// <summary>Товар без позиции — «нужно решение» (D1169): строки с одним ключом (код, артикул, штрихкод).</summary>
public sealed record MarketplaceUnmatchedDto(string Key, string? Article, string? ExternalCode, string? Barcode, string? Name, int Lines, decimal Quantity,
    decimal Amount);

/// <summary>Строка отчёта для показа; Item — «код — наименование» найденной позиции.</summary>
public sealed record MarketplaceLineDto(int Row, MarketplaceLineKind Kind, string? Article, string? ExternalCode, string? Barcode, string? Name, string? Note,
    DateOnly? Date, decimal Quantity, decimal Amount, decimal Commission, decimal Logistics, decimal Payout, long? ItemId, string? Item, MarketplaceMatch MatchedBy);

/// <summary>Итоги отчёта (D1168): продажи, возвраты, комиссия, логистика, к перечислению.</summary>
public sealed record MarketplaceTotalsDto(decimal SalesQuantity, decimal SalesAmount, decimal ReturnsQuantity, decimal ReturnsAmount, decimal Commission,
    decimal Logistics, decimal Payout, int Lines, int Unmatched);

/// <summary>
/// Предпросмотр файла (D1168): итоги, первые строки, ошибки строк, несопоставленные товары и дубль (DuplicateId — уже загруженный
/// действующий отчёт с тем же отпечатком).
/// </summary>
public sealed record MarketplacePreviewDto(long ChannelId, MarketplaceKind Marketplace, DateOnly? From, DateOnly? To, MarketplaceTotalsDto Totals,
    IReadOnlyList<MarketplaceLineDto> Lines, IReadOnlyList<MarketplaceRowError> Errors, IReadOnlyList<MarketplaceUnmatchedDto> Unmatched, long? DuplicateId,
    int HeaderRow);

public sealed record MarketplaceReportListItemDto(long Id, DateOnly From, DateOnly To, string Channel, MarketplaceKind Marketplace, string FileName,
    MarketplaceTotalsDto Totals, MarketplaceReportStatus Status, string CreatedBy, DateTime CreatedAtUtc);

public sealed record MarketplaceReportDto(long Id, long ChannelId, string Channel, MarketplaceKind Marketplace, DateOnly From, DateOnly To, string FileName,
    MarketplaceReportStatus Status, string? CancelReason, string CreatedBy, DateTime CreatedAtUtc, MarketplaceTotalsDto Totals,
    IReadOnlyList<MarketplaceLineDto> Lines, int TotalLines, IReadOnlyList<MarketplaceUnmatchedDto> Unmatched, byte[] RowVersion);

/// <summary>Позиция для выбора при сопоставлении.</summary>
public sealed record MarketplaceItemOptionDto(long Id, string Code, string Name, string? Article);

/// <summary>Результат загрузки: id отчёта, сопоставлено решением пользователя, создано внешних связей каталога.</summary>
public sealed record MarketplaceSaveResult(long ReportId, int Resolved, int LinksCreated);

/// <summary>
/// Каналы продаж и отчёты маркетплейсов (D1107, D1168–D1170): свойства канала (площадка, FBS/FBO, контрагент), сопоставление
/// столбцов, загрузка отчёта о реализации из Excel (предпросмотр → сохранение), сопоставление позиций по внешним связям каталога
/// и штрихкодам, решение по несопоставленным, аннулирование, выгрузка. Права: каналы и столбцы — «Продажи: просмотр» / «изменение»;
/// отчёты (деньги) — «Продажи: отчёты» и «Цены и суммы»; загрузка и решения — «Продажи: изменение» и «Цены и суммы».
/// </summary>
public sealed class MarketplaceReportService(IKnitErpDbContext db, IAccessGuard guard, ISpreadsheetFormat spreadsheet, ICurrentUser currentUser, IClock clock)
{
    public const int MaxFileBytes = 15 * 1024 * 1024;
    public const int MaxRows = MarketplaceReport.MaxLines;
    public const int PreviewLines = 200;
    public const int CardLines = 1000;

    // ---------- Каналы ----------

    public async Task<IReadOnlyList<SalesChannelDto>> ChannelsAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        var org = ctx.OrganizationId;
        var settings = await db.SalesChannelSettings.AsNoTracking().Where(s => s.OrganizationId == org).ToDictionaryAsync(s => s.ChannelId, ct);
        var partnerIds = settings.Values.Where(s => s.CounterpartyId != null).Select(s => s.CounterpartyId!.Value).ToList();
        var partners = await db.Counterparties.AsNoTracking().Where(c => c.OrganizationId == org && partnerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var channels = await db.Lookups.AsNoTracking().Where(l => l.OrganizationId == org && l.Kind == LookupKind.SalesChannel && !l.IsArchived)
            .OrderBy(l => l.Name).Select(l => new { l.Id, l.Name }).ToListAsync(ct);
        return channels.Select(c =>
        {
            var s = settings.GetValueOrDefault(c.Id);
            return new SalesChannelDto(c.Id, c.Name, s?.Marketplace, s?.Scheme, s?.CounterpartyId,
                s?.CounterpartyId is { } p ? partners.GetValueOrDefault(p) : null, s?.RowVersion);
        }).ToList();
    }

    /// <summary>Свойства канала (D1107): площадка, схема FBS/FBO, контрагент-маркетплейс. Канал и контрагент — своей организации.</summary>
    public async Task SetChannelAsync(long channelId, MarketplaceKind? marketplace, FulfillmentScheme? scheme, long? counterpartyId, byte[]? rowVersion,
        CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesEdit, ct);
        var org = ctx.OrganizationId;
        var channel = await db.Lookups.AsNoTracking().SingleOrDefaultAsync(l => l.Id == channelId && l.OrganizationId == org && l.Kind == LookupKind.SalesChannel, ct)
                      ?? throw new NotFoundException("Канал продаж");
        if (counterpartyId is { } cp && !await db.Counterparties.AnyAsync(c => c.Id == cp && c.OrganizationId == org, ct))
        {
            throw new NotFoundException("Контрагент");
        }

        var settings = await db.SalesChannelSettings.SingleOrDefaultAsync(s => s.OrganizationId == org && s.ChannelId == channelId, ct);
        var before = settings is null ? null : Describe(settings);
        try
        {
            if (settings is null)
            {
                settings = SalesChannelSettings.Create(org, channelId);
                settings.Set(marketplace, scheme, counterpartyId);
                db.SalesChannelSettings.Add(settings);
            }
            else
            {
                settings.EnsureVersion(settings.RowVersion, rowVersion ?? []);
                settings.Set(marketplace, scheme, counterpartyId);
            }
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }

        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, org, ctx.UserId, AuditActions.SalesChannelChanged, nameof(Lookup), channelId.ToString(),
            before, Describe(settings), channel.Name, currentUser.CorrelationId));
        await db.SaveOrConflictAsync(ct);
    }

    private static string Describe(SalesChannelSettings s) =>
        $"площадка {(s.Marketplace is { } m ? SalesChannelSettings.MarketplaceName(m) : "—")}, схема {(s.Scheme is { } sc ? SalesChannelSettings.SchemeName(sc) : "—")}, "
        + $"контрагент {s.CounterpartyId?.ToString() ?? "—"}";

    // ---------- Столбцы ----------

    public async Task<IReadOnlyList<MarketplaceColumnDto>> ColumnsAsync(MarketplaceKind marketplace, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesView, ct);
        var headers = await HeadersAsync(ctx.OrganizationId, marketplace, ct);
        var defaults = MarketplaceFormats.Defaults(marketplace);
        return Enum.GetValues<MarketplaceField>().Select(f => new MarketplaceColumnDto(f, defaults[f], headers[f])).ToList();
    }

    /// <summary>Своё сопоставление столбцов (D1168): заголовок поля в файле; пусто — поле не берётся из файла.</summary>
    public async Task SetColumnsAsync(MarketplaceKind marketplace, IReadOnlyDictionary<MarketplaceField, string?> headers, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesEdit, ct);
        if (!Enum.IsDefined(marketplace))
        {
            throw new BusinessRuleException("sales.channel.marketplace", "Выберите площадку из списка.");
        }

        var org = ctx.OrganizationId;
        var maps = await db.MarketplaceColumnMaps.Where(m => m.OrganizationId == org && m.Marketplace == marketplace).ToDictionaryAsync(m => m.Field, ct);
        var changes = new List<string>();
        try
        {
            foreach (var (field, header) in headers)
            {
                if (!Enum.IsDefined(field))
                {
                    continue;
                }

                if (!maps.TryGetValue(field, out var map))
                {
                    map = MarketplaceColumnMap.Create(org, marketplace, field);
                    map.Set(MarketplaceFormats.Defaults(marketplace)[field]);
                    db.MarketplaceColumnMaps.Add(map);
                    maps[field] = map;
                }

                var old = map.Header;
                map.Set(header);
                if (old != map.Header)
                {
                    changes.Add($"{MarketplaceFormats.FieldName(field)}: «{map.Header}»");
                }
            }
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }

        if (changes.Count == 0)
        {
            db.DiscardChanges();
            return;
        }

        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, org, ctx.UserId, AuditActions.MarketplaceColumnsChanged, nameof(MarketplaceColumnMap),
            ((byte)marketplace).ToString(CultureInfo.InvariantCulture), null, string.Join("; ", changes), SalesChannelSettings.MarketplaceName(marketplace),
            currentUser.CorrelationId));
        await db.SaveOrConflictAsync(ct);
    }

    private async Task<Dictionary<MarketplaceField, string>> HeadersAsync(long org, MarketplaceKind marketplace, CancellationToken ct)
    {
        var result = MarketplaceFormats.Defaults(marketplace).ToDictionary(x => x.Key, x => x.Value);
        foreach (var m in await db.MarketplaceColumnMaps.AsNoTracking().Where(m => m.OrganizationId == org && m.Marketplace == marketplace).ToListAsync(ct))
        {
            result[m.Field] = m.Header;
        }

        return result;
    }

    // ---------- Загрузка ----------

    public async Task<MarketplacePreviewDto> PreviewAsync(long channelId, byte[] content, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        var (marketplace, parsed) = await ParseAsync(ctx, channelId, content, ct);
        await MatchAsync(ctx.OrganizationId, marketplace, parsed.Lines, ct);
        var duplicate = await DuplicateAsync(ctx.OrganizationId, parsed.Fingerprint, ct);
        var names = await ItemNamesAsync(ctx.OrganizationId, parsed.Lines.Take(PreviewLines), ct);
        return new MarketplacePreviewDto(channelId, marketplace, parsed.From, parsed.To, Totals(parsed.Lines),
            parsed.Lines.Take(PreviewLines).Select(l => Line(l, names)).ToList(), parsed.Errors, Unmatched(parsed.Lines), duplicate, parsed.HeaderRow);
    }

    /// <summary>
    /// Сохранение отчёта (D1168–D1170): файл разбирается заново на сервере. Строки с ошибками — только с acceptErrors (они не загружаются).
    /// Повтор файла (тот же отпечаток у действующего отчёта) — отказ. decisions — позиции для несопоставленных товаров по ключу:
    /// строки получают позицию, а в каталоге создаётся внешняя связь с площадкой (если у позиции её ещё нет), чтобы в следующий раз
    /// товар нашёлся сам. Период — из дат файла, если не задан.
    /// </summary>
    public async Task<MarketplaceSaveResult> SaveAsync(long channelId, byte[] content, string? fileName, DateOnly? from, DateOnly? to,
        IReadOnlyDictionary<string, long>? decisions, bool acceptErrors, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        var org = ctx.OrganizationId;
        var (marketplace, parsed) = await ParseAsync(ctx, channelId, content, ct);
        if (parsed.Errors.Count > 0 && !acceptErrors)
        {
            throw new BusinessRuleException("marketplace.row_errors",
                $"В файле {parsed.Errors.Count} строк с ошибками. Исправьте файл или подтвердите загрузку без этих строк.");
        }

        var periodFrom = from ?? parsed.From ?? throw new BusinessRuleException("marketplace.no_period", "В файле нет дат — укажите период отчёта.");
        var periodTo = to ?? parsed.To ?? periodFrom;
        if (await DuplicateAsync(org, parsed.Fingerprint, ct) is { } duplicate)
        {
            throw new BusinessRuleException("marketplace.duplicate", $"Этот отчёт уже загружен (№ {duplicate}). Повторно он не загружается.");
        }

        await MatchAsync(org, marketplace, parsed.Lines, ct);
        var (resolved, links) = await ApplyDecisionsAsync(org, marketplace, parsed.Lines, decisions ?? new Dictionary<string, long>(), ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.LockAsync($"marketplace-report:{org}", ct);
        if (await DuplicateAsync(org, parsed.Fingerprint, ct) is { } again)
        {
            db.DiscardChanges();
            throw new BusinessRuleException("marketplace.duplicate", $"Этот отчёт уже загружен (№ {again}). Повторно он не загружается.");
        }

        MarketplaceReport report;
        try
        {
            report = MarketplaceReport.Create(org, channelId, marketplace, periodFrom, periodTo, fileName, parsed.Fingerprint, parsed.Lines, ctx.UserId, clock.UtcNow);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }

        db.MarketplaceReports.Add(report);
        await db.SaveChangesAsync(ct);
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, org, ctx.UserId, AuditActions.MarketplaceReportImported, nameof(MarketplaceReport),
            report.Id.ToString(CultureInfo.InvariantCulture), null,
            $"{SalesChannelSettings.MarketplaceName(marketplace)} {periodFrom:dd.MM.yyyy}–{periodTo:dd.MM.yyyy}: строк {report.LineCount}, без позиции {report.UnmatchedCount}, "
            + $"продажи {report.SalesAmount.ToString("0.00", CultureInfo.InvariantCulture)}, возвраты {report.ReturnsAmount.ToString("0.00", CultureInfo.InvariantCulture)}, "
            + $"пропущено строк с ошибками {parsed.Errors.Count}", report.FileName, currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new MarketplaceSaveResult(report.Id, resolved, links);
    }

    /// <summary>Решение по несопоставленному товару в сохранённом отчёте (D1169): позиция для всех строк с этим ключом и связь каталога.</summary>
    public async Task<MarketplaceSaveResult> ResolveAsync(long reportId, string key, long itemId, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        var org = ctx.OrganizationId;
        var report = await db.MarketplaceReports.Include(r => r.Lines).SingleOrDefaultAsync(r => r.Id == reportId && r.OrganizationId == org, ct)
                     ?? throw new NotFoundException("Отчёт маркетплейса");
        report.EnsureVersion(report.RowVersion, rowVersion);
        if (report.Status != MarketplaceReportStatus.Active)
        {
            throw new BusinessRuleException("marketplace.cancelled", "Отчёт аннулирован — изменить его нельзя.");
        }

        var sample = report.Lines.FirstOrDefault(l => l.ItemId is null && l.Key == key)
                     ?? throw new BusinessRuleException("marketplace.no_unmatched", "Строк без позиции с этим товаром в отчёте нет — обновите страницу.");
        await EnsureItemsAsync(org, [itemId], ct);
        var count = report.Resolve(key, itemId);
        var links = await LinkAsync(org, report.Marketplace, sample, itemId, ct) ? 1 : 0;
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, org, ctx.UserId, AuditActions.MarketplaceReportResolved, nameof(MarketplaceReport),
            report.Id.ToString(CultureInfo.InvariantCulture), $"{sample.ExternalCode} {sample.Article} {sample.Barcode}".Trim(),
            $"позиция {itemId}, строк {count}", report.FileName, currentUser.CorrelationId));
        await db.SaveOrConflictAsync(ct);
        return new MarketplaceSaveResult(report.Id, count, links);
    }

    /// <summary>Аннулирование отчёта с причиной (D1170): отчёт остаётся в истории, тот же файл можно загрузить снова.</summary>
    public async Task CancelAsync(long reportId, string? reason, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        var report = await db.MarketplaceReports.SingleOrDefaultAsync(r => r.Id == reportId && r.OrganizationId == ctx.OrganizationId, ct)
                     ?? throw new NotFoundException("Отчёт маркетплейса");
        report.EnsureVersion(report.RowVersion, rowVersion);
        try
        {
            report.Cancel(reason, ctx.UserId, clock.UtcNow);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }

        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.MarketplaceReportCancelled, nameof(MarketplaceReport),
            report.Id.ToString(CultureInfo.InvariantCulture), "действует", "аннулирован", report.CancelReason, currentUser.CorrelationId));
        await db.SaveOrConflictAsync(ct);
    }

    // ---------- Просмотр ----------

    public async Task<IReadOnlyList<MarketplaceReportListItemDto>> ListAsync(long? channelId = null, CancellationToken ct = default)
    {
        var ctx = await DemandViewAsync(ct);
        var org = ctx.OrganizationId;
        var rows = await (from r in db.MarketplaceReports.AsNoTracking()
                          join l in db.Lookups.AsNoTracking() on r.ChannelId equals l.Id
                          join u in db.Users.AsNoTracking() on r.CreatedByUserId equals u.Id
                          where r.OrganizationId == org && (channelId == null || r.ChannelId == channelId)
                          orderby r.PeriodTo descending, r.Id descending
                          select new { r, Channel = l.Name, u.DisplayName })
            .Take(500).ToListAsync(ct);
        return rows.Select(x => new MarketplaceReportListItemDto(x.r.Id, x.r.PeriodFrom, x.r.PeriodTo, x.Channel, x.r.Marketplace, x.r.FileName, Totals(x.r),
            x.r.Status, x.DisplayName, x.r.CreatedAtUtc)).ToList();
    }

    public async Task<MarketplaceReportDto> GetAsync(long reportId, CancellationToken ct = default)
    {
        var ctx = await DemandViewAsync(ct);
        var org = ctx.OrganizationId;
        var report = await db.MarketplaceReports.AsNoTracking().SingleOrDefaultAsync(r => r.Id == reportId && r.OrganizationId == org, ct)
                     ?? throw new NotFoundException("Отчёт маркетплейса");
        var lines = await db.MarketplaceReportLines.AsNoTracking().Where(l => l.OrganizationId == org && l.ReportId == reportId)
            .OrderBy(l => l.RowNumber).ThenBy(l => l.Kind).ToListAsync(ct);
        var shown = lines.Take(CardLines).ToList();
        var names = await ItemNamesAsync(org, shown, ct);
        var channel = await db.Lookups.AsNoTracking().Where(l => l.Id == report.ChannelId).Select(l => l.Name).SingleAsync(ct);
        var by = await db.Users.AsNoTracking().Where(u => u.Id == report.CreatedByUserId).Select(u => u.DisplayName).SingleAsync(ct);
        return new MarketplaceReportDto(report.Id, report.ChannelId, channel, report.Marketplace, report.PeriodFrom, report.PeriodTo, report.FileName,
            report.Status, report.CancelReason, by, report.CreatedAtUtc, Totals(report), shown.Select(l => Line(l, names)).ToList(), lines.Count,
            Unmatched(lines), report.RowVersion);
    }

    /// <summary>Строки отчёта в Excel (D1165): числа — числовыми столбцами.</summary>
    public async Task<byte[]> ExportAsync(long reportId, CancellationToken ct = default)
    {
        var ctx = await DemandViewAsync(ct);
        var org = ctx.OrganizationId;
        var report = await db.MarketplaceReports.AsNoTracking().SingleOrDefaultAsync(r => r.Id == reportId && r.OrganizationId == org, ct)
                     ?? throw new NotFoundException("Отчёт маркетплейса");
        var lines = await db.MarketplaceReportLines.AsNoTracking().Where(l => l.OrganizationId == org && l.ReportId == reportId)
            .OrderBy(l => l.RowNumber).ThenBy(l => l.Kind).ToListAsync(ct);
        var names = await ItemNamesAsync(org, lines, ct);
        static string N(decimal v) => v.ToString(CultureInfo.InvariantCulture);
        static string K(MarketplaceLineKind k) => k switch { MarketplaceLineKind.Sale => "Продажа", MarketplaceLineKind.Return => "Возврат", _ => "Услуга" };
        var rows = lines.Select(l => (IReadOnlyList<string>)[l.RowNumber.ToString(CultureInfo.InvariantCulture), K(l.Kind), l.SaleDate?.ToString("dd.MM.yyyy") ?? "",
            l.Article ?? "", l.ExternalCode ?? "", l.Barcode ?? "", l.Name ?? "", l.Note ?? "", N(l.Quantity), N(l.Amount), N(l.Commission), N(l.Logistics),
            N(l.Payout), l.ItemId is { } i ? names.GetValueOrDefault(i) ?? "" : "нужно решение", ""]).ToList();
        return spreadsheet.Write([new SheetData("Отчёт маркетплейса", ["Строка файла", "Вид", "Дата", "Артикул", "Код площадки", "Штрихкод", "Наименование",
            "Обоснование", "Количество", "Сумма", "Комиссия", "Логистика", "К перечислению", "Позиция",
            $"{SalesChannelSettings.MarketplaceName(report.Marketplace)} {report.PeriodFrom:dd.MM.yyyy}–{report.PeriodTo:dd.MM.yyyy}"], rows,
            new HashSet<int> { 0, 8, 9, 10, 11, 12 })]);
    }

    /// <summary>Поиск позиции для сопоставления: по коду, артикулу, наименованию или штрихкоду, до 20 штук.</summary>
    public async Task<IReadOnlyList<MarketplaceItemOptionDto>> FindItemsAsync(string? search, CancellationToken ct = default)
    {
        var ctx = await DemandEditAsync(ct);
        var text = search?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length < 2)
        {
            return [];
        }

        var org = ctx.OrganizationId;
        var byBarcode = db.ItemBarcodes.AsNoTracking().Where(b => b.OrganizationId == org && b.Code == text).Select(b => b.ItemId);
        return await db.Items.AsNoTracking()
            .Where(i => i.OrganizationId == org && !i.IsArchived
                        && (i.Code.Contains(text) || i.Name.Contains(text) || (i.Article != null && i.Article.Contains(text)) || byBarcode.Contains(i.Id)))
            .OrderBy(i => i.Code).Take(20).Select(i => new MarketplaceItemOptionDto(i.Id, i.Code, i.Name, i.Article)).ToListAsync(ct);
    }

    // ---------- Внутреннее ----------

    private async Task<AccessContext> DemandEditAsync(CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesEdit, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        return ctx;
    }

    private async Task<AccessContext> DemandViewAsync(CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesReportView, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        return ctx;
    }

    private async Task<(MarketplaceKind, MarketplaceParseResult)> ParseAsync(AccessContext ctx, long channelId, byte[] content, CancellationToken ct)
    {
        var org = ctx.OrganizationId;
        if (!await db.Lookups.AnyAsync(l => l.Id == channelId && l.OrganizationId == org && l.Kind == LookupKind.SalesChannel, ct))
        {
            throw new NotFoundException("Канал продаж");
        }

        var marketplace = await db.SalesChannelSettings.AsNoTracking().Where(s => s.OrganizationId == org && s.ChannelId == channelId)
                              .Select(s => s.Marketplace).SingleOrDefaultAsync(ct)
                          ?? throw new BusinessRuleException("marketplace.channel", "У канала не указана площадка. Отметьте канал как Wildberries, Ozon или иной маркетплейс.");
        if (content.Length is 0 or > MaxFileBytes)
        {
            throw new BusinessRuleException("marketplace.file_size", "Файл отчёта пустой или больше 15 МБ.");
        }

        var sheet = spreadsheet.ReadWideSheet(new MemoryStream(content), MaxRows + MarketplaceFormats.HeaderSearchRows);
        var headers = await HeadersAsync(org, marketplace, ct);
        return (marketplace, MarketplaceFormats.Parse(marketplace, sheet, headers, org));
    }

    private async Task<long?> DuplicateAsync(long org, string fingerprint, CancellationToken ct) =>
        await db.MarketplaceReports.AsNoTracking()
            .Where(r => r.OrganizationId == org && r.Fingerprint == fingerprint && r.Status == MarketplaceReportStatus.Active)
            .Select(r => (long?)r.Id).FirstOrDefaultAsync(ct);

    /// <summary>
    /// Сопоставление (D1169): внешняя связь площадки по коду товара на площадке (nmId, SKU), затем по артикулу продавца (внешний
    /// код связи), затем штрихкод позиции. Неоднозначное совпадение (несколько позиций) — не сопоставляется.
    /// </summary>
    private async Task MatchAsync(long org, MarketplaceKind marketplace, IReadOnlyList<MarketplaceReportLine> lines, CancellationToken ct)
    {
        var codes = lines.Where(l => l.ExternalCode != null).Select(l => l.ExternalCode!).Distinct().ToList();
        var articles = lines.Where(l => l.Article != null).Select(l => l.Article!).Distinct().ToList();
        var barcodes = lines.Where(l => l.Barcode != null).Select(l => l.Barcode!).Distinct().ToList();
        Dictionary<string, long> byCode = [], byArticle = [];
        if (MarketplaceFormats.SystemOf(marketplace) is { } system)
        {
            var links = await db.ExternalLinks.AsNoTracking()
                .Where(x => x.OrganizationId == org && x.System == system && x.ObjectType == ExternalObjectType.Item
                            && (codes.Contains(x.ExternalId) || (x.ExternalCode != null && articles.Contains(x.ExternalCode))))
                .Select(x => new { x.ExternalId, x.ExternalCode, x.LocalId }).ToListAsync(ct);
            byCode = links.Where(x => codes.Contains(x.ExternalId)).GroupBy(x => x.ExternalId).Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key, g => g.Single().LocalId);
            byArticle = links.Where(x => x.ExternalCode != null && articles.Contains(x.ExternalCode)).GroupBy(x => x.ExternalCode!)
                .Where(g => g.Select(x => x.LocalId).Distinct().Count() == 1).ToDictionary(g => g.Key, g => g.First().LocalId);
        }

        var byBarcode = (await db.ItemBarcodes.AsNoTracking().Where(b => b.OrganizationId == org && barcodes.Contains(b.Code))
                .Select(b => new { b.Code, b.ItemId }).ToListAsync(ct))
            .GroupBy(b => b.Code).Where(g => g.Select(x => x.ItemId).Distinct().Count() == 1).ToDictionary(g => g.Key, g => g.First().ItemId);
        foreach (var line in lines)
        {
            if (line.ExternalCode is { } c && byCode.TryGetValue(c, out var a))
            {
                line.Match(a, MarketplaceMatch.ExternalLink);
            }
            else if (line.Article is { } art && byArticle.TryGetValue(art, out var b))
            {
                line.Match(b, MarketplaceMatch.ExternalLink);
            }
            else if (line.Barcode is { } bc && byBarcode.TryGetValue(bc, out var d))
            {
                line.Match(d, MarketplaceMatch.Barcode);
            }
        }
    }

    /// <summary>
    /// Решения пользователя: позиция — своей организации; строки с ключом получают её, а в каталоге создаётся связь с площадкой
    /// (внешний id — код площадки, иначе артикул), если у позиции и у этого внешнего id связи ещё нет.
    /// </summary>
    private async Task<(int Resolved, int Links)> ApplyDecisionsAsync(long org, MarketplaceKind marketplace, IReadOnlyList<MarketplaceReportLine> lines,
        IReadOnlyDictionary<string, long> decisions, CancellationToken ct)
    {
        if (decisions.Count == 0)
        {
            return (0, 0);
        }

        await EnsureItemsAsync(org, decisions.Values.Distinct().ToList(), ct);
        var links = 0;
        var resolved = 0;
        foreach (var (key, itemId) in decisions)
        {
            var hits = lines.Where(l => l.ItemId is null && l.Key == key).ToList();
            if (hits.Count == 0)
            {
                continue;
            }

            foreach (var line in hits)
            {
                line.Match(itemId, MarketplaceMatch.Manual);
            }

            resolved += hits.Count;
            links += await LinkAsync(org, marketplace, hits[0], itemId, ct) ? 1 : 0;
        }

        return (resolved, links);
    }

    private async Task EnsureItemsAsync(long org, IReadOnlyList<long> itemIds, CancellationToken ct)
    {
        var known = await db.Items.AsNoTracking().CountAsync(i => i.OrganizationId == org && itemIds.Contains(i.Id), ct);
        if (known != itemIds.Count)
        {
            throw new NotFoundException("Позиция");
        }
    }

    /// <summary>Связь каталога с площадкой для решения (внешний id — код площадки, иначе артикул), если ни позиция, ни внешний id ещё не связаны.</summary>
    private async Task<bool> LinkAsync(long org, MarketplaceKind marketplace, MarketplaceReportLine sample, long itemId, CancellationToken ct)
    {
        var externalId = sample.ExternalCode ?? sample.Article;
        if (MarketplaceFormats.SystemOf(marketplace) is not { } s || externalId is null || externalId.Length > ExternalLink.ExternalIdMaxLength)
        {
            return false;
        }

        var taken = await db.ExternalLinks.AnyAsync(x => x.OrganizationId == org && x.System == s && x.ObjectType == ExternalObjectType.Item
                                                         && (x.LocalId == itemId || x.ExternalId == externalId), ct)
                    || db.ExternalLinks.Local.Any(x => x.OrganizationId == org && x.System == s && (x.LocalId == itemId || x.ExternalId == externalId));
        if (taken)
        {
            return false;
        }

        db.ExternalLinks.Add(ExternalLink.Create(org, s, ExternalObjectType.Item, itemId, externalId, sample.Article, null, null, clock.UtcNow));
        return true;
    }

    private async Task<Dictionary<long, string>> ItemNamesAsync(long org, IEnumerable<MarketplaceReportLine> lines, CancellationToken ct)
    {
        var ids = lines.Where(l => l.ItemId != null).Select(l => l.ItemId!.Value).Distinct().ToList();
        return await db.Items.AsNoTracking().Where(i => i.OrganizationId == org && ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Code + " — " + i.Name, ct);
    }

    private static MarketplaceLineDto Line(MarketplaceReportLine l, IReadOnlyDictionary<long, string> names) =>
        new(l.RowNumber, l.Kind, l.Article, l.ExternalCode, l.Barcode, l.Name, l.Note, l.SaleDate, l.Quantity, l.Amount, l.Commission, l.Logistics, l.Payout,
            l.ItemId, l.ItemId is { } id ? names.GetValueOrDefault(id) : null, l.MatchedBy);

    private static MarketplaceTotalsDto Totals(IReadOnlyList<MarketplaceReportLine> lines)
    {
        var sales = lines.Where(l => l.Kind == MarketplaceLineKind.Sale).ToList();
        var returns = lines.Where(l => l.Kind == MarketplaceLineKind.Return).ToList();
        var services = lines.Where(l => l.Kind == MarketplaceLineKind.Service).ToList();
        return new MarketplaceTotalsDto(sales.Sum(l => l.Quantity), Money.Round(sales.Sum(l => l.Amount)), returns.Sum(l => l.Quantity),
            Money.Round(returns.Sum(l => l.Amount)),
            Money.Round(sales.Sum(l => l.Commission) - returns.Sum(l => l.Commission) + services.Sum(l => l.Commission)),
            Money.Round(lines.Sum(l => l.Logistics)), Money.Round(sales.Sum(l => l.Payout) - returns.Sum(l => l.Payout) + services.Sum(l => l.Payout)),
            lines.Count, lines.Count(l => l.NeedsItem));
    }

    private static MarketplaceTotalsDto Totals(MarketplaceReport r) =>
        new(r.SalesQuantity, r.SalesAmount, r.ReturnsQuantity, r.ReturnsAmount, r.Commission, r.Logistics, r.Payout, r.LineCount, r.UnmatchedCount);

    private static IReadOnlyList<MarketplaceUnmatchedDto> Unmatched(IEnumerable<MarketplaceReportLine> lines) =>
        lines.Where(l => l.NeedsItem).GroupBy(l => l.Key)
            .Select(g => new MarketplaceUnmatchedDto(g.Key, g.First().Article, g.First().ExternalCode, g.First().Barcode, g.First().Name, g.Count(),
                g.Sum(l => l.Kind == MarketplaceLineKind.Return ? -l.Quantity : l.Quantity), g.Sum(l => l.Kind == MarketplaceLineKind.Return ? -l.Amount : l.Amount)))
            .OrderByDescending(u => u.Amount).ThenBy(u => u.Key, StringComparer.Ordinal).ToList();
}
