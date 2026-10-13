using System.Text;

namespace KnitErp.Application.Crm;

/// <summary>
/// CSV для импорта CRM (D697): выгрузки amoCRM, Битрикс24 и Excel «CSV». Разделитель — «;», «,» или табуляция (по первой строке),
/// кавычки по RFC 4180 (удвоенная кавычка, переносы строк внутри поля), кодировка — UTF-8 (с BOM или без) или Windows-1251 (если файл
/// не читается как UTF-8). Возвращает строки так же, как чтение первого листа Excel.
/// </summary>
public static class CrmCsv
{
    /// <summary>XLSX — это ZIP: файл начинается с «PK\x03\x04».</summary>
    public static bool IsXlsx(ReadOnlySpan<byte> head) => head.Length >= 4 && head[0] == 0x50 && head[1] == 0x4B && head[2] == 0x03 && head[3] == 0x04;

    public static IReadOnlyList<IReadOnlyList<string>> Parse(byte[] content, int maxRows)
    {
        var text = Decode(content);
        var delimiter = Delimiter(text);
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            if (c == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (c == delimiter)
            {
                row.Add(field.ToString().Trim());
                field.Clear();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(field.ToString().Trim());
                field.Clear();
                if (row.Any(v => v.Length > 0))
                {
                    rows.Add(row);
                    if (rows.Count > maxRows)
                    {
                        return rows;
                    }
                }

                row = [];
            }
            else
            {
                field.Append(c);
            }
        }

        row.Add(field.ToString().Trim());
        if (row.Any(v => v.Length > 0))
        {
            rows.Add(row);
        }

        return rows;
    }

    private static string Decode(byte[] content)
    {
        if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(content, 3, content.Length - 3);
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1251).GetString(content);
        }
    }

    /// <summary>Разделитель — тот из «;», табуляции и «,», которого больше в первой строке вне кавычек (Excel в России пишет «;»).</summary>
    private static char Delimiter(string text)
    {
        var counts = new Dictionary<char, int> { [';'] = 0, ['\t'] = 0, [','] = 0 };
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && c is '\r' or '\n')
            {
                break;
            }
            else if (!quoted && counts.ContainsKey(c))
            {
                counts[c]++;
            }
        }

        return counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key == ';' ? 0 : p.Key == '\t' ? 1 : 2).First().Key;
    }
}
