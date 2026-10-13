using KnitErp.Application.Common;
using KnitErp.Application.Structure;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Hr;
using KnitErp.Domain.Tagging;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>Этап 1 модуля «Сотрудники»: личная карточка Т-2 и персональные данные по 152-ФЗ (D200–D202).</summary>
public sealed class HrPersonalDataTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    [SqlFact]
    public async Task Owner_saves_personal_data_encrypted_and_every_read_is_audited()
    {
        var org = await host.CreateOrganizationAsync();
        var (_, _, employeeId) = await host.HireAsync(org);
        var snils = HrTestKit.NextSnils();
        var inn = HrTestKit.NextPersonInn();

        await using (var hr = host.Hr(org.OwnerUserId, org.OrganizationId))
        {
            await hr.PersonalData.SaveAsync(employeeId, new PersonalDataValues(Gender.Female, "г. Тестовск", "Россия", "4500 000001",
                "Отделом тестов", new DateOnly(2020, 5, 1), "770-001", PersonIds.FormatSnils(snils), inn, "г. Тестовск, ул. Примерная, д. 1",
                null, "+7 (900) 000-00-00", "Среднее профессиональное", "Вязальщица", "Замужем", null), null);
        }

        await using (var hr = host.Hr(org.OwnerUserId, org.OrganizationId))
        {
            var pd = await hr.PersonalData.GetAsync(employeeId);
            Assert.Equal((snils, inn, "4500 000001", Gender.Female), (pd.Values.Snils, pd.Values.Inn, pd.Values.PassportNumber, pd.Values.Gender));
            Assert.Equal(new DateOnly(2020, 5, 1), pd.Values.PassportIssuedOn);
            Assert.True(pd.ConsentWarning);
        }

        // В базе — только шифртекст: ни СНИЛС, ни адреса открытым текстом.
        await using (var raw = new SqlConnection(host.NewDb().Database.GetConnectionString()))
        {
            await raw.OpenAsync();
            await using var cmd = new SqlCommand(
                "SELECT [Snils], [Inn], [RegistrationAddress], [PassportIssuedOn] FROM [kniterp].[hr_personal_data] WHERE [EmployeeId] = @id", raw);
            cmd.Parameters.AddWithValue("@id", employeeId);
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            for (var i = 0; i < 4; i++)
            {
                Assert.StartsWith("enc:v1:", reader.GetString(i));
            }
        }

        await using var db = host.NewDb();
        var audit = await db.AuditEntries.Where(a => a.OrganizationId == org.OrganizationId && a.EntityId == employeeId.ToString()).ToListAsync();
        Assert.Contains(audit, a => a.Action == AuditActions.PersonalDataViewed && a.ActorUserId == org.OwnerUserId);
        var changed = Assert.Single(audit, a => a.Action == AuditActions.PersonalDataChanged);
        Assert.Contains("СНИЛС", changed.After);
        Assert.All(audit, a => Assert.DoesNotContain(snils, (a.Before ?? "") + (a.After ?? "") + (a.Reason ?? "")));
        Assert.All(audit, a => Assert.DoesNotContain("Примерная", (a.Before ?? "") + (a.After ?? "") + (a.Reason ?? "")));
    }

    [SqlFact]
    public async Task Invalid_snils_inn_and_too_young_are_rejected()
    {
        var org = await host.CreateOrganizationAsync();
        var (_, _, employeeId) = await host.HireAsync(org, hiredOn: new DateOnly(2026, 1, 15));
        await using var hr = host.Hr(org.OwnerUserId, org.OrganizationId);

        Assert.Equal("hr.personal_data.snils", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            hr.PersonalData.SaveAsync(employeeId, new PersonalDataValues(Snils: "112-233-445 96"), null))).Code);
        Assert.Equal("hr.personal_data.inn", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            hr.PersonalData.SaveAsync(employeeId, new PersonalDataValues(Inn: "123456789012"), null))).Code);

        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var row = await s.Employees.GetAsync(employeeId);
        Assert.Equal("hr.employee.too_young", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.Employees.SetBirthdayAsync(employeeId, new DateOnly(2012, 3, 1), false, row.RowVersion))).Code);
    }

    [SqlFact]
    public async Task Without_personal_data_right_card_is_visible_but_data_is_not()
    {
        var org = await host.CreateOrganizationAsync();
        var (_, _, employeeId) = await host.HireAsync(org);
        var storekeeper = await host.InviteActiveAsync(org, SystemRoles.Storekeeper);

        await using (var s = host.As(storekeeper, org.OrganizationId))
        {
            Assert.Equal(employeeId, (await s.Employees.GetAsync(employeeId)).Id);
        }

        await using var hr = host.Hr(storekeeper, org.OrganizationId);
        var summary = await hr.PersonalData.SummaryAsync(employeeId);
        Assert.False(summary.CanViewPersonalData);
        Assert.Equal(ConsentState.Missing, summary.State);
        await Assert.ThrowsAsync<AccessDeniedException>(() => hr.PersonalData.GetAsync(employeeId));
        await Assert.ThrowsAsync<AccessDeniedException>(() => hr.PersonalData.CardT2Async(employeeId));
        await Assert.ThrowsAsync<AccessDeniedException>(() => hr.PersonalData.SaveAsync(employeeId, new PersonalDataValues(Phone: "+7 900 000 0000"), null));
    }

    [SqlFact]
    public async Task Employee_reads_only_own_personal_data()
    {
        var org = await host.CreateOrganizationAsync();
        var (_, _, mine) = await host.HireAsync(org, "0001");
        var (_, _, other) = await host.HireAsync(org, "0002", "Сидорова");
        var user = await host.InviteActiveAsync(org, SystemRoles.Employee, employeeId: mine);

        await using var hr = host.Hr(user, org.OrganizationId);
        Assert.Equal(mine, (await hr.PersonalData.GetAsync(mine)).EmployeeId);
        await Assert.ThrowsAsync<NotFoundException>(() => hr.PersonalData.GetAsync(other));
        await Assert.ThrowsAsync<AccessDeniedException>(() => hr.PersonalData.SaveAsync(mine, new PersonalDataValues(Phone: "+7 900 000 0000"), null));
    }

    [SqlFact]
    public async Task Birth_date_is_masked_without_personal_data_right_and_employee_always_sees_own_data()
    {
        // D130: дата рождения — ПДн: в списке и карточке только с правом просмотра ПДн или своя; Excel-выгрузка ПДн не содержит.
        var org = await host.CreateOrganizationAsync();
        var (_, _, mine) = await host.HireAsync(org, "0001");
        var (_, _, other) = await host.HireAsync(org, "0002", "Сидорова");
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            foreach (var id in new[] { mine, other })
            {
                await s.Employees.SetBirthdayAsync(id, new DateOnly(1990, 4, 2), false, (await s.Employees.GetAsync(id)).RowVersion);
            }

            var list = await s.Employees.ListAsync(new EmployeeFilter());
            Assert.True(list.CanEditPersonalData);
            Assert.All(list.Employees, e => Assert.Equal(new DateOnly(1990, 4, 2), e.BirthDate));
        }

        // Кладовщик, он же сотрудник «0001»: видит всех сотрудников без дат рождения, кроме своей; свои ПДн читает без уровня «С» в роли.
        var keeper = await host.InviteActiveAsync(org, SystemRoles.Storekeeper, employeeId: mine);
        await using (var s = host.As(keeper, org.OrganizationId))
        {
            var list = await s.Employees.ListAsync(new EmployeeFilter());
            Assert.False(list.CanEditPersonalData);
            Assert.Equal(new DateOnly(1990, 4, 2), list.Employees.Single(e => e.Id == mine).BirthDate);
            Assert.Null(list.Employees.Single(e => e.Id == other).BirthDate);
            Assert.Null((await s.Employees.GetAsync(other)).BirthDate);
        }

        await using (var hr = host.Hr(keeper, org.OrganizationId))
        {
            Assert.Equal(mine, (await hr.PersonalData.GetAsync(mine)).EmployeeId);
            Assert.True((await hr.PersonalData.SummaryAsync(mine)).CanViewPersonalData);
            await Assert.ThrowsAsync<AccessDeniedException>(() => hr.PersonalData.GetAsync(other));
        }

        // Администратор ведёт сотрудников, но ПДн ему — только индивидуальной выдачей (D200): дат не видит и не меняет.
        var admin = await host.InviteActiveAsync(org, SystemRoles.Administrator);
        await using (var s = host.As(admin, org.OrganizationId))
        {
            var row = await s.Employees.GetAsync(other);
            Assert.Null(row.BirthDate);
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.Employees.SetBirthdayAsync(other, null, false, row.RowVersion));
            Assert.DoesNotContain("Дата рождения", EmployeeExchangeService.Columns);
            var xlsx = await s.EmployeeExchange.ExportAsync();
            var sheet = SqlTestHost.Spreadsheet.ReadFirstSheet(new MemoryStream(xlsx), 1000);
            Assert.DoesNotContain(sheet.SelectMany(r => r), c => c.Contains("1990"));
        }

        await using var db = host.NewDb();
        Assert.Equal(new DateOnly(1990, 4, 2), await db.Employees.Where(e => e.Id == other).Select(e => e.BirthDate).SingleAsync());
        Assert.True(await db.AuditEntries.AnyAsync(a => a.OrganizationId == org.OrganizationId && a.ActorUserId == keeper
                                                        && a.Action == AuditActions.PersonalDataViewed && a.EntityId == mine.ToString()));
    }

    [SqlFact]
    public async Task Consent_lifecycle_and_scans_are_insert_only()
    {
        var org = await host.CreateOrganizationAsync();
        var (_, _, employeeId) = await host.HireAsync(org);
        long scanId;
        await using (var hr = host.Hr(org.OwnerUserId, org.OrganizationId))
        {
            var v = await hr.PersonalData.GiveConsentAsync(employeeId, new DateOnly(2026, 1, 15), new DateOnly(2026, 6, 30), null);
            Assert.Equal(ConsentState.Expired, (await hr.PersonalData.SummaryAsync(employeeId)).State); // «сегодня» тестов — 09.10.2026
            v = await hr.PersonalData.GiveConsentAsync(employeeId, new DateOnly(2026, 7, 1), null, v);
            Assert.Equal(ConsentState.Valid, (await hr.PersonalData.SummaryAsync(employeeId)).State);
            await hr.PersonalData.RevokeConsentAsync(employeeId, new DateOnly(2026, 10, 1), v);
            Assert.Equal(ConsentState.Revoked, (await hr.PersonalData.SummaryAsync(employeeId)).State);

            Assert.Equal("hr.consent.scan_type", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                hr.PersonalData.AddConsentScanAsync(employeeId, "x.exe", "application/octet-stream", [1, 2, 3]))).Code);
            scanId = await hr.PersonalData.AddConsentScanAsync(employeeId, "consent.pdf", "application/pdf", "%PDF-1.4 test"u8.ToArray());
        }

        await using (var hr = host.Hr(org.OwnerUserId, org.OrganizationId))
        {
            var (name, type, content) = await hr.PersonalData.GetConsentScanAsync(scanId);
            Assert.Equal(("consent.pdf", "application/pdf"), (name, type));
            Assert.Equal("%PDF-1.4 test"u8.ToArray(), content);
        }

        await using var db = host.NewDb();
        Assert.True(await db.AuditEntries.AnyAsync(a => a.OrganizationId == org.OrganizationId && a.Action == AuditActions.ConsentScanViewed));
        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            db.Database.ExecuteSqlRawAsync("DELETE FROM [kniterp].[hr_consent_scans] WHERE [Id] = {0}", scanId));
        Assert.Contains("неизменяемы", ex.Message + ex.InnerException?.Message);
    }

    [SqlFact]
    public async Task Another_organization_gets_not_found()
    {
        var a = await host.CreateOrganizationAsync();
        var b = await host.CreateOrganizationAsync();
        var (_, _, employeeId) = await host.HireAsync(a);

        await using var hr = host.Hr(b.OwnerUserId, b.OrganizationId);
        await Assert.ThrowsAsync<NotFoundException>(() => hr.PersonalData.GetAsync(employeeId));
        await Assert.ThrowsAsync<NotFoundException>(() => hr.PersonalData.SaveAsync(employeeId, new PersonalDataValues(Phone: "+7 900 000 0000"), null));
        await using var s = host.As(b.OwnerUserId, b.OrganizationId);
        await Assert.ThrowsAsync<NotFoundException>(() => s.Employees.GetAsync(employeeId));
    }

    [SqlFact]
    public async Task Discussion_and_tags_on_employee_show_only_name_and_position()
    {
        var org = await host.CreateOrganizationAsync();
        var (_, positionId, employeeId) = await host.HireAsync(org);
        await using (var hr = host.Hr(org.OwnerUserId, org.OrganizationId))
        {
            await hr.PersonalData.SaveAsync(employeeId, new PersonalDataValues(Snils: HrTestKit.NextSnils()), null);
        }

        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        await s.Discussions.AddAsync(TaggedEntity.Employee, employeeId, "Проверить допуск к машине", null);
        await s.Tags.AssignAsync(TaggedEntity.Employee, employeeId, null, "Наставник");

        var feed = await s.Discussions.FeedAsync();
        var item = Assert.Single(feed);
        var position = await s.Db.Positions.Where(p => p.Id == positionId).Select(p => p.Name).SingleAsync();
        Assert.Equal(("Петрова Анна Сергеевна", position, $"employees/{employeeId}"), (item.Object.Title, item.Object.Subtitle, item.Object.Href));
        Assert.Single(await s.Tags.ForEntityAsync(TaggedEntity.Employee, employeeId));

        // Кладовщик видит сотрудников только для чтения — обсуждение доступно, но без персональных данных.
        var storekeeper = await host.InviteActiveAsync(org, SystemRoles.Storekeeper);
        await using var k = host.As(storekeeper, org.OrganizationId);
        var thread = await k.Discussions.ThreadAsync(TaggedEntity.Employee, employeeId);
        Assert.Single(thread.Comments);
    }

    [SqlFact]
    public async Task New_organization_owner_has_personal_data_edit_right()
    {
        var org = await host.CreateOrganizationAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var access = await s.Access.GetCurrentAccessAsync();
        Assert.Contains(Permissions.PersonalDataEdit, access.Granted);
    }

    [SqlFact]
    public async Task Rejected_first_save_does_not_leave_record_in_tab_context()
    {
        // У вкладки Blazor один DbContext: отказ проверки не должен оставить новую запись, которая уйдёт со следующим сохранением.
        var org = await host.CreateOrganizationAsync();
        var (_, _, employeeId) = await host.HireAsync(org);
        await using var hr = host.Hr(org.OwnerUserId, org.OrganizationId);
        await Assert.ThrowsAsync<BusinessRuleException>(() => hr.PersonalData.SaveAsync(employeeId, new PersonalDataValues(Snils: "112-233-445 96"), null));
        await hr.PersonalData.SaveAsync(employeeId, new PersonalDataValues(Snils: "112-233-445 95"), null);
        Assert.Equal("11223344595", (await hr.PersonalData.GetAsync(employeeId)).Values.Snils);
        Assert.Equal(1, await hr.Db.EmployeePersonalData.CountAsync(p => p.EmployeeId == employeeId));
    }
}
