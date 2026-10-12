using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Development;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Development;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>D700–D709: модели разработки — стадии и история, задание, файлы, права отдела, чужая организация, доска и «Мои задачи».</summary>
public sealed class DevModelTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 970_000_000;
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52];
    private static readonly byte[] Program = [0x53, 0x49, 0x4E, 0x00, 0x01, 0x02, 0x03, 0x04];

    private (DevModelService Models, DevSettingsService Settings, Services All) As(long userId, long organizationId)
    {
        var all = host.As(userId, organizationId);
        var user = new TestUser { UserId = userId, OrganizationId = organizationId, ClientAddress = host.ClientAddress };
        var guard = new AccessGuard(all.Db, user, host.Clock);
        return (new DevModelService(all.Db, guard, user, host.Clock), new DevSettingsService(all.Db, guard, user, host.Clock), all);
    }

    private static DevModelHeader Header(string name = "Свитер «Северный»", long? collection = null, long? customer = null, bool hasPattern = true,
        DateOnly? launch = null, IReadOnlyDictionary<DevRole, long?>? assignees = null) =>
        new(name, collection, DevCategory.Sweater, DevAudience.Women, false, hasPattern, customer, null, launch ?? new DateOnly(2026, 12, 15),
            DevPriority.Normal, assignees ?? new Dictionary<DevRole, long?>());

    [SqlFact]
    public async Task Model_lifecycle_history_brief_files_and_audit()
    {
        var org = await CreateOrganizationAsync();
        var (models, settings, s) = As(org.OwnerUserId, org.OrganizationId);
        await using var _ = s;
        var collection = await settings.AddLookupAsync(DevLookupKind.Collection, "Осень-зима 2026", null);
        var customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Вымышленный заказчик»", null, null, false, true, null));

        var id = await models.CreateAsync(Header(collection: collection, customer: customer,
            assignees: new Dictionary<DevRole, long?> { [DevRole.Designer] = org.OwnerUserId }));
        var second = await models.CreateAsync(Header("Шапка с помпоном", hasPattern: false));
        var m = await models.GetAsync(id);
        Assert.Equal("М-00001", m.Number);
        Assert.Equal("М-00002", (await models.GetAsync(second)).Number);
        Assert.Equal(("Осень-зима 2026", "ООО «Вымышленный заказчик»", DevStage.Idea), (m.CollectionName, m.CustomerName, m.Stage));
        Assert.Equal(DevStage.Brief, m.NextStage);
        Assert.Single(m.History);

        // Задание на разработку: Владелец видит и задаёт целевые суммы.
        await models.UpdateBriefAsync(id, new DevBrief("Свитер оверсайз с жаккардом", "42–52", "молочный, графит", 1500m, 4200m, 300,
            new DateOnly(2026, 11, 20), "50% шерсть, 50% акрил", null, "ручная стирка"), m.RowVersion);
        m = await models.GetAsync(id);
        Assert.Equal((1500m, 4200m, 300), (m.Brief.TargetCost!.Value, m.Brief.TargetPrice!.Value, m.Brief.RunQuantity!.Value));

        // Стадии: дальше, пропуск вперёд, назад; устаревшая версия — конфликт.
        await models.MoveAsync(id, DevStage.Brief, null, m.RowVersion);
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => models.MoveAsync(id, DevStage.Pattern, null, m.RowVersion));
        m = await models.GetAsync(id);
        await models.MoveAsync(id, DevStage.Sample, "рисунок и программа — в файлах", m.RowVersion);
        m = await models.GetAsync(id);
        await models.MoveAsync(id, DevStage.Rework, null, m.RowVersion);
        m = await models.GetAsync(id);
        Assert.Equal(DevStage.Rework, m.Stage);
        Assert.Equal(new[] { DevStage.Idea, DevStage.Brief, DevStage.Sample, DevStage.Rework }, m.History.Select(h => h.To));
        Assert.Equal("рисунок и программа — в файлах", m.History[2].Comment);

        // Модель без рисунка не попадает на стадию «Рисунок».
        var noPattern = await models.GetAsync(second);
        Assert.DoesNotContain(DevStage.Pattern, noPattern.MoveTargets);
        Assert.Equal("dev.stage.move", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            models.MoveAsync(second, DevStage.Pattern, null, noPattern.RowVersion))).Code);

        // Файлы: тип по сигнатуре, версия у каждого файла, скачивание пишет журнал.
        Assert.Equal("dev.file.type", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            models.AddFileAsync(id, DevFileKind.Pattern, "evil.png", "<html><script>alert(1)</script>"u8.ToArray(), null))).Code);
        var v1 = await models.AddFileAsync(id, DevFileKind.Pattern, "jacquard.png", Png, "первый вариант");
        var v2 = await models.AddFileAsync(id, DevFileKind.Pattern, "jacquard.png", Png, "после доработки");
        var prog = await models.AddFileAsync(id, DevFileKind.MachineProgram, "jacquard.sin", Program, null);
        m = await models.GetAsync(id);
        Assert.Equal(new[] { 2, 1 }, m.Files.Where(f => f.FileName == "jacquard.png").Select(f => f.Version));
        var download = await models.GetFileAsync(prog);
        Assert.Equal(("jacquard.sin", DevFileTypes.Binary), (download.FileName, download.ContentType));
        Assert.Equal(Program, download.Content);
        // D799: превью картинки журнал не пишет, превью не картинки не отдаётся.
        Assert.Equal(Png, (await models.GetFileAsync(v1, preview: true)).Content);
        await Assert.ThrowsAsync<NotFoundException>(() => models.GetFileAsync(prog, preview: true));

        await using var db = host.NewDb();
        // История стадий и файлы — только вставка, даже напрямую в базе.
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE [kniterp].[dev_model_stages] SET [Comment] = N'подмена' WHERE [ModelId] = {0}", id));
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM [kniterp].[dev_model_stages] WHERE [ModelId] = {0}", id));
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM [kniterp].[dev_files] WHERE [Id] = {0}", v1));
        Assert.Equal(4, await db.DevModelStages.CountAsync(h => h.ModelId == id));
        Assert.True(await db.DevFiles.AnyAsync(f => f.Id == v2));

        var audit = await db.AuditEntries.Where(e => e.OrganizationId == org.OrganizationId && e.EntityType == nameof(DevModel)).ToListAsync();
        Assert.Contains(audit, e => e.Action == AuditActions.DevModelCreated);
        Assert.Contains(audit, e => e.Action == AuditActions.DevStageChanged && e.After == "Доработка");
        Assert.Contains(audit, e => e.Action == AuditActions.DevBriefChanged);
        Assert.Contains(audit, e => e.Action == AuditActions.DevFileAdded);
        Assert.Single(audit, e => e.Action == AuditActions.DevFileViewed);
        // Целевые суммы в журнал не попадают (журнал видят без права на себестоимость).
        Assert.DoesNotContain(audit, e => (e.After ?? "").Contains("1500") || (e.After ?? "").Contains("4200"));
    }

    [SqlFact]
    public async Task Foreign_organization_gets_not_found_for_model_and_files()
    {
        var a = await CreateOrganizationAsync();
        var b = await CreateOrganizationAsync();
        long id, file;
        byte[] version;
        {
            var (models, _, s) = As(a.OwnerUserId, a.OrganizationId);
            await using var __ = s;
            id = await models.CreateAsync(Header());
            file = await models.AddFileAsync(id, DevFileKind.Sketch, "sketch.png", Png, null);
            version = (await models.GetAsync(id)).RowVersion;
        }

        var (other, otherSettings, os) = As(b.OwnerUserId, b.OrganizationId);
        await using var ___ = os;
        await Assert.ThrowsAsync<NotFoundException>(() => other.GetAsync(id));
        await Assert.ThrowsAsync<NotFoundException>(() => other.MoveAsync(id, DevStage.Brief, null, version));
        await Assert.ThrowsAsync<NotFoundException>(() => other.UpdateAsync(id, Header("Чужая"), version));
        await Assert.ThrowsAsync<NotFoundException>(() => other.CancelAsync(id, "чужая", version));
        await Assert.ThrowsAsync<NotFoundException>(() => other.GetFileAsync(file));
        await Assert.ThrowsAsync<NotFoundException>(() => other.AddFileAsync(id, DevFileKind.Sketch, "x.png", Png, null));
        Assert.Empty((await other.ListAsync(new DevModelFilter(IncludeFinal: true))).Rows);

        // Коллекция чужой организации в своей модели не принимается.
        long foreignCollection;
        {
            var (_, settings, s) = As(a.OwnerUserId, a.OrganizationId);
            await using var __ = s;
            foreignCollection = await settings.AddLookupAsync(DevLookupKind.Collection, "Весна 2027", null);
        }

        Assert.Equal("dev.model.collection", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            other.CreateAsync(Header(collection: foreignCollection)))).Code);
        // Исполнитель — только участник своей организации.
        Assert.Equal("dev.model.assignee", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            other.CreateAsync(Header(assignees: new Dictionary<DevRole, long?> { [DevRole.Technologist] = a.OwnerUserId })))).Code);
        Assert.NotEmpty(await otherSettings.StagesAsync());
    }

    [SqlFact]
    public async Task Only_approver_approves_technologist_hidden_costs_sampler_read_only()
    {
        var org = await CreateOrganizationAsync();
        var technologist = await InviteActiveAsync(org, SystemRoles.DevTechnologist);
        var chief = await InviteActiveAsync(org, SystemRoles.DevChief);
        var sampler = await InviteActiveAsync(org, SystemRoles.DevSampler);
        long id;
        {
            var (models, _, s) = As(org.OwnerUserId, org.OrganizationId);
            await using var __ = s;
            id = await models.CreateAsync(Header(assignees: new Dictionary<DevRole, long?> { [DevRole.Technologist] = technologist }));
            var m = await models.GetAsync(id);
            await models.UpdateBriefAsync(id, new DevBrief(null, null, null, 900m, 2500m, null, null, null, null, null), m.RowVersion);
        }

        {
            // Технолог ведёт модель, но не утверждает и не видит целевые суммы; его правка задания суммы не стирает.
            var (models, settings, s) = As(technologist, org.OrganizationId);
            await using var __ = s;
            var m = await models.GetAsync(id);
            Assert.False(m.CanSeeCost);
            Assert.Null(m.Brief.TargetCost);
            Assert.DoesNotContain(DevStage.Approved, m.MoveTargets);
            await models.MoveAsync(id, DevStage.Costing, null, m.RowVersion);
            m = await models.GetAsync(id);
            Assert.Equal("dev.stage.move", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                models.MoveAsync(id, DevStage.Approved, null, m.RowVersion))).Code);
            m = await models.GetAsync(id);
            await models.UpdateBriefAsync(id, m.Brief with { Description = "уточнено технологом" }, m.RowVersion);
            await Assert.ThrowsAsync<AccessDeniedException>(() => settings.SetStageAsync(DevStage.Approval, 3, false));
            var task = Assert.Single(await models.MyTasksAsync());
            Assert.True(task.ResponsibleNow);
            Assert.Equal(new[] { DevRole.Technologist }, task.MyRoles);
        }

        {
            // Главный технолог утверждает и видит суммы; отменить утверждённую модель без права утверждать нельзя.
            var (models, _, s) = As(chief, org.OrganizationId);
            await using var __ = s;
            var m = await models.GetAsync(id);
            Assert.Equal((900m, 2500m, "уточнено технологом"), (m.Brief.TargetCost!.Value, m.Brief.TargetPrice!.Value, m.Brief.Description!));
            await models.MoveAsync(id, DevStage.Approved, "образец принят", m.RowVersion);
            m = await models.GetAsync(id);
            Assert.Equal(DevStage.Approved, m.Stage);
            Assert.Null(m.NextStage);
            Assert.DoesNotContain(DevStage.Handed, m.MoveTargets);
        }

        {
            var (models, _, s) = As(technologist, org.OrganizationId);
            await using var __ = s;
            var m = await models.GetAsync(id);
            Assert.Empty(m.MoveTargets);
            Assert.Equal("dev.stage.move", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                models.MoveAsync(id, DevStage.Rework, null, m.RowVersion))).Code);
            Assert.Equal("dev.stage.move", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                models.CancelAsync(id, "передумали", m.RowVersion))).Code);
        }

        {
            // Образцовщица видит модель, но не меняет её и не загружает файлы модели.
            var (models, _, s) = As(sampler, org.OrganizationId);
            await using var __ = s;
            var m = await models.GetAsync(id);
            Assert.False(m.CanEdit);
            await Assert.ThrowsAsync<AccessDeniedException>(() => models.CreateAsync(Header()));
            await Assert.ThrowsAsync<AccessDeniedException>(() => models.MoveAsync(id, DevStage.Rework, null, m.RowVersion));
            await Assert.ThrowsAsync<AccessDeniedException>(() => models.AddFileAsync(id, DevFileKind.Photo, "x.png", Png, null));
        }

        // Роли отдела разработки есть в новой организации, «Разработка» — у Владельца.
        await using var db = host.NewDb();
        var roles = await db.Roles.Where(r => r.OrganizationId == org.OrganizationId && r.IsSystem).Select(r => r.Code).ToListAsync();
        Assert.Contains(SystemRoles.DevChief, roles);
        Assert.Contains(SystemRoles.DevDessinator, roles);
    }

    [SqlFact]
    public async Task Board_filters_overdue_and_stage_settings()
    {
        var org = await CreateOrganizationAsync();
        var designer = await InviteActiveAsync(org, SystemRoles.DevTechnologist);
        var (models, settings, s) = As(org.OwnerUserId, org.OrganizationId);
        await using var _ = s;
        var customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Пример»", null, null, false, true, null));
        var late = await models.CreateAsync(Header("Просроченная", launch: new DateOnly(2026, 10, 1)));
        var mine = await models.CreateAsync(Header("Для дизайнера", customer: customer,
            assignees: new Dictionary<DevRole, long?> { [DevRole.Designer] = designer }));
        var cancelled = await models.CreateAsync(Header("Отменённая"));
        await models.CancelAsync(cancelled, "заказчик отказался", (await models.GetAsync(cancelled)).RowVersion);

        var board = await models.BoardAsync(new DevModelFilter());
        var idea = board.Single(c => c.Stage == DevStage.Idea);
        Assert.Equal(2, idea.Models.Count);
        Assert.DoesNotContain(board, c => c.Stage is DevStage.Handed or DevStage.Cancelled);
        Assert.True(idea.Models.Single(r => r.Id == late).IsLate);

        Assert.Equal(late, Assert.Single((await models.ListAsync(new DevModelFilter(OverdueOnly: true))).Rows).Id);
        Assert.Equal(mine, Assert.Single((await models.ListAsync(new DevModelFilter(AssigneeUserId: designer))).Rows).Id);
        Assert.Equal(mine, Assert.Single((await models.ListAsync(new DevModelFilter(CustomerId: customer))).Rows).Id);
        Assert.Equal(cancelled, Assert.Single((await models.ListAsync(new DevModelFilter(Stage: DevStage.Cancelled))).Rows).Id);
        Assert.Equal(3, (await models.ListAsync(new DevModelFilter(IncludeFinal: true))).Total);

        // Стадия дольше норматива — просрочена; отключённая стадия пропускается кнопкой «Дальше» и уходит с доски.
        await settings.SetStageAsync(DevStage.Idea, 2, true);
        var saved = host.Clock.UtcNow;
        try
        {
            host.Clock.UtcNow = saved.AddDays(3);
            Assert.Contains(mine, (await models.ListAsync(new DevModelFilter(OverdueOnly: true))).Rows.Select(r => r.Id));
        }
        finally
        {
            host.Clock.UtcNow = saved;
        }

        await settings.SetStageAsync(DevStage.Program, 0, false);
        Assert.Equal("dev.stage.required", (await Assert.ThrowsAsync<BusinessRuleException>(() => settings.SetStageAsync(DevStage.Sample, 3, false))).Code);
        Assert.DoesNotContain((await models.BoardAsync(new DevModelFilter())), c => c.Stage == DevStage.Program);
        var m = await models.GetAsync(mine);
        await models.MoveAsync(mine, DevStage.YarnAndKnit, null, m.RowVersion);
        Assert.Equal(DevStage.Sample, (await models.GetAsync(mine)).NextStage);

        // Отменённая модель не меняется и возвращается в работу на прежнюю стадию.
        var c = await models.GetAsync(cancelled);
        Assert.Equal("dev.model.cancelled", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            models.UpdateAsync(cancelled, Header("Новое имя"), c.RowVersion))).Code);
        c = await models.GetAsync(cancelled);
        await models.RestoreAsync(cancelled, c.RowVersion);
        Assert.Equal(DevStage.Idea, (await models.GetAsync(cancelled)).Stage);

        // Справочник: дубль не принимается, архивная коллекция не выбирается в новой модели.
        var autumn = await settings.AddLookupAsync(DevLookupKind.Collection, "Школа", null);
        Assert.Equal("dev.lookup.duplicate", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            settings.AddLookupAsync(DevLookupKind.Collection, "Школа", null))).Code);
        var row = Assert.Single(await settings.LookupsAsync(DevLookupKind.Collection));
        await settings.SetLookupArchivedAsync(autumn, true, row.RowVersion);
        Assert.Equal("dev.model.collection", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            models.CreateAsync(Header(collection: autumn)))).Code);
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
