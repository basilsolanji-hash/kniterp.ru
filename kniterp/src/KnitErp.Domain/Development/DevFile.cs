using System.Text;
using KnitErp.Domain.Common;

namespace KnitErp.Domain.Development;

/// <summary>Что за файл модели (D705). Числа хранятся в базе — не перенумеровывать.</summary>
public enum DevFileKind : byte
{
    Sketch = 1,
    Inspiration = 2,
    Photo = 3,
    Pattern = 4,
    Lekalo = 5,
    MachineProgram = 6,
    Document = 7,
}

/// <summary>
/// Файл модели (D705): эскиз, фото, рисунок, лекала, программа вязальной машины. Только вставка: файл с тем же именем и видом
/// получает следующую версию, прежние остаются. Тип определяется по расширению и проверяется по первым байтам содержимого.
/// </summary>
public sealed class DevFile
{
    public const int MaxBytes = 50 * 1024 * 1024;

    /// <summary>Аудит 2026-10-12: предел хранилища одной модели — версии файлов не удаляются, без предела база растёт бесконечно.</summary>
    public const long MaxBytesPerModel = 2L * 1024 * 1024 * 1024;

    public const int MaxFilesPerModel = 1000;
    public const int FileNameMaxLength = 200;
    public const int CommentMaxLength = 300;

    private DevFile()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long ModelId { get; private set; }
    public DevFileKind Kind { get; private set; }
    public string FileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public int Version { get; private set; }
    public int SizeBytes { get; private set; }
    public byte[] Content { get; private set; } = [];
    public string? Comment { get; private set; }
    public DateTime UploadedAtUtc { get; private set; }
    public long UploadedByUserId { get; private set; }

    /// <summary>Деталь, для которой загружена программа машины (D711).</summary>
    public long? DetailId { get; private set; }

    /// <summary>Станок программы (D710).</summary>
    public long? MachineId { get; private set; }

    /// <summary>Параметры станка на момент загрузки («STOLL CMS 530 · E7 · 8 нитеводителей») — программа хранится вместе с ними.</summary>
    public string? MachineParams { get; private set; }

    /// <summary>Привязать программу к детали и станку до сохранения (после вставки файл неизменяем).</summary>
    public void AttachProgram(long detailId, long machineId, string machineParams)
    {
        if (Kind != DevFileKind.MachineProgram || Id != 0)
        {
            throw new BusinessRuleException("dev.file.program", "К станку привязывается только новая программа машины.");
        }

        DetailId = detailId;
        MachineId = machineId;
        MachineParams = machineParams.Length > 200 ? machineParams[..200] : machineParams;
    }

    public static string KindName(DevFileKind kind) => kind switch
    {
        DevFileKind.Sketch => "Эскиз",
        DevFileKind.Inspiration => "Образец для вдохновения",
        DevFileKind.Photo => "Фото",
        DevFileKind.Pattern => "Рисунок",
        DevFileKind.Lekalo => "Лекала",
        DevFileKind.MachineProgram => "Программа машины",
        _ => "Документ",
    };

    public static readonly IReadOnlyList<DevFileKind> Kinds =
        [DevFileKind.Sketch, DevFileKind.Inspiration, DevFileKind.Photo, DevFileKind.Pattern, DevFileKind.Lekalo, DevFileKind.MachineProgram, DevFileKind.Document];

    /// <summary>Подсказка для поля выбора файла: какие расширения принимает вид.</summary>
    public static string Accept(DevFileKind kind) => string.Join(",", DevFileTypes.Extensions(kind).Select(e => "." + e));

    /// <summary>Безопасное имя без пути (как у сканов договоров).</summary>
    public static string CleanName(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/')).Trim();
        return name.Length is 0 ? "file" : name.Length > FileNameMaxLength ? name[..FileNameMaxLength] : name;
    }

    public static DevFile Create(long organizationId, long modelId, DevFileKind kind, string? fileName, byte[] content, int version, string? comment,
        DateTime nowUtc, long userId)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new BusinessRuleException("dev.file.kind", "Выберите вид файла.");
        }

        if (content.Length == 0 || content.Length > MaxBytes)
        {
            throw new BusinessRuleException("dev.file.size", "Файл модели — до 50 МБ.");
        }

        var name = CleanName(fileName);
        var type = DevFileTypes.Detect(kind, name, content)
                   ?? throw new BusinessRuleException("dev.file.type", $"Для вида «{KindName(kind)}» подходят файлы: {Accept(kind)}. Содержимое должно соответствовать расширению.");
        return new DevFile
        {
            OrganizationId = organizationId,
            ModelId = modelId,
            Kind = kind,
            FileName = name,
            ContentType = type,
            Version = Math.Max(1, version),
            SizeBytes = content.Length,
            Content = content,
            Comment = DomainText.Optional(comment, CommentMaxLength, "Комментарий"),
            UploadedAtUtc = nowUtc,
            UploadedByUserId = userId,
        };
    }

    public bool IsImage => ContentType.StartsWith("image/", StringComparison.Ordinal) && ContentType != DevFileTypes.Dxf;
}

