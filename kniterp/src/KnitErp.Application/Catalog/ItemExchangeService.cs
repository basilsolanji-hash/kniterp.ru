using System.Globalization;
using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Catalog;

public enum ImportAction
{
    Create,
    Update,
    Unchanged,
    Error,
}

/// <summary>
/// Строка файла: исходные значения (для повторной проверки при применении) и итог проверки.
/// Extra — необязательные столбцы карточки (D82): заголовок → значение; столбца нет — поле не меняется.
/// </summary>
public sealed record ItemImportRow(
    int RowNumber, string Code, string Name, string TypeText, string UnitText, string Description,
    ImportAction Action, IReadOnlyList<string> Errors, IReadOnlyDictionary<string, string>? Extra = null);

/// <summary>
/// Протокол проверки и контрольные итоги. Применить можно только файл без ошибок. Bundles — строки листа «Состав комплектов» (D308):
/// для каждого комплекта из листа состав заменяется целиком.
/// </summary>
public sealed record ItemImportPlan(IReadOnlyList<ItemImportRow> Rows)
{
    public IReadOnlyList<BundleImportRow> Bundles { get; init; } = [];
    public int Total => Rows.Count;
    public int ToCreate => Rows.Count(r => r.Action == ImportAction.Create);
    public int ToUpdate => Rows.Count(r => r.Action == ImportAction.Update);
    public int Unchanged => Rows.Count(r => r.Action == ImportAction.Unchanged);
    public int ErrorRows => Rows.Count(r => r.Action == ImportAction.Error) + Bundles.Count(b => b.Errors.Count > 0);
    public bool CanApply => ErrorRows == 0 && (ToCreate + ToUpdate > 0 || Bundles.Count > 0);
}

public sealed record ItemImportResult(int Created, int Updated)
{
    /// <summary>Комплектов с заменённым составом (D308).</summary>
    public int Bundles { get; init; }
}

