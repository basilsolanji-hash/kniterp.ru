namespace KnitErp.Domain.Hr;

/// <summary>Проверка идентификаторов физического лица РФ: СНИЛС и ИНН (D201).</summary>
public static class PersonIds
{
    /// <summary>
    /// СНИЛС: 11 цифр, последние две — контрольное число. Сумма произведений первых девяти цифр на веса 9…1:
    /// меньше 100 — это и есть контрольное число, 100 и 101 — «00», больше 101 — остаток от деления на 101 (100 → «00»).
    /// Номера до 001-001-998 выдавались без проверки контрольного числа.
    /// </summary>
    public static bool IsValidSnils(string? snils)
    {
        var digits = Digits(snils);
        if (digits is not { Length: 11 })
        {
            return false;
        }

        var number = long.Parse(digits[..9]);
        if (number <= 1001998)
        {
            return true;
        }

        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += (digits[i] - '0') * (9 - i);
        }

        var check = sum < 100 ? sum : sum % 101 % 100;
        return check == int.Parse(digits[9..]);
    }

    /// <summary>СНИЛС в виде «123-456-789 01»; неверная длина — как есть.</summary>
    public static string FormatSnils(string snils)
    {
        var d = Digits(snils);
        return d is { Length: 11 } ? $"{d[..3]}-{d[3..6]}-{d[6..9]} {d[9..]}" : snils;
    }

    /// <summary>Только цифры: СНИЛС вводят с дефисами и пробелами.</summary>
    public static string? Digits(string? value) =>
        value is null ? null : new string(value.Where(char.IsAsciiDigit).ToArray());

    /// <summary>ИНН физического лица: 12 цифр, две контрольные (как в справочнике контрагентов).</summary>
    public static bool IsValidPersonInn(string? inn) => Organizations.RussianRequisites.IsValidPersonInn(inn);
}
