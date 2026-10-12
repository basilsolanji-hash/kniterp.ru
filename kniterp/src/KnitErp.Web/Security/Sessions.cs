using System.Security.Claims;
using KnitErp.Application.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace KnitErp.Web.Security;

public static class AuthSchemes
{
    /// <summary>Сессия вошедшего пользователя.</summary>
    public const string Session = CookieAuthenticationDefaults.AuthenticationScheme;

    /// <summary>Короткое состояние «пароль проверен, ждём второй фактор». Доступа к данным не даёт.</summary>
    public const string Pending = "KnitErp.Pending";

    public const string PendingUserClaim = "kniterp:pending-user";
}

/// <summary>Сохранение состояния между паролем и кодом 2FA в подписанной cookie на 5 минут.</summary>
public static class PendingSignInCookie
{
    public static Task SignInAsync(HttpContext http, PendingSignIn pending) =>
        http.SignInAsync(AuthSchemes.Pending, new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(AuthSchemes.PendingUserClaim, pending.UserId.ToString()),
            new Claim(SessionClaims.StampClaim, pending.SecurityStamp.ToString("N")),
        ], AuthSchemes.Pending)));

    public static async Task<PendingSignIn?> ReadAsync(HttpContext http)
    {
        var result = await http.AuthenticateAsync(AuthSchemes.Pending);
        if (!result.Succeeded
            || !long.TryParse(result.Principal.FindFirstValue(AuthSchemes.PendingUserClaim), out var userId)
            || SessionClaims.Stamp(result.Principal) is not { } stamp)
        {
            return null;
        }

        return new PendingSignIn(userId, stamp);
    }

    /// <summary>Полный вход: сессия выдаётся, промежуточное состояние удаляется.</summary>
    public static async Task CompleteAsync(HttpContext http, SessionIdentity session)
    {
        await http.SignOutAsync(AuthSchemes.Pending);
        await http.SignInAsync(AuthSchemes.Session, SessionClaims.Create(session));
        // Язык из профиля — на любом устройстве после входа (D60). Если в профиле язык не выбран,
        // остаётся выбранный на странице входа.
        if (session.Language is not null)
        {
            KnitErp.Web.Localization.Text.SetCookie(http, session.Language);
        }
    }
}

/// <summary>
/// Проверка cookie на HTTP-запросах: не реже раза в минуту сверяем штамп, статус пользователя, участие в организации
/// и серверную запись сессии (D122: завершённая на другом устройстве сессия закрывается).
/// Блокировка и смена прав закрывают сессию не позднее чем через 60 секунд.
/// </summary>
public static class SessionValidation
{
    /// <summary>Полная проверка: штамп, пользователь, участие, ограничения входа (D127).</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// D127: быстрая проверка записи сессии (завершена, наступил момент закрытия) — раз в 10 с: сессия, закрытая входом с другого
    /// устройства, перестаёт приниматься не позднее чем через 10 с после своего момента закрытия.
    /// </summary>
    public static readonly TimeSpan QuickInterval = TimeSpan.FromSeconds(10);

    /// <summary>Ключ HttpContext.Items: почему сессия закрыта — для сообщения на странице входа.</summary>
    public const string ClosedItem = "kniterp.closed";

    private const string ValidatedAtKey = "kniterp.validated";
    private const string CheckedAtKey = "kniterp.checked";
    private const string AddressKey = "kniterp.addr";
    private const string EndsAtKey = "kniterp.ends";

    public static async Task OnValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var now = DateTimeOffset.UtcNow;
        var items = context.Properties.Items;
        var address = context.HttpContext.Connection.RemoteIpAddress?.ToString();
        var validatedAt = Read(items, ValidatedAtKey);
        var checkedAt = Read(items, CheckedAtKey) ?? validatedAt;
        var endsAt = Read(items, EndsAtKey);

        // D127: адрес клиента сменился — полная проверка сразу (ограничение «разрешённые сети»), а не через минуту.
        var full = validatedAt is not { } v || now - v >= Interval || (items.TryGetValue(AddressKey, out var known) ? known : null) != address
                   || endsAt is { } e && now >= e;
        if (!full && checkedAt is { } c && now - c < QuickInterval)
        {
            return;
        }

        var session = context.Principal is null ? null : SessionClaims.Read(context.Principal);
        var signIn = context.HttpContext.RequestServices.GetRequiredService<SignInService>();
        var check = session is null
            ? SessionCheck.Invalid
            : await signIn.CheckSessionAsync(session.UserId, session.OrganizationId, session.SecurityStamp, session.SessionId, address, full,
                context.HttpContext.RequestAborted);
        if (!check.Valid)
        {
            if (ClosedCode(check.Closure) is { } code)
            {
                context.HttpContext.Items[ClosedItem] = code;
            }

            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(AuthSchemes.Session);
            return;
        }

        if (full)
        {
            items[ValidatedAtKey] = now.ToString("O");
            items[AddressKey] = address;
        }

