using KnitErp.Domain.Access;
using KnitErp.Domain.Common;
using KnitErp.Domain.Hr;

namespace KnitErp.UnitTests;

/// <summary>D125: ссылка анкеты сотрудника (4 часа, одноразовая), проверка значений анкеты и файлов. Номера — вымышленные.</summary>
public sealed class HrEmployeeDataRequestRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 12);
    private static readonly DateOnly HiredOn = new(2026, 1, 15);
    private static readonly DateTime Now = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    private static EmployeeDataRequest NewRequest(out string token)
    {
        token = SetupTokens.Generate();
        return EmployeeDataRequest.Issue(1, 2, SetupTokens.Hash(token), 3, Now);
    }

    [Fact]
    public void Link_lives_exactly_four_hours()
    {
        Assert.Equal(TimeSpan.FromHours(4), EmployeeDataRequest.Lifetime);
        var request = NewRequest(out var token);
        Assert.Equal(Now.AddHours(4), request.ExpiresAtUtc);
        Assert.True(request.IsOpen(Now.AddHours(4).AddMilliseconds(-1)));
        Assert.False(request.IsOpen(Now.AddHours(4)));
        Assert.Equal(43, token.Length); // 32 случайных байта в base64url без «=»
        Assert.Equal(32, request.TokenHash.Length);
        Assert.True(SetupTokens.Matches(token, request.TokenHash));
    }

    [Fact]
    public void Link_is_one_time_and_expired_or_cancelled_link_is_refused_with_the_same_message()
    {
        var used = NewRequest(out _);
        used.MarkSubmitted(Now.AddHours(1));
        var again = Assert.Throws<BusinessRuleException>(() => used.MarkSubmitted(Now.AddHours(1)));

        var expired = NewRequest(out _);
        var late = Assert.Throws<BusinessRuleException>(() => expired.MarkSubmitted(Now.AddHours(4)));

        var cancelled = NewRequest(out _);
        Assert.True(cancelled.Cancel(Now));
        Assert.False(cancelled.Cancel(Now)); // повторная отмена ничего не меняет
        var off = Assert.Throws<BusinessRuleException>(() => cancelled.MarkSubmitted(Now));

        Assert.All(new[] { again, late, off }, ex =>
        {
            Assert.Equal(EmployeeDataForm.InvalidLinkCode, ex.Code);
            Assert.Equal(EmployeeDataForm.InvalidLinkMessage, ex.Message);
        });
        Assert.False(used.Cancel(Now)); // использованная ссылка не отменяется
    }

    [Fact]
    public void Values_are_checked_like_the_card_snils_and_inn_checksums()
    {
        var clean = EmployeeDataForm.Normalize(new EmployeeDataValues(
            BirthDate: new DateOnly(1990, 5, 1), Snils: "112-233-445 95", Inn: "500100732259", Phone: " +7 900 000-00-00 ",
            PersonalEmail: "anna@example.test", PassportDivisionCode: "770-001"), HiredOn, Today, Now);
        Assert.Equal("11223344595", clean.Snils);
        Assert.Equal("+7 900 000-00-00", clean.Phone);
        Assert.Equal(["Дата рождения", "Код подразделения", "Телефон", "Личная почта", "СНИЛС", "ИНН"], clean.FilledFields());

        Assert.Equal("hr.personal_data.snils", Assert.Throws<BusinessRuleException>(() =>
            EmployeeDataForm.Normalize(new EmployeeDataValues(Snils: "112-233-445 96"), HiredOn, Today, Now)).Code);
        Assert.Equal("hr.personal_data.inn", Assert.Throws<BusinessRuleException>(() =>
            EmployeeDataForm.Normalize(new EmployeeDataValues(Inn: "500100732258"), HiredOn, Today, Now)).Code);
        Assert.Equal("hr.personal_data.email", Assert.Throws<BusinessRuleException>(() =>
            EmployeeDataForm.Normalize(new EmployeeDataValues(PersonalEmail: "anna at example"), HiredOn, Today, Now)).Code);
        Assert.Equal("hr.employee.birth_date", Assert.Throws<BusinessRuleException>(() =>
            EmployeeDataForm.Normalize(new EmployeeDataValues(BirthDate: Today.AddYears(-10)), HiredOn, Today, Now)).Code);
        Assert.Equal("hr.employee.too_young", Assert.Throws<BusinessRuleException>(() =>
            EmployeeDataForm.Normalize(new EmployeeDataValues(BirthDate: new DateOnly(2012, 10, 2)), new DateOnly(2026, 10, 1), Today.AddYears(1), Now)).Code);
        Assert.Equal("hr.personal_data.passport_date", Assert.Throws<BusinessRuleException>(() =>
            EmployeeDataForm.Normalize(new EmployeeDataValues(PassportIssuedOn: Today.AddDays(1)), HiredOn, Today, Now)).Code);
    }

    [Fact]
    public void Merge_replaces_only_filled_fields()
    {
        var current = new PersonalDataValues(Gender.Female, Phone: "111-11", Inn: "500100732259", Education: "среднее");
        var merged = new EmployeeDataValues(Phone: "222-22", RegistrationAddress: "г. Тестовск").MergeInto(current);
        Assert.Equal(("222-22", "500100732259", "г. Тестовск", "среднее", Gender.Female),
            (merged.Phone, merged.Inn, merged.RegistrationAddress, merged.Education, merged.Gender));
    }

    [Fact]
    public void Files_type_is_taken_from_signature_photo_is_image_only_and_kinds_are_limited()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0];
        var pdf = "%PDF-1.4 test"u8.ToArray();
        Assert.Equal("image/png", EmployeeDataSubmissionFile.Create(1, EmployeeDocumentKind.Photo, "../../me.png", png).ContentType);
        Assert.Equal("me.png", EmployeeDataSubmissionFile.Create(1, EmployeeDocumentKind.Photo, "../../me.png", png).FileName);
        Assert.Equal("application/pdf", EmployeeDataSubmissionFile.Create(1, EmployeeDocumentKind.Passport, "p.jpg", pdf).ContentType);
        Assert.Equal("hr.consent.scan_type", Assert.Throws<BusinessRuleException>(() =>
            EmployeeDataSubmissionFile.Create(1, EmployeeDocumentKind.Photo, "me.pdf", pdf)).Code);
        Assert.Equal("hr.consent.scan_type", Assert.Throws<BusinessRuleException>(() =>
            EmployeeDataSubmissionFile.Create(1, EmployeeDocumentKind.Passport, "x.pdf", "<html><script>"u8.ToArray())).Code);
        Assert.Equal("hr.consent.scan_size", Assert.Throws<BusinessRuleException>(() =>
            EmployeeDataSubmissionFile.Create(1, EmployeeDocumentKind.Passport, "x.pdf", new byte[ConsentScan.MaxBytes + 1])).Code);
        Assert.Equal("hr.document.kind", Assert.Throws<BusinessRuleException>(() =>
            EmployeeDataSubmissionFile.Create(1, EmployeeDocumentKind.Contract, "x.pdf", pdf)).Code);
    }

    [Fact]
    public void Decision_is_made_once_and_reject_needs_a_reason()
    {
        var request = NewRequest(out _);
        var consent = AgreementTemplates.Render(AgreementKind.PersonalDataConsent,
            new AgreementData("ООО «Тест»", "7700000000", null, "Петрова Анна", "0001", null, null, null, null, null, Today));
        var submission = EmployeeDataSubmission.Create(request, new EmployeeDataValues(Phone: "222-22"), consent, "203.0.113.5", Now);
        Assert.Equal((consent.Version, consent.Hash, "203.0.113.5"), (submission.ConsentVersion, submission.ConsentTextHash, submission.ClientAddress));
        Assert.Equal("hr.employee_data.reason", Assert.Throws<BusinessRuleException>(() => submission.Reject(5, "  ", Now)).Code);
        submission.Reject(5, "Паспорт не читается", Now);
        Assert.Equal("hr.employee_data.reviewed", Assert.Throws<BusinessRuleException>(() => submission.Accept(5, Now)).Code);
    }

    [Theory]
    [InlineData("anna@example.test", true)]
    [InlineData("a.b-c@mail.example.ru", true)]
    [InlineData("anna@", false)]
    [InlineData("anna@@example.test", false)]
    [InlineData("anna@example", false)]
    [InlineData("an na@example.test", false)]
    [InlineData("<x>@example.test", false)]
    public void Personal_email_simple_check(string email, bool valid) => Assert.Equal(valid, EmployeePersonalData.IsEmail(email));
}
