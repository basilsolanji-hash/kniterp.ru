using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Organizations;
using KnitErp.Application.Warehousing;
using KnitErp.Application.Workspace;
using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>Панель быстрого доступа: личные данные на сервере, поддержка, уведомления, поиск по правам, ИИ-помощник.</summary>
public sealed class WorkspaceToolsTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 730_000_000;

    [SqlFact]
    public async Task Panel_and_personal_data_are_per_user_and_survive_new_session()
    {
        var f = await SetUpAsync();
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            Assert.Equal(ToolCatalog.DefaultPinned, (await s.Personal.GetPanelAsync()).Pinned);
            await s.Personal.SavePanelAsync(new PanelSettings([ToolCatalog.Weather, ToolCatalog.Calculator, "nope"], 1));
            await s.Personal.SaveNoteAsync(null, "Позвонить поставщику пряжи");
            await s.Personal.AddLinkAsync("Поставщик", "yarn-supplier.ru");
            await s.Personal.ToggleFavoriteAsync("Остатки", "stock");
            Assert.Equal("business.ru", new Uri((await s.Personal.AddLinkAsync(null, "business.ru")).Last().Url).Host);
        }

        // Другое устройство — новая сессия того же пользователя: всё на месте.
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            var panel = await s.Personal.GetPanelAsync();
            Assert.Equal([ToolCatalog.Weather, ToolCatalog.Calculator], panel.Pinned);
            Assert.Equal(1, panel.Visible);
            Assert.Equal("Позвонить поставщику пряжи", (await s.Personal.GetNotesAsync()).Single().Text);
            Assert.Equal("https://yarn-supplier.ru/", (await s.Personal.GetLinksAsync())[0].Url);
            Assert.Equal("stock", (await s.Personal.GetFavoritesAsync()).Single().Href);
            await Assert.ThrowsAsync<KnitErp.Domain.Common.BusinessRuleException>(() => s.Personal.ToggleFavoriteAsync("x", "https://evil.example"));
        }

        // Другой пользователь своих данных не видит.
        await using (var s = host.As(f.Keeper, f.Org.OrganizationId))
        {
            Assert.Equal(ToolCatalog.DefaultPinned, (await s.Personal.GetPanelAsync()).Pinned);
            Assert.Empty(await s.Personal.GetNotesAsync());
        }
    }

    [SqlFact]
    public async Task Reminders_and_support_answers_become_notifications()
    {
        var f = await SetUpAsync();
        var today = PersonalToolsService.Today(host.Clock.UtcNow, "Europe/Moscow");
        await using (var s = host.As(f.Keeper, f.Org.OrganizationId))
        {
            await s.Personal.SaveTaskAsync(new TaskItem("", "Сдать отчёт по пряже", today, new TimeOnly(15, 0), true, false, null));
            await s.Personal.SaveTaskAsync(new TaskItem("", "Без даты", null, null, false, false, null));
            await s.Personal.SaveTaskAsync(new TaskItem("", "Завтра", today.AddDays(1), null, true, false, null));
            await Assert.ThrowsAsync<KnitErp.Domain.Common.BusinessRuleException>(() =>
                s.Personal.SaveTaskAsync(new TaskItem("", "Напомнить без даты", null, null, true, false, null)));

            var notes = await s.Notifications.ListAsync();
            Assert.Equal("Сдать отчёт по пряже — сегодня в 15:00", notes.Single().Text);

            // D81: уведомление можно убрать; новое — появится; «Очистить все» и «Показать скрытые».
            await s.Notifications.DismissAsync(notes.Single().Key);
            var list = await s.Notifications.ListWithHiddenAsync();
            Assert.Equal((0, 1), (list.Items.Count, list.Hidden));
            await s.Personal.SaveTaskAsync(new TaskItem("", "Позвонить поставщику", today, null, true, false, null));
            Assert.Equal("Позвонить поставщику — сегодня", (await s.Notifications.ListAsync()).Single().Text);
            await s.Notifications.DismissAllAsync();
            Assert.Empty(await s.Notifications.ListAsync());
            await s.Notifications.RestoreAsync();
            Assert.Equal(2, (await s.Notifications.ListAsync()).Count);
            await s.Notifications.DismissAllAsync();

            var number = await s.Support.CreateAsync("Не вижу склад цеха", "Нужен доступ к складу цеха", "Остатки");
            Assert.Equal("ОБ-000001", number);
            Assert.False((await s.Support.ListAsync()).CanHandle);
        }

        long ticketId;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            Assert.Contains(await s.Notifications.ListAsync(), n => n.Text == "Новые обращения в поддержку: 1");
            var queue = (await s.Support.ListAsync()).Queue;
            ticketId = queue.Single().Id;
            await s.Support.ReplyAsync(ticketId, "Выдал доступ, перезайдите", queue.Single().RowVersion);
        }

        await using (var s = host.As(f.Keeper, f.Org.OrganizationId))
        {
            Assert.Contains(await s.Notifications.ListAsync(), n => n.Text == "Обращение ОБ-000001: есть ответ" && n.ToolId == ToolCatalog.Support);
            var mine = (await s.Support.ListAsync()).Mine.Single();
            Assert.Equal("Выдал доступ, перезайдите", mine.Answer);

            // Открыл список — уведомление погасло. Ответить сам себе кладовщик не может.
            Assert.DoesNotContain(await s.Notifications.ListAsync(), n => n.ToolId == ToolCatalog.Support);
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.Support.ReplyAsync(ticketId, "x", mine.RowVersion));
            await s.Support.CloseAsync(ticketId, mine.RowVersion);
        }
    }

    [SqlFact]
    public async Task Quick_search_respects_permissions_and_warehouse_scope()
    {
        var f = await SetUpAsync();
        long shopDoc;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            var reason = (await s.Documents.GetOptionsAsync(StockOperationKind.Receipt)).Reasons[0].Id;
            await s.Documents.CreateAsync(StockOperationKind.Receipt, new StockDocumentHeader(f.Yarn, null, null, reason, new DateOnly(2026, 10, 1), null));
            shopDoc = await s.Documents.CreateAsync(StockOperationKind.Receipt, new StockDocumentHeader(f.Shop, null, null, reason, new DateOnly(2026, 10, 1), null));
            var hits = await s.Search.SearchAsync("ПТ-0000");
            Assert.Equal(2, hits.Count(h => h.Kind == SearchHitKind.Document));
            Assert.Contains(await s.Search.SearchAsync("шерсть"), h => h.Kind == SearchHitKind.Item && h.Href == "stock?search=%D0%9F%D0%A0-1");
        }

        await using (var s = host.As(f.Keeper, f.Org.OrganizationId))
        {
            var docs = (await s.Search.SearchAsync("ПТ-0000")).Where(h => h.Kind == SearchHitKind.Document).ToList();
            Assert.Equal("Поступление ПТ-000001", docs.Single().Title);
            Assert.DoesNotContain(docs, h => h.Href == $"stock-documents/{shopDoc}");
            // D910: справка в быстром поиске — статья о документах или готовый ответ базы знаний о сторно.
            Assert.Contains(await s.Search.SearchAsync("сторно"), h => h.Kind == SearchHitKind.Help && (h.HelpId == "documents" || h.HelpId == "qa-stock-0006"));
            Assert.Empty(await s.Search.SearchAsync("П"));
        }
    }

    [SqlFact]
    public async Task Assistant_falls_back_to_help_and_sends_only_role_context_to_model()
    {
        var f = await SetUpAsync();
        await using (var s = host.As(f.Keeper, f.Org.OrganizationId))
        {
            var reply = await s.Assistant.AskAsync("Как исправить проведённое списание?", "stock-documents/5", []);
            Assert.False(reply.FromModel);
            Assert.Equal("documents", reply.Articles[0].Id);
        }

        var model = new RecordingModel();
        host.AssistantModel = model;
        try
        {
            await using var s = host.As(f.Keeper, f.Org.OrganizationId);
            var reply = await s.Assistant.AskAsync("Как провести списание?", "stock-documents", [new AssistantTurn(true, "Привет"), new AssistantTurn(false, "Здравствуйте")]);
            Assert.True(reply.FromModel);
            Assert.Equal("Ответ модели", reply.Text);
            Assert.Contains("Кладовщик", model.System);
            Assert.Contains("Складские документы: черновик", model.System);
            Assert.DoesNotContain("Выдача административных прав", model.System);
            Assert.Contains("Поступление, перемещение, списание", model.System);
            Assert.Equal(3, model.Conversation.Count);
            Assert.DoesNotContain(f.Inn, model.System);
        }
        finally
        {
            host.AssistantModel = new DisabledAssistantModel();
        }
    }

    [SqlFact]
    public async Task Assistant_answers_in_interface_language()
    {
        var f = await SetUpAsync();
        host.UiText = new MarkedUiText();
        try
        {
            await using (var s = host.As(f.Keeper, f.Org.OrganizationId))
            {
                // Без модели: ответ из справки — на языке пользователя.
                var reply = await s.Assistant.AskAsync("Как исправить проведённое списание?", "stock-documents/5", []);
                Assert.StartsWith("[uz]Нашёл в справке:", reply.Text);
                Assert.Contains("«[uz]Поступление, перемещение, списание»", reply.Text);
                var hits = await s.Search.SearchAsync("сторно");
                Assert.Contains(hits, h => h.Kind == SearchHitKind.Help && h.Title == "[uz]Поступление, перемещение, списание");
            }

            var model = new RecordingModel();
            host.AssistantModel = model;
            await using (var s = host.As(f.Keeper, f.Org.OrganizationId))
            {
                await s.Assistant.AskAsync("Qanday qilib hisobdan chiqarish kerak?", "stock-documents", []);
                Assert.Contains("Отвечай по-узбекски (латиницей)", model.System);
                Assert.Contains("[uz]Создайте черновик кнопкой", model.System);
                Assert.Contains("[uz]Кладовщик", model.System);
                Assert.Contains("[uz]Складские документы: черновик", model.System);
            }
        }
        finally
        {
            host.UiText = new RussianUiText();
            host.AssistantModel = new DisabledAssistantModel();
        }
    }

    /// <summary>«Перевод» с пометкой: видно, какие тексты сервис переводит.</summary>
    private sealed class MarkedUiText : IUiText
    {
        public string LanguageCode => "uz";

        public string Translate(string russian) => "[uz]" + russian;
    }

    [SqlFact]
    public async Task Website_and_position_reach_current_access()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        var org = await s.Organizations.GetCurrentAsync();
        await s.Organizations.UpdateRequisitesAsync(new UpdateRequisitesCommand(null, org.TimeZoneId, org.RowVersion, "ks-fabrika.ru"));
        var access = await s.Access.GetCurrentAccessAsync();
        Assert.Equal("https://ks-fabrika.ru/", access.WebsiteUrl);
        Assert.Equal("Владелец организации", access.Subtitle);
        Assert.Equal("В", access.Initials); // «Владелец 7300…»: цифры в инициалы не попадают.
    }

    private sealed class RecordingModel : IAssistantModel
    {
        public string System { get; private set; } = string.Empty;
        public IReadOnlyList<AssistantTurn> Conversation { get; private set; } = [];
        public bool IsConfigured => true;

        public Task<string> AskAsync(string systemPrompt, IReadOnlyList<AssistantTurn> conversation, CancellationToken ct = default)
        {
            System = systemPrompt;
            Conversation = conversation;
            return Task.FromResult("Ответ модели");
        }
    }

    private sealed record Fixture(CreatedOrganization Org, string Inn, long Yarn, long Shop, long Keeper);

    private async Task<Fixture> SetUpAsync()
    {
        var inn = NextValidInn();
        CreatedOrganization org;
        await using (var s = host.As(null, null))
        {
            org = await s.Organizations.CreateWithOwnerAsync(new CreateOrganizationCommand(
                $"Тестовая организация {inn}", $"Тест {inn}", inn, null, false, "Europe/Moscow", $"owner-{inn}@test.local", $"Владелец {inn}"));
        }

        long yarn, shop;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var units = await s.Catalog.ListUnitsAsync();
            yarn = await s.Warehouses.CreateWarehouseAsync("Склад пряжи", null);
            shop = await s.Warehouses.CreateWarehouseAsync("Вязальный цех", null);
            await s.Catalog.CreateItemAsync(new ItemCommand("ПР-1", "Пряжа шерсть", ItemType.RawMaterial, units.Single(u => u.Symbol == "кг").Id, null));
        }

        long keeper;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            keeper = (await s.Access.InviteAsync(new InviteUserCommand($"keeper-{Guid.NewGuid():N}@test.local",
                "Кладовщик Пряжи", SystemRoles.Storekeeper, null, WarehouseId: yarn))).UserId;
        }

        await using var db = host.NewDb();
        (await db.Users.SingleAsync(u => u.Id == keeper)).Activate();
        await db.SaveChangesAsync();
        return new Fixture(org, inn, yarn, shop, keeper);
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
