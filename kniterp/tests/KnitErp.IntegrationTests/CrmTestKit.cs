using KnitErp.Application.Access;
using KnitErp.Application.Crm;
using KnitErp.Application.Organizations;
using KnitErp.Application.Sales;
using KnitErp.Application.Warehousing;
using KnitErp.Domain.Access;
using KnitErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>Сервисы CRM от имени пользователя — отдельно от общего набора, чтобы не мешать другим модулям.</summary>
public sealed class CrmServices(KnitErpDbContext db, CrmSettingsService settings, CrmLeadService leads, CrmDealService deals, CrmContactService contacts,
    SalesService sales) : IAsyncDisposable
{
    public required CrmActivityService Activities { get; init; }
    public required CrmOfferService Offers { get; init; }
    public required CrmReportService Reports { get; init; }
    public required CrmIntakeService Intake { get; init; }
    public required CrmMailingService Mailings { get; init; }
    public required CrmImportService Import { get; init; }
    public required CrmMessengerService Messenger { get; init; }
    public required CrmEmailTemplateService Templates { get; init; }
    public required CrmMailboxService Mailbox { get; init; }
    public required CrmDealSalesService DealSales { get; init; }
    public required CrmDealPhotoService Photos { get; init; }

    public KnitErpDbContext Db { get; } = db;
    public CrmSettingsService Settings { get; } = settings;
    public CrmLeadService Leads { get; } = leads;
    public CrmDealService Deals { get; } = deals;
    public CrmContactService Contacts { get; } = contacts;
    public SalesService Sales { get; } = sales;

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}

/// <summary>Организации и сотрудники для тестов CRM. Данные вымышленные: телефоны +7 900 000-00-xx, почты @example.org.</summary>
public static class CrmTestKit
{
    private static int _innSeed = 960_000_000;

    /// <summary>MAX без сети: запоминает отправленные сообщения и подписки.</summary>
    public static FakeMessengerClient Max { get; } = new(KnitErp.Domain.Crm.CrmMessengerKind.Max);
    public static FakeMessengerClient Telegram { get; } = new(KnitErp.Domain.Crm.CrmMessengerKind.Telegram);
    public static FakeMessengerClient WhatsApp { get; } = new(KnitErp.Domain.Crm.CrmMessengerKind.WhatsApp);
    /// <summary>IMAP без сети: письма кладёт тест.</summary>
    public static FakeMailboxClient Imap { get; } = new();
    private static int _counterpartySeed = 961_000_000;

    public static CrmServices Crm(this SqlTestHost host, long userId, long organizationId,
        params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) => host.Crm(userId, organizationId, null, interceptors);

    /// <summary>Сервисы CRM от имени токена API: apiScope ограничивает права (D88).</summary>
    public static CrmServices Crm(this SqlTestHost host, long userId, long organizationId, IReadOnlyCollection<string>? apiScope,
        params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors)
    {
        var db = interceptors.Length == 0 ? host.NewDb() : host.NewDb(interceptors);
        var user = new TestUser { UserId = userId, OrganizationId = organizationId, ApiScope = apiScope };
        var guard = new AccessGuard(db, user, host.Clock);
        var sales = new SalesService(db, guard, user, host.Clock, new StockDocumentService(db, guard, SqlTestHost.Spreadsheet, user, host.Clock),
            [new CrmSalesOrderEvents(host.Clock, user)]); // D683: «заказ подтверждён» → сделка выиграна, как в приложении
        return new CrmServices(db, new CrmSettingsService(db, guard, user, host.Clock), new CrmLeadService(db, guard, user, host.Clock, new FakeRequisitesLookup()),
            new CrmDealService(db, guard, user, host.Clock, sales), new CrmContactService(db, guard, user, host.Clock, SqlTestHost.Spreadsheet), sales)
        {
            Activities = new CrmActivityService(db, guard, user, host.Clock, host.Mail),
            Offers = new CrmOfferService(db, guard, user, host.Clock, host.Mail),
            Reports = new CrmReportService(db, guard, user, host.Clock, SqlTestHost.Spreadsheet),
            Intake = new CrmIntakeService(db, guard, user, host.Clock),
            Mailings = new CrmMailingService(db, guard, user, host.Clock, host.Mail),
            Import = new CrmImportService(db, guard, user, host.Clock, SqlTestHost.Spreadsheet),
            Messenger = new CrmMessengerService(db, guard, user, host.Clock, [Max, Telegram, WhatsApp], host.Mail),
            Templates = new CrmEmailTemplateService(db, guard, user, host.Clock),
            Mailbox = new CrmMailboxService(db, guard, user, host.Clock, Imap),
            DealSales = new CrmDealSalesService(db, guard, user, host.Clock, new CustomerInvoiceService(db, guard, user, host.Clock),
                new SalesOrderDocumentsService(db, guard, user, host.Clock, host.Mail)),
            Photos = new CrmDealPhotoService(db, guard, user, host.Clock),
        };
    }

