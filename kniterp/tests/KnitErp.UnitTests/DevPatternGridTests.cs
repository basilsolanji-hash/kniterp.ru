using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using KnitErp.Domain.Common;
using KnitErp.Domain.Development;

namespace KnitErp.UnitTests;

/// <summary>Сетка рисунка: доли цветов по PNG, BMP и CSV, отказ для фото и битых файлов, проверка текстовой сетки.</summary>
public sealed class DevPatternGridTests
{
    private static readonly byte[] Red = [0xFF, 0x00, 0x00];
    private static readonly byte[] White = [0xFF, 0xFF, 0xFF];
    private static readonly byte[] Navy = [0x10, 0x20, 0x80];

    [Fact]
    public void Rgb_png_counts_colors_by_share_descending_and_sums_to_100()
    {
        // 3×3: 5 белых, 3 красных, 1 синий.
        var pixels = Grid(3, 3, White, Red, White, Red, White, Navy, White, Red, White);
        var result = DevPatternGrid.Count(Png(3, 3, colorType: 2, pixels, filters: [0]), "image/png");

        Assert.Equal(["#FFFFFF", "#FF0000", "#102080"], result.Select(c => c.Key));
        Assert.Equal([5, 3, 1], result.Select(c => c.Cells));
        Assert.Equal(100m, result.Sum(c => c.Percent));
        // 55.56 + 33.33 + 11.11 = 100.00 — остаток не нужен; проверяем округление до сотых.
        Assert.Equal(55.56m, result[0].Percent);
        Assert.Equal(33.33m, result[1].Percent);
    }

