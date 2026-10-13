using System.Globalization;
using KnitErp.Application.Access;
using KnitErp.Application.Audit;
using KnitErp.Application.Common;
using KnitErp.Application.Sales;
using KnitErp.Application.Taxes;
using KnitErp.Application.Warehousing;
using KnitErp.Web.Components.Shared;

namespace KnitErp.Web.Exports;

/// <summary>
/// Выгрузка отчётов и журналов в Excel теми же фильтрами, что на экране. Данные дают сервисы — они же проверяют права
/// и область складов; здесь только раскладка по столбцам. Количества — числами, время — по часовому поясу организации.
/// </summary>
public sealed class ReportExports(
    StockService stock, StockReportService reports, AuditQueryService audit, UserAccessService access, ISpreadsheetFormat spreadsheet,
    StockDocumentService documents, InventoryService inventory, OpeningBalanceService openingBalances, SalesAnalyticsService analytics,
    VatInvoiceService vatInvoices)
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public async Task<byte[]> StockAsync(StockFilter filter, CancellationToken ct)
    {
        var rows = await stock.BalancesAsync(filter, ct);
        return spreadsheet.Write([new SheetData("Остатки", ["Склад", "Код", "Наименование", "Тип", "Остаток", "Резерв", "Доступно", "Ед."],
            rows.Select(r => Row(r.WarehouseName, r.Code, r.Name, r.TypeName, Number(r.Quantity), Number(r.Reserved), Number(r.Available), r.UnitSymbol)).ToList(),
            Numeric(4, 5, 6))]);
    }

    public async Task<byte[]> MovementsAsync(MovementFilter filter, CancellationToken ct)
    {
        var report = await reports.MovementsAsync(filter, ct);
        return spreadsheet.Write([new SheetData("Движения", ["Дата", "Документ", "Склад", "Код", "Наименование", "Приход", "Расход", "Ед."],
            report.Rows.Select(r => Row(r.Date.ToString("dd.MM.yyyy"), r.Document, r.WarehouseName, r.Code, r.Name,
                r.Incoming == 0 ? "" : Number(r.Incoming), r.Outgoing == 0 ? "" : Number(r.Outgoing), r.UnitSymbol)).ToList(), Numeric(5, 6))]);
    }

    public async Task<byte[]> TurnoverAsync(TurnoverFilter filter, CancellationToken ct)
    {
        var rows = await reports.TurnoverAsync(filter, ct);
        var title = $"Обороты {filter.From:dd.MM.yyyy}–{filter.To:dd.MM.yyyy}";
        return spreadsheet.Write([new SheetData("Обороты", ["Склад", "Код", "Наименование", "На начало", "Приход", "Расход", "На конец", "Ед.", title],
            rows.Select(r => Row(r.WarehouseName, r.Code, r.Name, Number(r.Opening), Number(r.Incoming), Number(r.Outgoing), Number(r.Closing),
                r.UnitSymbol, "")).ToList(), Numeric(3, 4, 5, 6))]);
    }

    public async Task<byte[]> InventoryVariancesAsync(InventoryVarianceFilter filter, CancellationToken ct)
    {
        var report = await reports.InventoryVariancesAsync(filter, ct);
        var title = $"Отклонения инвентаризации {filter.From:dd.MM.yyyy}–{filter.To:dd.MM.yyyy}";
        return spreadsheet.Write([new SheetData("Отклонения", ["Дата", "Инвентаризация", "Склад", "Код", "Наименование", "Учёт", "Факт",
                "Излишек", "Недостача", "Ед.", "Комментарий", title],
            report.Rows.Select(r => Row(r.Date.ToString("dd.MM.yyyy"), r.Number, r.WarehouseName, r.Code, r.Name, Number(r.Book), Number(r.Counted),
                r.Difference > 0 ? Number(r.Difference) : "", r.Difference < 0 ? Number(-r.Difference) : "", r.UnitSymbol, r.Comment ?? "", "")).ToList(),
            Numeric(5, 6, 7, 8))]);
    }

    /// <summary>Реестр складских документов с фильтрами экрана.</summary>
    public async Task<byte[]> StockDocumentsAsync(StockDocumentListFilter filter, CancellationToken ct)
    {
        var rows = await documents.ListAsync(filter, StockDocumentService.MaxRegistryRows, ct);
        var title = filter.From is null && filter.To is null ? "Реестр складских документов"
            : $"Реестр складских документов {filter.From:dd.MM.yyyy}–{filter.To:dd.MM.yyyy}";
        return spreadsheet.Write([new SheetData("Документы", ["Номер", "Дата", "Вид", "Склад", "Склад-получатель", "Контрагент", "Причина",
                "Строк", "Статус", "Автор", title],
            rows.Select(r => Row(r.Number, r.DocumentDate.ToString("dd.MM.yyyy"), r.KindName, r.WarehouseName, r.TargetWarehouseName ?? "",
                r.CounterpartyName ?? "", r.ReasonName ?? "", r.LineCount.ToString(CultureInfo.InvariantCulture), r.StatusName, r.CreatedBy, "")).ToList(),
            Numeric(7))]);
    }

    /// <summary>Реестр инвентаризаций с числом излишков и недостач.</summary>
    public async Task<byte[]> InventoriesAsync(InventoryListFilter filter, CancellationToken ct)
    {
        var rows = await inventory.ListAsync(filter, ct);
        return spreadsheet.Write([new SheetData("Инвентаризации", ["Номер", "Дата", "Склад", "Позиций", "Излишков", "Недостач", "Статус", "Автор"],
            rows.Select(r => Row(r.Number, r.CountDate.ToString("dd.MM.yyyy"), r.WarehouseName, r.LineCount.ToString(CultureInfo.InvariantCulture),
                r.SurplusCount.ToString(CultureInfo.InvariantCulture), r.ShortageCount.ToString(CultureInfo.InvariantCulture), r.StatusName, r.CreatedBy)).ToList(),
            Numeric(3, 4, 5))]);
    }

    /// <summary>Реестр документов начальных остатков.</summary>
    public async Task<byte[]> OpeningBalancesAsync(CancellationToken ct)
    {
        var rows = await openingBalances.ListAsync(ct);
        return spreadsheet.Write([new SheetData("Начальные остатки", ["Номер", "Склад", "На дату", "Строк", "Статус", "Автор"],
            rows.Select(r => Row(r.Number, r.WarehouseName, r.AsOfDate.ToString("dd.MM.yyyy"), r.LineCount.ToString(CultureInfo.InvariantCulture),
                r.StatusName, r.CreatedBy)).ToList(), Numeric(3))]);
    }

    public async Task<byte[]> AuditAsync(CancellationToken ct)
    {
        var rows = await audit.ListAsync(1000, ct);
        var tz = (await access.GetCurrentAccessAsync(ct)).TimeZoneId;
        return spreadsheet.Write([new SheetData("Журнал аудита", ["Время", "Кто", "Действие", "Объект", "Было", "Стало", "Причина"],
            rows.Select(r => Row(Local(r.OccurredAtUtc, tz), r.ActorName ?? "система", AuditLabels.Action(r.Action), r.ObjectName,
                r.Before ?? "", r.After ?? "", r.Reason ?? "")).ToList())]);
    }

    public async Task<byte[]> SignInsAsync(DateOnly? from, DateOnly? to, bool failuresOnly, CancellationToken ct)
    {
        var tz = (await access.GetCurrentAccessAsync(ct)).TimeZoneId;
        var rows = await audit.ListSignInsAsync(ToUtc(from, tz), ToUtc(to?.AddDays(1), tz), failuresOnly, 5000, ct);
        return spreadsheet.Write([new SheetData("Журнал входов", ["Время", "Пользователь", "Событие", "Способ", "Подробности", "Адрес"],
            rows.Select(r => Row(Local(r.OccurredAtUtc, tz), r.UserName ?? "—", AuditLabels.Action(r.Action), r.Method ?? "", r.Detail ?? "",
                r.Address ?? "")).ToList())]);
    }

    public async Task<byte[]> SalesByItemAsync(SalesAnalyticsFilter filter, CancellationToken ct)
    {
        var rows = await analytics.SalesByItemAsync(filter, ct);
        var title = $"Товары и реализации {filter.From:dd.MM.yyyy}–{filter.To:dd.MM.yyyy}";
        return spreadsheet.Write([new SheetData("Реализации", ["Код", "Наименование", "Количество", "Ед.", "Средняя цена", "Выручка без НДС", "НДС",
                "С НДС", "Покупателей", title],
            rows.Select(r => Row(r.Code, r.Name, Number(r.Quantity), r.UnitSymbol, r.AveragePrice is { } p ? Number(p) : "", Number(r.Revenue),
                Number(r.Vat), Number(r.RevenueWithVat), r.Customers.ToString(CultureInfo.InvariantCulture), "")).ToList(), Numeric(2, 4, 5, 6, 7, 8))]);
    }

    public async Task<byte[]> ProfitabilityAsync(SalesAnalyticsFilter filter, ProfitGrouping grouping, CancellationToken ct)
    {
        var data = await analytics.ProfitabilityAsync(filter, grouping, ct);
        var title = $"Прибыльность {filter.From:dd.MM.yyyy}–{filter.To:dd.MM.yyyy}";
        var source = new Dictionary<KnitErp.Domain.Sales.CostSource, string>
        {
            [KnitErp.Domain.Sales.CostSource.Purchases] = "закупкам", [KnitErp.Domain.Sales.CostSource.TechCard] = "техкарте",
            [KnitErp.Domain.Sales.CostSource.Components] = "компонентам", [KnitErp.Domain.Sales.CostSource.Service] = "услуге (0)",
        };
        return spreadsheet.Write([new SheetData("Прибыльность", [grouping == ProfitGrouping.Item ? "Позиция" : "Покупатель", "Код", "Количество",
                "Выручка без НДС", "Себестоимость", "Себестоимость полная", "Прибыль", "Маржа, %", "Себестоимость по", title],
            data.Rows.Select(r => Row(r.Name, r.Code ?? "", r.Quantity is { } q ? Number(q) : "", Number(r.Revenue), Number(r.Cost),
                r.CostComplete ? "да" : "нет", r.Profit is { } p ? Number(p) : "", r.MarginPercent is { } m ? Number(m) : "",
                source.GetValueOrDefault(r.Source, ""), "")).ToList(), Numeric(2, 3, 4, 6, 7))]);
    }

    /// <summary>Журнал выданных счетов-фактур: только действующие (аннулированные — нет).</summary>
    public async Task<byte[]> IssuedVatInvoicesAsync(VatJournalFilter filter, CancellationToken ct)
    {
        var rows = await vatInvoices.IssuedAsync(filter with { IncludeCancelled = false }, ct);
        var title = $"Счета-фактуры выданные {filter.From:dd.MM.yyyy}–{filter.To:dd.MM.yyyy}";
        return spreadsheet.Write([new SheetData("Выданные", ["Номер", "Дата", "Отгрузка", "Покупатель", "ИНН", "КПП", "Заказ", "Без НДС", "НДС", "Всего", "Продавец", title],
            rows.Select(r => Row(r.InvoiceNumber, r.Date.ToString("dd.MM.yyyy"), r.Number, r.Customer, r.Inn ?? "", r.Kpp ?? "", r.OrderNumber, Number(r.AmountWithoutVat),
                Number(r.VatAmount), Number(r.Amount), r.Seller ?? "", "")).ToList(), Numeric(7, 8, 9))]);
    }

    public async Task<byte[]> ReceivedVatInvoicesAsync(VatJournalFilter filter, CancellationToken ct)
    {
        var rows = await vatInvoices.ReceivedAsync(filter with { IncludeCancelled = false }, ct);
        var title = $"Счета-фактуры полученные {filter.From:dd.MM.yyyy}–{filter.To:dd.MM.yyyy}";
        return spreadsheet.Write([new SheetData("Полученные", ["Номер", "Дата", "Поставщик", "ИНН", "КПП", "Приёмка", "Дата приёмки", "Без НДС", "НДС",
                "Всего", "Расхождение с приёмкой", title],
            rows.Select(r => Row(r.SupplierNumber, r.Date.ToString("dd.MM.yyyy"), r.Supplier, r.Inn ?? "", r.Kpp ?? "", r.ReceiptNumber,
                r.ReceiptDate.ToString("dd.MM.yyyy"), Number(r.AmountWithoutVat), Number(r.VatAmount), Number(r.Amount), Number(r.Difference), "")).ToList(),
            Numeric(7, 8, 9, 10))]);
    }

    /// <summary>Начало местных суток организации в UTC — граница фильтра по дате.</summary>
    public static DateTime? ToUtc(DateOnly? localDate, string timeZoneId)
    {
        if (localDate is not { } d)
        {
            return null;
        }

        var local = d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var tz) ? TimeZoneInfo.ConvertTimeToUtc(local, tz) : local;
    }

    private static IReadOnlyList<string> Row(params string[] cells) => cells;

    private static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static HashSet<int> Numeric(params int[] columns) => [.. columns];

    private static string Local(DateTime utc, string tz) => TimeZones.ToLocal(utc, tz).ToString("dd.MM.yyyy HH:mm:ss");
}

/// <summary>Ссылка на выгрузку с текущими фильтрами страницы; пустые фильтры не передаются.</summary>
public static class ExportLink
{
    public static string Build(string path, params (string Name, string? Value)[] query)
    {
        var parts = query.Where(q => !string.IsNullOrWhiteSpace(q.Value))
            .Select(q => $"{q.Name}={Uri.EscapeDataString(q.Value!.Trim())}").ToList();
        return parts.Count == 0 ? path : $"{path}?{string.Join("&", parts)}";
    }

    public static string? Date(DateTime? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
