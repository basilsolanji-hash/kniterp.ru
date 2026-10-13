using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Application.Sales;
using KnitErp.Application.Structure;
using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Sales;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// D1110: видимость заказов по роли — «Своего подразделения» (с вложенными подразделениями) и «Свои»: журнал, карточка (чужое — 404),
/// документы заказа и дебиторка; Владелец (роль без настройки) видит всё.
/// </summary>
public sealed class SalesVisibilityTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static readonly DateOnly Day = new(2026, 10, 9);

    [SqlFact]
    public async Task Department_visibility_covers_nested_departments_and_hides_the_rest()
    {
        var org = await CreateOrgAsync();
        long head, east, other;
        var orders = new Dictionary<string, long>();
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var sales = await s.Structure.CreateDepartmentAsync("Отдел продаж", null);
            var eastDept = await s.Structure.CreateDepartmentAsync("Продажи — Восток", sales);
            var otherDept = await s.Structure.CreateDepartmentAsync("Розница", null);
            var position = await s.Structure.CreatePositionAsync("Менеджер");
            head = await InviteAsync(s, "head");
            east = await InviteAsync(s, "east");
            other = await InviteAsync(s, "other");
            var people = new[] { (head, sales, "0001"), (east, eastDept, "0002"), (other, otherDept, "0003") };
            var employees = new List<(long Employee, long User)>();
            foreach (var (user, dept, number) in people)
            {
                employees.Add((await s.Employees.HireAsync(new EmployeeCommand(number, "Менеджер", number, null, dept, position, Day)), user));
            }

            await using (var db = host.NewDb())
            {
                foreach (var (employee, user) in employees)
                {
                    (await db.Employees.SingleAsync(e => e.Id == employee)).LinkUser(user);
                    (await db.Users.SingleAsync(u => u.Id == user)).Activate();
                }

                await db.SaveChangesAsync();
            }

            var role = (await s.Agreements.RoleSettingsAsync()).Single(r => r.Role == SystemRoles.NameOf(SystemRoles.SalesManager));
            await s.Agreements.SetRoleSettingsAsync(role.RoleId, null, null, SalesOrderVisibility.Department);

            var unit = (await s.Catalog.ListUnitsAsync()).Single(u => u.Symbol == "шт").Id;
            var store = await s.Warehouses.CreateWarehouseAsync("Склад", null);
            var item = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-1", "Свитер", ItemType.Finished, unit, null));
            foreach (var (name, manager) in new[] { ("head", head), ("east", east), ("other", other) })
            {
                var customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «" + name + "»", null, null, false, true, null));
                await s.Agreements.SetCustomerTermsAsync(customer, null, manager, null);
                var order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, customer, store, null, null, true, null));
                await s.Sales.SetOrderLineAsync(order, item, 1, 1000m, 22m, (await s.Sales.GetOrderAsync(order)).RowVersion);
                orders[name] = order;
            }

            Assert.Equal(3, (await s.Sales.ListOrdersAsync(new SalesOrderFilter())).Count);
        }

        // Руководитель отдела продаж: свои и подчинённого подразделения «Восток»; розница — 404.
        await using (var h = host.As(head, org.OrganizationId))
        {
            Assert.Equal(new[] { orders["head"], orders["east"] }.Order(), (await h.Sales.ListOrdersAsync(new SalesOrderFilter())).Select(o => o.Id).Order());
            await h.Sales.GetOrderAsync(orders["east"]);
            await Assert.ThrowsAsync<NotFoundException>(() => h.Sales.GetOrderAsync(orders["other"]));
            await Assert.ThrowsAsync<NotFoundException>(() => h.OrderDocuments.PrintSetAsync(orders["other"]));
        }

        // Менеджер «Востока»: вложенных подразделений нет — только свои.
        await using (var e = host.As(east, org.OrganizationId))
        {
            Assert.Equal([orders["east"]], (await e.Sales.ListOrdersAsync(new SalesOrderFilter())).Select(o => o.Id));
            await Assert.ThrowsAsync<NotFoundException>(() => e.Sales.GetOrderAsync(orders["head"]));
        }

        // «Свои»: розница видит только свой заказ.
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var role = (await s.Agreements.RoleSettingsAsync()).Single(r => r.Role == SystemRoles.NameOf(SystemRoles.SalesManager));
            await s.Agreements.SetRoleSettingsAsync(role.RoleId, null, null, SalesOrderVisibility.Own);
        }

        await using (var h = host.As(head, org.OrganizationId))
        {
            Assert.Equal([orders["head"]], (await h.Sales.ListOrdersAsync(new SalesOrderFilter())).Select(o => o.Id));
            await Assert.ThrowsAsync<NotFoundException>(() => h.Sales.GetOrderAsync(orders["east"]));
        }
    }

    private async Task<long> InviteAsync(Services s, string name) =>
        (await s.Access.InviteAsync(new InviteUserCommand($"{name}-{Guid.NewGuid():N}@test.local", name, SystemRoles.SalesManager, null))).UserId;

    private static int _innSeed = 821_000_000;

    private static string Inn()
    {
        int[] w = [2, 4, 10, 3, 5, 9, 4, 6, 8];
        var body = Interlocked.Increment(ref _innSeed).ToString("D9");
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += (body[i] - '0') * w[i];
        }

        return body + (sum % 11 % 10);
    }

    private async Task<CreatedOrganization> CreateOrgAsync()
    {
        var inn = Inn();
        await using var s = host.As(null, null);
        return await s.Organizations.CreateWithOwnerAsync(new CreateOrganizationCommand(
            $"Тестовая организация {inn}", $"Тест {inn}", inn, null, false, "Europe/Moscow", $"owner-{inn}-{Guid.NewGuid():N}@test.local", $"Владелец {inn}", "RU"));
    }
}
