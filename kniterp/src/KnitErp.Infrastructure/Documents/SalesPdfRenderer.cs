using System.Globalization;
using KnitErp.Application.Printing;
using KnitErp.Application.Sales;
using KnitErp.Domain.Organizations;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Shapes;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;
using QRCoder;

namespace KnitErp.Infrastructure.Documents;

/// <summary>
/// QR-код платежа (D1121): SVG — для HTML-формы, PNG — для PDF. Текст — в UTF-8 (ST00012), уровень коррекции M.
/// </summary>
public static class PaymentQrImage
{
    public static string Svg(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M, forceUtf8: true);
        return new SvgQRCode(data).GetGraphic(4);
    }

    /// <summary>
    /// PNG для PDF: 8-битный оттенок серого без палитры — такой PNG PDFsharp читает надёжно (палитровый PNG из QRCoder он не принимает).
    /// </summary>
    public static byte[] Png(string payload, int pixelsPerModule = 6)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M, forceUtf8: true);
        var matrix = data.ModuleMatrix;
        var size = matrix.Count * pixelsPerModule;
        using var raw = new MemoryStream();
        for (var y = 0; y < size; y++)
        {
            raw.WriteByte(0); // фильтр строки: нет
            var row = matrix[y / pixelsPerModule];
            for (var x = 0; x < size; x++)
            {
                raw.WriteByte(row[x / pixelsPerModule] ? (byte)0 : (byte)255);
            }
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, size);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), size);
        header[8] = 8; // глубина
        header[9] = 0; // оттенки серого
        Chunk(png, "IHDR", header);
        using (var compressed = new MemoryStream())
        {
            using (var z = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            {
                raw.Position = 0;
                raw.CopyTo(z);
            }

            Chunk(png, "IDAT", compressed.ToArray());
        }

        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        Span<byte> buffer = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        png.Write(buffer);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        png.Write(typeBytes);
        png.Write(data);
        var crc = Crc(Crc(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buffer, crc);
        png.Write(buffer);
    }

    /// <summary>CRC-32 (полином 0xEDB88320) блока PNG.</summary>
    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc;
    }
}

/// <summary>
/// PDF счёта, УПД и заказа покупателя (D1123) для вложений в письма — PDFsharp/MigraDoc 6 (MIT). Шрифт — DejaVu Sans (свободная
/// лицензия Bitstream Vera/DejaVu), встроенный ресурс: на сервере могут отсутствовать системные шрифты с кириллицей. Формы печатаются
/// по-русски, как HTML-формы (D68): это документы российского учёта. Логотип, печать, подписи и QR — по настройкам юрлица (D1120).
/// </summary>
public sealed class SalesPdfRenderer : ISalesPdfRenderer
{
    private const string Font = "DejaVu Sans";
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly object FontGate = new();
    private static bool _fontsReady;

