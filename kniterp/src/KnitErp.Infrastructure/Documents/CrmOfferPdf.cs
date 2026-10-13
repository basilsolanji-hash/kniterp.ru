using KnitErp.Application.Crm;
using KnitErp.Application.Sales;
using MigraDoc.DocumentObjectModel;

namespace KnitErp.Infrastructure.Documents;

/// <summary>
/// PDF коммерческого предложения CRM (D696) — тем же движком, шрифтом и оформлением, что заказ, счёт и УПД продаж: логотип, стороны,
/// таблица позиций, итог прописью, срок действия, условия, подпись руководителя и печать (если включены в настройках документов).
/// </summary>
public sealed partial class SalesPdfRenderer : ICrmOfferPdfRenderer
{
    public byte[] Offer(CrmOfferDto d, SalesPrintDecorations deco)
    {
        var (doc, sec) = NewDocument($"КП {d.Number}", landscape: false, 9);
        Logo(sec, deco.Logo);
        Title(sec, $"Коммерческое предложение № {d.Number} от {Date(d.Date)}");
        var rows = new List<(string, string)>
        {
            ("Поставщик:", string.Join(", ", new[] { d.Seller, d.SellerInn is null ? null : $"ИНН {d.SellerInn}", d.SellerAddress }.Where(x => x is not null))),
            ("Покупатель:", d.CustomerInn is null ? d.Customer : $"{d.Customer}, ИНН {d.CustomerInn}"),
        };
        if (d.Contact is { } contact)
        {
            rows.Add(("Контактное лицо:", contact));
        }

        rows.Add(("Действует до:", d.ValidUntil.ToString("dd.MM.yyyy", Ru)));
        Parties(sec, [.. rows]);

        sec.AddParagraph().Format.SpaceAfter = Unit.FromMillimeter(2);
        var t = Grid(sec, 8, 22, 76, 20, 12, 22, 26);
        var h = t.AddRow();
        h.HeadingFormat = true;
        h.Format.Font.Bold = true;
        string[] heads = ["№", "Артикул", "Наименование", "Кол-во", "Ед.", "Цена", "Сумма"];
        for (var i = 0; i < heads.Length; i++)
        {
            Text(h.Cells[i], heads[i]);
        }

        foreach (var l in d.Lines)
        {
            var r = t.AddRow();
            Text(r.Cells[0], l.LineNo.ToString(Ru));
            Text(r.Cells[1], l.Code);
            Text(r.Cells[2], l.Name);
            Num(r.Cells[3], Quantity(l.Quantity));
            Text(r.Cells[4], l.UnitSymbol);
            Num(r.Cells[5], Money(l.Price));
            Num(r.Cells[6], Money(l.Amount));
        }

        Totals(sec, ("Итого:", Money(d.Amount)), (d.PricesIncludeVat ? "В том числе НДС:" : "НДС:", d.PricesIncludeVat ? "по ставкам позиций" : "не включён"));
        Para(sec, $"Всего наименований {d.Lines.Count}, на сумму {Money(d.Amount)} {d.CurrencyCode}");
        Para(sec, d.AmountInWords, bold: true);
        Para(sec, $"Предложение действует до {d.ValidUntil:dd.MM.yyyy}.");
        if (d.Terms is { } terms)
        {
            Para(sec, "Условия: " + terms);
        }

        sec.AddParagraph().Format.SpaceAfter = Unit.FromMillimeter(4);
        var s = Grid(sec, 62, 40, 46, 38);
        s.Borders.Visible = false;
        var row = s.AddRow();
        row.VerticalAlignment = MigraDoc.DocumentObjectModel.Tables.VerticalAlignment.Bottom;
        row.Height = Unit.FromMillimeter(14);
        Text(row.Cells[0], "Руководитель");
        var line = row.Cells[1].AddParagraph();
        line.Format.Borders.Bottom.Width = 0.5;
        if (deco.DirectorSignature is { } signature)
        {
            AddImage(line, signature, 12);
        }

        Text(row.Cells[2], d.SellerDirector ?? string.Empty);
        if (deco.Stamp is { } stamp)
        {
            AddImage(row.Cells[3].AddParagraph(), stamp, 34);
        }

        Para(sec, $"Менеджер: {d.CreatedBy}", size: 8);
        return Render(doc);
    }
}
