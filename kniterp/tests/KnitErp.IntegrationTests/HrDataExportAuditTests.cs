using KnitErp.Domain.Audit;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>D223: массовые выгрузки данных сотрудников за пределы системы оставляют след в журнале — без имён и чисел.</summary>
public sealed class HrDataExportAuditTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    [SqlFact]
    public async Task Production_scores_export_is_audited_without_names()
    {
        var org = await host.CreateOrganizationAsync();
        await host.HireAsync(org, "0001", "Выгрузкина");
        var from = new DateOnly(2026, 12, 1);
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            Assert.NotEmpty(await s.Scores.ExportAsync(from, from.AddDays(6)));
        }

        await using var db = host.NewDb();
        var entry = await db.AuditEntries.SingleAsync(a => a.OrganizationId == org.OrganizationId && a.Action == AuditActions.ProductionScoresExported);
        Assert.Equal(org.OwnerUserId, entry.ActorUserId);
        Assert.Contains("01.12.2026–07.12.2026", entry.After);
        Assert.DoesNotContain("Выгрузкина", (entry.Before ?? "") + entry.After + entry.Reason);
    }
}