    public byte[] Invoice(InvoicePrintDto d, SalesPrintDecorations deco)
    {
        var (doc, sec) = NewDocument($"Счёт {d.Number}", landscape: false, 9);
        var r = d.Requisites;
        Logo(sec, deco.Logo);
        if (d.Cancelled)
        {
            Warning(sec, "СЧЁТ ОТМЕНЁН");
        }

        var bank = Grid(sec, 75, 15, 25, 55);
        var row = bank.AddRow();
        Text(row.Cells[0], r.BankName ?? "—");
        Text(row.Cells[0], "Банк получателя", 7);
        row.Cells[0].MergeRight = 1;
        row.Cells[0].MergeDown = 1;
        Text(row.Cells[2], "БИК");
        Text(row.Cells[3], r.BankBic ?? string.Empty);
        row = bank.AddRow();
        Text(row.Cells[2], "Сч. №");
        Text(row.Cells[3], r.BankCorrAccount ?? string.Empty);
        row = bank.AddRow();
        Text(row.Cells[0], "ИНН " + d.Seller.Inn);
        Text(row.Cells[1], "КПП " + d.Seller.Kpp);
        Text(row.Cells[2], "Сч. №");
        Text(row.Cells[3], r.BankAccount ?? string.Empty);
        row.Cells[2].MergeDown = 1;
        row.Cells[3].MergeDown = 1;
        row = bank.AddRow();
        Text(row.Cells[0], d.Seller.Name);
        Text(row.Cells[0], "Получатель", 7);
        row.Cells[0].MergeRight = 1;

        Title(sec, $"Счёт на оплату № {d.Number} от {Date(d.Date)}");
        Parties(sec, ("Поставщик (Исполнитель):", Party(d.Seller)), ("Покупатель (Заказчик):", Party(d.Buyer)), ("Основание:", d.Basis));
        Lines(sec, d.Lines, d.PricesIncludeVat);
        Totals(sec,
            ("Итого:", Money(d.PricesIncludeVat ? d.Total : d.Total - d.VatTotal)),
            (d.PricesIncludeVat ? "В том числе НДС:" : "НДС:", d.VatTotal == 0 ? (r.VatExempt ? "Без налога (НДС)" : "Без НДС") : Money(d.VatTotal)),
            ("Всего к оплате:", Money(d.Total)));
        Para(sec, $"Всего наименований {d.Lines.Count}, на сумму {Money(d.Total)} {d.CurrencyCode}");
        Para(sec, d.TotalInWords, bold: true);
        if (d.DueDate is { } due)
        {
            Para(sec, $"Оплатить не позднее {due:dd.MM.yyyy}.");
        }

        if (deco.PaymentQr is { } qr)
        {
            var q = sec.AddParagraph();
            q.Format.SpaceBefore = Unit.FromMillimeter(3);
            var image = q.AddImage(Base64(PaymentQrImage.Png(qr)));
            image.Width = Unit.FromMillimeter(32);
            image.LockAspectRatio = true;
            Para(sec, "Оплата по QR-коду в приложении банка", size: 7);
        }

        Signatures(sec, r, deco, r.SoleProprietor
            ? [("Индивидуальный предприниматель", r.DirectorName, deco.DirectorSignature)]
            : [("Руководитель", r.DirectorName, deco.DirectorSignature), ("Бухгалтер", r.AccountantName ?? r.DirectorName, deco.AccountantSignature ?? deco.DirectorSignature)]);
        return Render(doc);
    }

    public byte[] Order(SalesOrderPrintDto d, SalesPrintDecorations deco)
    {
        var (doc, sec) = NewDocument($"Заказ {d.Number}", landscape: false, 9);
        Logo(sec, deco.Logo);
        if (d.Cancelled)
        {
            Warning(sec, "ЗАКАЗ ОТМЕНЁН");
        }

        Title(sec, $"Заказ покупателя № {d.Number} от {Date(d.Date)}");
        var rows = new List<(string, string)> { ("Поставщик:", Party(d.Seller)), ("Покупатель:", Party(d.Buyer)) };
        if (d.CustomerReference is { } reference)
        {
            rows.Add(("Основание:", reference));
        }

        if (d.DeliveryAddress is { } address)
        {
            rows.Add(("Адрес доставки:", address));
        }

        rows.Add(("Склад отгрузки:", d.Warehouse));
        if (d.ShipDate is { } ship)
        {
            rows.Add(("Дата отгрузки:", ship.ToString("dd.MM.yyyy", Ru)));
        }

        rows.Add(("Состояние:", d.Status));
        Parties(sec, [.. rows]);
        Lines(sec, d.Lines, d.PricesIncludeVat);
        Totals(sec,
            ("Итого:", Money(d.PricesIncludeVat ? d.Total : d.Total - d.VatTotal)),
            (d.PricesIncludeVat ? "В том числе НДС:" : "НДС:", d.VatTotal == 0 ? (d.Requisites.VatExempt ? "Без налога (НДС)" : "Без НДС") : Money(d.VatTotal)),
            ("Всего:", Money(d.Total)));
        Para(sec, $"Всего наименований {d.Lines.Count}, на сумму {Money(d.Total)} {d.CurrencyCode}");
        Para(sec, d.TotalInWords, bold: true);
        if (d.Comment is { } comment)
        {
            Para(sec, "Комментарий: " + comment);
        }

        Signatures(sec, d.Requisites, deco, [(d.Requisites.SoleProprietor ? "Индивидуальный предприниматель" : "Руководитель", d.Requisites.DirectorName,
            deco.DirectorSignature)]);
        return Render(doc);
    }

