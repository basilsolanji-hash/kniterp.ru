using KnitErp.Infrastructure.Documents;
using SkiaSharp;

namespace KnitErp.IntegrationTests;

/// <summary>D729: фото уменьшается до предела по длинной стороне, поворот и отражение по EXIF применяются к пикселям.</summary>
public sealed class ImageShrinkerTests
{
    /// <summary>JPEG ширина × высота: левая половина красная, правая синяя; при <paramref name="orientation"/> — с меткой EXIF Orientation.</summary>
    private static byte[] Jpeg(int width, int height, ushort? orientation = null)
    {
        using var bmp = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Blue);
            using var red = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(0, 0, width / 2f, height, red);
        }

        using var img = SKImage.FromBitmap(bmp);
        var jpeg = img.Encode(SKEncodedImageFormat.Jpeg, 95).ToArray();
        if (orientation is not { } o)
        {
            return jpeg;
        }

        // APP1 «Exif»: заголовок TIFF (big-endian), одна запись IFD — тег 0x0112 (Orientation), SHORT, значение o.
        byte[] tiff =
        [
            0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08,
            0x00, 0x01,
            0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, (byte)(o >> 8), (byte)o, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        ];
        var payload = new List<byte>("Exif\0\0"u8.ToArray());
        payload.AddRange(tiff);
        var length = payload.Count + 2;
        var app1 = new List<byte> { 0xFF, 0xE1, (byte)(length >> 8), (byte)length };
        app1.AddRange(payload);
        return [.. jpeg[..2], .. app1, .. jpeg[2..]];
    }

    private static SKColor Pixel(byte[] content, int x, int y)
    {
        using var bmp = SKBitmap.Decode(content);
        return bmp.GetPixel(x, y);
    }

    private static bool IsRed(SKColor c) => c.Red > 200 && c.Blue < 60;

    private static bool IsBlue(SKColor c) => c.Blue > 200 && c.Red < 60;

    [Fact]
    public void Large_photo_is_scaled_to_the_limit_and_small_one_is_left_as_is()
    {
        var shrinker = new SkiaImageShrinker();
        var result = shrinker.Shrink(Jpeg(3000, 1000), "image/jpeg", 2000)!;
        using (var bmp = SKBitmap.Decode(result))
        {
            Assert.Equal((2000, 667), (bmp.Width, bmp.Height));
        }

        Assert.Null(shrinker.Shrink(Jpeg(800, 600), "image/jpeg", 2000));
        Assert.Null(shrinker.Shrink([1, 2, 3], "image/jpeg", 2000)); // битая картинка — как загружена
        Assert.Null(shrinker.Shrink(Jpeg(10, 10), "image/bmp", 2000)); // другие форматы не трогаем
    }

    [Fact]
    public void Exif_rotation_is_applied_even_without_scaling()
    {
        var shrinker = new SkiaImageShrinker();

        // 6 — повернуть на 90° по часовой: левая (красная) половина оказывается сверху, картинка становится вертикальной.
        var rotated = shrinker.Shrink(Jpeg(300, 100, 6), "image/jpeg", 2000)!;
        using (var bmp = SKBitmap.Decode(rotated))
        {
            Assert.Equal((100, 300), (bmp.Width, bmp.Height));
        }

        Assert.True(IsRed(Pixel(rotated, 50, 30)));
        Assert.True(IsBlue(Pixel(rotated, 50, 270)));

        // 8 — повернуть на 90° против часовой: красная половина снизу.
        var ccw = shrinker.Shrink(Jpeg(300, 100, 8), "image/jpeg", 2000)!;
        Assert.True(IsBlue(Pixel(ccw, 50, 30)));
        Assert.True(IsRed(Pixel(ccw, 50, 270)));

        // 3 — 180°: красная половина справа; 2 — зеркально по горизонтали: тоже справа.
        foreach (ushort o in new ushort[] { 3, 2 })
        {
            var flipped = shrinker.Shrink(Jpeg(300, 100, o), "image/jpeg", 2000)!;
            Assert.True(IsBlue(Pixel(flipped, 30, 50)), $"ориентация {o}");
            Assert.True(IsRed(Pixel(flipped, 270, 50)), $"ориентация {o}");
        }

        // 5 и 7 — транспонирование: картинка вертикальная; 5 — красная сверху, 7 — снизу.
        var t5 = shrinker.Shrink(Jpeg(300, 100, 5), "image/jpeg", 2000)!;
        Assert.True(IsRed(Pixel(t5, 50, 30)));
        var t7 = shrinker.Shrink(Jpeg(300, 100, 7), "image/jpeg", 2000)!;
        Assert.True(IsRed(Pixel(t7, 50, 270)));
    }

    /// <summary>Ревью 13.10: PNG в несколько сотен байт с заголовком 30000×30000 не разворачивается в память — понятный отказ.</summary>
    [Fact]
    public void Decompression_bomb_is_rejected_before_decoding()
    {
        static uint Crc(byte[] data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in data)
            {
                crc ^= b;
                for (var k = 0; k < 8; k++)
                {
                    crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
                }
            }

            return crc ^ 0xFFFFFFFFu;
        }

        static byte[] Chunk(string type, byte[] data)
        {
            var typed = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            var len = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
            var crc = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc(typed));
            return [.. len, .. typed, .. crc];
        }

        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, 30_000);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), 30_000);
        ihdr[8] = 8;
        ihdr[9] = 2;
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Chunk("IHDR", ihdr), .. Chunk("IDAT", [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]),
            .. Chunk("IEND", [])];
        var ex = Assert.Throws<KnitErp.Domain.Common.BusinessRuleException>(() => new SkiaImageShrinker().Shrink(png, "image/png", 2000));
        Assert.Equal("dev.file.image.size", ex.Code);
    }
}