/// <summary>Допустимые типы файлов разработки и проверка их сигнатур (D705).</summary>
public static class DevFileTypes
{
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string Bmp = "image/bmp";
    public const string Pdf = "application/pdf";
    public const string Dxf = "image/vnd.dxf";
    public const string Binary = "application/octet-stream";

    private static readonly string[] Images = ["png", "jpg", "jpeg", "bmp"];

    public static IReadOnlyList<string> Extensions(DevFileKind kind) => kind switch
    {
        DevFileKind.Sketch => [.. Images, "pdf"],
        DevFileKind.Inspiration or DevFileKind.Photo => Images,
        DevFileKind.Pattern => ["png", "bmp"],
        DevFileKind.Lekalo => ["pdf", "dxf"],
        // Stoll M1plus — .sin, Shima Seiki SDS-ONE — .dat.
        DevFileKind.MachineProgram => ["sin", "dat"],
        _ => ["pdf", "png", "jpg", "jpeg"],
    };

    /// <summary>Тип содержимого по расширению, если первые байты ему соответствуют; иначе null.</summary>
    public static string? Detect(DevFileKind kind, string fileName, byte[] c)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        if (!Extensions(kind).Contains(ext))
        {
            return null;
        }

        return ext switch
        {
            "png" => IsPng(c) ? Png : null,
            "jpg" or "jpeg" => c.Length >= 3 && c[0] == 0xFF && c[1] == 0xD8 && c[2] == 0xFF ? Jpeg : null,
            "bmp" => c.Length >= 26 && c[0] == (byte)'B' && c[1] == (byte)'M' ? Bmp : null,
            "pdf" => c.Length >= 5 && c[0] == 0x25 && c[1] == 0x50 && c[2] == 0x44 && c[3] == 0x46 && c[4] == 0x2D ? Pdf : null,
            "dxf" => IsDxf(c) ? Dxf : null,
            // Допущение D705: форматы программ машин закрыты и общей сигнатуры не имеют — проверяем, что это не страница,
            // не скрипт и не исполняемый файл под чужим расширением; отдаются только вложением как двоичные данные.
            "sin" or "dat" => LooksDangerous(c) ? null : Binary,
            _ => null,
        };
    }

    public static bool IsPng(byte[] c) =>
        c.Length >= 8 && c[0] == 0x89 && c[1] == 0x50 && c[2] == 0x4E && c[3] == 0x47 && c[4] == 0x0D && c[5] == 0x0A && c[6] == 0x1A && c[7] == 0x0A;

    /// <summary>DXF: двоичный («AutoCAD Binary DXF») или текстовый, начинающийся с группы «0 / SECTION» (возможно, после комментария 999).</summary>
    private static bool IsDxf(byte[] c)
    {
        var head = Encoding.ASCII.GetString(c, 0, Math.Min(c.Length, 512));
        if (head.StartsWith("AutoCAD Binary DXF", StringComparison.Ordinal))
        {
            return true;
        }

        var lines = head.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(4).ToList();
        var start = lines.Count >= 2 && lines[0] == "999" ? 2 : 0;
        return lines.Count >= start + 2 && lines[start] == "0" && lines[start + 1] == "SECTION";
    }

    private static bool LooksDangerous(byte[] c)
    {
        if (c.Length >= 2 && c[0] == (byte)'M' && c[1] == (byte)'Z')
        {
            return true;
        }

        if (c.Length >= 4 && c[0] == 0x7F && c[1] == (byte)'E' && c[2] == (byte)'L' && c[3] == (byte)'F')
        {
            return true;
        }

        var head = Encoding.ASCII.GetString(c, 0, Math.Min(c.Length, 256)).TrimStart('﻿', ' ', '\t', '\r', '\n').ToLowerInvariant();
        return head.StartsWith('<') || head.StartsWith("#!", StringComparison.Ordinal) || head.StartsWith("%pdf", StringComparison.Ordinal);
    }
}
