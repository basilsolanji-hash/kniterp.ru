using System.Xml;
using System.Xml.Linq;

namespace KnitErp.Application.Common;

/// <summary>
/// Чтение XML из внешних файлов (D146, D149): без DTD и внешних сущностей (XXE), без загрузки по ссылкам, с ограничением размера.
/// Кодировка — из заголовка XML (windows-1251 у ЦБ и ФНС).
/// </summary>
public static class SafeXml
{
    static SafeXml() => System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

    public static XDocument Load(byte[] content, int maxBytes)
    {
        if (content.Length == 0 || content.Length > maxBytes)
        {
            throw new KnitErp.Domain.Common.BusinessRuleException("xml.size", $"Файл пустой или больше {maxBytes / 1024 / 1024} МБ.");
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersFromEntities = 0, MaxCharactersInDocument = 4L * maxBytes,
            IgnoreComments = true, IgnoreProcessingInstructions = true,
        };
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var reader = XmlReader.Create(stream, settings);
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException)
        {
            throw new KnitErp.Domain.Common.BusinessRuleException("xml.invalid", "Файл не похож на XML или повреждён.");
        }
    }
}
