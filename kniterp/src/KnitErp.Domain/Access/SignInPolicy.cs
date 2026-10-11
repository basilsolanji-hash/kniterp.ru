using System.Security.Cryptography;
using System.Text;
using KnitErp.Domain.Common;

namespace KnitErp.Domain.Access;

/// <summary>Параметры входа. Значения — рабочие допущения, пока заказчик не утвердил политику (docs/decisions.md).</summary>
public static class SignInPolicy
{
    /// <summary>Срок ссылки приглашения — 72 часа (план ядра доступа).</summary>
    public static readonly TimeSpan InvitationLifetime = TimeSpan.FromHours(72);

    // Допущение D81: ссылка «Забыли пароль» на почту — 1 час, повторно не чаще раза в 5 минут;
    // ссылка, выданная администратором, — 24 часа (передаётся лично, а не по почте).
    public static readonly TimeSpan PasswordResetLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan PasswordResetResendInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan AdminPasswordResetLifetime = TimeSpan.FromHours(24);

    // Допущение D81: «Доверять этому устройству» — 30 дней, не больше 10 устройств на пользователя.
    public static readonly TimeSpan TrustedDeviceLifetime = TimeSpan.FromDays(30);
    public const int MaxTrustedDevices = 10;

    // Допущение D25: 5 неудачных попыток подряд блокируют вход на 15 минут.
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    // Допущение D26: длина пароля 10–128 символов, без требований к составу (NIST SP 800-63B).
    public const int PasswordMinLength = 10;
    public const int PasswordMaxLength = 128;

    /// <summary>Роли, которым вход без второго фактора запрещён.</summary>
    public static bool RequiresTwoFactor(string roleCode) => SystemRoles.IsAdministrative(roleCode);

    public static void EnsurePasswordAcceptable(string? password, string email)
    {
        if (password is null || password.Length < PasswordMinLength)
        {
            throw new BusinessRuleException("auth.password.too_short", $"Пароль должен быть не короче {PasswordMinLength} символов.");
        }

        if (password.Length > PasswordMaxLength)
        {
            throw new BusinessRuleException("auth.password.too_long", $"Пароль должен быть не длиннее {PasswordMaxLength} символов.");
        }

        if (string.Equals(password.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new BusinessRuleException("auth.password.equals_email", "Пароль не должен совпадать с email.");
        }

        if (password.Distinct().Count() < 3)
        {
            throw new BusinessRuleException("auth.password.too_simple", "Пароль слишком простой.");
        }

        // D115 (NIST SP 800-63B, 5.1.1.2): запрещены пароли из списков утечек, клавиатурные и числовые ряды и пароль из имени почты.
        var lower = password.Trim().ToLowerInvariant();
        var local = email.Split('@')[0].Trim().ToLowerInvariant();
        if (CommonPasswords.Contains(lower) || IsSequence(lower) || (local.Length >= 4 && lower.Contains(local, StringComparison.Ordinal)))
        {
            throw new BusinessRuleException("auth.password.common",
                "Такой пароль легко подобрать: он есть в списках утёкших паролей, это ряд клавиш или он содержит имя почты. Придумайте другой — например, фразу из нескольких слов.");
        }
    }

    /// <summary>Частые пароли длиной от 10 символов (из открытых списков утечек; короче не пропускает правило длины).</summary>
    private static readonly HashSet<string> CommonPasswords = new(StringComparer.Ordinal)
    {
        "1234567890", "0987654321", "qwertyuiop", "asdfghjkl;", "1q2w3e4r5t", "1qaz2wsx3edc", "zaq12wsxcde3", "qwerty1234", "qwerty12345",
        "qwerty123456", "password12", "password123", "password1!", "password1234", "passw0rd123", "iloveyou12", "123456789a", "a123456789",
        "123456789q", "q123456789", "1234567890q", "abc1234567", "1234qwerasdf", "qwertyuiop1", "123qweasdzxc", "1q2w3e4r5t6y", "zxcvbnm123",
        "йцукенгшщз", "йцукенгшщзх", "пароль1234", "пароль12345", "qazwsxedcr", "welcome123", "admin12345", "administrator", "letmein123",
        "trustno1234", "monkey1234", "dragon1234", "football12", "baseball12", "sunshine12", "princess12", "superman12", "michael123",
        "1111122222", "1122334455", "1234554321", "1234512345", "0123456789", "9876543210", "knitERP123".ToLowerInvariant(), "kniterp2026",
    };

    /// <summary>Ряд символов подряд по возрастанию или убыванию («abcdefghij», «9876543210»).</summary>
    private static bool IsSequence(string value)
    {
        if (value.Length < 4)
        {
            return false;
        }

        var step = value[1] - value[0];
        if (step is not (1 or -1))
        {
            return false;
        }

        for (var i = 2; i < value.Length; i++)
        {
            if (value[i] - value[i - 1] != step)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Одноразовые токены ссылок. В базе хранится только SHA-256.</summary>
public static class SetupTokens
{
    public static string Generate() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty));

    public static bool Matches(string? token, byte[] expectedHash) =>
        CryptographicOperations.FixedTimeEquals(Hash(token ?? string.Empty), expectedHash);
}