    [Fact]
    public void Rounding_remainder_goes_to_largest_share()
    {
        // Три цвета по одной клетке: 33.33 × 3 = 99.99, остаток 0.01 — первому.
        var result = DevPatternGrid.Count(Png(3, 1, 2, Grid(3, 1, Red, White, Navy), [0]), "image/png");
        Assert.Equal(100m, result.Sum(c => c.Percent));
        Assert.Equal(33.34m, result[0].Percent);
        Assert.All(result.Skip(1), c => Assert.Equal(33.33m, c.Percent));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Each_png_filter_decodes_the_same_picture(byte filter)
    {
        var pixels = Gradient(5, 4);
        var expected = DevPatternGrid.Count(Png(5, 4, 2, pixels, [0]), "image/png");
        var actual = DevPatternGrid.Count(Png(5, 4, 2, pixels, [filter]), "image/png");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Rgba_png_with_mixed_filters_ignores_alpha()
    {
        var rgb = Gradient(4, 5);
        var rgba = new byte[4 * 5 * 4];
        for (var i = 0; i < 4 * 5; i++)
        {
            Array.Copy(rgb, i * 3, rgba, i * 4, 3);
            rgba[i * 4 + 3] = (byte)(i * 13);
        }

        var expected = DevPatternGrid.Count(Png(4, 5, 2, rgb, [0]), "image/png");
        var actual = DevPatternGrid.Count(Png(4, 5, 6, rgba, [0, 1, 2, 3, 4]), "image/png");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Palette_png_maps_indexes_to_colors()
    {
        byte[] palette = [.. Red, .. White, .. Navy];
        byte[] indexes = [1, 1, 1, 0, 0, 2, 1, 1];
        var result = DevPatternGrid.Count(Png(4, 2, 3, indexes, [0, 4], palette), "image/png");

        Assert.Equal(["#FFFFFF", "#FF0000", "#102080"], result.Select(c => c.Key));
        Assert.Equal([5, 2, 1], result.Select(c => c.Cells));
        Assert.Equal(62.5m, result[0].Percent);
    }

    [Fact]
    public void Gray_png_gives_gray_keys()
    {
        byte[] gray = [0x00, 0x80, 0x80, 0x80];
        var result = DevPatternGrid.Count(Png(2, 2, 0, gray, [3]), "image/png");
        Assert.Equal(["#808080", "#000000"], result.Select(c => c.Key));
    }

    [Fact]
    public void Interlaced_or_16_bit_png_is_refused()
    {
        var png = Png(2, 1, 2, Grid(2, 1, Red, White), [0]);
        var interlaced = (byte[])png.Clone();
        interlaced[8 + 8 + 12] = 1; // байт interlace в IHDR
        var e = Assert.Throws<BusinessRuleException>(() => DevPatternGrid.Count(interlaced, "image/png"));
        Assert.Equal("dev.pattern.grid.format", e.Code);
        Assert.Contains("PNG 8 бит", e.Message);

        var deep = (byte[])png.Clone();
        deep[8 + 8 + 8] = 16; // глубина цвета
        Assert.Equal("dev.pattern.grid.format", Assert.Throws<BusinessRuleException>(() => DevPatternGrid.Count(deep, "image/png")).Code);
    }

    [Fact]
    public void More_than_16_colors_looks_like_photo()
    {
        var pixels = new byte[17 * 3];
        for (var i = 0; i < 17; i++)
        {
            pixels[i * 3] = (byte)(i * 10);
        }

        var e = Assert.Throws<BusinessRuleException>(() => DevPatternGrid.Count(Png(17, 1, 2, pixels, [0]), "image/png"));
        Assert.Equal("dev.pattern.grid.colors", e.Code);
        Assert.Contains("фото", e.Message);

        var csv = Encoding.UTF8.GetBytes(string.Join(",", Enumerable.Range(0, 17)));
        Assert.Equal("dev.pattern.grid.colors", Assert.Throws<BusinessRuleException>(() => DevPatternGrid.Count(csv, "text/csv")).Code);
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/bmp")]
    [InlineData("text/csv")]
    [InlineData("image/jpeg")]
    public void Broken_file_is_refused_with_format_code(string contentType)
    {
        byte[] junk = [0x89, 0x50, 0x00, 0x01, 0x3C, 0xFF, 0xFE, 0x00, 0x42, 0x4D];
        var e = Assert.Throws<BusinessRuleException>(() => DevPatternGrid.Count(junk, contentType));
        Assert.Equal("dev.pattern.grid.format", e.Code);
    }

    [Fact]
    public void Truncated_png_data_is_refused()
    {
        var png = Png(3, 3, 2, Gradient(3, 3), [0]);
        // Отрезаем IDAT посередине и дописываем IEND — данных меньше, чем строк.
        var cut = Png(3, 3, 2, Gradient(3, 3), [0], truncateIdat: true);
        Assert.NotEqual(png.Length, cut.Length);
        Assert.Equal("dev.pattern.grid.format", Assert.Throws<BusinessRuleException>(() => DevPatternGrid.Count(cut, "image/png")).Code);
    }

    [Fact]
    public void Oversized_png_is_refused_with_size_code()
    {
        var e = Assert.Throws<BusinessRuleException>(() => DevPatternGrid.Count(Png(2001, 1, 0, new byte[2001], [0]), "image/png"));
        Assert.Equal("dev.pattern.grid.size", e.Code);
    }

    [Theory]
    [InlineData(24, false)]
    [InlineData(24, true)]
    [InlineData(32, false)]
    public void Bmp_counts_colors(int bits, bool topDown)
    {
        // 3×2: верхняя строка красная, нижняя — белый, белый, синий.
        var bmp = Bmp(3, 2, bits, topDown, Grid(3, 2, Red, Red, Red, White, White, Navy));
        var result = DevPatternGrid.Count(bmp, "image/bmp");
        Assert.Equal(["#FF0000", "#FFFFFF", "#102080"], result.Select(c => c.Key));
        Assert.Equal([3, 2, 1], result.Select(c => c.Cells));
        Assert.Equal(100m, result.Sum(c => c.Percent));
    }

    [Fact]
    public void Compressed_bmp_is_refused()
    {
        var bmp = Bmp(2, 1, 24, false, Grid(2, 1, Red, White));
        bmp[30] = 1; // BI_RLE8
        Assert.Equal("dev.pattern.grid.format", Assert.Throws<BusinessRuleException>(() => DevPatternGrid.Count(bmp, "image/bmp")).Code);
    }

    [Fact]
    public void Csv_with_separators_counts_tokens()
    {
        var csv = Encoding.UTF8.GetBytes("﻿A,B;A\r\nA\tC  A\r\n\r\nB,A,A\n");
        var result = DevPatternGrid.Count(csv, "text/csv");
        Assert.Equal(["A", "B", "C"], result.Select(c => c.Key));
        Assert.Equal([6, 2, 1], result.Select(c => c.Cells));
        Assert.Equal(100m, result.Sum(c => c.Percent));
    }

    [Fact]
    public void Csv_without_separators_takes_each_character_as_cell()
    {
        var csv = Encoding.UTF8.GetBytes("ХХО\nХОО\nХХХ");
        var result = DevPatternGrid.Count(csv, "text/csv");
        Assert.Equal(["Х", "О"], result.Select(c => c.Key));
        Assert.Equal([6, 3], result.Select(c => c.Cells));
        Assert.Equal(66.67m, result[0].Percent);
        Assert.Equal(33.33m, result[1].Percent);
    }

    [Fact]
    public void Empty_csv_is_refused_with_size_code()
    {
        var e = Assert.Throws<BusinessRuleException>(() => DevPatternGrid.Count(Encoding.UTF8.GetBytes(" \r\n\n"), "text/csv"));
        Assert.Equal("dev.pattern.grid.size", e.Code);
    }

    [Fact]
    public void Text_grid_check_accepts_plain_grid()
    {
        Assert.True(DevPatternGrid.LooksLikeTextGrid(Encoding.UTF8.GetBytes("1,2,1\n2;2;1\n")));
        Assert.True(DevPatternGrid.LooksLikeTextGrid([0xEF, 0xBB, 0xBF, (byte)'x', (byte)'o']));
    }

    [Fact]
    public void Text_grid_check_rejects_markup_control_chars_bad_utf8_and_huge_grids()
    {
        Assert.False(DevPatternGrid.LooksLikeTextGrid(Encoding.UTF8.GetBytes("<script>alert(1)</script>")));
        Assert.False(DevPatternGrid.LooksLikeTextGrid(Encoding.UTF8.GetBytes("a,b\0c")));
        Assert.False(DevPatternGrid.LooksLikeTextGrid(Encoding.UTF8.GetBytes("a\u001bb")));
        Assert.False(DevPatternGrid.LooksLikeTextGrid([0x41, 0xC3, 0x28]));
        Assert.False(DevPatternGrid.LooksLikeTextGrid(Encoding.UTF8.GetBytes(new string('x', 2001))));
        Assert.False(DevPatternGrid.LooksLikeTextGrid(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("x\n", 2001)))));
        Assert.True(DevPatternGrid.LooksLikeTextGrid(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("x\n", 2000)))));
    }

