using KnitErp.Domain.Common;
using KnitErp.Domain.Hr;

namespace KnitErp.UnitTests;

/// <summary>Правила персональных данных (D201): СНИЛС, ИНН физлица, телефон, согласие. Номера — вымышленные.</summary>
public sealed class HrPersonalDataRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 11);
    private static readonly DateTime Now = new(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("112-233-445 95", true)]
    [InlineData("11223344595", true)]
    [InlineData("112-233-445 96", false)]
    [InlineData("001-001-998 00", true)] // до 001-001-998 контрольное число не проверяется
    [InlineData("1122334459", false)]
    [InlineData(null, false)]
    public void Snils_checksum(string? snils, bool valid) => Assert.Equal(valid, PersonIds.IsValidSnils(snils));

    [Fact]
    public void Snils_sum_over_101_uses_remainder()
    {
        // 9-9-9…: сумма 9·45 = 405, 405 mod 101 = 1 → «01».
        Assert.True(PersonIds.IsValidSnils("99999999901"));
        Assert.False(PersonIds.IsValidSnils("99999999900"));
        Assert.Equal("999-999-999 01", PersonIds.FormatSnils("99999999901"));
    }

    [Fact]
    public void Person_inn_needs_twelve_digits_with_checksums()
    {
        Assert.True(PersonIds.IsValidPersonInn("500100732259"));
        Assert.False(PersonIds.IsValidPersonInn("500100732258"));
        Assert.False(PersonIds.IsValidPersonInn("7707083893"));
    }

    [Fact]
    public void Update_returns_changed_field_names_without_values()
    {
        var pd = EmployeePersonalData.Create(1, 2, Now);
        var changed = pd.Update(new PersonalDataValues(Snils: "112-233-445 95", Phone: "+7 (900) 000-00-00"), Today, Now);
        Assert.Equal(["СНИЛС", "Телефон"], changed);
        Assert.Equal("11223344595", pd.Snils);
        Assert.Empty(pd.Update(new PersonalDataValues(Snils: "11223344595", Phone: "+7 (900) 000-00-00"), Today, Now));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12")]
    public void Bad_phone_is_rejected(string phone) =>
        Assert.Equal("hr.personal_data.phone",
            Assert.Throws<BusinessRuleException>(() => EmployeePersonalData.Create(1, 2, Now).Update(new PersonalDataValues(Phone: phone), Today, Now)).Code);

    [Fact]
    public void Passport_division_code_and_date_are_checked()
    {
        var pd = EmployeePersonalData.Create(1, 2, Now);
        Assert.Equal("hr.personal_data.division",
            Assert.Throws<BusinessRuleException>(() => pd.Update(new PersonalDataValues(PassportDivisionCode: "770001"), Today, Now)).Code);
        Assert.Equal("hr.personal_data.passport_date",
            Assert.Throws<BusinessRuleException>(() => pd.Update(new PersonalDataValues(PassportIssuedOn: Today.AddDays(1)), Today, Now)).Code);
    }

    [Fact]
    public void Consent_states()
    {
        var pd = EmployeePersonalData.Create(1, 2, Now);
        Assert.Equal(ConsentState.Missing, pd.ConsentOn(Today));
        pd.GiveConsent(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), Today, Now);
        Assert.Equal(ConsentState.Valid, pd.ConsentOn(Today));
        Assert.Equal(ConsentState.Expired, pd.ConsentOn(new DateOnly(2027, 1, 1)));
        pd.RevokeConsent(Today, Now);
        Assert.Equal(ConsentState.Revoked, pd.ConsentOn(Today));
        Assert.Equal("hr.consent.none", Assert.Throws<BusinessRuleException>(() => pd.RevokeConsent(Today, Now)).Code);
        Assert.Equal("hr.consent.period",
            Assert.Throws<BusinessRuleException>(() => pd.GiveConsent(Today, Today.AddDays(-1), Today, Now)).Code);
        Assert.Equal("hr.consent.future",
            Assert.Throws<BusinessRuleException>(() => pd.GiveConsent(Today.AddDays(1), null, Today, Now)).Code);
    }

    [Fact]
    public void Consent_scan_limits()
    {
        Assert.Equal("hr.consent.scan_size", Assert.Throws<BusinessRuleException>(() =>
            ConsentScan.Create(1, 2, "a.pdf", "application/pdf", new byte[ConsentScan.MaxBytes + 1], Now, 3)).Code);
        Assert.Equal("c.pdf", ConsentScan.Create(1, 2, "../../c.pdf", "application/pdf", "%PDF-1.4"u8.ToArray(), Now, 3).FileName);
        Assert.Equal("hr.consent.scan_type", Assert.Throws<BusinessRuleException>(() =>
            ConsentScan.Create(1, 2, "x.pdf", "application/pdf", "<html><script>"u8.ToArray(), Now, 3)).Code);
    }
}
