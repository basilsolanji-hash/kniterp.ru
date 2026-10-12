using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// D124: ссылка приглашения уходит письмом. Письмо — только на адрес учётной записи и только с действующей ссылкой этого
/// пользователя; чужая организация — 404; без права управления пользователями — отказ; отправка записывается в журнал.
/// </summary>
public sealed class InvitationMailTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 871_000_000;

    [SqlFact]
    public async Task Invitation_link_is_mailed_only_to_the_invited_address_with_a_live_token()
    {
        var org = await CreateOrganizationAsync();
        var other = await CreateOrganizationAsync();
        var email = $"sewer-{Guid.NewGuid():N}@test.local";
        InvitationResult invitation;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            invitation = await s.Access.InviteAsync(new InviteUserCommand(email, "Швея", SystemRoles.Employee, null));
        }

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var mailer = Mailer(s.Db, org.OwnerUserId, org.OrganizationId);
            Assert.True(mailer.IsAvailable);
            var sent = await mailer.SendAsync(invitation.UserId, invitation.SetupToken!, passwordReset: false);
            Assert.Equal((true, email), (sent.Sent, sent.Email));

            var letter = Assert.Single(host.Mail.Sent, m => m.To == email);
            Assert.Contains($"https://erp.test/account/invite?token={Uri.EscapeDataString(invitation.SetupToken!)}", letter.Text);
            Assert.Contains("«Тест ", letter.Subject);
            Assert.True(await s.Db.AuditEntries.AnyAsync(a => a.OrganizationId == org.OrganizationId && a.Action == AuditActions.InvitationEmailed
                                                              && a.EntityId == invitation.UserId.ToString()));

            // Чужая или устаревшая ссылка — письмо не уходит.
            var stale = await Assert.ThrowsAsync<BusinessRuleException>(() => mailer.SendAsync(invitation.UserId, "чужой-токен", false));
            Assert.Equal("access.invitation.mail_stale", stale.Code);
        }

        // Новая ссылка отменяет прежнюю: письмо с прежней уже не отправить.
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            await s.Access.ReissueInvitationAsync(invitation.UserId);
        }

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            await Assert.ThrowsAsync<BusinessRuleException>(() => Mailer(s.Db, org.OwnerUserId, org.OrganizationId)
                .SendAsync(invitation.UserId, invitation.SetupToken!, false));
        }

        // Пользователь чужой организации — 404, даже с верной ссылкой.
        await using (var s = host.As(other.OwnerUserId, other.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => Mailer(s.Db, other.OwnerUserId, other.OrganizationId)
                .SendAsync(invitation.UserId, invitation.SetupToken!, false));
        }

        Assert.Single(host.Mail.Sent, m => m.To == email);
    }

    [SqlFact]
    public async Task Without_user_management_right_mail_is_refused()
    {
        var org = await CreateOrganizationAsync();
        InvitationResult keeper, target;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var store = await s.Warehouses.CreateWarehouseAsync("Склад", null);
            keeper = await s.Access.InviteAsync(new InviteUserCommand($"keeper-{Guid.NewGuid():N}@test.local", "Кладовщик", SystemRoles.Storekeeper, null,
                WarehouseId: store));
            target = await s.Access.InviteAsync(new InviteUserCommand($"t-{Guid.NewGuid():N}@test.local", "Швея", SystemRoles.Employee, null));
        }

        await using (var db = host.NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == keeper.UserId)).Activate();
            await db.SaveChangesAsync();
        }

        await using (var s = host.As(keeper.UserId, org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => Mailer(s.Db, keeper.UserId, org.OrganizationId)
                .SendAsync(target.UserId, target.SetupToken!, false));
        }
    }

    private InvitationMailer Mailer(IKnitErpDbContext db, long userId, long organizationId)
    {
        var user = new TestUser { UserId = userId, OrganizationId = organizationId };
        return new InvitationMailer(db, new AccessGuard(db, user, host.Clock), user, host.Clock, host.Mail);
    }

    private async Task<CreatedOrganization> CreateOrganizationAsync()
    {
        int[] w = [2, 4, 10, 3, 5, 9, 4, 6, 8];
        var body = Interlocked.Increment(ref _innSeed).ToString("D9");
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += (body[i] - '0') * w[i];
        }

        var inn = body + (sum % 11 % 10);
        await using var s = host.As(null, null);
        return await s.Organizations.CreateWithOwnerAsync(new CreateOrganizationCommand(
            $"Тестовая организация {inn}", $"Тест {inn}", inn, null, false, "Europe/Moscow", $"owner-{inn}@test.local", $"Владелец {inn}"));
    }
}
