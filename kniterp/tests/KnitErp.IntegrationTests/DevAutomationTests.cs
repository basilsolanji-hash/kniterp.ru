using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Development;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Access;
using KnitErp.Domain.Common;
using KnitErp.Domain.Development;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>D790–D798: автоматизация разработки — подстановки в новую модель, копия, автопереход, Excel, уведомления и поиск.</summary>
public sealed class DevAutomationTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 971_000_000;
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];
    private static readonly byte[] Program = [0x53, 0x49, 0x4E, 0x00, 0x01, 0x02];

    private (DevModelService Models, DevSettingsService Settings, DevExchangeService Exchange, Services All) As(long userId, long organizationId)
    {
        var all = host.As(userId, organizationId);
        var user = new TestUser { UserId = userId, OrganizationId = organizationId, ClientAddress = host.ClientAddress };
        var guard = new AccessGuard(all.Db, user, host.Clock);
        var models = new DevModelService(all.Db, guard, user, host.Clock);
        return (models, new DevSettingsService(all.Db, guard, user, host.Clock), new DevExchangeService(all.Db, guard, models, SqlTestHost.Spreadsheet, host.Clock), all);
    }

    private static DevModelHeader Header(string name = "", DevCategory category = DevCategory.Sweater, long? collection = null, bool hasPattern = true,
        DateOnly? launch = null, IReadOnlyDictionary<DevRole, long?>? assignees = null) =>
        new(name, collection, category, DevAudience.Women, false, hasPattern, null, null, launch, DevPriority.Normal, assignees ?? new Dictionary<DevRole, long?>());

    [SqlFact]
    public async Task New_model_gets_defaults_template_launch_date_and_name_copy_keeps_brief()
    {
        var org = await CreateOrganizationAsync();
        var technologist = await InviteActiveAsync(org, SystemRoles.DevTechnologist);
        var (models, settings, _, s) = As(org.OwnerUserId, org.OrganizationId);
        await using var __ = s;
        await settings.SaveAutomationAsync(new Dictionary<DevRole, long?> { [DevRole.Technologist] = technologist, [DevRole.Designer] = org.OwnerUserId }, true, true);
        await settings.SaveTemplateAsync(new DevCategoryTemplateDto(DevCategory.Sweater, "42–52", "молочный", "50% шерсть, 50% акрил", null, "ручная стирка"));
        var collection = await settings.AddLookupAsync(DevLookupKind.Collection, "Осень-зима 2026", null);

        var id = await models.CreateAsync(Header(collection: collection, assignees: new Dictionary<DevRole, long?> { [DevRole.Designer] = technologist }));
        var m = await models.GetAsync(id);
        Assert.Equal("Свитер — Осень-зима 2026", m.Name);
        Assert.Equal(technologist, m.Assignees.Single(a => a.Role == DevRole.Technologist).UserId);
        Assert.Equal(technologist, m.Assignees.Single(a => a.Role == DevRole.Designer).UserId); // явно выбранный не заменяется
        Assert.Equal(DateOnly.FromDateTime(host.Clock.UtcNow.AddHours(3)).AddDays(43), m.PlannedLaunch);
        Assert.Equal(("42–52", "50% шерсть, 50% акрил", "ручная стирка"), (m.Brief.SizeRange!, m.Brief.Composition!, m.Brief.Care!));
        var options = await models.OptionsAsync();
        Assert.Equal(technologist, options.DefaultAssignees![DevRole.Technologist]);

        // Шапка без шаблона: задание пустое; кнопка «из шаблона» отвечает понятной ошибкой. Явная дата запуска не меняется.
        var hat = await models.CreateAsync(Header("Шапка «Помпон»", DevCategory.Hat, launch: new DateOnly(2026, 11, 1)));
        var h = await models.GetAsync(hat);
        Assert.Equal((new DateOnly(2026, 11, 1), (string?)null, false), (h.PlannedLaunch!.Value, h.Brief.SizeRange, h.HasTemplate));
        Assert.Equal("dev.template.empty", (await Assert.ThrowsAsync<BusinessRuleException>(() => models.FillBriefFromTemplateAsync(hat, h.RowVersion))).Code);

        // Задание дополняется из шаблона только в пустых полях.
        await settings.SaveTemplateAsync(new DevCategoryTemplateDto(DevCategory.Hat, "54–58", null, "100% акрил", null, null));
        h = await models.GetAsync(hat);
        await models.UpdateBriefAsync(hat, h.Brief with { Composition = "шерсть" }, h.RowVersion);
        h = await models.GetAsync(hat);
        await models.FillBriefFromTemplateAsync(hat, h.RowVersion);
        h = await models.GetAsync(hat);
        Assert.Equal(("54–58", "шерсть"), (h.Brief.SizeRange!, h.Brief.Composition!));

        // «Создать похожую»: карточка и задание копируются, стадия — «Идея», файлов и истории нет.
        await models.AddFileAsync(id, DevFileKind.Sketch, "sketch.png", Png, null);
        m = await models.GetAsync(id);
        await models.MoveAsync(id, DevStage.Brief, null, m.RowVersion);
        var copy = await models.GetAsync(await models.CopyAsync(id));
        Assert.Equal(("Свитер — Осень-зима 2026 (копия)", DevStage.Idea, "42–52"), (copy.Name, copy.Stage, copy.Brief.SizeRange!));
        Assert.Empty(copy.Files);
        Assert.Single(copy.History);
        Assert.Equal(m.Assignees.Select(a => a.UserId), copy.Assignees.Select(a => a.UserId));

        // Без автоматической даты запуск остаётся пустым.
        await settings.SaveAutomationAsync(new Dictionary<DevRole, long?>(), false, true);
        Assert.Null((await models.GetAsync(await models.CreateAsync(Header("Без даты")))).PlannedLaunch);
    }

    [SqlFact]
    public async Task Uploading_program_or_pattern_moves_stage_and_can_be_switched_off()
    {
        var org = await CreateOrganizationAsync();
        var (models, settings, _, s) = As(org.OwnerUserId, org.OrganizationId);
        await using var __ = s;
        var id = await models.CreateAsync(Header("Свитер «Ромбы»"));

        await models.AddFileAsync(id, DevFileKind.Pattern, "romby.png", Png, null);
        var m = await models.GetAsync(id);
        Assert.Equal(DevStage.Pattern, m.Stage);
        Assert.StartsWith("Автоматически", m.History.Last().Comment);

        await models.MoveAsync(id, DevStage.Program, null, m.RowVersion);
        Assert.Contains(DevAutomationRules.AllHints[4], (await models.GetAsync(id)).Hints!);
        await models.AddFileAsync(id, DevFileKind.MachineProgram, "romby.sin", Program, null);
        m = await models.GetAsync(id);
        Assert.Equal(DevStage.Sample, m.Stage);

        // Программа на другой стадии и выключенный автопереход стадию не меняют.
        await models.AddFileAsync(id, DevFileKind.MachineProgram, "romby.sin", Program, "версия 2");
        Assert.Equal(DevStage.Sample, (await models.GetAsync(id)).Stage);
        await settings.SaveAutomationAsync(new Dictionary<DevRole, long?>(), true, false);
        var other = await models.CreateAsync(Header("Без автоперехода"));
        await models.AddFileAsync(other, DevFileKind.Pattern, "p.png", Png, null);
        Assert.Equal(DevStage.Idea, (await models.GetAsync(other)).Stage);
    }

    [SqlFact]
    public async Task Excel_template_import_is_all_or_nothing_and_export_reimports_unchanged()
    {
        var org = await CreateOrganizationAsync();
        var (models, settings, exchange, s) = As(org.OwnerUserId, org.OrganizationId);
        await using var __ = s;
        await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Вымышленный магазин»", null, null, false, true, null));

        Assert.NotEmpty(await exchange.TemplateAsync());
        var header = DevExchangeService.Columns.Take(12).ToList();
        IReadOnlyList<string> Row(params string[] cells) => cells;
        var bad = SqlTestHost.Spreadsheet.Write([new SheetData("Модели", header, [
            Row("Свитер «Лес»", "Свитер", "Женское", "Весна 2027", "ООО «Вымышленный магазин»", "01.03.2027", "Высокий", "да", "42–50", "зелёный", "200", ""),
            Row("", "Трусы", "", "", "ООО «Нет такого»", "завтра", "", "может быть", "", "", "-5", ""),
        ])]);
        var plan = await exchange.PreviewAsync(new MemoryStream(bad));
        Assert.Equal((1, 1), (plan.ToCreate, plan.ErrorRows));
        var errors = plan.Rows.Single(r => r.Action == ImportAction.Error).Errors;
        Assert.Contains("Неизвестная категория.", errors);
        Assert.Contains("Заказчик не найден среди покупателей.", errors);
        await Assert.ThrowsAsync<BusinessRuleException>(() => exchange.ApplyAsync(plan.Rows));
        Assert.Equal(0, (await models.ListAsync(new DevModelFilter(IncludeFinal: true))).Total);

        var good = SqlTestHost.Spreadsheet.Write([new SheetData("Модели", header, [
            Row("Свитер «Лес»", "свитер", "женское", "Весна 2027", "ООО «Вымышленный магазин»", "01.03.2027", "Высокий", "да", "42–50", "зелёный", "200", ""),
            Row("", "Шапка", "Детское", "Весна 2027", "", "", "", "нет", "", "", "", "из Excel"),
        ])]);
        plan = await exchange.PreviewAsync(new MemoryStream(good));
        Assert.True(plan.CanApply);
        Assert.Equal(2, (await exchange.ApplyAsync(plan.Rows)).Created);
        var list = (await models.ListAsync(new DevModelFilter())).Rows;
        Assert.Equal(2, list.Count);
        var forest = list.Single(r => r.Name == "Свитер «Лес»");
        Assert.Equal(("Весна 2027", "ООО «Вымышленный магазин»", DevPriority.High, new DateOnly(2027, 3, 1)),
            (forest.CollectionName!, forest.CustomerName!, forest.Priority, forest.PlannedLaunch!.Value));
        Assert.Contains(list, r => r.Name == "Шапка — Весна 2027" && r.PlannedLaunch is not null);
        Assert.Single(await settings.LookupsAsync(DevLookupKind.Collection));

        // Выгрузка загружается обратно без изменений: строки с номерами пропускаются.
        var export = await exchange.ExportAsync(new DevModelFilter(IncludeFinal: true));
        var again = await exchange.PreviewAsync(new MemoryStream(export));
        Assert.Equal((0, 2, false), (again.ToCreate, again.Unchanged, again.CanApply));
    }

    [SqlFact]
    public async Task Bell_and_quick_search_show_development_models()
    {
        var org = await CreateOrganizationAsync();
        var (models, _, _, s) = As(org.OwnerUserId, org.OrganizationId);
        await using var __ = s;
        var late = await models.CreateAsync(Header("Свитер «Колокольчик»", launch: new DateOnly(2026, 10, 1),
            assignees: new Dictionary<DevRole, long?> { [DevRole.Designer] = org.OwnerUserId }));
        var waiting = await models.CreateAsync(Header("Шарф «Согласование»"));
        await models.MoveAsync(waiting, DevStage.Approval, null, (await models.GetAsync(waiting)).RowVersion);

        var bell = await s.Notifications.ListAsync();
        Assert.Contains(bell, n => n.Text == "Модели разработки на вашей стадии: 1" && n.Href == "dev/my");
        Assert.Contains(bell, n => n.Text == "Просрочено у вас в разработке: 1");
        Assert.Contains(bell, n => n.Text == "Модели ждут согласования и утверждения: 1");

        var number = (await models.GetAsync(late)).Number;
        var hits = await s.Search.SearchAsync(number);
        Assert.Contains(hits, h => h.Href == $"dev/models/{late}");
        Assert.Contains(await s.Search.SearchAsync("Колокольчик"), h => h.Href == $"dev/models/{late}");
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
