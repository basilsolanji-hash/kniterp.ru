using System.Globalization;
using System.Text;
using KnitErp.Domain.Common;

namespace KnitErp.Application.Finance;

/// <summary>Платёжный документ выписки: кто, кому, сколько, за что.</summary>
public sealed record BankStatementDocument(
    int Index, string Kind, string Number, DateOnly Date, decimal Amount,
    string PayerAccount, string? PayerInn, string PayerName,
    string PayeeAccount, string? PayeeInn, string PayeeName,
    string Purpose, DateOnly? Debited, DateOnly? Credited);

/// <summary>Разобранная выписка: расчётные счета, за которые она выгружена, и документы.</summary>
public sealed record BankStatement(IReadOnlyList<string> Accounts, DateOnly? From, DateOnly? To, IReadOnlyList<BankStatementDocument> Documents);

/// <summary>
/// D119 (А1): разбор выписки в формате обмена 1С «Клиент-банк» (1CClientBankExchange) — его выгружают все банки РФ.
/// Кодировка — из заголовка «Кодировка=Windows» (1251) или «DOS» (866), иначе UTF-8. Неизвестные поля пропускаются,
/// документ без суммы, даты или счетов считается ошибкой файла: лучше остановить загрузку, чем записать неверную оплату.
/// </summary>
public static class ClientBankStatement
{
    public const int MaxBytes = 5 * 1024 * 1024;
    public const int MaxDocuments = 5000;
    private const string Signature = "1CClientBankExchange";

    static ClientBankStatement() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static BankStatement Parse(byte[] content)
    {
        if (content.Length == 0 || content.Length > MaxBytes)
        {
            throw new BusinessRuleException("bank.statement.size", "Файл выписки пустой или больше 5 МБ.");
        }

        var text = Decode(content);
        if (!text.TrimStart('﻿', ' ', '\r', '\n').StartsWith(Signature, StringComparison.Ordinal))
        {
            throw new BusinessRuleException("bank.statement.format",
                "Это не выписка в формате 1С «Клиент-банк». В интернет-банке выберите выгрузку «в 1С» (файл kl_to_1c.txt).");
        }

        var accounts = new List<string>();
        var documents = new List<BankStatementDocument>();
        DateOnly? from = null, to = null;
        Dictionary<string, string>? doc = null;
        string? kind = null;
        var inAccount = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("СекцияДокумент=", StringComparison.Ordinal))
            {
                kind = line["СекцияДокумент=".Length..].Trim();
                doc = new Dictionary<string, string>(StringComparer.Ordinal);
                continue;
            }

            if (line == "КонецДокумента" && doc is not null)
            {
                documents.Add(ToDocument(documents.Count, kind ?? string.Empty, doc));
                if (documents.Count > MaxDocuments)
                {
                    throw new BusinessRuleException("bank.statement.size", $"В выписке больше {MaxDocuments} документов — выгрузите её за меньший период.");
                }

                doc = null;
                continue;
            }

            if (line == "СекцияРасчСчет")
            {
                inAccount = true;
                continue;
            }

            if (line == "КонецРасчСчет")
            {
                inAccount = false;
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var (key, value) = (line[..eq], line[(eq + 1)..].Trim());
            if (doc is not null)
            {
                doc[key] = value;
            }
            else if (key == "РасчСчет" && value.Length > 0 && !accounts.Contains(value))
            {
                accounts.Add(value);
            }
            else if (!inAccount && key == "ДатаНачала")
            {
                from = DateOrNull(value);
            }
            else if (!inAccount && key == "ДатаКонца")
            {
                to = DateOrNull(value);
            }
        }

        return new BankStatement(accounts, from, to, documents);
    }

    /// <summary>Отпечаток документа для защиты от повторной загрузки: та же выписка или пересекающийся период — без дублей.</summary>
    public static byte[] Fingerprint(BankStatementDocument d) =>
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|',
            d.Number, d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), d.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            d.PayerAccount, d.PayeeAccount, d.PayerInn ?? string.Empty, d.PayeeInn ?? string.Empty)));

    private static BankStatementDocument ToDocument(int index, string kind, IReadOnlyDictionary<string, string> f)
    {
        string Get(string key) => f.TryGetValue(key, out var v) ? v : string.Empty;
        string Name(string prefix) => Get(prefix + "1") is { Length: > 0 } n ? n : Get(prefix);

        var number = Get("Номер");
        var date = DateOrNull(Get("Дата"));
        if (date is null || !decimal.TryParse(Get("Сумма").Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount <= 0
            || Get("ПлательщикСчет").Length == 0 || Get("ПолучательСчет").Length == 0)
        {
            throw new BusinessRuleException("bank.statement.document",
                $"Документ № {number} в выписке без даты, суммы или счёта. Выгрузите выписку из банка заново.");
        }

        return new BankStatementDocument(index, kind, number, date.Value, amount,
            Get("ПлательщикСчет"), Inn(Get("ПлательщикИНН")), Name("Плательщик"),
            Get("ПолучательСчет"), Inn(Get("ПолучательИНН")), Name("Получатель"),
            Get("НазначениеПлатежа"), DateOrNull(Get("ДатаСписано")), DateOrNull(Get("ДатаПоступило")));
    }

    private static string? Inn(string value) => value is { Length: 10 or 12 } && value.All(char.IsAsciiDigit) && value.Any(c => c != '0') ? value : null;

    private static DateOnly? DateOrNull(string value) =>
        DateOnly.TryParseExact(value, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static string Decode(byte[] content)
    {
        // Имя ключа «Кодировка» в разных кодировках разное, а значение — латиница: ищем «=DOS» и «=Windows» побайтово.
        var probe = Encoding.Latin1.GetString(content, 0, Math.Min(content.Length, 4096));
        var encoding = probe.Contains("=DOS", StringComparison.OrdinalIgnoreCase) ? Encoding.GetEncoding(866)
            : probe.Contains("=Windows", StringComparison.OrdinalIgnoreCase) ? Encoding.GetEncoding(1251)
            : new UTF8Encoding(false);
        return encoding.GetString(content);
    }
}
