using KnitErp.Application.Common;
using KnitErp.Application.Crm;
using KnitErp.Domain.Access;
using KnitErp.Domain.Common;
using KnitErp.Domain.Crm;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// D650–D652 (требование владельца 13.10, п. 2): новая организация получает полный набор типовых источников и причин отказа;
/// новые виды активностей проходят ограничение базы; «свой вариант» добавляет только пользователь с правом настроек CRM, дубль — отказ.
/// </summary>
public sealed class CrmReferenceDefaultsTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    [SqlFact]
    public async Task New_organization_gets_full_reference_sets_and_new_activity_kinds_are_stored()
    {
        var org = await host.CreateCrmOrganizationAsync();
        var manager = await host.CrmUserAsync(org, SystemRoles.SalesManager);

        await using (var crm = host.Crm(org.OwnerUserId, org.OrganizationId))
        {
            var settings = await crm.Settings.GetAsync();
            Assert.Equal(CrmReference.DefaultSources, settings.Sources.Select(s => s.Name).ToList());
            Assert.Equal(CrmReference.DefaultLossReasons, settings.LossReasons.Select(s => s.Name).ToList());
            Assert.Equal(CrmReference.DefaultSources.Count, CrmReference.DefaultSources.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(CrmReference.DefaultLossReasons.Count, CrmReference.DefaultLossReasons.Distinct(StringComparer.OrdinalIgnoreCase).Count());

            // «Свой вариант» Владельцем: создаётся и виден в списках выбора; повтор того же названия — отказ, а не дубль.
            var own = await crm.Settings.CreateReferenceAsync(CrmReferenceKind.LossReason, "Не подошёл цвет пряжи");
            Assert.Contains((await crm.Deals.OptionsAsync()).LossReasons, r => r.Id == own);
            Assert.Equal("crm.reference.duplicate", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                crm.Settings.CreateReferenceAsync(CrmReferenceKind.LossReason, "Не подошёл цвет пряжи"))).Code);
        }

        await using (var crm = host.Crm(manager, org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => crm.Settings.CreateReferenceAsync(CrmReferenceKind.Source, "Сарафанное радио"));

            long customer;
            await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
            {
                customer = await s.Counterparties.CreateAsync(new KnitErp.Application.Catalog.CounterpartyCommand($"ООО «Виды {Guid.NewGuid():N}»"[..30], null, null, false, true, null));
            }

            var deal = await crm.Deals.CreateAsync(new CrmDealValues("Показ коллекции", customer));
            foreach (var kind in new[] { CrmActivityKind.VideoCall, CrmActivityKind.FactoryVisit, CrmActivityKind.Exhibition })
            {
                await crm.Activities.CreateAsync(new CrmActivityCommand(CrmTarget.Deal, deal, kind, CrmActivity.KindName(kind), Done: true));
            }

            Assert.Equal(3, await crm.Db.CrmActivities.CountAsync(a => a.TargetId == deal && a.Kind >= CrmActivityKind.VideoCall));
        }
    }
}
