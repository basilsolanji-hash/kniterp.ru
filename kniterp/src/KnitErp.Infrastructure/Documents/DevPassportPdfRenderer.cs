using System.Globalization;
using KnitErp.Application.Development;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;

namespace KnitErp.Infrastructure.Documents;

/// <summary>
/// PDF технического паспорта изделия (D725, ТЗ §9) — PDFsharp/MigraDoc со встроенным шрифтом DejaVu Sans, как счёт и УПД (D1123).
/// Разделы: общие сведения и эскиз, палитра и пряжа, детали с заправкой нитеводителей, табель мер, обработка и ВТО, маркировка и уход,
/// упаковка. Пустые разделы не печатаются. Форма — по-русски: это документ фабрики для цеха и заказчика.
/// </summary>
public sealed class DevPassportPdfRenderer : IDevPassportRenderer
{
    private const string Font = "DejaVu Sans";
    private const double PageWidthMm = 186; // A4 210 мм − поля 12 мм
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public byte[] Render(DevPassportDto p)
    {
        SalesPdfRenderer.EnsureFonts();
        var doc = new Document();
        doc.Info.Title = $"Технический паспорт {p.Number}";
        doc.Info.Author = "knitERP";
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = Font;
        normal.Font.Size = 9;
        var sec = doc.AddSection();
        sec.PageSetup = doc.DefaultPageSetup.Clone();
        sec.PageSetup.PageFormat = PageFormat.A4;
        sec.PageSetup.PageWidth = Unit.FromMillimeter(210);
        sec.PageSetup.PageHeight = Unit.FromMillimeter(297);
        foreach (var side in new Action<Unit>[] { u => sec.PageSetup.LeftMargin = u, u => sec.PageSetup.RightMargin = u, u => sec.PageSetup.TopMargin = u })
        {
            side(Unit.FromMillimeter(12));
        }

        sec.PageSetup.BottomMargin = Unit.FromMillimeter(15);
        var footer = sec.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = 7;
        footer.Format.Font.Color = Colors.Gray;
        footer.AddText($"{p.Organization} · {p.Number} «{p.Name}» · напечатано {p.PrintedOn.ToString("dd.MM.yyyy", Ru)} · стр. ");
        footer.AddPageField();
        footer.AddText(" из ");
        footer.AddNumPagesField();

        var title = sec.AddParagraph("Технический паспорт изделия");
        title.Format.Font.Size = 14;
        title.Format.Font.Bold = true;
        var sub = sec.AddParagraph($"Модель {p.Number} «{p.Name}»" + (p.VersionNo > 1 ? $", версия {p.VersionNo}" : "") + $" · {p.Stage}");
        sub.Format.Font.Size = 11;
        sub.Format.SpaceAfter = Unit.FromMillimeter(3);

        // Общие сведения слева, эскиз справа.
        var head = sec.AddTable();
        head.AddColumn(Unit.FromMillimeter(p.Sketch is null ? PageWidthMm : 116));
        if (p.Sketch is not null)
        {
            head.AddColumn(Unit.FromMillimeter(PageWidthMm - 116));
        }

        var hr = head.AddRow();
        var facts = new List<(string, string?)>
        {
            ("Категория", p.Category), ("Назначение", p.Audience), ("Детское изделие", p.ChildSafety ? "да — требования безопасности детской одежды" : null),
            ("Коллекция", p.Collection), ("Заказчик", p.Customer), ("Размерный ряд", p.SizeRange), ("Цветовая гамма", p.ColorRange),
            ("Описание", p.Description),
        };
        foreach (var (label, value) in facts.Where(f => !string.IsNullOrWhiteSpace(f.Item2)))
        {
            var para = hr.Cells[0].AddParagraph();
            para.Format.SpaceAfter = Unit.FromPoint(2);
            para.AddFormattedText(label + ": ", TextFormat.Bold);
            para.AddText(value!);
        }

        if (p.Sketch is { } sketch)
        {
            // Вписываем в 66 × 90 мм с сохранением пропорций.
            var img = hr.Cells[1].AddParagraph().AddImage("base64:" + Convert.ToBase64String(sketch));
            img.LockAspectRatio = true;
            const double maxW = PageWidthMm - 120, maxH = 90;
            var (w, h) = ImageSize(sketch);
            if (w > 0 && h > 0 && (double)h / w > maxH / maxW)
            {
                img.Height = Unit.FromMillimeter(maxH);
            }
            else
            {
                img.Width = Unit.FromMillimeter(maxW);
            }
        }

        if (p.Palette.Count > 0)
        {
            Heading(sec, "Палитра и пряжа");
            var t = Grid(sec, 40, 90, 56);
            Header(t, "Цвет", "Пряжа", "Примечание");
            foreach (var c in p.Palette)
            {
                Row(t, c.Label, c.ItemName ?? "—", c.Note ?? "");
            }
        }

        if (p.Details.Count > 0)
        {
            Heading(sec, "Пряжа и вязка: детали");
            var t = Grid(sec, 38, 52, 32, 30, 34);
            Header(t, "Деталь", "Станок", "Переплетение", "План", "Программа");
            foreach (var d in p.Details)
            {
                Row(t, d.Quantity > 1 ? $"{d.Name} × {d.Quantity}" : d.Name,
                    d.Machine is null ? "—" : d.Machine + (d.MachineParams is null ? "" : "\n" + d.MachineParams), d.Weave ?? "—",
                    string.Join("\n", new[] { Duration(d.PlanKnitSeconds), d.PlanWeightGrams is { } g ? g.ToString("0.##", Ru) + " г" : null }.OfType<string>()),
                    d.Program ?? "—");
            }

            foreach (var d in p.Details.Where(d => d.Feeders.Count > 0 || d.Notes is not null))
            {
                var h = sec.AddParagraph($"Заправка: {d.Name}");
                h.Format.Font.Bold = true;
                h.Format.SpaceBefore = Unit.FromMillimeter(3);
                h.Format.KeepWithNext = true;
                if (d.Notes is not null)
                {
                    sec.AddParagraph(d.Notes).Format.Font.Size = 8;
                }

                if (d.Feeders.Count > 0)
                {
                    var f = Grid(sec, 28, 79, 79);
                    Header(f, "Нитеводитель", "Слева", "Справа");
                    foreach (var r in d.Feeders)
                    {
                        Row(f, r.Feeder.ToString(Ru), Slot(r.Left), Slot(r.Right));
                    }
                }
            }
        }

        if (p.Measures.Count > 0 && p.Sizes.Count > 0)
        {
            Heading(sec, "Табель мер, см");
            var sizeWidth = Math.Max(10, (PageWidthMm - 56 - 12) / p.Sizes.Count);
            var widths = new List<double> { 56 };
            widths.AddRange(p.Sizes.Select(_ => sizeWidth));
            widths.Add(12);
            var t = Grid(sec, [.. widths]);
            Header(t, [.. new[] { "Точка измерения" }.Concat(p.Sizes.Select(s => s.IsBase ? s.Name + "*" : s.Name)).Append("±")]);
            foreach (var m in p.Measures)
            {
                Row(t, [.. new[] { m.Name }.Concat(p.Sizes.Select(s => m.Values.TryGetValue(s.Id, out var v) ? v.ToString("0.#", Ru) : "—"))
                    .Append(m.Tolerance.ToString("0.#", Ru))]);
            }

            if (p.Sizes.Any(s => s.IsBase))
            {
                sec.AddParagraph("* базовый размер").Format.Font.Size = 7;
            }
        }

        if (p.Operations.Count > 0)
        {
            Heading(sec, "Обработка и ВТО");
            var t = Grid(sec, 10, 70, 80, 26);
            Header(t, "№", "Операция", "Примечание", "Норма, мин");
            foreach (var o in p.Operations)
            {
                Row(t, o.Seq.ToString(Ru), o.Name, o.Note ?? "", o.NormMinutes is { } n ? n.ToString("0.##", Ru) : "—");
            }
        }

        var marking = new[] { ("Состав", p.Composition), ("Маркировка", p.Labeling), ("Уход", p.Care) }.Where(x => !string.IsNullOrWhiteSpace(x.Item2)).ToList();
        if (marking.Count > 0)
        {
            Heading(sec, "Маркировка и уход");
            foreach (var (label, value) in marking)
            {
                var para = sec.AddParagraph();
                para.AddFormattedText(label + ": ", TextFormat.Bold);
                para.AddText(value!);
            }
        }

        var perBox = p.Sizes.Where(s => s.PerBox is not null).ToList();
        if (p.PackBox is not null || perBox.Count > 0)
        {
            Heading(sec, "Упаковка");
            if (p.PackBox is not null)
            {
                var para = sec.AddParagraph();
                para.AddFormattedText("Короб: ", TextFormat.Bold);
                para.AddText(p.PackBox);
            }

            if (perBox.Count > 0)
            {
                sec.AddParagraph("В коробе, шт: " + string.Join("; ", perBox.Select(s => $"{s.Name} — {s.PerBox}")));
            }
        }

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms, false);
        return ms.ToArray();
    }

    /// <summary>Ширина и высота PNG (IHDR) или JPEG (маркер SOF); не распознано — (0, 0).</summary>
    internal static (int Width, int Height) ImageSize(byte[] b)
    {
        if (b.Length > 24 && b[0] == 0x89 && b[1] == 0x50)
        {
            return (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(16)), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(20)));
        }

        if (b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8)
        {
            var i = 2;
            while (i + 9 < b.Length)
            {
                if (b[i] != 0xFF)
                {
                    i++;
                    continue;
                }

                var marker = b[i + 1];
                var len = (b[i + 2] << 8) | b[i + 3];
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                {
                    return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
                }

                i += 2 + len;
            }
        }

        return (0, 0);
    }

    private static string Slot(DevThreadSlotDto? s)
    {
        if (s is null)
        {
            return "";
        }

        var parts = new List<string> { s.Label ?? s.Yarn };
        if (s.ItemName is not null && s.ItemName != (s.Label ?? s.Yarn))
        {
            parts.Add(s.ItemName);
        }

        parts.Add($"нитей {s.Ends}");
        if (s.Mode is not null)
        {
            parts.Add($"режим {s.Mode}");
        }

        if (s.Paraffin)
        {
            parts.Add("парафин");
        }

        if (s.Elastic)
        {
            parts.Add("эластан");
        }

        return string.Join(", ", parts);
    }

    private static string? Duration(int? seconds) => seconds is { } s ? $"{s / 60}:{s % 60:00} мин" : null;

    private static void Heading(Section sec, string text)
    {
        var h = sec.AddParagraph(text);
        h.Format.Font.Size = 11;
        h.Format.Font.Bold = true;
        h.Format.SpaceBefore = Unit.FromMillimeter(5);
        h.Format.SpaceAfter = Unit.FromMillimeter(1.5);
        h.Format.KeepWithNext = true;
    }

    private static Table Grid(Section sec, params double[] widthsMm)
    {
        var t = sec.AddTable();
        t.Borders.Width = 0.5;
        t.Borders.Color = Colors.Gray;
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

    private static void Header(Table t, params string[] cells)
    {
        var r = t.AddRow();
        r.HeadingFormat = true;
        r.Shading.Color = Colors.WhiteSmoke;
        r.Format.Font.Bold = true;
        for (var i = 0; i < cells.Length && i < t.Columns.Count; i++)
        {
            r.Cells[i].AddParagraph(cells[i]);
        }
    }

    private static void Row(Table t, params string[] cells)
    {
        var r = t.AddRow();
        for (var i = 0; i < cells.Length && i < t.Columns.Count; i++)
        {
            r.Cells[i].AddParagraph(cells[i]);
        }
    }
}
