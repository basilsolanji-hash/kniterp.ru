using System.Security.Claims;
using KnitErp.Application.Common;
using Microsoft.AspNetCore.Components.Authorization;

namespace KnitErp.Web.Security;

/// <summary>
/// Пользователь и организация из cookie сессии. Организация выбирается при входе и не берётся из параметров запроса.
/// В интерактивном Blazor источник — состояние аутентификации цепи (оно перепроверяется раз в минуту),
/// в обычном HTTP-запросе — HttpContext.User.
/// </summary>
public sealed class ClaimsCurrentUser(AuthenticationStateProvider authenticationState, IHttpContextAccessor httpContext) : ICurrentUser
{
    public long? UserId => SessionClaims.UserId(Principal);
    public long? OrganizationId => SessionClaims.OrganizationId(Principal);
    public string? CorrelationId { get; } = Guid.NewGuid().ToString("N");

    public string? ClientAddress => httpContext.HttpContext?.Connection.RemoteIpAddress?.ToString();

    /// <summary>Запрос по токену API (D88): права ограничены правами токена. У сессии в браузере этих утверждений нет.</summary>
    public IReadOnlyCollection<string>? ApiScope =>
        Principal.FindFirstValue(KnitErp.Web.Api.ApiTokenAuthenticationHandler.ScopeClaim) is { } scope
            ? scope.Split(',', StringSplitOptions.RemoveEmptyEntries)
            : null;

    public long? ApiTokenId =>
        long.TryParse(Principal.FindFirstValue(KnitErp.Web.Api.ApiTokenAuthenticationHandler.TokenClaim), out var id) ? id : null;

    private ClaimsPrincipal Principal
    {
        get
        {
            try
            {
                // ServerAuthenticationStateProvider хранит уже завершённую задачу, ожидание не блокирует поток.
                var task = authenticationState.GetAuthenticationStateAsync();
                if (task.IsCompletedSuccessfully)
                {
                    return task.Result.User;
                }
            }
            catch (InvalidOperationException)
            {
                // Вне Blazor (минимальные API) состояние не задано — берём пользователя запроса.
            }

            return httpContext.HttpContext?.User ?? new ClaimsPrincipal();
        }
    }
}

/// <summary>Состав cookie сессии: пользователь, организация, штамп безопасности.</summary>
public static class SessionClaims
{
    public const string OrganizationClaim = "kniterp:org";
    public const string StampClaim = "kniterp:stamp";
    public const string TwoFactorClaim = "kniterp:2fa";

    public static ClaimsPrincipal Create(KnitErp.Application.Authentication.SessionIdentity session) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, session.UserId.ToString()),
            new Claim(ClaimTypes.Name, session.DisplayName),
            new Claim(OrganizationClaim, session.OrganizationId.ToString()),
            new Claim(StampClaim, session.SecurityStamp.ToString("N")),
            new Claim(TwoFactorClaim, session.UsedTwoFactor ? "1" : "0"),
        ], AuthSchemes.Session));

    public static KnitErp.Application.Authentication.SessionIdentity? Read(ClaimsPrincipal principal) =>
        UserId(principal) is { } userId && OrganizationId(principal) is { } orgId && Stamp(principal) is { } stamp
            ? new(userId, principal.Identity?.Name ?? string.Empty, stamp, orgId, principal.FindFirstValue(TwoFactorClaim) == "1")
            : null;

    public static long? UserId(ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated == true && long.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static long? OrganizationId(ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated == true && long.TryParse(principal.FindFirstValue(OrganizationClaim), out var id) ? id : null;

    public static Guid? Stamp(ClaimsPrincipal principal) =>
        Guid.TryParseExact(principal.FindFirstValue(StampClaim), "N", out var stamp) ? stamp : null;
}
