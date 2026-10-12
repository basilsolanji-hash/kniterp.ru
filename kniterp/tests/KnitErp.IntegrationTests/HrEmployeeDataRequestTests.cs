using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Hr;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Hr;
using KnitErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// D125: ссылка «заполнить свои данные» — выдача (4 часа, одноразовая, новая отменяет прежнюю), письмо, анкета без входа, проверка
/// кадровиком (принять — в карточку, отклонить — с причиной), права и чужая организация. Данные вымышленные.
/// </summary>
public sealed class HrEmployeeDataRequestTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static readonly byte[] Pdf = "%PDF-1.4 test"u8.ToArray();
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

    [SqlFact]
    public async Task Link_is_mailed_submission_waits_for_review_and_accept_applies_to_the_card()
    {
        var org = await host.CreateOrganizationAsync();
        var (dep, _, emp) = await host.HireAsync(org, "0001", "Анкетова");
        var userId = await host.InviteActiveAsync(org, SystemRoles.Employee, dep, emp);
        string accountEmail;
        await using (var db = host.NewDb())
        {
            accountEmail = await db.Users.Where(u => u.Id == userId).Select(u => u.Email).SingleAsync();
        }

        EmployeeDataLinkDto link;
        await using (var db = host.NewDb())
        {
            link = await Service(db, org.OwnerUserId, org.OrganizationId).IssueAsync(emp);
        }

        Assert.StartsWith("https://erp.test/account/employee-data?token=", link.Link);
        Assert.Equal(host.Clock.UtcNow.AddHours(4), link.ExpiresAtUtc);
        Assert.Equal((true, accountEmail), (link.Sent, link.Email));
        var letter = Assert.Single(host.Mail.Sent, m => m.To == accountEmail);
        Assert.Contains(link.Link, letter.Text);
        Assert.Contains("Не пересылайте", letter.Text);
        Assert.Contains("Владелец", letter.Text); // кто просит
        var token = Token(link);

        // Открытая страница: организация и ФИО, без сохранённых ПДн.
        await using (var db = host.NewDb())
        {
            var form = await Service(db, null, null).DescribeAsync(token);
            Assert.NotNull(form);
            Assert.Equal("Анкетова Анна Сергеевна", form.EmployeeName);
            Assert.Equal(AgreementTemplates.Version, form.Consent.Version);
        }

        var snils = HrTestKit.NextSnils();
        var inn = HrTestKit.NextPersonInn();
        await using (var db = host.NewDb())
        {
            var anon = Service(db, null, null);
            // Без согласия — отказ; неверный СНИЛС — отказ; ссылка после ошибок остаётся рабочей.
            Assert.Equal("hr.employee_data.consent", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                anon.SubmitAsync(token, new EmployeeDataValues(Snils: snils), [], consent: false, "203.0.113.7"))).Code);
            Assert.Equal("hr.personal_data.snils", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                anon.SubmitAsync(token, new EmployeeDataValues(Snils: "112-233-445 96"), [], consent: true, "203.0.113.7"))).Code);
            Assert.Equal("hr.employee_data.empty", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                anon.SubmitAsync(token, new EmployeeDataValues(), [], consent: true, "203.0.113.7"))).Code);
            await anon.SubmitAsync(token, new EmployeeDataValues(BirthDate: new DateOnly(1990, 5, 1), PassportNumber: "4500 000001",
                    PassportIssuer: "ОВД Тестовского района", PassportIssuedOn: new DateOnly(2015, 6, 1), PassportDivisionCode: "770-001",
                    RegistrationAddress: "г. Тестовск, ул. Пробная, 1", Phone: "+7 900 000-00-01", PersonalEmail: "anketova@example.test",
                    Snils: snils, Inn: inn),
                [new EmployeeDataUpload(EmployeeDocumentKind.Passport, "passport.pdf", Pdf), new EmployeeDataUpload(EmployeeDocumentKind.Photo, "me.png", Png)],
                consent: true, "203.0.113.7");
        }

        // Повторная отправка той же ссылкой — нейтральный отказ.
        await using (var db = host.NewDb())
        {
            var anon = Service(db, null, null);
            Assert.Null(await anon.DescribeAsync(token));
            Assert.Equal(EmployeeDataForm.InvalidLinkCode, (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                anon.SubmitAsync(token, new EmployeeDataValues(Phone: "+7 900 000-00-02"), [], true, null))).Code);
        }

        // До принятия карточка не изменилась, заполненность считает только принятое.
        await using (var hr = host.Hr(org.OwnerUserId, org.OrganizationId))
        {
            var before = await hr.PersonalData.CompletenessAsync(emp);
            Assert.Contains("СНИЛС", before.Missing);
            Assert.Contains("Скан паспорта", before.Missing);
            Assert.Null((await hr.PersonalData.GetAsync(emp)).Values.Snils);
        }

        EmployeeDataReviewDto review;
        await using (var db = host.NewDb())
        {
            var service = Service(db, org.OwnerUserId, org.OrganizationId);
            var status = (await service.StatusAsync(emp))!;
            Assert.Equal((1, (DateTime?)null), (status.Pending, status.LinkExpiresAtUtc));
            review = Assert.Single(await service.ReviewAsync(emp));
            Assert.Contains(review.Fields, f => f.Name == "СНИЛС" && f.Current == null && f.Submitted == PersonIds.FormatSnils(snils));
            Assert.Equal([EmployeeDocumentKind.Passport, EmployeeDocumentKind.Photo], review.Files.Select(f => f.Kind).ToArray());
            Assert.Equal("203.0.113.7", review.ClientAddress);
            Assert.True(review.ConsentWillBeRecorded);
            var (_, type, content) = await service.GetFileAsync(review.Files[1].Id);
            Assert.Equal("image/png", type);
            Assert.Equal(Png, content);
            await service.AcceptAsync(review.Id, review.RowVersion);
            await Assert.ThrowsAsync<ConcurrencyConflictException>(() => service.AcceptAsync(review.Id, review.RowVersion));
        }

        await using (var hr = host.Hr(org.OwnerUserId, org.OrganizationId))
        {
            var pd = await hr.PersonalData.GetAsync(emp);
            Assert.Equal((snils, inn, "anketova@example.test", new DateOnly(1990, 5, 1)), (pd.Values.Snils, pd.Values.Inn, pd.Values.PersonalEmail, pd.BirthDate));
            Assert.Equal(ConsentState.Valid, pd.Consent);
            Assert.Equal([EmployeeDocumentKind.Photo, EmployeeDocumentKind.Passport], pd.Scans.Select(s => s.Kind).ToArray());
            var after = await hr.PersonalData.CompletenessAsync(emp);
            Assert.DoesNotContain("СНИЛС", after.Missing);
            Assert.DoesNotContain("Скан паспорта", after.Missing);
            Assert.DoesNotContain("Действующее согласие на обработку ПДн", after.Missing);
        }

        await using (var db = host.NewDb())
        {
            var audit = await db.AuditEntries.Where(a => a.OrganizationId == org.OrganizationId).Select(a => new { a.Action, a.After, a.Reason }).ToListAsync();
            foreach (var action in new[] { AuditActions.EmployeeDataLinkIssued, AuditActions.EmployeeDataLinkEmailed, AuditActions.EmployeeDataSubmitted,
                         AuditActions.EmployeeDataViewed, AuditActions.EmployeeDataAccepted, AuditActions.PersonalDataChanged })
            {
                Assert.Contains(audit, a => a.Action == action);
            }

            // В журнале — только названия полей, без значений ПДн.
            Assert.DoesNotContain(audit, a => (a.After + a.Reason).Contains(snils) || (a.After + a.Reason).Contains(inn)
                                              || (a.After + a.Reason).Contains("Тестовск") || (a.After + a.Reason).Contains("203.0.113.7"));

            // Значения анкеты в базе зашифрованы и не меняются (триггер); решение — один раз.
            var raw = await db.Database.SqlQueryRaw<string>(
                "SELECT [Snils] AS [Value] FROM [kniterp].[hr_employee_data_submissions] WHERE [Id] = {0}", review.Id).SingleAsync();
            Assert.StartsWith("enc:", raw);
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
                "UPDATE [kniterp].[hr_employee_data_submissions] SET [Phone] = NULL WHERE [Id] = {0}", review.Id));
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
                "UPDATE [kniterp].[hr_employee_data_submissions] SET [Status] = 3, [RejectReason] = N'x' WHERE [Id] = {0}", review.Id));
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
                "DELETE FROM [kniterp].[hr_employee_data_submission_files] WHERE [OrganizationId] = {0}", org.OrganizationId));
        }
    }

    [SqlFact]
    public async Task Link_expires_after_four_hours_and_new_link_cancels_previous()
    {
        var org = await host.CreateOrganizationAsync();
        var (_, _, emp) = await host.HireAsync(org, "0001", "Срокова");
        var start = host.Clock.UtcNow;
        try
        {
            string first, second;
            await using (var db = host.NewDb())
            {
                var service = Service(db, org.OwnerUserId, org.OrganizationId);
                var issued = await service.IssueAsync(emp);
                Assert.Null(issued.Sent); // ни учётной записи, ни личной почты — письма нет, ссылка на экране
                first = Token(issued);
                second = Token(await service.IssueAsync(emp));
            }

            await using (var db = host.NewDb())
            {
                var anon = Service(db, null, null);
                Assert.Null(await anon.DescribeAsync(first)); // отменена новой ссылкой
                Assert.Null(await anon.DescribeAsync("неизвестная-ссылка-неизвестная-ссылка-неизвестная"));
                Assert.Equal(EmployeeDataForm.InvalidLinkCode, (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                    anon.SubmitAsync(first, new EmployeeDataValues(Phone: "+7 900 000-00-03"), [], true, null))).Code);
                Assert.NotNull(await anon.DescribeAsync(second));
            }

            host.Clock.UtcNow = start.AddHours(4).AddMinutes(-1);
            await using (var db = host.NewDb())
            {
                Assert.NotNull(await Service(db, null, null).DescribeAsync(second));
            }

            host.Clock.UtcNow = start.AddHours(4);
            await using (var db = host.NewDb())
            {
                var anon = Service(db, null, null);
                Assert.Null(await anon.DescribeAsync(second));
                var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                    anon.SubmitAsync(second, new EmployeeDataValues(Phone: "+7 900 000-00-03"), [], true, null));
                Assert.Equal((EmployeeDataForm.InvalidLinkCode, EmployeeDataForm.InvalidLinkMessage), (ex.Code, ex.Message));
            }

            await using (var db = host.NewDb())
            {
                Assert.Equal(0, await db.EmployeeDataSubmissions.CountAsync(s => s.OrganizationId == org.OrganizationId));
                Assert.Equal(1, await db.EmployeeDataRequests.CountAsync(r => r.OrganizationId == org.OrganizationId && r.Status == EmployeeDataRequestStatus.Cancelled));
            }
        }
        finally
        {
            host.Clock.UtcNow = start;
        }
    }

    [SqlFact]
    public async Task Reject_keeps_the_card_and_other_organization_or_missing_right_is_refused()
    {
        var org = await host.CreateOrganizationAsync();
        var other = await host.CreateOrganizationAsync();
        var (_, _, emp) = await host.HireAsync(org, "0001", "Отказова");
        string token;
        await using (var db = host.NewDb())
        {
            token = Token(await Service(db, org.OwnerUserId, org.OrganizationId).IssueAsync(emp));
        }

        await using (var db = host.NewDb())
        {
            await Service(db, null, null).SubmitAsync(token, new EmployeeDataValues(Phone: "+7 900 000-00-04"), [], true, null);
        }

        // Чужая организация — «не найдено» на всё.
        await using (var db = host.NewDb())
        {
            var stranger = Service(db, other.OwnerUserId, other.OrganizationId);
            await Assert.ThrowsAsync<NotFoundException>(() => stranger.IssueAsync(emp));
            await Assert.ThrowsAsync<NotFoundException>(() => stranger.ReviewAsync(emp));
            var id = await db.EmployeeDataSubmissions.Where(s => s.EmployeeId == emp).Select(s => new { s.Id, s.RowVersion }).SingleAsync();
            await Assert.ThrowsAsync<NotFoundException>(() => stranger.AcceptAsync(id.Id, id.RowVersion));
            await Assert.ThrowsAsync<NotFoundException>(() => stranger.RejectAsync(id.Id, "чужая", id.RowVersion));
        }

        // Без права на сотрудников и ПДн — отказ (кладовщик); у сотрудника — тоже.
        var storekeeper = await host.InviteActiveAsync(org, SystemRoles.Storekeeper);
        await using (var db = host.NewDb())
        {
            var s = Service(db, storekeeper, org.OrganizationId);
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.IssueAsync(emp));
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.ReviewAsync(emp));
            Assert.Null(await s.StatusAsync(emp));
        }

        await using (var db = host.NewDb())
        {
            var service = Service(db, org.OwnerUserId, org.OrganizationId);
            var review = Assert.Single(await service.ReviewAsync(emp));
            Assert.Equal("hr.employee_data.reason", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                service.RejectAsync(review.Id, " ", review.RowVersion))).Code);
            await service.RejectAsync(review.Id, "Телефон не тот", review.RowVersion);
        }

        await using (var db = host.NewDb())
        {
            var service = Service(db, org.OwnerUserId, org.OrganizationId);
            var status = (await service.StatusAsync(emp))!;
            Assert.Equal((0, "Телефон не тот"), (status.Pending, status.LastRejectReason));
            Assert.Empty(await service.ReviewAsync(emp));
            var id = await db.EmployeeDataSubmissions.Where(s => s.EmployeeId == emp).Select(s => new { s.Id, s.RowVersion }).SingleAsync();
            Assert.Equal("hr.employee_data.reviewed", (await Assert.ThrowsAsync<BusinessRuleException>(() => service.AcceptAsync(id.Id, id.RowVersion))).Code);
        }

        await using (var hr = host.Hr(org.OwnerUserId, org.OrganizationId))
        {
            Assert.Null((await hr.PersonalData.GetAsync(emp)).Values.Phone);
        }
    }

    [SqlFact]
    public async Task Personal_email_from_the_card_receives_the_letter_and_no_mail_without_smtp()
    {
        var org = await host.CreateOrganizationAsync();
        var (_, _, emp) = await host.HireAsync(org, "0001", "Почтова");
        await using (var hr = host.Hr(org.OwnerUserId, org.OrganizationId))
        {
            await hr.PersonalData.SaveAsync(emp, new PersonalDataValues(PersonalEmail: "pochtova@example.test"), null);
        }

        await using (var db = host.NewDb())
        {
            var link = await Service(db, org.OwnerUserId, org.OrganizationId).IssueAsync(emp);
            Assert.Equal((true, "pochtova@example.test"), (link.Sent, link.Email));
        }

        host.Mail.IsConfigured = false;
        try
        {
            await using var db = host.NewDb();
            var link = await Service(db, org.OwnerUserId, org.OrganizationId).IssueAsync(emp);
            Assert.Equal((false, (bool?)null), (link.MailAvailable, link.Sent));
            Assert.Single(host.Mail.Sent, m => m.To == "pochtova@example.test");
        }
        finally
        {
            host.Mail.IsConfigured = true;
        }
    }

    private EmployeeDataRequestService Service(KnitErpDbContext db, long? userId, long? organizationId)
    {
        var user = new TestUser { UserId = userId, OrganizationId = organizationId };
        return new EmployeeDataRequestService(db, new AccessGuard(db, user, host.Clock), user, host.Clock, host.Mail);
    }

    private static string Token(EmployeeDataLinkDto link) => Uri.UnescapeDataString(link.Link[(link.Link.IndexOf("token=", StringComparison.Ordinal) + 6)..]);
}
