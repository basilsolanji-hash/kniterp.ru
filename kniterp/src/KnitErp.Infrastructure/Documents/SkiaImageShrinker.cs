using KnitErp.Application.Development;
using SkiaSharp;

namespace KnitErp.Infrastructure.Documents;

/// <summary>
/// Уменьшение фото (D729) — SkiaSharp (MIT). JPEG и PNG: длинная сторона до предела, поворот и отражение по EXIF применяются к пикселям
/// (метаданные при перекодировании не сохраняются — так же уходят координаты и модель телефона). JPEG — качество 85.
/// </summary>
public sealed class SkiaImageShrinker : IImageShrinker
{
    public byte[]? Shrink(byte[] content, string contentType, int maxSide)
    {
        var format = contentType switch
        {
            "image/jpeg" => SKEncodedImageFormat.Jpeg,
            "image/png" => SKEncodedImageFormat.Png,
            _ => (SKEncodedImageFormat?)null,
        };
        if (format is null || maxSide < 1)
        {
            return null;
        }

        try
        {
            using var data = SKData.CreateCopy(content);
            using var codec = SKCodec.Create(data);
            if (codec is null)
            {
                return null;
            }

            var origin = codec.EncodedOrigin;
            var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
            var (w, h) = swap ? (codec.Info.Height, codec.Info.Width) : (codec.Info.Width, codec.Info.Height);
            if (Math.Max(w, h) <= maxSide && origin == SKEncodedOrigin.TopLeft)
            {
                return null;
            }

            using var source = SKBitmap.Decode(codec);
            if (source is null)
            {
                return null;
            }

            var scale = Math.Min(1d, (double)maxSide / Math.Max(w, h));
            var (tw, th) = (Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));
            using var surface = SKSurface.Create(new SKImageInfo(tw, th, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            canvas.Scale((float)scale);
            Orient(canvas, origin, codec.Info.Width, codec.Info.Height);
            using var paint = new SKPaint { IsAntialias = true };
            using var image = SKImage.FromBitmap(source);
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
            using var snapshot = surface.Snapshot();
            using var encoded = snapshot.Encode(format.Value, format == SKEncodedImageFormat.Jpeg ? 85 : 100);
            return encoded?.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null; // битая картинка: оставляем как загружена — проверку типа файл уже прошёл
        }
    }

    /// <summary>Преобразование холста так, чтобы исходные пиксели (ширина × высота кодека) легли в правильной ориентации.</summary>
    private static void Orient(SKCanvas c, SKEncodedOrigin origin, int width, int height)
    {
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: c.Translate(width, 0); c.Scale(-1, 1); break;
            case SKEncodedOrigin.BottomRight: c.Translate(width, height); c.RotateDegrees(180); break;
            case SKEncodedOrigin.BottomLeft: c.Translate(0, height); c.Scale(1, -1); break;
            case SKEncodedOrigin.LeftTop: c.Scale(1, -1); c.RotateDegrees(-90); break;
            case SKEncodedOrigin.RightTop: c.Translate(height, 0); c.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom: c.Translate(height, width); c.Scale(-1, 1); c.RotateDegrees(-90); break;
            case SKEncodedOrigin.LeftBottom: c.Translate(0, width); c.RotateDegrees(-90); break;
        }
    }
}
