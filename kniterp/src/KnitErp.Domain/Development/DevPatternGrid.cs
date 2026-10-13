using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using KnitErp.Domain.Common;

namespace KnitErp.Domain.Development;

/// <summary>
/// Сетка рисунка: одна клетка файла — одна петля. Считает долю каждого цвета в раппорте по PNG, BMP или текстовой сетке (CSV/TXT).
/// Разбор без внешних библиотек: файл приходит от пользователя, поэтому размеры проверяются до выделения памяти.
/// </summary>
public static class DevPatternGrid
{
    public const int MaxSide = 2000;

    /// <summary>Предел размера текстовой сетки (CSV/TXT), байт.</summary>
    public const int MaxTextBytes = 16 * 1024 * 1024;
    public const int MaxColors = 16;

    /// <summary>Цвет сетки: ключ (#RRGGBB для картинки, обозначение клетки для текста), число клеток и доля в процентах.</summary>
    public sealed record DevGridColor(string Key, int Cells, decimal Percent);

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Похоже ли содержимое на безопасную текстовую сетку: UTF-8 (BOM допускается), не больше 2000 строк и 2000 клеток в строке,
    /// без &lt;, &gt;, NUL и управляющих символов, кроме перевода строки и табуляции.
    /// </summary>
    public static bool LooksLikeTextGrid(byte[] content)
    {
        var rows = TryParseText(content);
        return rows is not null && rows.Count <= MaxSide && rows.All(r => r.Count <= MaxSide);
    }

    /// <summary>
    /// Доли цветов сетки по убыванию числа клеток. Проценты округлены до сотых, остаток округления добавлен к самой большой доле —
    /// сумма ровно 100.
    /// </summary>
    public static IReadOnlyList<DevGridColor> Count(byte[] content, string contentType)
    {
        ArgumentNullException.ThrowIfNull(content);
        var counts = contentType switch
        {
            "image/png" => CountPixels(DecodePng(content)),
            "image/bmp" => CountPixels(DecodeBmp(content)),
            "text/csv" => CountText(content),
            _ => throw FormatError(),
        };
        return ToShares(counts);
    }

    private static List<DevGridColor> ToShares(Dictionary<string, int> counts)
    {
        long total = counts.Values.Sum(c => (long)c);
        if (total == 0)
        {
            throw SizeError();
        }

        var list = counts
            .OrderByDescending(p => p.Value)
            .ThenBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new DevGridColor(p.Key, p.Value, Math.Round(p.Value * 100m / total, 2, MidpointRounding.AwayFromZero)))
            .ToList();
        var remainder = 100m - list.Sum(c => c.Percent);
        if (remainder != 0)
        {
            list[0] = list[0] with { Percent = list[0].Percent + remainder };
        }

