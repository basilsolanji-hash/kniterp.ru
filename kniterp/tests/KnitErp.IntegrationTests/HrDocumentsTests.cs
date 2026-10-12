using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Hr;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>D226: документы личного дела (паспорт, регистрация, договор, фото…) и заполненность карточки. Файлы вымышленные.</summary>
public sealed class HrDocumentsTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static readonly byte[] Pdf = "%PDF-1.4 test"u8.ToArray();
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

    [SqlFact]
    public async Task Documents_by_kind_raise_completeness_photo_is_image_only_and_views_are_audited()
    {
        var org = await host.CreateOrganizationAsync();
        var (_, _, emp) = await host.HireAsync(org, "0001", "Документова");
        await using var hr = host.Hr(org.OwnerUserId, org.OrganizationId);
        var before = await hr.PersonalData.CompletenessAsync(emp);
        Assert.Contains("Скан паспорта", before.Missing);
        Assert.Contains("Скан трудового договора", before.Missing);

        await hr.PersonalData.AddDocumentAsync(emp, EmployeeDocumentKind.Passport, "passport.pdf", "application/pdf", Pdf);
        await hr.PersonalData.AddDocumentAsync(emp, EmployeeDocumentKind.Contract, "contract.pdf", "application/pdf", Pdf);
        var photo = await hr.PersonalData.AddDocumentAsync(emp, EmployeeDocumentKind.Photo, "photo.png", "image/png", Png);
        Assert.Equal("hr.consent.scan_type", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            hr.PersonalData.AddDocumentAsync(emp, EmployeeDocumentKind.Photo, "photo.pdf", "application/pdf", Pdf))).Code);
        Assert.Equal("hr.document.kind", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            hr.PersonalData.AddDocumentAsync(emp, (EmployeeDocumentKind)99, "x.pdf", "application/pdf", Pdf))).Code);

        var after = await hr.PersonalData.CompletenessAsync(emp);
        Assert.DoesNotContain("Скан паспорта", after.Missing);
        Assert.DoesNotContain("Фотография", after.Missing);
        Assert.Equal((before.Items.Count(i => i.Done) + 3) * 100 / before.Items.Count, after.Percent); // плюс три пункта из четырнадцати
        var list = (await hr.PersonalData.CompletenessListAsync())!;
        Assert.Equal(after.Percent, list.Employees[emp].Percent);
        Assert.Equal(0, list.Count("Скан паспорта"));

        var docs = (await hr.PersonalData.GetAsync(emp)).Scans;
        Assert.Equal([EmployeeDocumentKind.Photo, EmployeeDocumentKind.Contract, EmployeeDocumentKind.Passport], docs.Select(d => d.Kind).ToArray());
        var (_, contentType, content) = await hr.PersonalData.GetConsentScanAsync(photo);
        Assert.Equal("image/png", contentType);
        Assert.Equal(Png, content);
        await using (var db = host.NewDb())
        {
            var audit = await db.AuditEntries.Where(a => a.OrganizationId == org.OrganizationId).Select(a => new { a.Action, a.After }).ToListAsync();
            Assert.Equal(3, audit.Count(a => a.Action == AuditActions.EmployeeDocumentAdded));
            Assert.Contains(audit, a => a.Action == AuditActions.EmployeeDocumentViewed && a.After!.StartsWith("Фотография"));
        }

        // Без права на ПДн и на ведение сотрудников — показателя нет (null, не отказ: он не засоряет журнал).
        var storekeeper = await host.InviteActiveAsync(org, SystemRoles.Storekeeper);
        await using var other = host.Hr(storekeeper, org.OrganizationId);
        Assert.Null(await other.PersonalData.CompletenessListAsync());
    }
}
