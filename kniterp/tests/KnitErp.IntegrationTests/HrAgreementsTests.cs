using KnitErp.Application.Access;
using KnitErp.Application.Hr;
using KnitErp.Domain.Access;
using KnitErp.Domain.Common;
using KnitErp.Domain.Hr;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>D227: документы перед доступом — заполнены данными сотрудника, подписываются паролем (ПЭП), печатаются, подпись неизменяема.</summary>
public sealed class HrAgreementsTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private const string Password = "Сотрудник-тест-пароль-1";

    [SqlFact]
    public async Task Employee_signs_agreements_with_password_and_hr_sees_prints_and_marks_paper()
    {
        var org = await host.CreateOrganizationAsync();
        var (_, _, emp) = await host.HireAsync(org, "0007", "Подписева");
        var (_, _, second) = await host.HireAsync(org, "0008", "Бумажная");
        var worker = await host.InviteActiveAsync(org, SystemRoles.Employee, employeeId: emp);
        await using (var db = host.NewDb())
        {
            var user = await db.Users.SingleAsync(u => u.Id == worker);
            user.UpgradePasswordHash(new PasswordHasher<UserAccount>().HashPassword(user, Password));
            await db.SaveChangesAsync();
        }

        await using (var db = host.NewDb())
        {
            var me = Service(db, worker, org.OrganizationId);
            var pending = await me.PendingAsync();
            Assert.Equal(AgreementTemplates.All, pending.Select(p => p.Text.Kind).ToArray());
            var consent = string.Join("\n", pending[0].Text.Paragraphs);
            Assert.Contains("Подписева Анна Сергеевна", consent);
            Assert.Contains("паспорт ________________", consent); // ПДн не заполнены — строка для заполнения от руки
            Assert.Contains("Тестовая фабрика", consent);
            Assert.Equal("hr.agreement.password", (await Assert.ThrowsAsync<BusinessRuleException>(() => me.SignAsync("не тот"))).Code);
            Assert.Equal(3, (await me.PendingAsync()).Count);
            Assert.Equal(3, await me.SignAsync(Password));
            Assert.Empty(await me.PendingAsync());
            Assert.Equal(0, await me.SignAsync(Password)); // повторно подписывать нечего
        }

        await using (var db = host.NewDb())
        {
            var hr = Service(db, org.OwnerUserId, org.OrganizationId);
            var status = await hr.StatusAsync(emp);
            Assert.All(status, s => Assert.Equal((AgreementMethod.Electronic, true), (s.Signature!.Method, s.Signature.TextMatches)));
            var print = await hr.PrintAsync(emp, AgreementKind.Confidentiality);
            Assert.Equal(print.Text.Hash, print.Signature!.TextHash);
            Assert.True(await db.AuditEntries.AnyAsync(a => a.OrganizationId == org.OrganizationId && a.Action == KnitErp.Domain.Audit.AuditActions.AgreementPrinted));

            await hr.MarkPaperAsync(second, AgreementKind.PersonalDataConsent);
            Assert.Equal(AgreementMethod.Paper, (await hr.StatusAsync(second))[0].Signature!.Method);
            Assert.Null((await hr.StatusAsync(second))[1].Signature);
        }

        // Подпись неизменяема (триггер в базе).
        await using (var db = host.NewDb())
        {
            db.EmployeeAgreements.Remove(await db.EmployeeAgreements.FirstAsync(a => a.EmployeeId == emp));
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        // Заполненность учитывает подписанные документы.
        await using (var hrs = host.Hr(org.OwnerUserId, org.OrganizationId))
        {
            Assert.DoesNotContain("Подписаны согласие, правила работы и обязательство о конфиденциальности", (await hrs.PersonalData.CompletenessAsync(emp)).Missing);
            Assert.Contains("Подписаны согласие, правила работы и обязательство о конфиденциальности", (await hrs.PersonalData.CompletenessAsync(second)).Missing);
            await hrs.Settings.SaveRulesAsync(new HrRulesValues(RequireAgreements: false), (await hrs.Settings.GetAsync()).RowVersion);
        }

        var secondUser = await host.InviteActiveAsync(org, SystemRoles.Employee, employeeId: second);
        await using (var db = host.NewDb())
        {
            Assert.Empty(await Service(db, secondUser, org.OrganizationId).PendingAsync()); // правило выключено
        }
    }

    private EmployeeAgreementService Service(Infrastructure.Persistence.KnitErpDbContext db, long userId, long organizationId)
    {
        var user = new TestUser { UserId = userId, OrganizationId = organizationId };
        return new EmployeeAgreementService(db, new AccessGuard(db, user, host.Clock), user, host.Clock, new PasswordHasher<UserAccount>());
    }
}