    public byte[] Upd(UpdPrintDto d, SalesPrintDecorations deco)
    {
        var (doc, sec) = NewDocument($"УПД {d.Number}", landscape: true, 7);
        var r = d.Requisites;
        if (d.Reversed)
        {
            Warning(sec, "ОТГРУЗКА СТОРНИРОВАНА — СЧЁТ-ФАКТУРА АННУЛИРОВАН");
        }

        var head = Grid(sec, 48, 229);
        head.Borders.Visible = false;
        var hr = head.AddRow();
        Text(hr.Cells[0], "Универсальный передаточный документ", bold: true);
        Text(hr.Cells[0], "Статус: 1", bold: true);
        Text(hr.Cells[0], "1 — счёт-фактура и передаточный документ (акт); 2 — передаточный документ (акт)", 6);
        string Kpp(PrintPartyDto p) => p.Inn + (p.Kpp is null ? "" : "/" + p.Kpp);
        foreach (var line in new[]
                 {
                     $"Счёт-фактура № {d.Number} от {Date(d.Date)} (1)", "Исправление № -- от -- (1а)", $"Продавец: {d.Seller.Name} (2)",
                     $"Адрес: {d.Seller.Address} (2а)", $"ИНН/КПП продавца: {Kpp(d.Seller)} (2б)", "Грузоотправитель и его адрес: он же (3)",
                     $"Грузополучатель и его адрес: {d.Buyer.Name}{(d.Buyer.Address is null ? "" : ", " + d.Buyer.Address)} (4)",
                     $"К платёжно-расчётному документу {d.PaymentDocuments ?? "№ -- от --"} (5)",
                     $"Документ об отгрузке № п/п 1–{d.Lines.Count} № {d.ShipmentNumber} от {d.Date:dd.MM.yyyy} (5а)",
                     "К счёту-фактуре, выставленному при получении оплаты, частичной оплаты в счёт предстоящих поставок: -- (5б)",
                     $"Покупатель: {d.Buyer.Name} (6)", $"Адрес: {d.Buyer.Address} (6а)", $"ИНН/КПП покупателя: {Kpp(d.Buyer)} (6б)",
                     $"Валюта: наименование, код {d.Currency} (7)", "Идентификатор государственного контракта, договора (соглашения): -- (8)",
                 })
        {
            Text(hr.Cells[1], line);
        }

        var goods = Grid(sec, 16, 7, 50, 12, 10, 10, 15, 17, 20, 12, 12, 17, 20, 10, 16, 33);
        goods.Format.Font.Size = 6;
        var th = goods.AddRow();
        th.HeadingFormat = true;
        th.Format.Font.Bold = true;
        var heads = new[]
        {
            "Код товара", "№ п/п", "Наименование товара (описание работ, услуг), имущественного права", "Код вида товара", "Ед. изм. код",
            "Ед. изм. обозн.", "Количество (объём)", "Цена (тариф) за единицу", "Стоимость без налога — всего", "В том числе акциз",
            "Налоговая ставка", "Сумма налога, предъявляемая покупателю", "Стоимость с налогом — всего", "Страна: код", "Страна: наименование",
            "Рег. номер декларации на товары / партии товара",
        };
        for (var i = 0; i < heads.Length; i++)
        {
            Text(th.Cells[i], heads[i]);
        }

        var codes = goods.AddRow();
        codes.HeadingFormat = true;
        string[] columnCodes = ["А", "1", "1а", "1б", "2", "2а", "3", "4", "5", "6", "7", "8", "9", "10", "10а", "11"];
        for (var i = 0; i < columnCodes.Length; i++)
        {
            Text(codes.Cells[i], columnCodes[i]);
            codes.Cells[i].Format.Alignment = ParagraphAlignment.Center;
        }

        foreach (var l in d.Lines)
        {
            var gr = goods.AddRow();
            Text(gr.Cells[0], l.Code);
            Text(gr.Cells[1], l.No.ToString(Ru));
            Text(gr.Cells[2], l.Name);
            Text(gr.Cells[3], l.TnVedCode ?? "--");
            Text(gr.Cells[4], l.UnitCode);
            Text(gr.Cells[5], l.UnitSymbol);
            Num(gr.Cells[6], Quantity(l.Quantity));
            Num(gr.Cells[7], Money(l.Price));
            Num(gr.Cells[8], Money(l.AmountWithoutVat));
            Text(gr.Cells[9], "без акциза");
            Text(gr.Cells[10], Vat(l.VatPercent));
            Num(gr.Cells[11], l.VatPercent is null ? (r.VatExempt ? "Без налога (НДС)" : "без НДС") : Money(l.VatAmount));
            Num(gr.Cells[12], Money(l.Amount));
            Text(gr.Cells[13], l.OriginCountryCode ?? "--");
            Text(gr.Cells[14], l.OriginCountryName ?? "--");
            Text(gr.Cells[15], l.CustomsDeclaration ?? "--");
        }

        var total = goods.AddRow();
        total.Format.Font.Bold = true;
        Text(total.Cells[0], "Всего к оплате");
        total.Cells[0].MergeRight = 7;
        Num(total.Cells[8], Money(d.TotalWithoutVat));
        Text(total.Cells[9], "X");
        total.Cells[9].MergeRight = 1;
        Num(total.Cells[11], Money(d.VatTotal));
        Num(total.Cells[12], Money(d.Total));
        Para(sec, "Всего к оплате: " + d.TotalInWords, bold: true);

        Signatures(sec, r, deco, r.SoleProprietor
            ? [("Индивидуальный предприниматель или иное уполномоченное лицо", r.DirectorName, deco.DirectorSignature)]
            : [("Руководитель организации или иное уполномоченное лицо", r.DirectorName, deco.DirectorSignature),
                ("Главный бухгалтер или иное уполномоченное лицо", r.AccountantName ?? r.DirectorName, deco.AccountantSignature ?? deco.DirectorSignature)],
            stamp: false);

        var transfer = Grid(sec, 138, 139);
        transfer.Format.Font.Size = 7;
        var who = r.SoleProprietor ? "Индивидуальный предприниматель" : r.DirectorPosition ?? string.Empty;
        foreach (var (left, right) in new[]
                 {
                     ($"Основание передачи (сдачи) / получения (приёмки): {d.Basis} [8]", "Данные о транспортировке и грузе: ____________ [9]"),
                     ($"Товар (груз) передал [10]: {who} ____________ {r.DirectorName}", "Товар (груз) получил [15]: ____________ ____________ ____________"),
                     ($"Дата отгрузки, передачи (сдачи): {Date(d.Date)} [11]", "Дата получения (приёмки): «___» __________ 20__ г. [16]"),
                     ($"Иные сведения об отгрузке, передаче: склад отгрузки — {d.Warehouse} [12]", "Иные сведения о получении, приёмке: ____________ [17]"),
                     ($"Ответственный за правильность оформления факта хозяйственной жизни [13]: {who} ____________ {r.DirectorName}",
                         "Ответственный за правильность оформления факта хозяйственной жизни [18]: ____________"),
                     ($"Составитель документа: {d.Seller.Name}, ИНН/КПП {Kpp(d.Seller)} [14]", $"Составитель документа: {d.Buyer.Name} [19]"),
                 })
        {
            var tr = transfer.AddRow();
            Text(tr.Cells[0], left);
            Text(tr.Cells[1], right);
        }

        var mp = transfer.AddRow();
        Text(mp.Cells[0], "М. П.");
        Text(mp.Cells[1], "М. П.");
        if (deco.Stamp is { } stamp)
        {
            AddImage(mp.Cells[0].AddParagraph(), stamp, 30);
        }

        return Render(doc);
    }

