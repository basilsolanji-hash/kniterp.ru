using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Access;

/// <summary>
/// D124: ссылка приглашения или смены пароля уходит сотруднику письмом сразу после выдачи (раньше — только показ ссылки, D28).
/// Письмо — только на адрес учётной записи из базы и только с действующей ссылкой этого пользователя (сверка с хэшем):
/// администратор не может отправить письмо на чужой адрес или с чужим текстом. Ссылка строится от адреса сервера
/// (App:PublicUrl), а не от заголовка запроса. Почта не настроена или письмо не ушло — ссылка остаётся на экране, как прежде.
/// </summary>
public sealed class InvitationMailer(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, IEmailSender email)
{
    public const string InvitePath = "account/invite";
    public const string ResetPath = "account/reset-password";

    /// <summary>Можно ли отправлять письма (настроены SMTP и адрес сервера).</summary>
    public bool IsAvailable => email.IsConfigured && !string.IsNullOrWhiteSpace(email.PublicUrl);

    /// <summary>Ссылка для показа на экране: от адреса сервера, если он задан, иначе — от адреса страницы.</summary>
    public string Link(string fallbackBaseUri, string token, bool passwordReset) =>
        $"{(email.PublicUrl ?? fallbackBaseUri.TrimEnd('/'))}/{(passwordReset ? ResetPath : InvitePath)}?token={Uri.EscapeDataString(token)}";

    /// <summary>Отправляет письмо со ссылкой. true — письмо принято почтовым сервером; адрес получателя — из учётной записи.</summary>
    public async Task<SentInvitation> SendAsync(long userId, string token, bool passwordReset, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.UserManage, ct);
        if (!await db.OrganizationMembers.AnyAsync(m => m.OrganizationId == ctx.OrganizationId && m.UserId == userId, ct))
        {
            throw new NotFoundException("Пользователь");
        }

        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        var now = clock.UtcNow;
        if (user.SetupTokenHash is not { } hash || user.SetupTokenExpiresAtUtc is not { } expires || expires <= now
            || !SetupTokens.Matches(token, hash))
        {
            throw new BusinessRuleException("access.invitation.mail_stale", "Ссылка уже недействительна. Выдайте новую.");
        }

        if (!IsAvailable)
        {
            return new SentInvitation(false, user.Email);
        }

        var organization = await db.Organizations.AsNoTracking().Where(o => o.Id == ctx.OrganizationId).Select(o => o.ShortName).SingleAsync(ct);
        var link = Link(email.PublicUrl!, token, passwordReset);
        var (subject, text) = passwordReset
            ? ("knitERP: смена пароля", ResetLetter(user.DisplayName, organization, link, expires))
            : ($"knitERP: приглашение в «{organization}»", InviteLetter(user.DisplayName, organization, link, expires));
        var sent = await email.SendAsync(user.Email, subject, text, ct);

        db.AuditEntries.Add(AuditEntry.Create(now, ctx.OrganizationId, ctx.UserId, AuditActions.InvitationEmailed, nameof(UserAccount),
            userId.ToString(), null, sent ? "письмо отправлено" : "письмо не отправлено", passwordReset ? "смена пароля" : "приглашение",
            currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
        return new SentInvitation(sent, user.Email);
    }

    internal static string InviteLetter(string name, string organization, string link, DateTime expiresUtc) =>
        $"""
         Здравствуйте, {name}!

         Вас пригласили работать в knitERP — организация «{organization}».

         Чтобы начать, откройте ссылку, придумайте пароль и подключите вход с кодом из приложения-аутентификатора
         (Яндекс Ключ, Google Authenticator или аналог):
         {link}

         Ссылка личная и действует до {expiresUtc:dd.MM.yyyy HH:mm} (UTC). Не пересылайте её другим.
         Если вы не ждали этого письма, просто не открывайте ссылку.
         """;

    internal static string ResetLetter(string name, string organization, string link, DateTime expiresUtc) =>
        $"""
         Здравствуйте, {name}!

         Администратор организации «{organization}» выдал вам ссылку для смены пароля в knitERP:
         {link}

         Ссылка действует до {expiresUtc:dd.MM.yyyy HH:mm} (UTC). Прежний пароль работает, пока не задан новый;
         код из приложения-аутентификатора при входе по-прежнему нужен.
         Если вы не просили сменить пароль, сообщите администратору.
         """;
}

/// <param name="Sent">Письмо принято почтовым сервером.</param>
/// <param name="Email">Адрес, на который отправлено (для сообщения администратору).</param>
public sealed record SentInvitation(bool Sent, string Email);
