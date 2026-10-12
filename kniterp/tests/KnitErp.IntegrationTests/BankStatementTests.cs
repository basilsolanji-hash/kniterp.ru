using System.Text;
using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Finance;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Finance;
using KnitErp.Domain.Sales;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// D119 (А1): выписка банка → оплаты покупателей и поставщикам. Контрагент — по ИНН, заказ — по номеру в назначении,
/// повторная загрузка без дублей, неизвестный ИНН и переводы между своими счетами — «нужно решение», права как у ручного ввода.
/// </summary>
public sealed class BankStatementTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private const string OurAccount = "40702810938000000001";
    private static int _innSeed = 860_000_000;

    [SqlFact]
    public async Task Statement_creates_allocated_payments_once_and_lists_what_needs_a_decision()
    {
        var org = await CreateOrgAsync();
        var customerInn = NextInn();
        var supplierInn = NextInn();
        long customer, order;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var entity = (await s.LegalEntities.ListAsync()).Single();
            await s.LegalEntities.AddAccountAsync(entity.Id, new LegalEntityAccountCommand("ПАО «Тестбанк»", "044525225", OurAccount, null));
            var units = await s.Catalog.ListUnitsAsync();
            var store = await s.Warehouses.CreateWarehouseAsync("Склад", null);
            var item = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-1", "Свитер", ItemType.Finished, units.Single(u => u.Symbol == "шт").Id, null));
            customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Магазин»", customerInn, null, false, true, null));
            await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Пряжа»", supplierInn, null, true, false, null));
            order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(new DateOnly(2026, 10, 1), customer, store, null, null, true, null));
            await s.Sales.SetOrderLineAsync(order, item, 1, 1000m, null, (await s.Sales.GetOrderAsync(order)).RowVersion);
            await s.Sales.ConfirmOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion);
        }

        string orderNumber;
        await using (var db = host.NewDb())
        {
            orderNumber = await db.SalesOrders.Where(o => o.Id == order).Select(o => o.Number).SingleAsync();
        }

        var file = Statement(
            Doc("101", "05.10.2026", "600.00", "40702810500000000077", customerInn, "ООО \"Магазин\"", OurAccount, $"Оплата по заказу {orderNumber}"),
            Doc("7", "06.10.2026", "300.00", OurAccount, null, "ООО \"Фабрика\"", "40702810600000000088", "Оплата за пряжу", payeeInn: supplierInn),
            Doc("55", "06.10.2026", "50.00", "40702810100000000099", NextInn(), "ООО \"Незнакомец\"", OurAccount, "Аванс"));

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var preview = await s.BankStatements.PreviewAsync(file);
            Assert.Equal(
                [(BankLineStatus.Ready, (long?)order), (BankLineStatus.Ready, null), (BankLineStatus.UnknownCounterparty, null)],
                preview.Lines.Select(l => (l.Status, l.Direction == BankLineDirection.Incoming ? l.OrderId : null)).ToArray());
            var result = await s.BankStatements.ImportAsync(file, null);
            Assert.Equal((2, 600m, 300m), (result.Created, result.Incoming, result.Outgoing));
        }

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            // Повторная загрузка той же выписки — строки «уже загружено», оплат не прибавилось.
            var again = await s.BankStatements.PreviewAsync(file);
            Assert.Equal(2, again.Lines.Count(l => l.Status == BankLineStatus.Duplicate));
            Assert.Equal(0, (await s.BankStatements.ImportAsync(file, null)).Created);
            var payment = await s.Db.CustomerPayments.SingleAsync(p => p.OrganizationId == org.OrganizationId);
            Assert.Equal(("101", 600m), (payment.DocumentNumber, payment.Amount));
            Assert.Equal(600m, await s.Db.CustomerPaymentAllocations.Where(a => a.PaymentId == payment.Id && a.OrderId == order && a.RemovedAtUtc == null)
                .SumAsync(a => a.Amount));
            Assert.Equal(1, await s.Db.SupplierPayments.CountAsync(p => p.OrganizationId == org.OrganizationId));
            Assert.Equal(2, await s.Db.BankStatementLines.CountAsync(l => l.OrganizationId == org.OrganizationId));
            // Журнал строк выписки — только вставка: удаление открыло бы повторную загрузку.
            await Assert.ThrowsAnyAsync<Exception>(() => s.Db.Database.ExecuteSqlRawAsync(
                "DELETE FROM [kniterp].[bank_statement_lines] WHERE [OrganizationId] = {0}", org.OrganizationId));
        }

        // Без прав на оплаты — отказ; счёт не из «Счетов и касс» — понятная ошибка.
        var keeper = await InviteActiveAsync(org, SystemRoles.Storekeeper);
        await using (var s = host.As(keeper, org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.BankStatements.PreviewAsync(file));
        }

        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var foreign = Statement(Doc("1", "05.10.2026", "10.00", "40702810500000000077", customerInn, "ООО \"Магазин\"", "40702810999999999999", "x"))
                .Select(b => b).ToArray();
            var text = Encoding.GetEncoding(1251).GetString(foreign).Replace("РасчСчет=" + OurAccount, "РасчСчет=40702810999999999999");
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => s.BankStatements.PreviewAsync(Encoding.GetEncoding(1251).GetBytes(text)));
            Assert.Equal("bank.statement.account", ex.Code);
        }
    }

    private static string Doc(string number, string date, string amount, string payerAccount, string? payerInn, string payer, string payeeAccount,
        string purpose, string? payeeInn = null) =>
        $"""
         СекцияДокумент=Платежное поручение
         Номер={number}
         Дата={date}
         Сумма={amount}
         ПлательщикСчет={payerAccount}
         ПлательщикИНН={payerInn}
         Плательщик1={payer}
         ПолучательСчет={payeeAccount}
         ПолучательИНН={payeeInn}
         Получатель1={(payeeAccount == OurAccount ? "ООО \"Фабрика\"" : payer)}
         НазначениеПлатежа={purpose}
         КонецДокумента
         """;

    private static byte[] Statement(params string[] docs)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var text = $"""
                    1CClientBankExchange
                    ВерсияФормата=1.03
                    Кодировка=Windows
                    ДатаНачала=01.10.2026
                    ДатаКонца=11.10.2026
                    РасчСчет={OurAccount}
                    {string.Join("\n", docs)}
                    КонецФайла
                    """;
        return Encoding.GetEncoding(1251).GetBytes(text);
    }

    private async Task<long> InviteActiveAsync(CreatedOrganization org, string role)
    {
        long id;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var store = (await s.Stock.ListWarehousesAsync()).First().Id;
            id = (await s.Access.InviteAsync(new InviteUserCommand($"keeper-{Guid.NewGuid():N}@test.local", "Кладовщик", role, null, WarehouseId: store))).UserId;
        }

        await using var db = host.NewDb();
        (await db.Users.SingleAsync(u => u.Id == id)).Activate();
        await db.SaveChangesAsync();
        return id;
    }

    private static string NextInn()
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
        var inn = NextInn();
        await using var s = host.As(null, null);
        return await s.Organizations.CreateWithOwnerAsync(new CreateOrganizationCommand(
            $"Тестовая организация {inn}", $"Тест {inn}", inn, null, false, "Europe/Moscow", $"owner-{inn}@test.local", $"Владелец {inn}", "RU"));
    }
}