    public byte[] PriceList(PriceListDto d)
    {
        var (doc, sec) = NewDocument($"Прайс-лист {d.PriceType}", landscape: false, 8.5);
        Title(sec, $"Прайс-лист: {d.PriceType}");
        Para(sec, $"{d.Organization} · на {Date(d.Date)} · цены в {d.CurrencyCode}, {(d.IncludesVat ? "с НДС" : "без НДС")}");
        sec.AddParagraph().Format.SpaceAfter = Unit.FromMillimeter(2);
        var t = d.StockShown ? Grid(sec, 26, 24, 82, 12, 22, 20) : Grid(sec, 28, 26, 98, 12, 22);
        var h = t.AddRow();
        h.HeadingFormat = true;
        h.Format.Font.Bold = true;
        string[] heads = d.StockShown ? ["Код", "Артикул", "Наименование", "Ед.", "Цена", "В наличии"] : ["Код", "Артикул", "Наименование", "Ед.", "Цена"];
        for (var i = 0; i < heads.Length; i++)
        {
            Text(h.Cells[i], heads[i]);
        }

        string? group = null;
        var first = true;
        foreach (var r in d.Rows)
        {
            if (first || r.Group != group)
            {
                var g = t.AddRow();
                g.Cells[0].MergeRight = heads.Length - 1;
                g.Shading.Color = Colors.WhiteSmoke;
                Text(g.Cells[0], r.Group ?? "Без группы", bold: true);
                group = r.Group;
                first = false;
            }

            var row = t.AddRow();
            Text(row.Cells[0], r.Code);
            Text(row.Cells[1], r.Article ?? string.Empty);
            Text(row.Cells[2], r.Name);
            Text(row.Cells[3], r.Unit);
            Num(row.Cells[4], Money(r.Price));
            if (d.StockShown)
            {
                Num(row.Cells[5], r.Stock is > 0 ? Quantity(r.Stock.Value) : "—");
            }
        }

        if (d.Truncated)
        {
            Para(sec, $"Показаны первые {d.Rows.Count} позиций — уточните отбор.", bold: true);
        }

        return Render(doc);
    }

