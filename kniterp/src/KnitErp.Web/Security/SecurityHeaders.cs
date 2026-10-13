namespace KnitErp.Web.Security;

/// <summary>
/// Заголовки безопасности браузера. CSP разрешает скрипты, стили и соединения только со своего сервера: внедрённый
/// чужой скрипт не выполнится и не отправит данные наружу. Встроенные стили разрешены (Blazor и разметка ими
/// пользуются), встроенные скрипты — нет. Страницу нельзя открыть внутри чужого сайта (защита от подмены щелчков).
/// </summary>
public static class SecurityHeaders
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; " +
        "connect-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'self'; object-src 'none'";

    public static Task Apply(HttpContext context, Func<Task> next)
    {
        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        // Допущение D693: микрофон — только своему сайту (диктовка заметок CRM, Web Speech API); чужим вставкам и сайтам — нет.
        headers["Permissions-Policy"] = "camera=(), microphone=(self), geolocation=(), payment=(), usb=()";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        return next();
    }
}