    public static async Task<CreatedOrganization> CreateCrmOrganizationAsync(this SqlTestHost host)
    {
        var inn = LegalInn(Interlocked.Increment(ref _innSeed));
        await using var s = host.As(null, null);
        return await s.Organizations.CreateWithOwnerAsync(new CreateOrganizationCommand(
            $"Тестовая фабрика CRM {inn}", $"CRM {inn}", inn, null, false, "Europe/Moscow", $"owner-{inn}-{Guid.NewGuid():N}@test.local", $"Владелец {inn}", "RU"));
    }

    /// <summary>Приглашённый и активированный сотрудник с системной ролью.</summary>
    public static async Task<long> CrmUserAsync(this SqlTestHost host, CreatedOrganization org, string roleCode)
    {
        long id;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            id = (await s.Access.InviteAsync(new InviteUserCommand($"{roleCode}-{Guid.NewGuid():N}@test.local", SystemRoles.NameOf(roleCode), roleCode,
                SystemRoles.IsAdministrative(roleCode) ? "Тест" : null))).UserId;
        }

        await using var db = host.NewDb();
        (await db.Users.SingleAsync(u => u.Id == id)).Activate();
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>ИНН покупателя с верным контрольным числом (вымышленный).</summary>
    public static string NextCounterpartyInn() => LegalInn(Interlocked.Increment(ref _counterpartySeed));

    private static string LegalInn(int seed)
    {
        int[] w = [2, 4, 10, 3, 5, 9, 4, 6, 8];
        var body = seed.ToString("D9");
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += (body[i] - '0') * w[i];
        }

        return body + (sum % 11 % 10);
    }
}

public sealed class FakeMessengerClient(KnitErp.Domain.Crm.CrmMessengerKind kind) : ICrmMessengerClient
{
    public KnitErp.Domain.Crm.CrmMessengerKind Kind => kind;

    public bool IsAvailable => true;

    /// <summary>Отправленные сообщения: собеседник без префикса мессенджера (как уходит в API) и текст.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<(string UserId, string Text)> Sent { get; } = new();

    public System.Collections.Concurrent.ConcurrentQueue<string> Subscriptions { get; } = new();

    public Task<string?> AccountNameAsync(CrmMessengerCredentials c, CancellationToken ct = default) =>
        Task.FromResult<string?>(c.Token == "bad" ? null : $"Фабрика-бот {kind}");

    public Task<bool> SubscribeAsync(CrmMessengerCredentials c, string url, string secret, CancellationToken ct = default)
    {
        Subscriptions.Enqueue(url);
        return Task.FromResult(true);
    }

    public Task<bool> SendAsync(CrmMessengerCredentials c, string peer, string text, CancellationToken ct = default)
    {
        Sent.Enqueue((peer, text));
        return Task.FromResult(true);
    }
}

public sealed class FakeMailboxClient : ICrmMailboxClient
{
    /// <summary>Письма ящика по логину: тест добавляет, клиент отдаёт те, что после курсора.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, List<CrmInboundEmail>> Boxes { get; } = new();

    public Task TestAsync(CrmMailboxCredentials c, CancellationToken ct = default) =>
        c.Password == "wrong" ? throw new CrmMailboxException("Логин или пароль не подошли.") : Task.CompletedTask;

    public Task<CrmMailboxFetch> FetchAsync(CrmMailboxCredentials c, long? uidValidity, long? lastUid, int max, CancellationToken ct = default)
    {
        var list = Boxes.GetOrAdd(c.Login, _ => []);
        lock (list)
        {
            return Task.FromResult(new CrmMailboxFetch(7, list.Where(e => uidValidity != 7 || lastUid is null || e.Uid > lastUid).OrderBy(e => e.Uid).Take(max).ToList()));
        }
    }

    public void Add(string login, CrmInboundEmail email)
    {
        var list = Boxes.GetOrAdd(login, _ => []);
        lock (list)
        {
            list.Add(email);
        }
    }
}

/// <summary>D676: IMAP не ходит во внутреннюю сеть (SSRF).</summary>
public sealed class ImapAddressTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.5", true)]
    [InlineData("192.168.1.10", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("93.184.216.34", false)]
    [InlineData("2606:2800:220:1:248:1893:25c8:1946", false)]
    public void Internal_addresses_are_refused(string ip, bool internalAddress) =>
        Assert.Equal(internalAddress, KnitErp.Infrastructure.Email.ImapMailboxClient.IsInternal(System.Net.IPAddress.Parse(ip)));
}