    private static (Document, Section) NewDocument(string title, bool landscape, double fontSize)
    {
        EnsureFonts();
        var doc = new Document();
        doc.Info.Title = title;
        doc.Info.Author = "knitERP";
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = Font;
        normal.Font.Size = fontSize;
        var sec = doc.AddSection();
        sec.PageSetup = doc.DefaultPageSetup.Clone();
        sec.PageSetup.PageFormat = PageFormat.A4;
        sec.PageSetup.Orientation = Orientation.Portrait;
        sec.PageSetup.PageWidth = Unit.FromMillimeter(landscape ? 297 : 210);
        sec.PageSetup.PageHeight = Unit.FromMillimeter(landscape ? 210 : 297);
        var margin = Unit.FromMillimeter(landscape ? 8 : 12);
        sec.PageSetup.LeftMargin = margin;
        sec.PageSetup.RightMargin = margin;
        sec.PageSetup.TopMargin = margin;
        sec.PageSetup.BottomMargin = margin;
        return (doc, sec);
    }

    private static byte[] Render(Document doc)
    {
        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms, false);
        return ms.ToArray();
    }

    private static Table Grid(Section sec, params double[] widthsMm)
    {
        var t = sec.AddTable();
        t.Borders.Width = 0.5;
        t.TopPadding = Unit.FromPoint(1.5);
        t.BottomPadding = Unit.FromPoint(1.5);
        t.LeftPadding = Unit.FromPoint(2);
        t.RightPadding = Unit.FromPoint(2);
        foreach (var w in widthsMm)
        {
            t.AddColumn(Unit.FromMillimeter(w));
        }

        return t;
    }

    private static void Logo(Section sec, SalesPrintImage? logo)
    {
        if (logo is null)
        {
            return;
        }

        var p = sec.AddParagraph();
        p.Format.SpaceAfter = Unit.FromMillimeter(3);
        var image = p.AddImage(Base64(logo.Content));
        image.Height = Unit.FromMillimeter(15);
        image.LockAspectRatio = true;
    }

    private static void Warning(Section sec, string text)
    {
        var p = sec.AddParagraph(text);
        p.Format.Font.Size = 14;
        p.Format.Font.Bold = true;
        p.Format.Font.Color = Colors.Red;
        p.Format.SpaceAfter = Unit.FromMillimeter(3);
    }

    private static void Title(Section sec, string text)
    {
        var p = sec.AddParagraph(text);
        p.Format.Font.Size = 13;
        p.Format.Font.Bold = true;
        p.Format.SpaceBefore = Unit.FromMillimeter(5);
        p.Format.SpaceAfter = Unit.FromMillimeter(2);
        p.Format.Borders.Bottom.Width = 1;
    }

    private static void Parties(Section sec, params (string Label, string Value)[] rows)
    {
        var t = Grid(sec, 38, 148);
        t.Borders.Visible = false;
        foreach (var (label, value) in rows)
        {
            var r = t.AddRow();
            Text(r.Cells[0], label);
            Text(r.Cells[1], value, bold: true);
        }
    }

    private static void Lines(Section sec, IReadOnlyList<PrintLineDto> lines, bool pricesIncludeVat)
    {
        sec.AddParagraph().Format.SpaceAfter = Unit.FromMillimeter(2);
        var t = Grid(sec, 8, 84, 20, 12, 22, 14, 26);
        var h = t.AddRow();
        h.HeadingFormat = true;
        h.Format.Font.Bold = true;
        string[] heads = ["№", "Товары (работы, услуги)", "Кол-во", "Ед.", "Цена", "НДС", "Сумма"];
        for (var i = 0; i < heads.Length; i++)
        {
            Text(h.Cells[i], heads[i]);
        }

        foreach (var l in lines)
        {
            var r = t.AddRow();
            Text(r.Cells[0], l.No.ToString(Ru));
            Text(r.Cells[1], l.Name);
            Num(r.Cells[2], Quantity(l.Quantity));
            Text(r.Cells[3], l.UnitSymbol);
            Num(r.Cells[4], Money(l.Price));
            Text(r.Cells[5], Vat(l.VatPercent));
            Num(r.Cells[6], Money(pricesIncludeVat ? l.Amount : l.AmountWithoutVat));
        }
    }

    private static void Totals(Section sec, params (string Label, string Value)[] rows)
    {
        var t = Grid(sec, 150, 36);
        t.Borders.Visible = false;
        foreach (var (label, value) in rows)
        {
            var r = t.AddRow();
            r.Format.Font.Bold = true;
            Num(r.Cells[0], label);
            Num(r.Cells[1], value);
        }
    }

    /// <summary>Подписи: должность — линия с факсимиле (если загружено и включено) — ФИО; печать — рядом.</summary>
    private static void Signatures(Section sec, PrintRequisites r, SalesPrintDecorations deco,
        IReadOnlyList<(string Role, string? Name, SalesPrintImage? Signature)> signers, bool stamp = true)
    {
        sec.AddParagraph().Format.SpaceAfter = Unit.FromMillimeter(4);
        var t = Grid(sec, 62, 40, 46, 38);
        t.Borders.Visible = false;
        for (var i = 0; i < signers.Count; i++)
        {
            var (role, name, signature) = signers[i];
            var row = t.AddRow();
            row.VerticalAlignment = VerticalAlignment.Bottom;
            row.Height = Unit.FromMillimeter(14);
            Text(row.Cells[0], role);
            var line = row.Cells[1].AddParagraph();
            line.Format.Borders.Bottom.Width = 0.5;
            if (signature is not null)
            {
                AddImage(line, signature, 12);
            }

            Text(row.Cells[2], name ?? string.Empty);
            if (i == 0 && stamp && deco.Stamp is { } image)
            {
                row.Cells[3].MergeDown = signers.Count - 1;
                AddImage(row.Cells[3].AddParagraph(), image, 34);
            }
        }

        _ = r;
    }

    private static void AddImage(Paragraph p, SalesPrintImage image, double heightMm)
    {
        var img = p.AddImage(Base64(image.Content));
        img.Height = Unit.FromMillimeter(heightMm);
        img.LockAspectRatio = true;
    }

    private static void Para(Section sec, string text, bool bold = false, double? size = null)
    {
        var p = sec.AddParagraph(text);
        p.Format.SpaceBefore = Unit.FromMillimeter(1.5);
        p.Format.Font.Bold = bold;
        if (size is { } s)
        {
            p.Format.Font.Size = s;
        }
    }

    private static void Text(Cell cell, string text, double? size = null, bool bold = false)
    {
        var p = cell.AddParagraph(text);
        if (size is { } s)
        {
            p.Format.Font.Size = s;
        }

        p.Format.Font.Bold = bold;
    }

    private static void Num(Cell cell, string text)
    {
        cell.AddParagraph(text).Format.Alignment = ParagraphAlignment.Right;
    }

    private static string Base64(byte[] content) => "base64:" + Convert.ToBase64String(content);

    private static string Date(DateOnly d) => d.ToString("d MMMM yyyy", Ru) + " г.";

    private static string Money(decimal v) => v.ToString("#,0.00", Ru);

    private static string Quantity(decimal v) => v.ToString("#,0.######", Ru);

    private static string Vat(decimal? percent) => percent is { } p ? p.ToString("0.##", Ru) + "%" : "без НДС";

    private static string Party(PrintPartyDto p) =>
        string.Join(", ", new[] { p.Name, p.Inn is null ? null : $"ИНН {p.Inn}", p.Kpp is null ? null : $"КПП {p.Kpp}", p.Address }.Where(x => x is not null));

    /// <summary>Шрифт подключается один раз на процесс: PDFsharp не даёт сменить FontResolver после первого использования.</summary>
    internal static void EnsureFonts()
    {
        if (_fontsReady)
        {
            return;
        }

        lock (FontGate)
        {
            if (!_fontsReady)
            {
                GlobalFontSettings.FontResolver ??= new EmbeddedFontResolver();
                _fontsReady = true;
            }
        }
    }

    /// <summary>Любое имя шрифта — DejaVu Sans (обычный или полужирный) из ресурсов сборки.</summary>
    private sealed class EmbeddedFontResolver : IFontResolver
    {
        private const string Regular = "DejaVuSans";
        private const string Bold = "DejaVuSans-Bold";

        public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
            new(isBold ? Bold : Regular, false, isItalic);

        public byte[] GetFont(string faceName)
        {
            var name = $"{typeof(SalesPdfRenderer).Assembly.GetName().Name}.Documents.Fonts.{faceName}.ttf";
            using var stream = typeof(SalesPdfRenderer).Assembly.GetManifestResourceStream(name)
                               ?? throw new InvalidOperationException($"Нет встроенного шрифта {faceName}.");
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
    }
}
