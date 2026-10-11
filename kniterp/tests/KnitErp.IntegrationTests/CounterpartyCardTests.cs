using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Application.Sales;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>D500: карточка контрагента — договоры, сканы, история заказов и документов.</summary>
public sealed class CounterpartyCardTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 610_000_000;
    private static readonly byte[] Pdf = "%PDF-1.7\n%test\n"u8.ToArray();

    private (CounterpartyCardService Cards, Services All) As(long userId, long organizationId)
    {
        var all = host.As(userId, organizationId);
        var user = new TestUser { UserId = userId, OrganizationId = organizationId, ClientAddress = host.ClientAddress };
        return (new CounterpartyCardService(all.Db, new AccessGuard(all.Db, user, host.Clock), user, host.Clock), all);
    }

    private static ContractTerms Terms(string number, decimal? limit = 500_000m) =>
        new(number, new DateOnly(2026, 1, 15), ContractKind.Sale, new DateOnly(2026, 1, 15), new DateOnly(2026, 12, 31),
            "Поставка трикотажа", 30, limit, null);

    [SqlFact]
    public async Task Contract_lifecycle_with_scan_and_audit()
    {
        var org = await CreateOrganizationAsync();
        var (cards, s) = As(org.OwnerUserId, org.OrganizationId);
        await using var _ = s;
        var customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Покупатель»", null, null, false, true, null));

        var id = await cards.AddContractAsync(customer, Terms("12/2026"));
        var contract = Assert.Single(await cards.ContractsAsync(customer));
        Assert.Equal(("12/2026", 30, 500_000m, ContractStatus.Active), (contract.Number, contract.PaymentDays, contract.AmountLimit!.Value, contract.Status));

        // Тот же номер у того же контрагента — нельзя.
        Assert.Equal("contract.duplicate",
            (await Assert.ThrowsAsync<BusinessRuleException>(() => cards.AddContractAsync(customer, Terms("12/2026")))).Code);

        // Скан: подделка под PDF отклоняется по сигнатуре, настоящий — сохраняется и пишет журнал просмотра.
        Assert.Equal("contract.file_type", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            cards.AddFileAsync(id, "evil.pdf", "application/pdf", "<html><script>alert(1)</script>"u8.ToArray()))).Code);
        var fileId = await cards.AddFileAsync(id, "../../dogovor.pdf", "application/pdf", Pdf);
        var file = await cards.GetFileAsync(fileId);
        Assert.Equal(("dogovor.pdf", "application/pdf"), (file.FileName, file.ContentType));
        Assert.Equal(Pdf, file.Content);

        await using (var db = host.NewDb())
        {
            // Содержимое в базе зашифровано: в колонке нет исходного текста PDF.
            var raw = await db.Database.SqlQueryRaw<string>(
                "SELECT [Content] AS [Value] FROM [kniterp].[counterparty_contract_files] WHERE [Id] = {0}", fileId).SingleAsync();
            Assert.DoesNotContain("%PDF", raw, StringComparison.Ordinal);

            // Только вставка: изменить или удалить скан нельзя даже напрямую в базе.
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
                "DELETE FROM [kniterp].[counterparty_contract_files] WHERE [Id] = {0}", fileId));

            var actions = await db.AuditEntries.Where(e => e.OrganizationId == org.OrganizationId && e.EntityType == nameof(CounterpartyContract))
                .Select(e => e.Action).ToListAsync();
            Assert.Contains(AuditActions.ContractCreated, actions);
            Assert.Contains(AuditActions.ContractFileAdded, actions);
            Assert.Contains(AuditActions.ContractFileViewed, actions);
        }

        // Закрытый договор не меняется, открытый снова — меняется; устаревшая версия — конфликт.
        await cards.CloseContractAsync(id, contract.RowVersion);
        var closed = Assert.Single(await cards.ContractsAsync(customer));
        Assert.Equal("contract.closed", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            cards.UpdateContractAsync(id, Terms("12/2026-А"), closed.RowVersion))).Code);
        Assert.Equal("contract.closed", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            cards.AddFileAsync(id, "late.pdf", "application/pdf", Pdf))).Code);
        await cards.ReopenContractAsync(id, closed.RowVersion);
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => cards.UpdateContractAsync(id, Terms("12/2026-А"), closed.RowVersion));
        var reopened = Assert.Single(await cards.ContractsAsync(customer));
        await cards.UpdateContractAsync(id, Terms("12/2026-А", limit: 750_000m), reopened.RowVersion);
        Assert.Equal(("12/2026-А", 750_000m), (Assert.Single(await cards.ContractsAsync(customer)).Number, Assert.Single(await cards.ContractsAsync(customer)).AmountLimit!.Value));
    }

    [SqlFact]
    public async Task Foreign_organization_sees_nothing_and_storekeeper_cannot_edit_or_see_amounts()
    {
        var a = await CreateOrganizationAsync();
        var b = await CreateOrganizationAsync();
        long customer, contractId, fileId;
        {
            var (cards, s) = As(a.OwnerUserId, a.OrganizationId);
            await using var _ = s;
            customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Клиент»", null, null, false, true, null));
            contractId = await cards.AddContractAsync(customer, Terms("1"));
            fileId = await cards.AddFileAsync(contractId, "scan.pdf", "application/pdf", Pdf);
        }

        {
            var (cards, s) = As(b.OwnerUserId, b.OrganizationId);
            await using var _ = s;
            await Assert.ThrowsAsync<NotFoundException>(() => cards.GetAsync(customer));
            await Assert.ThrowsAsync<NotFoundException>(() => cards.ContractsAsync(customer));
            await Assert.ThrowsAsync<NotFoundException>(() => cards.GetFileAsync(fileId));
            await Assert.ThrowsAsync<NotFoundException>(() => cards.AddContractAsync(customer, Terms("2")));
            await Assert.ThrowsAsync<NotFoundException>(() => cards.AddFileAsync(contractId, "x.pdf", "application/pdf", Pdf));
        }

        var storekeeper = await InviteActiveAsync(a, SystemRoles.Storekeeper);
        {
            var (cards, s) = As(storekeeper, a.OrganizationId);
            await using var _ = s;
            var card = await cards.GetAsync(customer);
            Assert.False(card.CanEditContracts || card.CanSeePrices || card.CanSeeSales);
            var contract = Assert.Single(await cards.ContractsAsync(customer));
            Assert.Null(contract.AmountLimit);
            await Assert.ThrowsAsync<AccessDeniedException>(() => cards.AddContractAsync(customer, Terms("3")));
            await Assert.ThrowsAsync<AccessDeniedException>(() => cards.CloseContractAsync(contractId, contract.RowVersion));
            await Assert.ThrowsAsync<AccessDeniedException>(() => cards.AddFileAsync(contractId, "x.pdf", "application/pdf", Pdf));
        }
    }

    [SqlFact]
    public async Task Order_and_document_history_is_filtered_by_counterparty()
    {
        var org = await CreateOrganizationAsync();
        var (_, s) = As(org.OwnerUserId, org.OrganizationId);
        await using var __ = s;
        var mine = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Мой покупатель»", null, null, false, true, null));
        var other = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Другой»", null, null, false, true, null));

        var store = await s.Warehouses.CreateWarehouseAsync("Склад готовой продукции", null);
        var day = new DateOnly(2026, 10, 8);
        var myOrder = await s.Sales.CreateOrderAsync(new KnitErp.Domain.Sales.SalesOrderHeader(day, mine, store, null, null, false, null));
        await s.Sales.CreateOrderAsync(new KnitErp.Domain.Sales.SalesOrderHeader(day, other, store, null, null, false, null));

        var history = await s.Sales.ListOrdersAsync(new SalesOrderFilter(CustomerId: mine));
        Assert.Equal(myOrder, Assert.Single(history).Id);
        Assert.Equal(2, (await s.Sales.ListOrdersAsync(new SalesOrderFilter())).Count);
        Assert.Empty(await s.Invoices.ListAsync(new CustomerInvoiceFilter(IncludeCancelled: true, CustomerId: mine)));
        Assert.Empty(await s.Documents.ListAsync(new StockDocumentListFilter(CounterpartyId: mine)));
        Assert.Empty(await s.Purchases.ListOrdersAsync(new KnitErp.Application.Purchasing.PurchaseOrderFilter(SupplierId: other)));
    }

    private async Task<CreatedOrganization> CreateOrganizationAsync()
    {
        var inn = NextValidInn();
        await using var s = host.As(null, null);
        return await s.Organizations.CreateWithOwnerAsync(new CreateOrganizationCommand(
            $"Тестовая организация {inn}", $"Тест {inn}", inn, null, false, "Europe/Moscow",
            $"owner-{inn}@test.local", $"Владелец {inn}"));
    }

    private async Task<long> InviteActiveAsync(CreatedOrganization org, string roleCode)
    {
        long id;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            id = (await s.Access.InviteAsync(new InviteUserCommand($"{roleCode}-{Guid.NewGuid():N}@test.local",
                SystemRoles.NameOf(roleCode), roleCode, SystemRoles.IsAdministrative(roleCode) ? "Тест" : null))).UserId;
        }

        await using var db = host.NewDb();
        (await db.Users.SingleAsync(u => u.Id == id)).Activate();
        await db.SaveChangesAsync();
        return id;
    }

    private static string NextValidInn()
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
}