/// <summary>
/// Шаблонный импорт и выгрузка номенклатуры в Excel (MVP 1.0: «шаблонный импорт и экспорт основных данных,
/// протокол ошибок, контрольные итоги»). Импорт — всё или ничего: одна ошибка в файле, и не применяется ни одна строка.
/// <para>
/// D82: кроме пяти обязательных столбцов — необязательные столбцы карточки: артикул, группа, штрихкоды, страна, декларация,
/// ТН ВЭД, вес, объём, неснижаемый остаток, закупочная цена, цены по видам («Цена: Цена продажи»), основная позиция
/// и характеристики модификации («Цвет: красный; Размер: 48»). Столбец есть — значение применяется (пустая ячейка очищает
/// поле); столбца нет — поле не меняется. Цены — только с правом «Цены и суммы».
/// </para>
/// </summary>
public sealed partial class ItemExchangeService(
    IKnitErpDbContext db, IAccessGuard guard, ISpreadsheetFormat spreadsheet, ICurrentUser currentUser, IClock clock)
{
    public const int MaxRows = 5000;
    public static readonly IReadOnlyList<string> Columns = ["Код", "Наименование", "Тип", "Единица", "Описание"];

    public const string ArticleColumn = "Артикул";
    public const string GroupColumn = "Группа";
    public const string BarcodesColumn = "Штрихкоды";
    public const string CountryColumn = "Страна (ОКСМ)";
    public const string DeclarationColumn = "Номер декларации";
    public const string TnVedColumn = "Код ТН ВЭД";
    public const string WeightColumn = "Вес, кг";
    public const string VolumeColumn = "Объём, м³";
    public const string MinStockColumn = "Неснижаемый остаток";
    public const string PurchasePriceColumn = "Закупочная цена";
    public const string ParentColumn = "Основная позиция (код)";
    public const string VariantColumn = "Характеристики";
    public const string PricePrefix = "Цена: ";

    /// <summary>Необязательные столбцы в порядке выгрузки; цены по видам — после них.</summary>
    public static readonly IReadOnlyList<string> ExtraColumns =
    [
        ArticleColumn, GroupColumn, BarcodesColumn, CountryColumn, DeclarationColumn, TnVedColumn, WeightColumn, VolumeColumn, MinStockColumn,
        PurchasePriceColumn, ParentColumn, VariantColumn, PaymentSubjectColumn, MarkingColumn, GtinColumn, MinPriceColumn, PacksColumn,
    ];

    private static readonly string[] DetailColumns =
        [ArticleColumn, GroupColumn, CountryColumn, DeclarationColumn, TnVedColumn, WeightColumn, VolumeColumn, MinStockColumn, PurchasePriceColumn];

    /// <summary>Пустой шаблон с примером и листом допустимых значений.</summary>
    public async Task<byte[]> TemplateAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogImport, ct);
        var units = await ActiveUnitsAsync(ctx, ct);
        var lookups = await LookupsAsync(ctx, ct);
        var prices = ctx.Permissions.Has(Permissions.PriceView);
        var header = HeaderFor(prices, lookups.PriceTypes.Keys);
        IReadOnlyList<string> Example(params string[] values) =>
            header.Select((h, i) => i < values.Length ? values[i] : "").ToList();
        return spreadsheet.Write(
        [
            new SheetData("Номенклатура", header,
            [
                Example("ПР-0001", "Пряжа шерсть 50% серая", ItemTypes.Name(ItemType.RawMaterial), "кг", "Пример — удалите строку", "YW-50"),
                Example("Ф-015", "Пуговица 15 мм", ItemTypes.Name(ItemType.Accessory), "шт", ""),
            ]),
            new SheetData("Допустимые значения", ["Тип", "Единица (обозначение)", "Единица (наименование)", "Группа", "Характеристика", MarkingColumn, PaymentSubjectColumn],
                Enumerable.Range(0, new[] { ItemTypes.All.Count, units.Count, lookups.Groups.Count, lookups.Characteristics.Count, Marking.All.Count }.Max())
                    .Select(i => (IReadOnlyList<string>)
                    [
                        i < ItemTypes.All.Count ? ItemTypes.Name(ItemTypes.All[i]) : "",
                        i < units.Count ? units[i].Symbol : "",
                        i < units.Count ? units[i].Name : "",
                        i < lookups.Groups.Count ? lookups.Groups.Keys.ElementAt(i) : "",
                        i < lookups.Characteristics.Count ? lookups.Characteristics.Keys.ElementAt(i) : "",
                        i == 0 ? NotMarked : i < Marking.All.Count ? Marking.Name(Marking.All[i]) : "",
                        i < 3 ? TradeRules.PaymentSubjectName(new[] { PaymentSubject.Goods, PaymentSubject.ExcisableGoods, PaymentSubject.Service }[i]) : "",
                    ]).ToList()),
            new SheetData("Как заполнять", ["Столбец", "Что писать"],
            [
                ["Код, Наименование, Тип, Единица, Описание", "Обязательные первые пять столбцов, порядок не менять."],
                ["Остальные столбцы", "Необязательные: удалите ненужные. Пустая ячейка очищает значение, удалённый столбец — не меняет его."],
                [GroupColumn, "Путь группы через «/»: «Пряжа / Шерсть». Группа должна быть заведена в настройках номенклатуры."],
                [BarcodesColumn, "Через запятую: EAN-13, EAN-8, GTIN-14 (с контрольной цифрой) или Code 128."],
                [CountryColumn, "Цифровой код по ОКСМ: 643 — Россия, 156 — Китай."],
                [ParentColumn + ", " + VariantColumn, "Для модификации: код основной позиции и «Цвет: красный; Размер: 48»."],
                [PricePrefix + "…", "Цена по виду цен, как в карточке позиции (нужно право «Цены и суммы»)."],
                [PaymentSubjectColumn, "«Товар», «Подакцизный товар» или «Услуга» (у услуги — всегда «Услуга»)."],
                [MarkingColumn, "Вид продукции «Честного ЗНАКа» с листа «Допустимые значения» или «нет»."],
                [GtinColumn, "14 цифр с контрольной (или EAN-13 — дополнится нулём); уникален в организации."],
                [MinPriceColumn, "Минимальная цена продажи (нужно право «Цены и суммы»)."],
                [PacksColumn, "«упак = 12; кор = 48 / 4600000000008»: единица из справочника = сколько основных единиц, после «/» — штрихкод упаковки."],
                [BundleSheet, "Лист с тремя столбцами: код комплекта, код компонента, количество. Состав каждого комплекта из листа заменяется целиком."],
            ]),
            new SheetData(BundleSheet, BundleColumns, [["НБ-001", "Ш-001", "1"], ["НБ-001", "ШФ-001", "1"]]),
        ]);
    }

    /// <summary>Действующая номенклатура в формате шаблона: выгрузку можно поправить и загрузить обратно.</summary>
    public async Task<byte[]> ExportAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogView, ct);
        var org = ctx.OrganizationId;
        var prices = ctx.Permissions.Has(Permissions.PriceView);
        var lookups = await LookupsAsync(ctx, ct);
        var groupPath = lookups.Groups.ToDictionary(g => g.Value, g => g.Key);
        var characteristicName = lookups.AllCharacteristics;
        var items = await db.Items.AsNoTracking().Where(i => i.OrganizationId == org && !i.IsArchived)
            .OrderBy(i => i.Type).ThenBy(i => i.Name).ToListAsync(ct);
        var units = await db.Units.AsNoTracking().Where(u => u.OrganizationId == org).ToDictionaryAsync(u => u.Id, u => u.Symbol, ct);
        var codes = await db.Items.AsNoTracking().Where(i => i.OrganizationId == org).ToDictionaryAsync(i => i.Id, i => i.Code, ct);
        var barcodes = (await db.ItemBarcodes.AsNoTracking().Where(b => b.OrganizationId == org).OrderBy(b => b.Id).ToListAsync(ct))
            .ToLookup(b => b.ItemId, b => b.Code);
        var values = (await db.ItemCharacteristicValues.AsNoTracking().Where(v => v.OrganizationId == org).ToListAsync(ct)).ToLookup(v => v.ItemId);
        var itemPrices = prices
            ? (await db.ItemPrices.AsNoTracking().Where(p => p.OrganizationId == org).ToListAsync(ct)).ToLookup(p => p.ItemId)
            : null;
        var header = HeaderFor(prices, lookups.PriceTypes.Keys);
        var packs = (await db.ItemPacks.AsNoTracking().Where(p => p.OrganizationId == org).ToListAsync(ct)).ToLookup(p => p.ItemId);
        var rows = items.Select(i =>
        {
            var cells = new Dictionary<string, string>
            {
                [ArticleColumn] = i.Article ?? "",
                [GroupColumn] = i.GroupId is { } g && groupPath.TryGetValue(g, out var path) ? path : "",
                [BarcodesColumn] = string.Join(", ", barcodes[i.Id]),
                [CountryColumn] = i.OriginCountryCode ?? "",
                [DeclarationColumn] = i.CustomsDeclaration ?? "",
                [TnVedColumn] = i.TnVedCode ?? "",
                [WeightColumn] = Number(i.WeightKg),
                [VolumeColumn] = Number(i.VolumeM3),
                [MinStockColumn] = Number(i.MinStock),
                [PurchasePriceColumn] = prices ? Number(i.PurchasePrice) : "",
                [ParentColumn] = i.ParentItemId is { } p && codes.TryGetValue(p, out var parentCode) ? parentCode : "",
                [VariantColumn] = VariantText(values[i.Id].Select(v => (characteristicName.GetValueOrDefault(v.CharacteristicId, "?"), v.Value))),
            };
            ExportTrade(cells, i, packs, units, prices); // D308
            foreach (var (name, id) in lookups.PriceTypes)
            {
                cells[PricePrefix + name] = Number(itemPrices?[i.Id].FirstOrDefault(p => p.PriceTypeId == id)?.Price);
            }

            return (IReadOnlyList<string>)header.Select((h, index) => index switch
            {
                0 => i.Code,
                1 => i.Name,
                2 => ItemTypes.Name(i.Type),
                3 => units.GetValueOrDefault(i.UnitId, ""),
                4 => i.Description ?? "",
                _ => cells.GetValueOrDefault(h, ""),
            }).ToList();
        }).ToList();
        return spreadsheet.Write([new SheetData("Номенклатура", header, rows), new SheetData(BundleSheet, BundleColumns, await ExportBundlesAsync(org, ct))]);
    }

    /// <summary>Чтение и полная проверка файла. В базе ничего не меняется.</summary>
    public async Task<ItemImportPlan> PreviewAsync(Stream file, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogImport, ct);
        var sheet = spreadsheet.ReadFirstSheet(file, MaxRows);
        if (sheet.Count == 0)
        {
            throw new BusinessRuleException("import.file_empty", "Файл пустой.");
        }

        var header = sheet[0];
        for (var c = 0; c < Columns.Count; c++)
        {
            var actual = c < header.Count ? header[c] : "";
            if (!string.Equals(actual, Columns[c], StringComparison.OrdinalIgnoreCase))
            {
                throw new BusinessRuleException("import.header",
                    $"Столбец {c + 1} должен называться «{Columns[c]}», а в файле «{actual}». Скачайте шаблон и заполните его.");
            }
        }

        // Необязательные столбцы — по названию, в любом порядке; неизвестный столбец — ошибка, чтобы опечатка не потеряла данные.
        var lookups = await LookupsAsync(ctx, ct);
        var extra = new List<(int Index, string Name)>();
        for (var c = Columns.Count; c < header.Count; c++)
        {
            var name = header[c].Trim();
            if (name.Length == 0)
            {
                continue;
            }

            var known = ExtraColumns.FirstOrDefault(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase))
                        ?? (name.StartsWith(PricePrefix, StringComparison.OrdinalIgnoreCase)
                            && lookups.PriceTypes.Keys.FirstOrDefault(t => string.Equals(t, name[PricePrefix.Length..].Trim(), StringComparison.OrdinalIgnoreCase))
                                is { } type
                                ? PricePrefix + type
                                : null)
                        ?? throw new BusinessRuleException("import.header",
                            $"Неизвестный столбец «{name}» (столбец {c + 1}). Названия — как в шаблоне; вид цены — «{PricePrefix}название вида».");
            if (IsPriceColumn(known) && !ctx.Permissions.Has(Permissions.PriceView))
            {
                throw new BusinessRuleException("import.prices_denied", $"Столбец «{known}»: загружать цены можно только с правом «Цены и суммы».");
            }

            if (extra.Any(e => e.Name == known))
            {
                throw new BusinessRuleException("import.header", $"Столбец «{known}» повторяется.");
            }

            extra.Add((c, known));
        }

        var raw = sheet.Skip(1)
            .Select((cells, i) => (RowNumber: i + 2, Cells: cells))
            .Where(r => r.Cells.Any(v => v.Length > 0))
            .Select(r => new ItemImportRow(r.RowNumber, Cell(r.Cells, 0), Cell(r.Cells, 1), Cell(r.Cells, 2), Cell(r.Cells, 3), Cell(r.Cells, 4),
                ImportAction.Error, [], extra.Count == 0 ? null : extra.ToDictionary(e => e.Name, e => Cell(r.Cells, e.Index))))
            .ToList();
        if (raw.Count == 0)
        {
            throw new BusinessRuleException("import.file_empty", "В файле нет строк с данными.");
        }

        var plan = (await PlanAsync(ctx, raw, ct)).Plan;
        // D308: лист «Состав комплектов» — необязательный; листа нет — составы не меняются.
        var bundleSheet = file.CanSeek ? spreadsheet.ReadNamedSheet(Rewind(file), BundleSheet, MaxRows) : null;
        return plan with { Bundles = await PlanBundlesAsync(ctx, bundleSheet, plan.Rows, ct) };

        static Stream Rewind(Stream s)
        {
            s.Position = 0;
            return s;
        }
    }

    /// <summary>
    /// Применение. Строки проверяются заново на текущих данных базы (между проверкой и применением справочник мог измениться);
    /// при любой ошибке не применяется ничего.
    /// </summary>
    public async Task<ItemImportResult> ApplyAsync(IReadOnlyList<ItemImportRow> rows, CancellationToken ct = default) =>
        await ApplyAsync(rows, [], ct);

    /// <summary>
    /// Применение со строками листа «Состав комплектов» (D308): они тоже проверяются заново.
    /// Аудит каталога (D347): загрузки одной организации идут по очереди (именованная блокировка до конца транзакции) — две
    /// одновременные загрузки с одинаковыми кодами больше не падают на уникальном индексе. При отказе на середине отслеживание
    /// DbContext вкладки сбрасывается: транзакция откатилась, и полузагруженные позиции не должны уйти следующим сохранением.
    /// </summary>
    public async Task<ItemImportResult> ApplyAsync(IReadOnlyList<ItemImportRow> rows, IReadOnlyList<BundleImportRow> bundles, CancellationToken ct = default)
    {
        try
        {
            return await ApplyCoreAsync(rows, bundles, ct);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    private async Task<ItemImportResult> ApplyCoreAsync(IReadOnlyList<ItemImportRow> rows, IReadOnlyList<BundleImportRow> bundles, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.CatalogImport, ct);
        if (rows.Count is 0 or > MaxRows)
        {
            throw new BusinessRuleException("import.file_empty", "Нет строк для применения.");
        }

        if (rows.Any(r => r.Extra?.Keys.Any(IsPriceColumn) == true))
        {
            await guard.DemandAsync(Permissions.PriceView, ct);
        }

        await using var tx = await db.BeginTransactionAsync(ct);
        await db.LockAsync($"kniterp.catalog.import.{ctx.OrganizationId}", ct); // D347: план строится заново уже под блокировкой
        var (plan, parsed) = await PlanAsync(ctx, rows, ct);
        IReadOnlyList<IReadOnlyList<string>> bundleSheet =
            [BundleColumns, .. bundles.Select(b => (IReadOnlyList<string>)[b.BundleCode, b.ComponentCode, b.QuantityText])];
        plan = plan with { Bundles = await PlanBundlesAsync(ctx, bundleSheet, plan.Rows, ct) };
        if (plan.ErrorRows > 0)
        {
            throw new BusinessRuleException("import.has_errors",
                $"В файле есть ошибки ({plan.ErrorRows} строк) — ничего не загружено. Исправьте файл и проверьте его снова.");
        }

        var units = await ActiveUnitsAsync(ctx, ct);
        var serviceVat = await DefaultServiceVatAsync(ctx.OrganizationId, ct);
        var existing = await db.Items.Where(i => i.OrganizationId == ctx.OrganizationId).ToDictionaryAsync(i => i.Code, ct);
        var created = new List<Item>();
        var touched = new List<(Item Item, ItemImportRow Row)>();
        var updated = 0;

        // Сначала основные позиции, затем модификации: основная позиция из этого же файла получает номер раньше.
        var ordered = plan.Rows.Where(r => r.Action is ImportAction.Create or ImportAction.Update)
            .OrderBy(r => string.IsNullOrEmpty(parsed[r.RowNumber].ParentCode) ? 0 : 1).ThenBy(r => r.RowNumber).ToList();
        foreach (var pass in ordered.GroupBy(r => string.IsNullOrEmpty(parsed[r.RowNumber].ParentCode) ? 0 : 1))
        {
            foreach (var row in pass)
            {
                var type = ParseType(row.TypeText)!.Value;
                var unit = FindUnit(units, row.UnitText)!;
                var description = row.Description.Length == 0 ? null : row.Description;
                var p = parsed[row.RowNumber];
                if (row.Action == ImportAction.Create)
                {
                    var item = p.ParentCode is { Length: > 0 } parentCode
                        ? Item.CreateModification(existing[parentCode], row.Code, row.Name, Variants.Key(p.Values!), clock.UtcNow)
                        : Item.Create(ctx.OrganizationId, row.Code, row.Name, type, unit.Id, description, clock.UtcNow);
                    if (item.IsModification)
                    {
                        item.Update(row.Code, row.Name, type, unit.Id, description);
                    }

                    if (item.Type == ItemType.Service && item.VatRateId is null)
                    {
                        item.SetVatRate(serviceVat); // D300: ставка услуги — основная ставка организации
                    }

                    db.Items.Add(item);
                    existing[item.Code] = item;
                    created.Add(item);
                    touched.Add((item, row));
                }
                else
                {
                    var item = existing[Item.NormalizeCode(row.Code)];
                    foreach (var change in item.Update(row.Code, row.Name, type, unit.Id, description))
                    {
                        Audit(ctx, AuditActions.CatalogChanged, nameof(Item), item.Id.ToString(), change.Before, change.After, $"Импорт: {change.Field}");
                    }

                    if (item.Type == ItemType.Service && item.VatRateId is null && item.SetVatRate(serviceVat) is { } vatChange)
                    {
                        Audit(ctx, AuditActions.CatalogChanged, nameof(Item), item.Id.ToString(), vatChange.Before, vatChange.After, "Импорт: Ставка НДС");
                    }

                    touched.Add((item, row));
                    updated++;
                }
            }

            await db.SaveChangesAsync(ct);
        }

        foreach (var (item, row) in touched)
        {
            await ApplyExtrasAsync(ctx, item, parsed[row.RowNumber], ct);
        }

        await db.SaveChangesAsync(ct);

        var bundlesChanged = await ApplyBundlesAsync(ctx, plan.Bundles, ct); // D308

        // D317 (сессия 4): новым позициям с группой — незаданные в файле НДС, ТН ВЭД, страна и маркировка из значений по умолчанию группы.
        var defaultsByGroup = new Dictionary<long, GroupDefaultValues>();
        foreach (var item in created.Where(i => !i.IsModification && i.GroupId is not null))
        {
            var gid = item.GroupId!.Value;
            if (!defaultsByGroup.TryGetValue(gid, out var d))
            {
                defaultsByGroup[gid] = d = await GroupDefaultsService.ResolveAsync(db, ctx.OrganizationId, gid, ct);
            }

            if (d.IsEmpty)
            {
                continue;
            }

            if (item.VatRateId is null && d.VatRateId is { } vat)
            {
                item.SetVatRate(vat);
            }

            item.SetDetails(item.Details with
            {
                TnVedCode = item.TnVedCode ?? d.TnVedCode,
                OriginCountryCode = item.OriginCountryCode ?? d.OriginCountryCode,
                OriginCountryName = item.OriginCountryCode is null ? d.OriginCountryName : item.OriginCountryName,
            });
            if (d.Tracking is { } tracking && !item.IsMarked && ItemTypes.IsStocked(item.Type))
            {
                item.SetTrade(item.Trade with { IsMarked = tracking != TrackingType.None, TrackingType = tracking });
            }
        }

        foreach (var item in created)
        {
            Audit(ctx, AuditActions.CatalogCreated, nameof(Item), item.Id.ToString(), null, $"{item.Code} {item.Name}", "Импорт");
        }

        Audit(ctx, AuditActions.CatalogImported, "ItemImport", null, null,
            $"строк {plan.Total}: создано {created.Count}, обновлено {updated}, без изменений {plan.Unchanged}", null);
        await db.SaveOrConflictAsync(ct);
        // D322: автоцены по правилам — для позиций, у которых файл задал закупочную цену.
        var withPurchase = touched.Where(t => parsed[t.Row.RowNumber].Details?.PurchasePrice is not null).Select(t => t.Item.Id).ToList();
        await AutoPrices.ApplyAsync(db, ctx.OrganizationId, withPurchase, ctx.UserId, clock.UtcNow, ct);
        await tx.CommitAsync(ct);
        return new ItemImportResult(created.Count, updated) { Bundles = bundlesChanged };
    }

    /// <summary>Разобранные необязательные столбцы строки. null у поля — столбца нет, поле не меняется.</summary>
    private sealed record Parsed(
        ItemDetails? Details, IReadOnlyList<(BarcodeType Type, string Code)>? Barcodes, IReadOnlyDictionary<long, decimal?>? Prices,
        string? ParentCode, IReadOnlyDictionary<long, string>? Values, TradeSpec? Trade = null)
    {
        public static readonly Parsed None = new(null, null, null, null, null);
    }

    /// <summary>Справочники организации для разбора столбцов: пути групп, виды цен, характеристики.</summary>
    private sealed record Lookups(
        IReadOnlyDictionary<string, long> Groups, IReadOnlyDictionary<string, long> PriceTypes, IReadOnlyDictionary<string, long> Characteristics,
        IReadOnlyDictionary<long, string> AllCharacteristics);

    private async Task ApplyExtrasAsync(AccessContext ctx, Item item, Parsed p, CancellationToken ct)
    {
        var id = item.Id.ToString();
        if (p.Details is { } details)
        {
            foreach (var c in item.SetDetails(details))
            {
                Audit(ctx, AuditActions.CatalogChanged, nameof(Item), id, c.Before, c.After, $"Импорт: {c.Field}");
            }
        }

        if (p.Barcodes is { } barcodes)
        {
            var current = await db.ItemBarcodes.Where(b => b.ItemId == item.Id).ToListAsync(ct);
            var removed = current.Where(b => barcodes.All(n => n.Code != b.Code)).ToList();
            foreach (var old in removed)
            {
                db.ItemBarcodes.Remove(old);
                Audit(ctx, AuditActions.CatalogChanged, nameof(Item), id, old.Code, null, "Импорт: штрихкод удалён");
            }

            if (removed.Count > 0)
            {
                await db.SaveChangesAsync(ct);
            }

            foreach (var (type, code) in barcodes.Where(n => current.All(b => b.Code != n.Code)))
            {
                db.ItemBarcodes.Add(ItemBarcode.Create(ctx.OrganizationId, item.Id, type, code));
                Audit(ctx, AuditActions.CatalogChanged, nameof(Item), id, null, code, "Импорт: штрихкод добавлен");
            }
        }

        if (p.Prices is { } prices)
        {
            var current = await db.ItemPrices.Where(x => x.ItemId == item.Id).ToDictionaryAsync(x => x.PriceTypeId, ct);
            foreach (var (typeId, price) in prices)
            {
                var old = current.GetValueOrDefault(typeId);
                var before = old?.Price; // до изменения: old.Set меняет цену на месте
                if (before == price)
                {
                    continue;
                }

                if (price is null)
                {
                    db.ItemPrices.Remove(old!);
                }
                else if (old is null)
                {
                    db.ItemPrices.Add(ItemPrice.Create(ctx.OrganizationId, item.Id, typeId, price.Value));
                }
                else
                {
                    old.Set(price.Value);
                }

                db.ItemPriceHistory.Add(ItemPriceHistory.Create(ctx.OrganizationId, item.Id, typeId, before, price, clock.UtcNow, ctx.UserId,
                    PriceChangeSource.Import)); // D305
                Audit(ctx, AuditActions.CatalogChanged, nameof(Item), id, before?.ToString("0.00##", CultureInfo.InvariantCulture),
                    price?.ToString("0.00##", CultureInfo.InvariantCulture), "Импорт: цена");
            }
        }

        await ApplyTradeAsync(ctx, item, p.Trade, ct); // D308

        if (p.Values is { } values && item.IsModification)
        {
            var key = Variants.Key(values);
            var current = await db.ItemCharacteristicValues.Where(v => v.ItemId == item.Id).ToListAsync(ct);
            if (key != item.VariantKey || current.Count != values.Count)
            {
                Audit(ctx, AuditActions.CatalogChanged, nameof(Item), id, item.VariantKey, key, "Импорт: характеристики");
                item.SetVariantKey(key);
            }

            if (current.Count > 0)
            {
                db.ItemCharacteristicValues.RemoveRange(current);
                await db.SaveChangesAsync(ct); // освобождает уникальный ключ до вставки новых значений
            }

            foreach (var (characteristicId, value) in values)
            {
                db.ItemCharacteristicValues.Add(ItemCharacteristicValue.Create(ctx.OrganizationId, item.Id, characteristicId, value));
            }
        }

        // D347: без сохранения на каждую строку — одно на весь файл (ApplyCoreAsync). Раньше 5000 строк грузились ~5,5 мин:
        // каждое сохранение заново просматривало тысячи отслеживаемых позиций.
    }

    /// <summary>Основная ставка НДС организации для новой услуги из файла (D300): действующая ставка вида «Основная».</summary>
    private Task<long?> DefaultServiceVatAsync(long organizationId, CancellationToken ct) =>
        db.VatRates.AsNoTracking().Where(r => r.OrganizationId == organizationId && !r.IsArchived && r.Kind == VatRateKind.Standard)
            .OrderBy(r => r.Id).Select(r => (long?)r.Id).FirstOrDefaultAsync(ct);

    private async Task<(ItemImportPlan Plan, Dictionary<int, Parsed> Parsed)> PlanAsync(AccessContext ctx, IReadOnlyList<ItemImportRow> raw, CancellationToken ct)
    {
        var org = ctx.OrganizationId;
        var units = await ActiveUnitsAsync(ctx, ct);
        var lookups = await LookupsAsync(ctx, ct);
        var withExtra = raw.Any(r => r.Extra is { Count: > 0 });
        var parentCodes = withExtra
            ? raw.Select(r => Item.NormalizeCode(r.Extra?.GetValueOrDefault(ParentColumn))).Where(c => c.Length > 0).Distinct().ToList()
            : [];
        var codes = raw.Select(r => Item.NormalizeCode(r.Code)).Where(c => c.Length > 0).Concat(parentCodes).Distinct().ToList();
        var existing = await db.Items.AsNoTracking()
            .Where(i => i.OrganizationId == org && codes.Contains(i.Code))
            .ToDictionaryAsync(i => i.Code, ct);
        var ids = existing.Values.Select(i => i.Id).ToList();

        // Текущие штрихкоды, цены и характеристики позиций файла — для «без изменений» и проверки повторов штрихкодов.
        var fileBarcodes = withExtra
            ? raw.SelectMany(r => SplitBarcodes(r.Extra?.GetValueOrDefault(BarcodesColumn))).Distinct().ToList()
            : [];
        List<(string Code, long ItemId, string ItemCode)> barcodeOwners = [];
        ILookup<long, ItemPrice>? currentPrices = null;
        List<(string Code, long? ParentItemId, string? VariantKey)> parentKeys = [];
        if (withExtra)
        {
            barcodeOwners = (await db.ItemBarcodes.AsNoTracking()
                    .Where(b => b.OrganizationId == org && (fileBarcodes.Contains(b.Code) || ids.Contains(b.ItemId)))
                    .Join(db.Items.AsNoTracking(), b => b.ItemId, i => i.Id, (b, i) => new { Barcode = b.Code, b.ItemId, ItemCode = i.Code })
                    .ToListAsync(ct))
                .Select(x => (x.Barcode, x.ItemId, x.ItemCode)).ToList();
            // D307: штрихкоды упаковок заняты для любой позиции (ItemId = long.MinValue — не совпадает ни с одной).
            barcodeOwners.AddRange((await db.ItemPacks.AsNoTracking()
                    .Where(p => p.OrganizationId == org && p.Barcode != null && fileBarcodes.Contains(p.Barcode))
                    .Join(db.Items.AsNoTracking(), p => p.ItemId, i => i.Id, (p, i) => new { Barcode = p.Barcode!, ItemCode = i.Code })
                    .ToListAsync(ct))
                .Select(x => (x.Barcode, long.MinValue, x.ItemCode)));
            currentPrices = (await db.ItemPrices.AsNoTracking().Where(p => p.OrganizationId == org && ids.Contains(p.ItemId)).ToListAsync(ct))
                .ToLookup(p => p.ItemId);
            parentKeys = (await db.Items.AsNoTracking()
                    .Where(i => i.OrganizationId == org && i.ParentItemId != null && ids.Contains(i.ParentItemId.Value))
                    .Select(i => new { i.Code, i.ParentItemId, i.VariantKey }).ToListAsync(ct))
                .Select(x => (x.Code, x.ParentItemId, x.VariantKey)).ToList();
        }

        var duplicates = raw.GroupBy(r => Item.NormalizeCode(r.Code)).Where(g => g.Key.Length > 0 && g.Count() > 1)
            .ToDictionary(g => g.Key, g => g.Select(r => r.RowNumber).ToList());
        var fileBarcodeRows = raw.SelectMany(r => SplitBarcodes(r.Extra?.GetValueOrDefault(BarcodesColumn)).Select(b => (Barcode: b, r.RowNumber)))
            .GroupBy(x => x.Barcode).Where(g => g.Select(x => x.RowNumber).Distinct().Count() > 1)
            .ToDictionary(g => g.Key, g => g.Select(x => x.RowNumber).Distinct().ToList());
        var fileParents = raw.Where(r => string.IsNullOrWhiteSpace(r.Extra?.GetValueOrDefault(ParentColumn)))
            .Select(r => Item.NormalizeCode(r.Code)).ToHashSet();
        var fileVariantKeys = new Dictionary<(string Parent, string Key), int>();

        // D300, D302: услуга или комплект из файла — только у позиции, которой не было на складе и которая не входит в комплекты;
        // у услуги — ставка НДС (своя или основная организации); комплект с составом не меняет тип. Те же правила, что в карточке.
        var serviceCodes = raw.Where(r => ParseType(r.TypeText) == ItemType.Service).Select(r => Item.NormalizeCode(r.Code)).ToHashSet();
        var notStockedCodes = raw.Where(r => ParseType(r.TypeText) is { } t && !ItemTypes.IsStocked(t)).Select(r => Item.NormalizeCode(r.Code)).ToHashSet();
        var serviceVat = serviceCodes.Count > 0 ? await DefaultServiceVatAsync(org, ct) : null;
        var convertIds = existing.Values.Where(i => notStockedCodes.Contains(i.Code) && ItemTypes.IsStocked(i.Type)).Select(i => i.Id).ToList();
        var stocked = convertIds.Count == 0
            ? []
            : (await db.StockMovements.AsNoTracking().Where(m => m.OrganizationId == org && convertIds.Contains(m.ItemId)).Select(m => m.ItemId)
                .Concat(db.StockBatches.AsNoTracking().Where(b => b.OrganizationId == org && convertIds.Contains(b.ItemId)).Select(b => b.ItemId))
                .Concat(db.BundleComponents.AsNoTracking().Where(c => c.OrganizationId == org && convertIds.Contains(c.ComponentItemId)).Select(c => c.ComponentItemId))
                .Concat(db.ItemPacks.AsNoTracking().Where(p => p.OrganizationId == org && convertIds.Contains(p.ItemId)).Select(p => p.ItemId))
                .Concat(db.Items.AsNoTracking().Where(i => i.OrganizationId == org && i.ParentItemId != null && convertIds.Contains(i.ParentItemId.Value))
                    .Select(i => i.ParentItemId!.Value))
                .Distinct().ToListAsync(ct)).ToHashSet();
        var bundleIds = existing.Values.Where(i => i.Type == ItemType.Bundle).Select(i => i.Id).ToList();
        var withComponents = bundleIds.Count == 0
            ? []
            : (await db.BundleComponents.AsNoTracking().Where(c => c.OrganizationId == org && bundleIds.Contains(c.BundleItemId))
                .Select(c => c.BundleItemId).Distinct().ToListAsync(ct)).ToHashSet();

        var catalogLookups = await CatalogLookupsAsync(org, raw, existing, ct); // D308: торговые столбцы и упаковки
        var rows = new List<ItemImportRow>(raw.Count);
        var parsed = new Dictionary<int, Parsed>();
        foreach (var r in raw)
        {
            var errors = new List<string>();
            var code = Item.NormalizeCode(r.Code);
            if (duplicates.TryGetValue(code, out var same))
            {
                errors.Add($"Код {code} повторяется в строках {string.Join(", ", same)}.");
            }

            var type = ParseType(r.TypeText);
            if (type is null)
            {
                errors.Add(r.TypeText.Length == 0 ? "Не указан тип." : $"Неизвестный тип «{r.TypeText}». Допустимые — на листе «Допустимые значения» шаблона.");
            }

            var unit = FindUnit(units, r.UnitText);
            if (unit is null)
            {
                errors.Add(r.UnitText.Length == 0 ? "Не указана единица." : $"Неизвестная единица «{r.UnitText}».");
            }

            var action = ImportAction.Error;
            var p = Parsed.None;
            if (type is not null && unit is not null)
            {
                var description = r.Description.Length == 0 ? null : r.Description;
                try
                {
                    // Те же правила, что при ручном вводе: проверка через доменную сущность.
                    var candidate = Item.Create(org, r.Code, r.Name, type.Value, unit.Id, description, clock.UtcNow);
                    existing.TryGetValue(candidate.Code, out var current);
                    if (current?.IsArchived == true)
                    {
                        errors.Add($"Код {candidate.Code} принадлежит архивной позиции «{current.Name}». Верните её из архива или смените код.");
                    }
                    else
                    {
                        var extrasChanged = false;
                        if (r.Extra is { Count: > 0 } extra)
                        {
                            (p, extrasChanged) = ParseExtras(extra, current, existing, lookups, errors, ctx.Permissions.Has(Permissions.PriceView),
                                currentPrices?[current?.Id ?? 0].ToList() ?? [], barcodeOwners, fileBarcodeRows, fileParents);
                            if (p.ParentCode is { Length: > 0 } parent && p.Values is { } values && errors.Count == 0)
                            {
                                var key = Variants.Key(values);
                                if (fileVariantKeys.TryGetValue((parent, key), out var otherRow))
                                {
                                    errors.Add($"Такой же набор характеристик у модификации в строке {otherRow}.");
                                }

                                fileVariantKeys[(parent, key)] = r.RowNumber;
                                var parentId = existing.GetValueOrDefault(parent)?.Id;
                                if (parentKeys.Any(k => k.ParentItemId == parentId && k.VariantKey == key && k.Code != candidate.Code))
                                {
                                    errors.Add($"У позиции {parent} уже есть модификация с такими характеристиками.");
                                }
                            }

                            if (ParseTrade(extra, current, candidate, catalogLookups, errors, ctx.Permissions.Has(Permissions.PriceView), r.RowNumber) is { } trade)
                            {
                                p = p with { Trade = trade };
                                extrasChanged |= trade.Changed;
                            }
                        }

                        if (candidate.Type == ItemType.Service)
                        {
                            if (current?.VatRateId is null && serviceVat is null)
                            {
                                errors.Add("У услуги ставка НДС обязательна: заведите основную ставку НДС организации или укажите ставку в карточке.");
                            }

                            if (p.Details is { } d && (d.WeightKg is not null || d.VolumeM3 is not null || d.MinStock is not null))
                            {
                                errors.Add("У услуги нет веса, объёма и неснижаемого остатка — оставьте эти столбцы пустыми.");
                            }
                        }

                        if (!ItemTypes.IsStocked(candidate.Type) && current is not null && ItemTypes.IsStocked(current.Type)
                            && (stocked.Contains(current.Id) || current.IsModification))
                        {
                            errors.Add($"Позиция {current.Code} уже была на складе, входит в комплект, имеет модификации или сама модификация — её нельзя сделать услугой или комплектом.");
                        }

                        if (candidate.Type == ItemType.Bundle && p.Details?.MinStock is not null)
                        {
                            errors.Add("У комплекта нет неснижаемого остатка — оставьте этот столбец пустым.");
                        }

                        if (current is { Type: ItemType.Bundle } && candidate.Type != ItemType.Bundle && withComponents.Contains(current.Id))
                        {
                            errors.Add($"У комплекта {current.Code} есть состав — сначала уберите компоненты, затем меняйте тип.");
                        }

                        // D339: единица — как в карточке: не у модификации (она как у основной), не при упаковках (их коэффициенты
                        // в старой единице), не у позиции в учёте (записанные количества относятся к прежней единице).
                        if (current is not null && current.UnitId != candidate.UnitId)
                        {
                            if (current.IsModification)
                            {
                                errors.Add("У модификации единица — как у основной позиции: меняйте её в основной позиции.");
                            }
                            else if (await db.ItemPacks.AnyAsync(x => x.OrganizationId == org && x.ItemId == current.Id, ct))
                            {
                                errors.Add($"У позиции {current.Code} есть упаковки: их коэффициенты заданы в основной единице. Уберите упаковки, затем меняйте единицу.");
                            }
                            else if (await db.ItemUsageAsync(org, current.Id, null, ct) is { } usage)
                            {
                                errors.Add($"Позиция {current.Code} уже в учёте ({ItemUsage.Describe(usage)}): единицу менять нельзя — записанные количества относятся к прежней. Создайте новую позицию с нужной единицей.");
                            }
                        }

                        action = current is null
                            ? ImportAction.Create
                            : extrasChanged || current.Name != candidate.Name || current.Type != candidate.Type || current.UnitId != candidate.UnitId
                              || current.Description != candidate.Description
                                ? ImportAction.Update
                                : ImportAction.Unchanged;
                    }
                }
                catch (BusinessRuleException ex)
                {
                    errors.Add(ex.Message);
                }
            }

            parsed[r.RowNumber] = p;
            rows.Add(r with { Code = code.Length > 0 ? code : r.Code, Action = errors.Count > 0 ? ImportAction.Error : action, Errors = errors });
        }

        return (new ItemImportPlan(rows), parsed);
    }

    /// <summary>
    /// Разбор необязательных столбцов строки по правилам карточки. Возвращает разобранные значения и признак, что они отличаются
    /// от текущих; ошибки — в список строки.
    /// </summary>
    private static (Parsed Parsed, bool Changed) ParseExtras(
        IReadOnlyDictionary<string, string> extra, Item? current, IReadOnlyDictionary<string, Item> existing, Lookups lookups, List<string> errors,
        bool canSeePrices, IReadOnlyList<ItemPrice> currentPrices, IReadOnlyList<(string Code, long ItemId, string ItemCode)> barcodeOwners,
        IReadOnlyDictionary<string, List<int>> fileBarcodeRows, IReadOnlySet<string> fileParents)
    {
        var changed = false;
        T? Try<T>(Func<T> parse)
        {
            try
            {
                return parse();
            }
            catch (BusinessRuleException ex)
            {
                errors.Add(ex.Message);
                return default;
            }
        }

        string? Text(string column) => extra.TryGetValue(column, out var v) ? (v.Length == 0 ? null : v) : null;
        bool Has(string column) => extra.ContainsKey(column);

        // Основная позиция и характеристики (модификация).
        string? parentCode = null;
        IReadOnlyDictionary<long, string>? values = null;
        var parentItem = default(Item);
        if (Has(ParentColumn))
        {
            parentCode = Item.NormalizeCode(Text(ParentColumn));
            if (parentCode.Length > 0)
            {
                parentItem = existing.GetValueOrDefault(parentCode);
                if (parentItem is null && !fileParents.Contains(parentCode))
                {
                    errors.Add($"Основная позиция {parentCode} не найдена ни в номенклатуре, ни в файле.");
                }
                else if (parentItem?.IsModification == true)
                {
                    errors.Add($"{parentCode} — сама модификация; модификации создаются у основной позиции.");
                }
                else if (parentItem?.IsArchived == true)
                {
                    errors.Add($"Основная позиция {parentCode} в архиве.");
                }

                if (parentCode == Item.NormalizeCode(current?.Code))
                {
                    errors.Add("Позиция не может быть модификацией самой себя.");
                }
            }

            if (current is not null && (current.ParentItemId ?? 0) != (parentItem?.Id ?? 0) && !(current.ParentItemId is null && parentCode.Length == 0))
            {
                errors.Add("Основную позицию у существующей позиции загрузкой не меняют.");
            }
        }
        else if (current?.IsModification == true)
        {
            parentCode = existing.Values.FirstOrDefault(i => i.Id == current.ParentItemId)?.Code;
        }

        if (Has(VariantColumn) || parentCode is { Length: > 0 })
        {
            var text = Text(VariantColumn);
            if (parentCode is not { Length: > 0 })
            {
                if (text is not null)
                {
                    errors.Add($"Характеристики указываются только у модификации — заполните «{ParentColumn}».");
                }
            }
            else if (text is null)
            {
                if (Has(VariantColumn) || current is null)
                {
                    errors.Add("У модификации укажите характеристики: «Цвет: красный; Размер: 48».");
                }
            }
            else
            {
                values = Try(() => ParseVariant(text, lookups.Characteristics));
                if (values is not null && current is not null && Variants.Key(values) != current.VariantKey)
                {
                    changed = true;
                }
            }
        }

        // Сведения карточки.
        ItemDetails? details = null;
        if (DetailColumns.Any(Has))
        {
            var d = current?.Details ?? parentItem?.Details ?? new ItemDetails(null, null, null, null, null, null, null, null, null, null);
            if (Has(ArticleColumn))
            {
                d = d with { Article = Text(ArticleColumn) };
            }

            if (Has(GroupColumn))
            {
                var path = Text(GroupColumn);
                long? groupId = null;
                if (path is not null)
                {
                    var normalized = string.Join(" / ", path.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                    if (lookups.Groups.TryGetValue(normalized, out var gid))
                    {
                        groupId = gid;
                    }
                    else
                    {
                        errors.Add($"Группа «{path}» не найдена. Заведите её в настройках номенклатуры; путь — через «/».");
                    }
                }

                d = d with { GroupId = groupId };
            }

            if (Has(CountryColumn))
            {
                d = d with { OriginCountryCode = Text(CountryColumn), OriginCountryName = null };
            }

            if (Has(DeclarationColumn))
            {
                d = d with { CustomsDeclaration = Text(DeclarationColumn) };
            }

            if (Has(TnVedColumn))
            {
                d = d with { TnVedCode = Text(TnVedColumn) };
            }

            if (Has(WeightColumn))
            {
                d = d with { WeightKg = Try(() => Decimals.Parse(Text(WeightColumn), WeightColumn)) };
            }

            if (Has(VolumeColumn))
            {
                d = d with { VolumeM3 = Try(() => Decimals.Parse(Text(VolumeColumn), VolumeColumn)) };
            }

            if (Has(MinStockColumn))
            {
                d = d with { MinStock = Try(() => Decimals.Parse(Text(MinStockColumn), MinStockColumn)) };
            }

            if (Has(PurchasePriceColumn) && canSeePrices)
            {
                d = d with { PurchasePrice = Try(() => Decimals.Parse(Text(PurchasePriceColumn), PurchasePriceColumn)) };
            }

            details = Try(() => ItemDetailRules.Normalize(d));
            if (details is not null && (current is null || details != ItemDetailRules.Normalize(current.Details)))
            {
                changed = true;
            }
        }

        // Штрихкоды: столбец задаёт полный список позиции.
        IReadOnlyList<(BarcodeType, string)>? barcodes = null;
        if (Has(BarcodesColumn))
        {
            var list = new List<(BarcodeType, string)>();
            foreach (var raw in SplitBarcodes(Text(BarcodesColumn)))
            {
                var barcode = Try(() => ItemBarcode.Create(0, 0, ItemBarcode.Detect(raw), raw));
                if (barcode is null)
                {
                    continue;
                }

                if (fileBarcodeRows.TryGetValue(barcode.Code, out var rowsWithCode))
                {
                    errors.Add($"Штрихкод {barcode.Code} повторяется в строках {string.Join(", ", rowsWithCode)}.");
                }

                var owner = barcodeOwners.FirstOrDefault(b => b.Code == barcode.Code && b.ItemId != (current?.Id ?? 0));
                if (owner.Code is not null)
                {
                    errors.Add($"Штрихкод {barcode.Code} уже у позиции {owner.ItemCode}.");
                }

                list.Add((barcode.Type, barcode.Code));
            }

            if (list.Count > ItemBarcode.MaxPerItem)
            {
                errors.Add($"У позиции не больше {ItemBarcode.MaxPerItem} штрихкодов.");
            }

            barcodes = list.DistinctBy(b => b.Item2).ToList();
            var now = barcodeOwners.Where(b => b.ItemId == (current?.Id ?? -1)).Select(b => b.Code).Order().ToList();
            if (!now.SequenceEqual(barcodes.Select(b => b.Item2).Order()))
            {
                changed = true;
            }
        }

        // Цены по видам.
        Dictionary<long, decimal?>? prices = null;
        foreach (var (name, typeId) in lookups.PriceTypes)
        {
            if (!Has(PricePrefix + name) || !canSeePrices)
            {
                continue;
            }

            prices ??= [];
            var price = Try(() => Decimals.Parse(Text(PricePrefix + name), PricePrefix + name) is { } v ? ItemPrice.Check(v) : (decimal?)null);
            prices[typeId] = price;
            if (currentPrices.FirstOrDefault(x => x.PriceTypeId == typeId)?.Price != price)
            {
                changed = true;
            }
        }

        return (new Parsed(details, barcodes, prices, parentCode, values), changed);
    }

    /// <summary>«Цвет: красный; Размер: 48» → номер характеристики → значение. Неизвестная характеристика — ошибка.</summary>
    private static IReadOnlyDictionary<long, string> ParseVariant(string text, IReadOnlyDictionary<string, long> characteristics)
    {
        var result = new Dictionary<long, string>();
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = part.IndexOf(':');
            if (colon <= 0)
            {
                throw new BusinessRuleException("import.variant", $"«{part}»: пишите «Характеристика: значение», например «Размер: 48».");
            }

            var name = part[..colon].Trim();
            var id = characteristics.FirstOrDefault(c => string.Equals(c.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
            if (id == 0)
            {
                throw new BusinessRuleException("import.variant", $"Характеристики «{name}» нет. Заведите её в настройках номенклатуры.");
            }

            if (!result.TryAdd(id, Variants.CleanValue(part[(colon + 1)..])))
            {
                throw new BusinessRuleException("import.variant", $"Характеристика «{name}» указана дважды.");
            }
        }

        _ = Variants.Key(result);
        return result;
    }

    private static string VariantText(IEnumerable<(string Name, string Value)> values) =>
        string.Join("; ", values.Select(v => $"{v.Name}: {v.Value}"));

    private static IEnumerable<string> SplitBarcodes(string? text) =>
        (text ?? string.Empty).Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(b => b.Replace(" ", string.Empty, StringComparison.Ordinal));

    private static bool IsPriceColumn(string column) =>
        column == PurchasePriceColumn || column == MinPriceColumn || column.StartsWith(PricePrefix, StringComparison.Ordinal);

    private static IReadOnlyList<string> HeaderFor(bool prices, IEnumerable<string> priceTypes) =>
    [
        .. Columns, .. ExtraColumns.Where(c => prices || (c != PurchasePriceColumn && c != MinPriceColumn)), .. prices ? priceTypes.Select(t => PricePrefix + t) : [],
    ];

    private static string Number(decimal? value) =>
        value is { } v ? v.ToString("0.######", CultureInfo.GetCultureInfo("ru-RU")) : "";

    private async Task<Lookups> LookupsAsync(AccessContext ctx, CancellationToken ct)
    {
        var org = ctx.OrganizationId;
        var groups = await db.ItemGroups.AsNoTracking().Where(g => g.OrganizationId == org)
            .Select(g => new { g.Id, g.ParentId, g.Name, g.IsArchived }).ToListAsync(ct);
        var byId = groups.ToDictionary(g => g.Id);
        string Path(long id)
        {
            var parts = new List<string>();
            for (long? cur = id; cur is { } c && byId.TryGetValue(c, out var g) && parts.Count <= ItemGroup.MaxDepth; cur = g.ParentId)
            {
                parts.Insert(0, g.Name);
            }

            return string.Join(" / ", parts);
        }

        var paths = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups.Where(g => !g.IsArchived))
        {
            paths.TryAdd(Path(g.Id), g.Id);
        }

        var priceTypes = await db.PriceTypes.AsNoTracking().Where(t => t.OrganizationId == org && !t.IsArchived)
            .OrderByDescending(t => t.IsDefault).ThenBy(t => t.SortOrder).ThenBy(t => t.Id).Select(t => new { t.Id, t.Name }).ToListAsync(ct);
        var characteristics = await db.Characteristics.AsNoTracking().Where(c => c.OrganizationId == org)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToListAsync(ct);
        return new Lookups(
            paths,
            priceTypes.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase),
            characteristics.Where(c => !c.IsArchived).GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase),
            characteristics.ToDictionary(c => c.Id, c => c.Name));
    }

    private static ItemType? ParseType(string text) =>
        ItemTypes.All.Cast<ItemType?>().FirstOrDefault(t => string.Equals(ItemTypes.Name(t!.Value), text.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Единица по обозначению («кг»), наименованию («Килограмм») или коду ОКЕИ («166»).</summary>
    private static UnitOfMeasure? FindUnit(IReadOnlyList<UnitOfMeasure> units, string text)
    {
        var t = text.Trim();
        return t.Length == 0
            ? null
            : units.FirstOrDefault(u => string.Equals(u.Symbol, t, StringComparison.OrdinalIgnoreCase))
              ?? units.FirstOrDefault(u => string.Equals(u.Name, t, StringComparison.OrdinalIgnoreCase))
              ?? units.FirstOrDefault(u => u.Code == t);
    }

    private async Task<List<UnitOfMeasure>> ActiveUnitsAsync(AccessContext ctx, CancellationToken ct) =>
        await db.Units.AsNoTracking().Where(u => u.OrganizationId == ctx.OrganizationId && !u.IsArchived).OrderBy(u => u.Name).ToListAsync(ct);

    private static string Cell(IReadOnlyList<string> cells, int index) => index < cells.Count ? cells[index].Trim() : string.Empty;

    private void Audit(AccessContext ctx, string action, string entityType, string? id, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, entityType, id,
            before, after, reason, currentUser.CorrelationId));
}