    // ---------- Сборка тестовых файлов ----------

    private static byte[] Grid(int width, int height, params byte[][] colors)
    {
        Assert.Equal(width * height, colors.Length);
        return colors.SelectMany(c => c).ToArray();
    }

    /// <summary>Картинка из четырёх цветов с разными каналами — чтобы фильтры давали разные предсказания.</summary>
    private static byte[] Gradient(int width, int height)
    {
        byte[][] colors = [[0x12, 0x34, 0x56], [0xFE, 0x01, 0x80], [0x00, 0xFF, 0x7F], [0x9A, 0x9A, 0x10]];
        var result = new List<byte>();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                result.AddRange(colors[(x * 3 + y * 2 + x * y) % colors.Length]);
            }
        }

        return result.ToArray();
    }

    /// <summary>Минимальный кодировщик PNG 8 бит: строки фильтруются по очереди фильтрами из <paramref name="filters"/>.</summary>
    private static byte[] Png(int width, int height, byte colorType, byte[] samples, byte[] filters, byte[]? palette = null, bool truncateIdat = false)
    {
        var bpp = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        var stride = width * bpp;
        var raw = new MemoryStream();
        var prev = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var row = samples.AsSpan(y * stride, stride).ToArray();
            var filter = filters[y % filters.Length];
            raw.WriteByte(filter);
            for (var i = 0; i < stride; i++)
            {
                int a = i >= bpp ? row[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                var pred = filter switch
                {
                    1 => a,
                    2 => b,
                    3 => (a + b) >> 1,
                    4 => Paeth(a, b, c),
                    _ => 0,
                };
                raw.WriteByte((byte)(row[i] - pred));
            }

            prev = row;
        }

        var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(raw.ToArray());
        }

        var idat = packed.ToArray();
        if (truncateIdat)
        {
            idat = idat[..(idat.Length / 2)];
        }

        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;
        ihdr[9] = colorType;
        Chunk(png, "IHDR", ihdr);
        if (palette is not null)
        {
            Chunk(png, "PLTE", palette);
        }

        // Данные делятся на два IDAT — декодер обязан их склеить.
        Chunk(png, "IDAT", idat[..(idat.Length / 2)]);
        Chunk(png, "IDAT", idat[(idat.Length / 2)..]);
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void Chunk(Stream s, string type, byte[] body)
    {
        var len = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, body.Length);
        s.Write(len);
        s.Write(Encoding.ASCII.GetBytes(type));
        s.Write(body);
        s.Write(new byte[4]); // CRC декодер не проверяет
    }

    /// <summary>BMP с BITMAPINFOHEADER без сжатия; пиксели передаются сверху вниз в RGB.</summary>
    private static byte[] Bmp(int width, int height, int bits, bool topDown, byte[] rgb)
    {
        var bytesPerPixel = bits / 8;
        var stride = (bits * width + 31) / 32 * 4;
        var data = new byte[54 + stride * height];
        data[0] = (byte)'B';
        data[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(2), data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(22), topDown ? -height : height);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(28), (short)bits);
        for (var y = 0; y < height; y++)
        {
            var row = 54 + (topDown ? y : height - 1 - y) * stride;
            for (var x = 0; x < width; x++)
            {
                var src = (y * width + x) * 3;
                var dst = row + x * bytesPerPixel;
                data[dst] = rgb[src + 2];
                data[dst + 1] = rgb[src + 1];
                data[dst + 2] = rgb[src];
                if (bytesPerPixel == 4)
                {
                    data[dst + 3] = 0xFF;
                }
            }
        }

        return data;
    }
}
