using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Integrations;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Integrations;
using KnitErp.Domain.Sales;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>D88: токены API, ограничение прав по токену, вебхуки (постановка из журнала аудита, подпись, повторы, защита адреса).</summary>
public sealed class IntegrationApiTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 860_000_000;
    private static readonly DateOnly Day = new(2026, 10, 9);

    [SqlFact]
    public async Task Tokens_are_issued_within_own_rights_stored_as_hash_and_restrict_requests()
    {
        var org = await CreateOrgAsync();
        long tokenId;
        string secret;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            // Права токена — только свои и не запрещённые для API.
            var options = await s.Integrations.OptionsAsync();
            Assert.DoesNotContain(options.Permissions, p => p.Code == Permissions.UserManage);
            Assert.Equal("api.token.permission_forbidden", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Integrations.IssueTokenAsync("Магазин", [Permissions.UserManage], 30))).Code);
            Assert.Equal("api.token.permissions", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Integrations.IssueTokenAsync("Магазин", [], 30))).Code);
            Assert.Equal("api.token.expiry", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Integrations.IssueTokenAsync("Магазин", [Permissions.CatalogView], 400))).Code);

            var issued = await s.Integrations.IssueTokenAsync("Интернет-магазин", [Permissions.CatalogView, Permissions.SalesView], 90);
            (tokenId, secret) = (issued.Id, issued.Secret);
            Assert.StartsWith("knt_", secret);
            Assert.True(secret.Length >= 40);

            // В базе — только хэш и первые символы; секрета нет.
            var row = await s.Db.ApiTokens.AsNoTracking().SingleAsync(t => t.Id == tokenId);
            Assert.Equal(ApiToken.Hash(secret), row.TokenHash);
            Assert.Equal(secret[..ApiToken.PrefixLength], row.Prefix);
            Assert.DoesNotContain(secret, row.Permissions + row.Name + row.Prefix[ApiToken.PrefixLength..]);
            var list = await s.Integrations.ListTokensAsync();
            Assert.True(list.Single().IsActive);
            Assert.True(await s.Db.AuditEntries.AnyAsync(a => a.OrganizationId == org.OrganizationId && a.Action == KnitErp.Domain.Audit.AuditActions.ApiTokenIssued
                                                             && a.After!.Contains(row.Prefix)));
        }

        // Запрос по токену: права — пересечение; чего нет у токена — отказ, даже если у владельца есть.
        await using (var api = host.As(org.OwnerUserId, org.OrganizationId, [Permissions.CatalogView, Permissions.SalesView]))
        {
            var ctx = await api.Access.GetCurrentAccessAsync();
            Assert.Contains(Permissions.CatalogView, ctx.Granted);
            Assert.DoesNotContain(Permissions.CatalogEdit, ctx.Granted);
            await api.Catalog.ListItemsAsync(new ItemFilter());
            await Assert.ThrowsAsync<AccessDeniedException>(() => api.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Новый»", null, null, false, true, null)));
            await Assert.ThrowsAsync<AccessDeniedException>(() => api.Integrations.ListTokensAsync());
        }

        // Отзыв: действует сразу, повторный — ошибка; чужая организация — «не найдено».
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            await s.Integrations.RevokeTokenAsync(tokenId);
            Assert.False((await s.Integrations.ListTokensAsync()).Single().IsActive);
            Assert.Equal("api.token.revoked", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.Integrations.RevokeTokenAsync(tokenId))).Code);
        }

        var other = await CreateOrgAsync();
        await using var o = host.As(other.OwnerUserId, other.OrganizationId);
        await Assert.ThrowsAsync<NotFoundException>(() => o.Integrations.RevokeTokenAsync(tokenId));
    }

    [SqlFact]
    public async Task Token_cannot_exceed_creator_rights_and_creator_downgrade_limits_it()
    {
        var org = await CreateOrgAsync();
        long admin;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            admin = (await s.Access.InviteAsync(new InviteUserCommand($"adm-{Guid.NewGuid():N}@test.local", "Администратор", SystemRoles.Administrator,
                "Интеграции интернет-магазина"))).UserId;
        }

        await using (var db = host.NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == admin)).Activate();
            await db.SaveChangesAsync();
        }

        await using (var a = host.As(admin, org.OrganizationId))
        {
            // У администратора нет права «Складские документы: черновик» — выдать его токену нельзя.
            Assert.DoesNotContain((await a.Integrations.OptionsAsync()).Permissions, p => p.Code == Permissions.WarehouseDocumentCreate);
            Assert.Equal("api.token.over_rights", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                a.Integrations.IssueTokenAsync("x", [Permissions.WarehouseDocumentCreate], 30))).Code);
        }

        // Создатель заблокирован — по его токену прав нет совсем.
        await using (var db = host.NewDb())
        {
            (await db.OrganizationMembers.SingleAsync(m => m.UserId == admin && m.OrganizationId == org.OrganizationId)).Block(org.OwnerUserId, DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        await using var api = host.As(admin, org.OrganizationId, [Permissions.CatalogView]);
        await Assert.ThrowsAnyAsync<AccessDeniedException>(() => api.Catalog.ListItemsAsync(new ItemFilter()));
    }

    [SqlFact]
    public async Task Webhooks_take_events_from_audit_sign_retry_and_never_replay_history()
    {
        var f = await SeedAsync();
        var sender = new FakeSender();
        long subscriptionId;
        string secret;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            // Событие до подписки в доставку не попадает.
            await ConfirmOrderAsync(s, f);

            Assert.Equal("webhook.url_https", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Integrations.CreateWebhookAsync("http://shop.example.ru/hook", ["sales_order.confirmed"]))).Code);
            Assert.Equal("webhook.url_private", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Integrations.CreateWebhookAsync("https://192.168.1.10/hook", ["sales_order.confirmed"]))).Code);
            Assert.Equal("webhook.url_private", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Integrations.CreateWebhookAsync("https://localhost/hook", ["sales_order.confirmed"]))).Code);
            Assert.Equal("webhook.url_credentials", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Integrations.CreateWebhookAsync("https://user:pass@shop.example.ru/hook", ["sales_order.confirmed"]))).Code);
            Assert.Equal("webhook.event_unknown", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Integrations.CreateWebhookAsync("https://shop.example.ru/hook", ["order.deleted"]))).Code);

            var issued = await s.Integrations.CreateWebhookAsync("https://shop.example.ru/hook", ["sales_order.confirmed", "customer_payment.created"]);
            (subscriptionId, secret) = (issued.Id, issued.Secret);
            Assert.StartsWith("whsec_", secret);

            // Секрет в базе зашифрован мастер-ключом.
            var raw = await s.Db.Database.SqlQuery<string>($"SELECT [Secret] AS [Value] FROM [kniterp].[webhook_subscriptions] WHERE [Id] = {subscriptionId}").SingleAsync();
            Assert.StartsWith("enc:v1:", raw);
            Assert.DoesNotContain(secret, raw);

            // Два события после подписки: подтверждение заказа и оплата; проводка склада не подписана.
            await ConfirmOrderAsync(s, f);
            await s.Sales.CreatePaymentAsync(Day, f.Customer, null, 100m, null);
        }

        await using (var db = host.NewDb())
        {
            var dispatcher = new WebhookDispatcher(db, sender, host.Clock);
            Assert.Equal(2, await dispatcher.EnqueueAsync());
            Assert.Equal(0, await dispatcher.EnqueueAsync());

            // Первая отправка: подписчик отвечает 500 — повтор через минуту; вторая — 200.
            sender.Status = 500;
            Assert.Equal(0, await dispatcher.DeliverAsync());
            var failed = await db.WebhookDeliveries.AsNoTracking().Where(d => d.SubscriptionId == subscriptionId).ToListAsync();
            Assert.All(failed, d => Assert.Equal((1, WebhookDeliveryStatus.Pending, 500), (d.Attempts, d.Status, d.LastStatusCode!.Value)));
            Assert.Equal(0, await dispatcher.DeliverAsync());

            host.Clock.UtcNow = host.Clock.UtcNow.AddMinutes(2);
            sender.Status = 200;
            sender.Sent.Clear();
            Assert.Equal(2, await dispatcher.DeliverAsync());
        }

        host.Clock.UtcNow = new DateTime(2026, 10, 9, 5, 0, 0, DateTimeKind.Utc);

        // Порядок, типы и подпись: получатель проверяет HMAC от «метка.тело» своим секретом.
        Assert.Equal(["sales_order.confirmed", "customer_payment.created"], sender.Sent.Select(x => x.Headers["X-KnitErp-Event"]));
        foreach (var (_, headers, body) in sender.Sent)
        {
            var ts = long.Parse(headers["X-KnitErp-Timestamp"]);
            Assert.Equal("v1=" + WebhookDelivery.Sign(secret, ts, body), headers["X-KnitErp-Signature"]);
            Assert.Contains("\"sequence\":", body);
            Assert.DoesNotContain(secret, body);
        }

        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            var deliveries = await s.Integrations.DeliveriesAsync(subscriptionId);
            Assert.All(deliveries, d => Assert.Equal(WebhookDeliveryStatus.Delivered, d.Status));

            // Выключенная подписка не копит события; включённая — не присылает пропущенное.
            var w = (await s.Integrations.ListWebhooksAsync()).Single();
            await s.Integrations.SetWebhookActiveAsync(subscriptionId, false, w.RowVersion);
            await ConfirmOrderAsync(s, f);
            w = (await s.Integrations.ListWebhooksAsync()).Single();
            await s.Integrations.SetWebhookActiveAsync(subscriptionId, true, w.RowVersion);
            await s.Integrations.SendTestAsync(subscriptionId);
        }

        await using (var db = host.NewDb())
        {
            var dispatcher = new WebhookDispatcher(db, sender, host.Clock);
            Assert.Equal(0, await dispatcher.EnqueueAsync());
            sender.Sent.Clear();
            Assert.Equal(1, await dispatcher.DeliverAsync());
            Assert.Equal("ping", sender.Sent.Single().Headers["X-KnitErp-Event"]);
        }

        var other = await CreateOrgAsync();
        await using var o = host.As(other.OwnerUserId, other.OrganizationId);
        Assert.Empty(await o.Integrations.ListWebhooksAsync());
        await Assert.ThrowsAsync<NotFoundException>(() => o.Integrations.SendTestAsync(subscriptionId));
    }

    /// <summary>D128: подписка — после повторного подтверждения паролем и только на события модулей, которые видишь сам.</summary>
    [SqlFact]
    public async Task Webhook_requires_step_up_and_rights_for_subscribed_events()
    {
        var f = await SeedAsync();
        var admin = await AdminAsync(f.Org);
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            // Администратору запрещён просмотр CRM.
            await s.Access.GrantIndividualAsync(admin, Permissions.CrmView, true, null, "тест");
        }

        await using (var s = host.As(admin, f.Org.OrganizationId))
        {
            Assert.Equal("webhook.event_rights", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Integrations.CreateWebhookAsync("https://shop.example.ru/hook", ["sales_order.confirmed", "crm.deal.won"]))).Code);
            Assert.DoesNotContain((await s.Integrations.OptionsAsync()).Events, e => e.Event.StartsWith("crm.", StringComparison.Ordinal));
            await s.Integrations.CreateWebhookAsync("https://shop.example.ru/hook", ["sales_order.confirmed"]);
        }

        var other = await CreateOrgAsync();
        host.AutoStepUp = false;
        try
        {
            await using var s = host.As(other.OwnerUserId, other.OrganizationId);
            Assert.Equal("auth.stepup.required", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Integrations.CreateWebhookAsync("https://shop.example.ru/hook", ["sales_order.confirmed"]))).Code);
            Assert.Equal(0, await s.Db.WebhookSubscriptions.CountAsync(w => w.OrganizationId == other.OrganizationId));
        }
        finally
        {
            host.AutoStepUp = true;
        }

        long id;
        await using (var s = host.As(other.OwnerUserId, other.OrganizationId))
        {
            id = (await s.Integrations.CreateWebhookAsync("https://shop.example.ru/hook", ["sales_order.confirmed"])).Id;
        }

        host.AutoStepUp = false;
        try
        {
            // Отметку подтверждения от создания сбрасываем: она живёт 10 минут.
            host.Clock.UtcNow = host.Clock.UtcNow.AddMinutes(11);
            await using var s = host.As(other.OwnerUserId, other.OrganizationId);
            var w = (await s.Integrations.ListWebhooksAsync()).Single();
            Assert.Equal("auth.stepup.required", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.Integrations.SetWebhookActiveAsync(id, false, w.RowVersion))).Code);
        }
        finally
        {
            host.AutoStepUp = true;
            host.Clock.UtcNow = new DateTime(2026, 10, 9, 5, 0, 0, DateTimeKind.Utc);
        }
    }

    /// <summary>
    /// D128: событие не уходит, если создатель подписки заблокирован, — ни новое, ни уже поставленное в доставку; причина видна
    /// в доставке. В теле события нет сумм — только номер документа.
    /// </summary>
    [SqlFact]
    public async Task Webhook_of_blocked_creator_is_not_sent_and_payload_has_no_amounts()
    {
        var f = await SeedAsync();
        var admin = await AdminAsync(f.Org);
        var sender = new FakeSender();
        long subscriptionId;
        await using (var s = host.As(admin, f.Org.OrganizationId))
        {
            subscriptionId = (await s.Integrations.CreateWebhookAsync("https://shop.example.ru/hook", ["sales_order.confirmed"])).Id;
        }

        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            await ConfirmOrderAsync(s, f);
        }

        await using (var db = host.NewDb())
        {
            var dispatcher = new WebhookDispatcher(db, sender, host.Clock);
            Assert.Equal(1, await dispatcher.EnqueueAsync());
            var payload = (await db.WebhookDeliveries.AsNoTracking().SingleAsync(d => d.SubscriptionId == subscriptionId)).Payload;
            using var json = System.Text.Json.JsonDocument.Parse(payload);
            var summary = json.RootElement.GetProperty("data").GetProperty("summary").GetString();
            var number = await db.SalesOrders.AsNoTracking().Where(o => o.OrganizationId == f.Org.OrganizationId).Select(o => o.Number).SingleAsync();
            Assert.Equal(number, summary);
        }

        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            await s.Access.BlockAsync(admin, "Увольнение");
            await ConfirmOrderAsync(s, f);
        }

        await using (var db = host.NewDb())
        {
            var dispatcher = new WebhookDispatcher(db, sender, host.Clock);
            Assert.Equal(0, await dispatcher.EnqueueAsync());
            Assert.Equal(0, await dispatcher.DeliverAsync());
            Assert.Empty(sender.Sent);
            var deliveries = await db.WebhookDeliveries.AsNoTracking().Where(d => d.SubscriptionId == subscriptionId).ToListAsync();
            Assert.Equal(2, deliveries.Count);
            Assert.All(deliveries, d =>
            {
                Assert.Equal(WebhookDeliveryStatus.Failed, d.Status);
                Assert.Contains("заблокирован", d.LastError);
            });
        }
    }

    private async Task<long> AdminAsync(CreatedOrganization org)
    {
        long id;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            id = (await s.Access.InviteAsync(new InviteUserCommand($"admin-{Guid.NewGuid():N}@test.local", "Администратор", SystemRoles.Administrator,
                "Тест"))).UserId;
        }

        await using var db = host.NewDb();
        (await db.Users.SingleAsync(u => u.Id == id)).Activate();
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public void Delivery_backoff_gives_up_after_about_a_day()
    {
        var now = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);
        var d = WebhookDelivery.Create(1, 1, Guid.NewGuid(), "ping", 0, "{}", now);
        for (var i = 0; i < WebhookDelivery.Backoff.Count; i++)
        {
            d.FailedAttempt(503, "Service Unavailable", now);
            Assert.Equal(WebhookDeliveryStatus.Pending, d.Status);
        }

        d.FailedAttempt(503, new string('x', 900), now);
        Assert.Equal(WebhookDeliveryStatus.Failed, d.Status);
        Assert.Equal(WebhookDelivery.ErrorMaxLength, d.LastError!.Length);
        Assert.InRange(WebhookDelivery.Backoff.Aggregate(TimeSpan.Zero, (a, b) => a + b).TotalHours, 20, 24);
        Assert.Equal(WebhookDispatcher.EventId(5, 77), WebhookDispatcher.EventId(5, 77));
        Assert.NotEqual(WebhookDispatcher.EventId(5, 77), WebhookDispatcher.EventId(6, 77));
    }

    private static async Task ConfirmOrderAsync(Services s, Fixture f)
    {
        var id = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, null, null, true, null));
        await s.Sales.SetOrderLineAsync(id, f.Item, 1, 100m, null, (await s.Sales.GetOrderAsync(id)).RowVersion);
        await s.Sales.ConfirmOrderAsync(id, (await s.Sales.GetOrderAsync(id)).RowVersion);
    }

    private sealed class FakeSender : IWebhookSender
    {
        public int Status { get; set; } = 200;
        public List<(string Url, IReadOnlyDictionary<string, string> Headers, string Body)> Sent { get; } = [];

        public Task<(int? StatusCode, string? Error)> SendAsync(string url, IReadOnlyDictionary<string, string> headers, string body, CancellationToken ct)
        {
            Sent.Add((url, headers, body));
            return Task.FromResult<(int?, string?)>((Status, Status >= 300 ? "ошибка" : null));
        }
    }

    private sealed record Fixture(CreatedOrganization Org, long Store, long Customer, long Item);

    private async Task<Fixture> SeedAsync()
    {
        var org = await CreateOrgAsync();
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        var units = await s.Catalog.ListUnitsAsync();
        var store = await s.Warehouses.CreateWarehouseAsync("Склад", null);
        var item = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-1", "Свитер", ItemType.Finished, units.Single(u => u.Symbol == "шт").Id, null));
        var customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Магазин»", null, null, false, true, null));
        return new Fixture(org, store, customer, item);
    }

    private async Task<CreatedOrganization> CreateOrgAsync()
    {
        int[] w = [2, 4, 10, 3, 5, 9, 4, 6, 8];
        var body = Interlocked.Increment(ref _innSeed).ToString("D9");
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += (body[i] - '0') * w[i];
        }

        var inn = body + (sum % 11 % 10);
        await using var s = host.As(null, null);
        return await s.Organizations.CreateWithOwnerAsync(new CreateOrganizationCommand(
            $"Тестовая организация {inn}", $"Тест {inn}", inn, null, false, "Europe/Moscow", $"owner-{inn}-{Guid.NewGuid():N}@test.local", $"Владелец {inn}", "RU"));
    }
}
