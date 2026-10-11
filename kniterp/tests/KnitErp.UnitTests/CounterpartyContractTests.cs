using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;

namespace KnitErp.UnitTests;

/// <summary>D500: правила договора контрагента и файлов договора.</summary>
public sealed class CounterpartyContractTests
{
    private static readonly DateTime Now = new(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);

    private static ContractTerms Terms(DateOnly? from = null, DateOnly? to = null, int? days = 14, decimal? limit = null, string number = " 5/2026 ") =>
        new(number, new DateOnly(2026, 2, 1), ContractKind.Purchase, from, to, null, days, limit, null);

    [Fact]
    public void Contract_is_trimmed_and_validity_is_checked_by_date_and_status()
    {
        var c = CounterpartyContract.Create(1, 2, Terms(new DateOnly(2026, 2, 1), new DateOnly(2026, 12, 31)), Now, 3);
        Assert.Equal("5/2026", c.Number);
        Assert.True(c.IsValidOn(new DateOnly(2026, 6, 1)));
        Assert.False(c.IsValidOn(new DateOnly(2027, 1, 1)));
        c.Close();
        Assert.False(c.IsValidOn(new DateOnly(2026, 6, 1)));
        Assert.Throws<BusinessRuleException>(c.Close);
        c.Reopen();
        Assert.True(c.IsValidOn(new DateOnly(2026, 6, 1)));
    }

    [Theory]
    [InlineData("contract.period", 2026, 5, 1, 2026, 4, 30, 10, null)]
    [InlineData("contract.payment_days", null, null, null, null, null, null, 400, null)]
    [InlineData("contract.amount", null, null, null, null, null, null, 10, -1)]
    public void Invalid_terms_are_rejected(string code, int? fy, int? fm, int? fd, int? ty, int? tm, int? td, int days, int? limit)
    {
        DateOnly? from = fy is { } y ? new DateOnly(y, fm!.Value, fd!.Value) : null;
        DateOnly? to = ty is { } y2 ? new DateOnly(y2, tm!.Value, td!.Value) : null;
        var ex = Assert.Throws<BusinessRuleException>(() => CounterpartyContract.Create(1, 2, Terms(from, to, days, limit), Now, 3));
        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public void Empty_number_is_rejected_and_update_reports_changes()
    {
        Assert.Throws<BusinessRuleException>(() => CounterpartyContract.Create(1, 2, Terms(number: "  "), Now, 3));
        var c = CounterpartyContract.Create(1, 2, Terms(), Now, 3);
        var changes = c.Update(Terms(days: 30, limit: 100.005m));
        Assert.Equal(["Отсрочка, дней", "Лимит суммы"], changes.Select(x => x.Field).ToArray());
        Assert.Equal(100.01m, c.AmountLimit);
    }

    [Fact]
    public void Contract_file_type_is_checked_by_signature_and_name_is_stripped_of_path()
    {
        byte[] pdf = [0x25, 0x50, 0x44, 0x46, 0x2D, 0x31];
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0];
        var f = CounterpartyContractFile.Create(1, 2, @"C:\scans\..\договор.pdf", "Application/PDF", pdf, Now, 3);
        Assert.Equal(("договор.pdf", "application/pdf"), (f.FileName, f.ContentType));
        Assert.Equal("image/png", CounterpartyContractFile.Create(1, 2, "s.png", "image/png", png, Now, 3).ContentType);

        Assert.Equal("contract.file_type", Assert.Throws<BusinessRuleException>(() =>
            CounterpartyContractFile.Create(1, 2, "x.pdf", "application/pdf", "<html>"u8.ToArray(), Now, 3)).Code);
        Assert.Equal("contract.file_type", Assert.Throws<BusinessRuleException>(() =>
            CounterpartyContractFile.Create(1, 2, "x.html", "text/html", pdf, Now, 3)).Code);
        Assert.Equal("contract.file_size", Assert.Throws<BusinessRuleException>(() =>
            CounterpartyContractFile.Create(1, 2, "x.pdf", "application/pdf", new byte[CounterpartyContractFile.MaxBytes + 1], Now, 3)).Code);
    }

    [Fact]
    public void Language_buttons_are_alphabetical_and_default_stays_first_in_all()
    {
        Assert.Equal(["Беларуская", "Қазақша", "Русский", "Oʻzbekcha"], UiLanguages.Menu.Select(l => l.NativeName).ToArray());
        Assert.Equal(UiLanguages.Default, UiLanguages.All[0].Code);
        Assert.Equal(UiLanguages.All.Count, UiLanguages.Menu.Count);
    }
}
