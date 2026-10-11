using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Common;
using KnitErp.Domain.Production;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Production;

/// <summary>Строка печатной формы: материал или изделие, единица с кодом ОКЕИ, количество; цена и сумма — только с правом «Цены и суммы».</summary>
public sealed record ProductionPrintLineDto(
    int No, string Code, string Name, string UnitName, string UnitCode, decimal Quantity, decimal? Price, decimal? Amount, decimal? Norm = null);

/// <summary>
/// Данные печатной формы документа производственного заказа (D96): требование-накладная по образцу М-11 (выдача в цех, возврат остатка)
/// или накладная на передачу готовой продукции в места хранения по образцу МХ-18 (выпуск).
/// </summary>
public sealed record ProductionPrintDto(
    string Form, string Okud, string Title, string Number, DateOnly Date, string Organization, string? Inn, string? Kpp,
    string Sender, string Receiver, string OrderNumber, string ProductName, string? Reason, string? Comment,
    IReadOnlyList<ProductionPrintLineDto> Lines, decimal? Total, bool PricesVisible, bool Reversed, string? DirectorName, string? DirectorPosition,
    string? Produced = null)
{
    public bool IsOutput => Form == ProductionPrintService.OutputForm;

    /// <summary>Акт (отходы или расход при выпуске) — собственная форма организации, без ОКУД.</summary>
    public bool IsAct => Form is ProductionPrintService.WasteActForm or ProductionPrintService.ConsumptionActForm;

    public bool IsConsumption => Form == ProductionPrintService.ConsumptionActForm;
}

/// <summary>
/// Печать документов производственного заказа (D96). С 2013 года унифицированные формы не обязательны (ч. 4 ст. 9 Федерального закона
/// № 402-ФЗ): формы — по образцу М-11 (постановление Госкомстата России от 30.10.1997 № 71а) и МХ-18 (постановление Госкомстата России
/// от 09.08.1999 № 66) со всеми обязательными реквизитами первичного документа (ч. 2 ст. 9): наименование документа, дата, организация,
/// содержание операции, натуральные (и денежные) измерители с единицами, должности и подписи ответственных лиц.
/// Право — «Производство: просмотр заказов»; цены и суммы — с «Цены и суммы».
/// </summary>
public sealed class ProductionPrintService(IKnitErpDbContext db, IAccessGuard guard)
{
    public const string RequisitionForm = "М-11";
    public const string OutputForm = "МХ-18";
    public const string WasteActForm = "Акт об отходах";
    public const string ConsumptionActForm = "Акт расхода";

    public async Task<ProductionPrintDto> DocumentAsync(long stockDocumentId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.ProductionOrderView, ct);
        var link = await db.ProductionOrderDocuments.AsNoTracking()
                       .SingleOrDefaultAsync(l => l.StockDocumentId == stockDocumentId && l.OrganizationId == ctx.OrganizationId, ct)
                   ?? throw new NotFoundException("Документ");

        var doc = await db.StockDocuments.AsNoTracking().Include(d => d.Lines).SingleAsync(d => d.Id == stockDocumentId, ct);
        if (doc.Status is not (StockDocumentStatus.Posted or StockDocumentStatus.Reversed))
        {
            throw new BusinessRuleException("production.print.status", "Печатается проведённый документ.");
        }

