using System.Security.Claims;
using System.Text.Encodings.Web;
using KnitErp.Application.Common;
using KnitErp.Domain.Integrations;
using KnitErp.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KnitErp.Web.Api;

/// <summary>
/// Вход в API по токену (D88): заголовок «Authorization: Bearer knt_…». Токен ищется по SHA-256, проверяются отзыв и срок; права
/// запроса = права токена ∩ текущие права его создателя (это делает AccessGuard по утверждениям kniterp:scope). Cookie сессии
/// для API не действуют — так API не подвержен подделке межсайтовых запросов.
/// </summary>
public sealed class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IKnitErpDbContext db, IClock clock)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "KnitErp.ApiToken";
    public const string TokenClaim = "kniterp:token";
    public const string ScopeClaim = "kniterp:scope";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var secret = header["Bearer ".Length..].Trim();
        if (!secret.StartsWith(ApiToken.SecretPrefix, StringComparison.Ordinal) || secret.Length > 200)
        {
            return AuthenticateResult.Fail("Неверный формат токена.");
        }

        var hash = ApiToken.Hash(secret);
        var token = await db.ApiTokens.AsNoTracking().SingleOrDefaultAsync(t => t.TokenHash == hash, Context.RequestAborted);
        var now = clock.UtcNow;
        if (token is null || !token.IsActive(now))
        {
            return AuthenticateResult.Fail("Токен недействителен: не найден, отозван или истёк.");
        }

        // Отметка использования — не чаще раза в минуту, отдельным UPDATE без загрузки строки.
        if (token.LastUsedAtUtc is not { } last || now - last >= TimeSpan.FromMinutes(1))
        {
            await db.ApiTokens.Where(t => t.Id == token.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAtUtc, now), Context.RequestAborted);
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, token.CreatedByUserId.ToString()),
            new Claim(ClaimTypes.Name, "API: " + token.Name),
            new Claim(SessionClaims.OrganizationClaim, token.OrganizationId.ToString()),
            new Claim(TokenClaim, token.Id.ToString()),
            new Claim(ScopeClaim, token.Permissions),
        ], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        await Response.WriteAsJsonAsync(ApiErrors.Problem(401, "auth.required", "Нужен действующий токен: заголовок Authorization: Bearer knt_…"),
            ApiJson.Options, "application/problem+json");
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        await Response.WriteAsJsonAsync(ApiErrors.Problem(403, "auth.forbidden", "Недостаточно прав."), ApiJson.Options, "application/problem+json");
    }
}