        return list;
    }

    // ---------- Текстовая сетка ----------

    private static Dictionary<string, int> CountText(byte[] content)
    {
        var rows = TryParseText(content) ?? throw FormatError();
        if (rows.Count == 0 || rows.Count > MaxSide || rows.Any(r => r.Count > MaxSide))
        {
            throw SizeError();
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var cell in rows.SelectMany(r => r))
        {
            AddColor(counts, cell);
        }

        return counts;
    }

    /// <summary>Строки сетки (только непустые) или null, если это не безопасный текст. Предел строк проверяется по ходу разбора.</summary>
    private static List<List<string>>? TryParseText(byte[]? content)
    {
        // Ревью 13.10: сетка 2000 × 2000 клеток в тексте — заведомо меньше 16 МБ; больше — не разбираем (иначе память на каждый запрос).
        if (content is null || content.Length > MaxTextBytes)
        {
            return null;
        }

        string text;
        try
        {
            var span = content.AsSpan();
            if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            {
                span = span[3..];
            }

            text = StrictUtf8.GetString(span);
        }
        catch (ArgumentException)
        {
            return null;
        }

        foreach (var ch in text)
        {
            if (ch is '<' or '>' || (char.IsControl(ch) && ch is not ('\r' or '\n' or '\t')))
            {
                return null;
            }
        }

        var rows = new List<List<string>>();
        for (var start = 0; start < text.Length;)
        {
            var end = text.IndexOf('\n', start);
            if (end < 0)
            {
                end = text.Length;
            }

            var line = text.AsSpan(start, end - start).TrimEnd('\r');
            start = end + 1;
            var cells = SplitRow(line);
            if (cells is null)
            {
                rows.Add(new List<string>(new string[MaxSide + 1])); // строка длиннее 2000 клеток — сетка заведомо велика
                break;
            }

            if (cells.Count == 0)
            {
                continue;
            }

            rows.Add(cells);
            if (rows.Count > MaxSide)
            {
                // Дальше разбирать незачем — сетка уже слишком велика.
                break;
            }
        }

        return rows;
    }

    /// <summary>
    /// Клетки строки: разделители , ; табуляция и пробелы; без разделителей каждый непробельный символ — клетка. Больше 2000 клеток — null
    /// (разбор останавливается сразу, без списка на всю строку).
    /// </summary>
    private static List<string>? SplitRow(ReadOnlySpan<char> line)
    {
        var cells = new List<string>();
        if (line.IndexOfAny(",;\t ") >= 0 && !line.IsWhiteSpace())
        {
            foreach (var range in line.SplitAny(",;\t "))
            {
                var cell = line[range].Trim();
                if (cell.IsEmpty)
                {
                    continue;
                }

                if (cells.Count == MaxSide)
                {
                    return null;
                }

                cells.Add(cell.ToString());
            }

            return cells;
        }

        foreach (var rune in line.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                continue;
            }

            if (cells.Count == MaxSide)
            {
                return null;
            }

            cells.Add(rune.ToString());
        }

        return cells;
    }

    // ---------- Картинки ----------

    /// <summary>Растр в виде RGB по три байта на пиксель, строки сверху вниз.</summary>
    private sealed record Raster(int Width, int Height, byte[] Rgb);

    private static Dictionary<string, int> CountPixels(Raster raster)
    {
        var byColor = new Dictionary<int, int>();
        var rgb = raster.Rgb;
        for (var i = 0; i < rgb.Length; i += 3)
        {
            var key = (rgb[i] << 16) | (rgb[i + 1] << 8) | rgb[i + 2];
            if (byColor.TryGetValue(key, out var n))
            {
                byColor[key] = n + 1;
            }
            else
            {
                if (byColor.Count == MaxColors)
                {
                    throw ColorsError();
                }

                byColor[key] = 1;
            }
        }

        return byColor.ToDictionary(p => "#" + p.Key.ToString("X6"), p => p.Value, StringComparer.Ordinal);
    }

    private static void AddColor(Dictionary<string, int> counts, string key)
    {
        if (counts.TryGetValue(key, out var n))
        {
            counts[key] = n + 1;
            return;
        }

        if (counts.Count == MaxColors)
        {
            throw ColorsError();
        }

        counts[key] = 1;
    }

    private static void EnsureSize(long width, long height)
    {
        if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide)
        {
            throw SizeError();
        }
    }

    /// <summary>PNG 8 бит на канал без чересстрочной развёртки: серый, RGB, палитра, серый с альфой, RGBA. Альфа не учитывается.</summary>
    private static Raster DecodePng(byte[] data)
    {
        if (data.Length < PngSignature.Length + 25 || !data.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
        {
            throw FormatError();
        }

        var pos = PngSignature.Length;
        int width = 0, height = 0, colorType = -1;
        byte[]? palette = null;
        using var idat = new MemoryStream();
        var first = true;
        var ended = false;
        while (pos + 12 <= data.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            var type = Encoding.ASCII.GetString(data, pos + 4, 4);
            if (length > (uint)(data.Length - pos - 12))
            {
                throw FormatError();
            }

            var body = data.AsSpan(pos + 8, (int)length);
            pos += 12 + (int)length;
            if (first)
            {
                if (type != "IHDR" || length != 13)
                {
                    throw FormatError();
                }

                first = false;
                var w = BinaryPrimitives.ReadUInt32BigEndian(body);
                var h = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                // Допускается только 8 бит, стандартное сжатие и фильтры, без чересстрочной развёртки.
                if (body[8] != 8 || body[10] != 0 || body[11] != 0 || body[12] != 0 || body[9] is not (0 or 2 or 3 or 4 or 6))
                {
                    throw FormatError();
                }

                EnsureSize(w, h);
                width = (int)w;
                height = (int)h;
                colorType = body[9];
                continue;
            }

            if (type == "PLTE")
            {
                if (length == 0 || length % 3 != 0 || length > 256 * 3)
                {
                    throw FormatError();
                }

                palette = body.ToArray();
            }
            else if (type == "IDAT")
            {
                idat.Write(body);
            }
            else if (type == "IEND")
            {
                ended = true;
                break;
            }
        }

        if (!ended || idat.Length == 0 || (colorType == 3 && palette is null))
        {
            throw FormatError();
        }

        var bpp = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        var stride = width * bpp;
        var raw = new byte[(long)height * (stride + 1)];
        try
        {
            idat.Position = 0;
            using var z = new ZLibStream(idat, CompressionMode.Decompress);
            z.ReadExactly(raw);
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
        {
            throw FormatError();
        }

        var rgb = new byte[width * height * 3];
        var prev = new byte[stride];
        var cur = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * (stride + 1);
            Unfilter(raw[rowStart], raw.AsSpan(rowStart + 1, stride), prev, cur, bpp);
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 3;
                var p = x * bpp;
                switch (colorType)
                {
                    case 0:
                    case 4:
                        rgb[o] = rgb[o + 1] = rgb[o + 2] = cur[p];
                        break;
                    case 3:
                        var idx = cur[p] * 3;
                        if (idx + 2 >= palette!.Length)
                        {
                            throw FormatError();
                        }

                        rgb[o] = palette[idx];
                        rgb[o + 1] = palette[idx + 1];
                        rgb[o + 2] = palette[idx + 2];
                        break;
                    default:
                        rgb[o] = cur[p];
                        rgb[o + 1] = cur[p + 1];
                        rgb[o + 2] = cur[p + 2];
                        break;
                }
            }

            (prev, cur) = (cur, prev);
        }

        return new Raster(width, height, rgb);
    }

    /// <summary>Снимает фильтр строки PNG: 0 None, 1 Sub, 2 Up, 3 Average, 4 Paeth.</summary>
    private static void Unfilter(byte filter, ReadOnlySpan<byte> src, byte[] prev, byte[] cur, int bpp)
    {
        for (var i = 0; i < src.Length; i++)
        {
            int a = i >= bpp ? cur[i - bpp] : 0;
            int b = prev[i];
            int c = i >= bpp ? prev[i - bpp] : 0;
            var predictor = filter switch
            {
                0 => 0,
                1 => a,
                2 => b,
                3 => (a + b) >> 1,
                4 => Paeth(a, b, c),
                _ => throw FormatError(),
            };
            cur[i] = (byte)(src[i] + predictor);
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>BMP с заголовком BITMAPINFOHEADER (и его расширениями), без сжатия, 24 или 32 бита, снизу вверх или сверху вниз.</summary>
    private static Raster DecodeBmp(byte[] data)
    {
        if (data.Length < 54 || data[0] != (byte)'B' || data[1] != (byte)'M')
        {
            throw FormatError();
        }

        var span = data.AsSpan();
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(span[10..]);
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(span[14..]);
        var w = BinaryPrimitives.ReadInt32LittleEndian(span[18..]);
        var h = BinaryPrimitives.ReadInt32LittleEndian(span[22..]);
        var planes = BinaryPrimitives.ReadUInt16LittleEndian(span[26..]);
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(span[28..]);
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(span[30..]);
        if (headerSize < 40 || planes != 1 || bits is not (24 or 32) || compression != 0)
        {
            throw FormatError();
        }

        var topDown = h < 0;
        long height = Math.Abs((long)h);
        EnsureSize(w, height);
        var width = w;
        var bytesPerPixel = bits / 8;
        var stride = (bits * width + 31) / 32 * 4;
        if (offset < 14 + headerSize || offset + (long)stride * height > data.Length)
        {
            throw FormatError();
        }

        var rgb = new byte[width * (int)height * 3];
        for (var y = 0; y < height; y++)
        {
            var srcRow = (int)offset + (int)(topDown ? y : height - 1 - y) * stride;
            for (var x = 0; x < width; x++)
            {
                var s = srcRow + x * bytesPerPixel;
                var o = (y * width + x) * 3;
                rgb[o] = data[s + 2];
                rgb[o + 1] = data[s + 1];
                rgb[o + 2] = data[s];
            }
        }

        return new Raster(width, (int)height, rgb);
    }

    // ---------- Ошибки ----------

    private static BusinessRuleException FormatError() =>
        new BusinessRuleException("dev.pattern.grid.format",
            "Не удалось прочитать сетку рисунка. Сохраните сетку как PNG 8 бит без чересстрочной развёртки, BMP 24 или 32 бит либо CSV.");

    private static BusinessRuleException ColorsError() =>
        new BusinessRuleException("dev.pattern.grid.colors",
            "В рисунке больше 16 цветов — это не сетка рисунка, похоже на фото. Загрузите сетку, где одна клетка — одна петля.");

    private static BusinessRuleException SizeError() =>
        new BusinessRuleException("dev.pattern.grid.size", "Сетка рисунка пустая или больше 2000×2000 клеток.");
}