        var order = await db.ProductionOrders.AsNoTracking().Include(o => o.Materials).SingleAsync(o => o.Id == link.ProductionOrderId, ct);
        var org = await db.Organizations.AsNoTracking().SingleAsync(o => o.Id == ctx.OrganizationId, ct);
        var entity = await db.LegalEntities.AsNoTracking()
            .Where(e => e.OrganizationId == ctx.OrganizationId && e.IsDefault).FirstOrDefaultAsync(ct);
        var warehouses = await db.Warehouses.AsNoTracking()
            .Where(w => w.Id == doc.WarehouseId || w.Id == doc.TargetWarehouseId).ToDictionaryAsync(w => w.Id, w => w.Name, ct);
        var reason = doc.ReasonId is { } rid ? await db.OperationReasons.AsNoTracking().Where(r => r.Id == rid).Select(r => r.Name).SingleAsync(ct) : null;
        var ids = doc.Lines.Select(l => l.ItemId).Append(order.ItemId).Distinct().ToList();
        var items = await db.Items.AsNoTracking().Where(i => ids.Contains(i.Id))
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new { i.Id, i.Code, i.Name, UnitName = u.Name, UnitCode = u.Code })
            .ToDictionaryAsync(x => x.Id, ct);
        var pricesVisible = ctx.Permissions.Has(Permissions.PriceView);

        // Учётная цена: материалы — плановая цена из снимка норм заказа (без НДС), изделие — плановая себестоимость единицы.
        // Бухгалтер может исправить её в своей программе — форма это допускает.
        // Допущение D96.
        decimal? PriceOf(long itemId)
        {
            if (!pricesVisible)
            {
                return null;
            }

            if (itemId == order.ItemId)
            {
                return order.PlannedCost() is { } c ? decimal.Round(c / order.Quantity, 2, MidpointRounding.AwayFromZero) : null;
            }

            return order.Materials.FirstOrDefault(m => m.ItemId == itemId)?.PlannedPrice;
        }

        var lines = doc.Lines.Select(l => (Line: l, Item: items[l.ItemId], Price: PriceOf(l.ItemId)))
            .OrderBy(x => x.Item.Code)
            .Select((x, i) => new ProductionPrintLineDto(i + 1, x.Item.Code, x.Item.Name, x.Item.UnitName, x.Item.UnitCode, x.Line.Quantity,
                x.Price is { } p ? decimal.Round(p, 2, MidpointRounding.AwayFromZero) : null, x.Price is { } q ? Money.Round(q * x.Line.Quantity) : null))
            .ToList();
        var output = link.Purpose == ProductionDocumentPurpose.Output;
        if (link.Purpose is ProductionDocumentPurpose.Waste or ProductionDocumentPurpose.Consumption)
        {
            return await ActAsync(ctx, link, doc, order, lines, items.ToDictionary(x => x.Key, x => (x.Value.Code, x.Value.Name)), warehouses[doc.WarehouseId],
                reason, entity?.Name ?? org.FullName, entity?.Inn ?? org.Inn, entity is null ? (org.KppVerified ? org.Kpp : null) : entity.Kpp,
                pricesVisible, entity?.DirectorName, entity?.DirectorPosition, ct);
        }

        var (sender, receiver) = output
            ? ($"Цех (производство), заказ {order.Number}", warehouses[doc.WarehouseId])
            : (warehouses[doc.WarehouseId], warehouses[doc.TargetWarehouseId!.Value]);
        return new ProductionPrintDto(
            output ? OutputForm : RequisitionForm, output ? "0335018" : "0315006",
            output ? "НАКЛАДНАЯ НА ПЕРЕДАЧУ ГОТОВОЙ ПРОДУКЦИИ В МЕСТА ХРАНЕНИЯ" : "ТРЕБОВАНИЕ-НАКЛАДНАЯ",
            doc.Number, doc.DocumentDate, entity?.Name ?? org.FullName, entity?.Inn ?? org.Inn, entity is null ? (org.KppVerified ? org.Kpp : null) : entity.Kpp,
            sender, receiver, order.Number, $"{items[order.ItemId].Code} {items[order.ItemId].Name}", reason, doc.Comment,
            lines, lines.All(l => l.Amount is not null) && lines.Count > 0 ? lines.Sum(l => l.Amount!.Value) : null,
            pricesVisible, doc.Status == StockDocumentStatus.Reversed, entity?.DirectorName, entity?.DirectorPosition);
    }

    /// <summary>
    /// Акт (D96, дополнение): списание отходов и брака из цеха с причиной или расход материалов при выпуске — с нормой на выпущенное
    /// и фактом по каждому материалу. Унифицированной формы нет (ТОРГ-16 — для товаров в торговле): собственная форма с реквизитами
    /// ч. 2 ст. 9 Федерального закона № 402-ФЗ, подписи комиссии и утверждение руководителем.
    /// </summary>
    private async Task<ProductionPrintDto> ActAsync(
        AccessContext ctx, ProductionOrderDocument link, StockDocument doc, ProductionOrder order, IReadOnlyList<ProductionPrintLineDto> lines,
        IReadOnlyDictionary<long, (string Code, string Name)> items, string wip, string? reason, string organization, string? inn, string? kpp,
        bool pricesVisible, string? director, string? directorPosition, CancellationToken ct)
    {
        var consumption = link.Purpose == ProductionDocumentPurpose.Consumption;
        string? produced = null;
        if (consumption)
        {
            // Выпуск, проведённый в той же операции: следующая связь заказа с целью «выпуск».
            var output = await (from l in db.ProductionOrderDocuments.AsNoTracking()
                                where l.ProductionOrderId == order.Id && l.Purpose == ProductionDocumentPurpose.Output && l.Id > link.Id
                                orderby l.Id
                                join d in db.StockDocuments.AsNoTracking() on l.StockDocumentId equals d.Id
                                select d).Include(d => d.Lines).FirstOrDefaultAsync(ct);
            var qty = output?.Lines.Where(x => x.ItemId == order.ItemId).Sum(x => x.Quantity) ?? 0m;
            if (qty > 0)
            {
                produced = $"{items[order.ItemId].Code} {items[order.ItemId].Name} — {Quantities.Format(qty)} (накладная {output!.Number})";
                lines = lines.Select(l =>
                {
                    var material = order.Materials.FirstOrDefault(m => items[m.ItemId].Code == l.Code);
                    return material is null ? l : l with { Norm = decimal.Round(material.PlannedNet * qty / order.Quantity, 6) };
                }).ToList();
            }
        }

        return new ProductionPrintDto(consumption ? ConsumptionActForm : WasteActForm, "",
            consumption ? "АКТ РАСХОДА МАТЕРИАЛОВ В ПРОИЗВОДСТВО" : "АКТ О СПИСАНИИ МАТЕРИАЛОВ (ОТХОДЫ И БРАК ПРОИЗВОДСТВА)",
            doc.Number, doc.DocumentDate, organization, inn, kpp, wip, consumption ? "Производство (затраты на выпуск)" : "Списание",
            order.Number, $"{items[order.ItemId].Code} {items[order.ItemId].Name}", reason, doc.Comment, lines,
            lines.All(l => l.Amount is not null) && lines.Count > 0 ? lines.Sum(l => l.Amount!.Value) : null,
            pricesVisible, doc.Status == StockDocumentStatus.Reversed, director, directorPosition, produced);
    }
}
