using KnitErp.Web.Localization;
using KnitErp.Application.Authentication;
using KnitErp.Domain.Access;
using QRCoder;

namespace KnitErp.Web.Security;

public static class SignInMessages
{
    /// <summary>Текст не раскрывает, существует ли email: неверный email и неверный пароль выглядят одинаково.</summary>
    public static string For(SignInStatus status) => status switch
    {
        SignInStatus.LockedOut =>
            Text.L("Слишком много неудачных попыток. Вход закрыт на {0} минут, попробуйте позже.", SignInPolicy.LockoutDuration.TotalMinutes),
        SignInStatus.NoOrganization => Text.L("Нет доступа ни к одной организации. Обратитесь к администратору."),
        SignInStatus.NetworkDenied => Text.L("Вход с этого адреса запрещён администратором."),
        SignInStatus.ScheduleDenied => Text.L("Вход в это время запрещён администратором."),
        _ => Text.L("Неверный email или пароль."),
    };

    /// <summary>D127: почему закрыта прежняя сессия (код из адреса страницы входа); неизвестный код — null.</summary>
    public static string? ForClosed(string? code) => code switch
    {
        "network" => Text.L("Вход с этого адреса запрещён администратором."),
        "device" => Text.L("Сеанс закрыт: выполнен вход с другого устройства."),
        "schedule" => Text.L("Сеанс закрыт: закончилось разрешённое время работы."),
        _ => null,
    };
}

public static class QrCodes
{
    public static string PngDataUri(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
        return "data:image/png;base64," + Convert.ToBase64String(new PngByteQRCode(data).GetGraphic(5));
    }
}
