using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Application.Sales;
using KnitErp.Domain.Access;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// D1146: вводный остаток расчётов с покупателем — в долге (баланс, дебиторка по срокам) и в акте сверки; один действующий на юрлицо;
/// аннулирование с причиной; права «Цены и суммы»; чужая организация — 404.
/// </summary>
public sealed class SalesOpeningBalanceTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    [SqlFact]
    public async Task Opening_balance_counts_in_debt_receivables_and_settlement_statement()
    {
        var org = await CreateOrgAsync();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(host.Clock.UtcNow, "Europe/Moscow"));
        long customer, advance, entity, opening, manager;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Старый клиент»", null, null, false, true, null));
            advance = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Аванс»", null, null, false, true, null));
            await using (var db = host.NewDb())
            {
                entity = await db.LegalEntities.Where(e => e.OrganizationId == org.OrganizationId).Select(e => e.Id).FirstAsync();
            }

            var start = today.AddDays(-40);
            opening = await s.Sales.AddOpeningBalanceAsync(customer, entity, start, 5000m, "Сальдо из прежней программы");
            await s.Sales.AddOpeningBalanceAsync(advance, entity, start, -1200m, null);
            Assert.Equal("sales.opening.exists",
                (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Sales.AddOpeningBalanceAsync(customer, entity, start, 100m, null))).Code);
            Assert.Equal("sales.opening.amount",
                (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Sales.AddOpeningBalanceAsync(advance, entity, start, 0m, null))).Code);

            // Баланс: долг 5 000 и аванс 1 200 (долг −1 200).
            var balances = await s.Sales.BalancesAsync();
            Assert.Equal(5000m, balances.Single(b => b.CustomerId == customer).Debt);
            Assert.Equal(-1200m, balances.Single(b => b.CustomerId == advance).Debt);

            // Дебиторка по срокам: 5 000 просрочено на 40 дней (срок — дата остатка).
            var report = await s.Sales.ReceivablesAsync(new ReceivablesFilter());
            var row = report.Rows.Single(r => r.CustomerId == customer);
            Assert.Equal((5000m, 5000m, 40), (row.Debt, row.Days31To60, row.OverdueDays));
            Assert.DoesNotContain(report.Rows, r => r.CustomerId == advance);

            // Акт сверки: остаток до периода — во входящем сальдо, внутри периода — строкой в дебете.
            var before = await s.Settlements.BuildAsync(customer, entity, today.AddDays(-10), today);
            Assert.Equal(5000m, before.Opening);
            var inside = await s.Settlements.BuildAsync(customer, entity, start, today);
            Assert.Equal((0m, 5000m), (inside.Opening, inside.Rows.Single(r => r.Document == "Вводный остаток расчётов").Debit));
            var credit = await s.Settlements.BuildAsync(advance, entity, start, today);
            Assert.Equal(1200m, credit.Rows.Single().Credit);

            // Аннулирование с причиной — долга нет, можно ввести новый остаток.
            var list = await s.Sales.OpeningBalancesAsync(customer);
            await Assert.ThrowsAsync<BusinessRuleException>(() => s.Sales.CancelOpeningBalanceAsync(opening, " ", list.Rows.Single().RowVersion));
            await s.Sales.CancelOpeningBalanceAsync(opening, "Неверная сумма", list.Rows.Single().RowVersion);
            Assert.DoesNotContain(await s.Sales.BalancesAsync(), b => b.CustomerId == customer && b.Debt != 0);
            await s.Sales.AddOpeningBalanceAsync(customer, entity, start, 4500m, null);
            Assert.Equal(2, (await s.Sales.OpeningBalancesAsync(customer)).Rows.Count);

            manager = (await s.Access.InviteAsync(new InviteUserCommand($"storekeeper-{Guid.NewGuid():N}@test.local", "Кладовщик", SystemRoles.Storekeeper, null)))
                .UserId;
        }

        await using (var db = host.NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == manager)).Activate();
            await db.SaveChangesAsync();
        }

        // Без права «Цены и суммы» — отказ; чужая организация — 404.
        await using (var k = host.As(manager, org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => k.Sales.OpeningBalancesAsync(customer));
        }

        var foreign = await CreateOrgAsync();
        await using (var o = host.As(foreign.OwnerUserId, foreign.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => o.Sales.OpeningBalancesAsync(customer));
            await Assert.ThrowsAsync<NotFoundException>(() => o.Sales.CancelOpeningBalanceAsync(opening, "чужое", [1]));
        }
    }

    private static int _innSeed = 822_000_000;

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