        items[CheckedAtKey] = now.ToString("O");
        items[EndsAtKey] = check.EndsAtUtc is { } ends ? new DateTimeOffset(DateTime.SpecifyKind(ends, DateTimeKind.Utc)).ToString("O") : null;
        context.ShouldRenew = true;
    }

    /// <summary>Код причины закрытия для адреса страницы входа: network, device, schedule; иначе null.</summary>
    public static string? ClosedCode(SessionClosure closure) => closure switch
    {
        SessionClosure.Network => "network",
        SessionClosure.OtherDevice => "device",
        SessionClosure.Schedule => "schedule",
        _ => null,
    };

    /// <summary>Перенаправление на вход дополняется причиной закрытия сессии (D127), если она известна.</summary>
    public static Func<RedirectContext<CookieAuthenticationOptions>, Task> RedirectToLogin(
        Func<RedirectContext<CookieAuthenticationOptions>, Task> fallback) => context =>
    {
        if (context.HttpContext.Items[ClosedItem] is string code)
        {
            context.RedirectUri = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(context.RedirectUri, "closed", code);
        }

        return fallback(context);
    };

    private static DateTimeOffset? Read(IDictionary<string, string?> items, string key) =>
        items.TryGetValue(key, out var raw)
        && DateTimeOffset.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var value) ? value : null;
}

/// <summary>
/// Адрес клиента вкладки Blazor (D127): запоминается при создании цепи — это адрес соединения WebSocket, он не меняется, пока
/// соединение живо (новое соединение — новый HTTP-запрос с проверкой cookie). Может быть неизвестен — тогда сети проверяет HTTP.
/// </summary>
public sealed class CircuitClientAddress(IHttpContextAccessor accessor)
{
    public string? Address { get; } = accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
}

/// <summary>D127: «сеанс закроется через N с» — для баннера на старом устройстве.</summary>
public sealed record SessionNotice(DateTime EndsAtUtc, KnitErp.Domain.Access.SessionEndReason? Reason);

/// <summary>
/// Та же проверка для открытой вкладки Blazor: цепь живёт долго. Быстрая проверка записи сессии — раз в 10 с, полная — раз в минуту
/// (D127). Назначен момент закрытия — следующая проверка ровно в этот момент; баннер получает уведомление через <see cref="NoticeChanged"/>.
/// </summary>
public sealed class SessionRevalidatingAuthenticationStateProvider(
    ILoggerFactory loggerFactory, IServiceScopeFactory scopeFactory, CircuitClientAddress clientAddress)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    private DateTime _lastFullUtc = DateTime.UtcNow;

    public SessionNotice? Notice { get; private set; }

    /// <summary>Причина закрытия, если цепь закрыта проверкой (для страницы входа).</summary>
    public string? ClosedCode { get; private set; }

    public event Action? NoticeChanged;

    protected override TimeSpan RevalidationInterval
    {
        get
        {
            if (Notice is { } notice)
            {
                var left = notice.EndsAtUtc - DateTime.UtcNow + TimeSpan.FromMilliseconds(300);
                return left < TimeSpan.FromMilliseconds(500) ? TimeSpan.FromMilliseconds(500)
                    : left > SessionValidation.QuickInterval ? SessionValidation.QuickInterval : left;
            }

            return SessionValidation.QuickInterval;
        }
    }

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken cancellationToken)
    {
        if (SessionClaims.Read(state.User) is not { } session)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        var full = now - _lastFullUtc >= SessionValidation.Interval;
        await using var scope = scopeFactory.CreateAsyncScope();
        var signIn = scope.ServiceProvider.GetRequiredService<SignInService>();
        var check = await signIn.CheckSessionAsync(session.UserId, session.OrganizationId, session.SecurityStamp, session.SessionId,
            clientAddress.Address, full, cancellationToken);
        if (full)
        {
            _lastFullUtc = now;
        }

        var notice = check.Valid && check.EndsAtUtc is { } ends ? new SessionNotice(ends, check.EndReason) : null;
        ClosedCode = check.Valid ? null : SessionValidation.ClosedCode(check.Closure);
        if (notice != Notice || !check.Valid)
        {
            // Закрытие тоже сообщается: маршрутизатор страниц не всегда перерисовывается при выходе — баннер уводит на вход сам.
            Notice = notice;
            NoticeChanged?.Invoke();
        }

        return check.Valid;
    }
}

/// <summary>
/// Cookie доверенного устройства (D81): случайный токен, в базе — только его SHA-256. Только для страниц /account,
/// недоступна скриптам, только по HTTPS. Выход из системы её не удаляет — в этом и смысл «доверять устройству».
/// </summary>
public static class TrustedDeviceCookie
{
    public const string Name = "kniterp.device";

    public static string? Read(HttpContext http) => http.Request.Cookies.TryGetValue(Name, out var value) ? value : null;

    public static void Write(HttpContext http, string token) =>
        http.Response.Cookies.Append(Name, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/account",
            IsEssential = true,
            Expires = DateTimeOffset.UtcNow + KnitErp.Domain.Access.SignInPolicy.TrustedDeviceLifetime,
        });

    /// <summary>«Chrome, Windows» — по заголовку User-Agent, чтобы пользователь узнал устройство в списке.</summary>
    public static string Label(HttpContext http) => KnitErp.Domain.Access.SessionDevice.Label(http.Request.Headers.UserAgent.ToString());
}
