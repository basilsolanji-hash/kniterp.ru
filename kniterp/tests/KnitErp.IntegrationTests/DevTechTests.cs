using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Development;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Access;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Development;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// D710–D717: техдокументация модели по ролям — станки и журнал, палитра дизайнера, детали и заправка дессинатора, программа с параметрами
/// станка, размеры и табель мер с градацией конструктора, операции технолога, факт образца цеха, показатели заполнения, права и изоляция.
/// </summary>
public sealed class DevTechTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 972_000_000;
    private static readonly byte[] Program = [0x53, 0x49, 0x4E, 0x00, 0x01, 0x02];

    private sealed record Kit(DevModelService Models, DevTechService Tech, DevShopService Shop, DevSettingsService Settings, Services All) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => All.DisposeAsync();
    }

    private Kit As(long userId, long organizationId)
    {
        var all = host.As(userId, organizationId);
        var user = new TestUser { UserId = userId, OrganizationId = organizationId, ClientAddress = host.ClientAddress };
        var guard = new AccessGuard(all.Db, user, host.Clock);
        var models = new DevModelService(all.Db, guard, user, host.Clock);
        var tech = new DevTechService(all.Db, guard, user, host.Clock, models);
        return new Kit(models, tech, new DevShopService(all.Db, guard, user, host.Clock, tech), new DevSettingsService(all.Db, guard, user, host.Clock), all);
    }

    private static DevModelHeader Header(string name) =>
        new(name, null, DevCategory.Accessory, DevAudience.Unisex, false, false, null, null, new DateOnly(2026, 12, 1), DevPriority.Normal,
            new Dictionary<DevRole, long?>());

    [SqlFact]
    public async Task Tech_card_is_built_by_roles_and_shop_records_sample_fact()
    {
        var org = await CreateOrganizationAsync();
        var dessinator = await InviteActiveAsync(org, SystemRoles.DevDessinator);
        var sampler = await InviteActiveAsync(org, SystemRoles.DevSampler);
        long modelId, machineId, front, back, sample, op;
        long main, extra;
        await using (var k = As(org.OwnerUserId, org.OrganizationId))
        {
            // Справочники: станок, переплетение, операции, пряжа в номенклатуре.
            machineId = await k.Tech.CreateMachineAsync(new DevMachineValues("STOLL №3", DevMachineBrand.Stoll, "CMS 530", 7, 8, 130, 2, null));
            var weave = await k.Settings.AddLookupAsync(DevLookupKind.Weave, "Ластик 2×2", null);
            op = await k.Shop.AddOperationTypeAsync(new DevOperationValues("Вязание", "шт", 12.5m, 3m, 1));
            await k.Shop.AddOperationTypeAsync(new DevOperationValues("Упаковка в короба", "шт", 1m, 0.2m, 5));
            var kg = (await k.All.Catalog.ListUnitsAsync()).Single(u => u.Code == "166").Id;
            var cotton = await k.All.Catalog.CreateItemAsync(new ItemCommand("ПР-1", "Хлопок 20/1 молочный", ItemType.RawMaterial, kg, null));
            var cottonRed = await k.All.Catalog.CreateItemAsync(new ItemCommand("ПР-2", "Хлопок 20/1 красный", ItemType.RawMaterial, kg, null));

            // Дизайнер: модель и палитра из номенклатуры.
            modelId = await k.Models.CreateAsync(Header("Подвяз с полосками, 2 цвета"));
            main = await k.Tech.AddColorAsync(modelId, "Основной", cotton, "#F2EFE6", null);
            extra = await k.Tech.AddColorAsync(modelId, "доп.1", cottonRed, "#C0392B", null);
            Assert.Equal("dev.color.duplicate", (await Assert.ThrowsAsync<BusinessRuleException>(() => k.Tech.AddColorAsync(modelId, "доп.1", null, null, null))).Code);
            var finished = await k.All.Catalog.CreateItemAsync(new ItemCommand("СВ-1", "Свитер", ItemType.Finished,
                (await k.All.Catalog.ListUnitsAsync()).Single(u => u.Code == "796").Id, null));
            Assert.Equal("dev.color.yarn", (await Assert.ThrowsAsync<BusinessRuleException>(() => k.Tech.AddColorAsync(modelId, "доп.2", finished, null, null))).Code);

            // Детали.
            front = await k.Tech.AddDetailAsync(modelId, "Подвяз", 1);
            back = await k.Tech.AddDetailAsync(modelId, "Подвяз узкий", 1);

            // Конструктор: сетка 1–4, базовый 2; размеры детали; упаковка; табель мер с градацией.
            await k.Shop.SetSizeGridAsync(modelId, ["1", "2", "3", "4"], "2");
            var sizes = (await k.Shop.SizesAsync(modelId)).Sizes;
            Assert.Equal(new[] { "1", "2", "3", "4" }, sizes.Select(z => z.Name));
            Assert.True(sizes[1].IsBase);
            await k.Shop.SetDetailSizesAsync(modelId, [(front, sizes[0].Id, 16m, 14m, null), (front, sizes[1].Id, 18m, 16m, null),
                (front, sizes[2].Id, 20m, 18m, null), (front, sizes[3].Id, 22.5m, 20m, null)]);
            await k.Shop.SetPackingAsync(modelId, new Dictionary<long, int?> { [sizes[0].Id] = 70, [sizes[1].Id] = 60, [sizes[2].Id] = 50, [sizes[3].Id] = 50 },
                "40 × 30 × 30 см");
            var length = await k.Shop.AddMeasureAsync(modelId, "Длина после ВТО", 0.5m, 2m);
            await k.Shop.SetMeasureValuesAsync(modelId, [(length, sizes[1].Id, 16m)]);
            Assert.Equal(3, await k.Shop.GradeAsync(modelId, overwrite: false));
            var graded = (await k.Shop.SizesAsync(modelId)).Measures.Single().Values;
            Assert.Equal(new[] { 14m, 16m, 18m, 20m }, sizes.Select(z => graded[z.Id]));
            // Размер с данными убрать нельзя.
            Assert.Equal("dev.size.used", (await Assert.ThrowsAsync<BusinessRuleException>(() => k.Shop.SetSizeGridAsync(modelId, ["2", "3", "4"], "2"))).Code);

            // Технолог: стандартные операции по этапам и своя операция с пояснением.
            Assert.Equal(2, await k.Shop.AddStandardOperationsAsync(modelId));
            var custom = await k.Shop.AddOperationTypeAsync(new DevOperationValues("Убрать боковые нитки", "шт", 0.5m, 0.3m, null));
            await k.Shop.AddOperationAsync(modelId, custom, "и разделить по 5 полосок", null, null);
            var ops = await k.Shop.OperationsAsync(modelId);
            Assert.Equal(new[] { "Вязание", "Упаковка в короба", "Убрать боковые нитки" }, ops.Rows.Select(r => r.Name));
            Assert.Equal((3.5m, 14m), (ops.TotalMinutes, ops.TotalCost!.Value));
            await k.Shop.MoveOperationAsync(ops.Rows[2].Id, -1);
            Assert.Equal("Убрать боковые нитки", (await k.Shop.OperationsAsync(modelId)).Rows[1].Name);

            // Образец создаёт технолог.
            sample = await k.Shop.CreateSampleAsync(modelId, sizes[1].Id);

            var detail = await k.Tech.DetailAsync(front);
            await k.Tech.SetKnittingAsync(front, machineId, weave, null, null, null, detail.RowVersion);
        }

        await using (var k = As(dessinator, org.OrganizationId))
        {
            // Дессинатор: план и заправка в пределах нитеводителей станка, программа с параметрами станка.
            var d = await k.Tech.DetailAsync(front);
            await k.Tech.SetKnittingAsync(front, machineId, d.WeaveId, 180, 100m, "Если хлопок 20/1 — заправляем в 4 конца, 24/1 — в 5 концов.", d.RowVersion);
            d = await k.Tech.DetailAsync(front);
            IReadOnlyList<DevThreadingRow> rows =
            [
                new(8, DevFeederSide.Left, extra, null, 4, true, "N", false),
                new(5, DevFeederSide.Left, main, null, 2, true, "N", false),
                new(5, DevFeederSide.Right, main, null, 2, true, "N", false),
                new(4, DevFeederSide.Right, null, "Спандекс — в цвет основы", 1, false, "N", true),
                new(1, DevFeederSide.Right, null, "Разделитель (нейлон)", 1, false, "N", false),
            ];
            Assert.Equal("dev.threading.feeder", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                k.Tech.SetThreadingAsync(front, [.. rows, new DevThreadingRow(9, DevFeederSide.Left, main, null, 2, true, "N", false)], d.RowVersion))).Code);
            await k.Tech.SetThreadingAsync(front, rows, d.RowVersion);
            // Устаревшая версия детали после правки заправки — конфликт (две вкладки).
            await Assert.ThrowsAsync<ConcurrencyConflictException>(() => k.Tech.SetThreadingAsync(front, rows, d.RowVersion));
            await k.Tech.AddProgramAsync(front, "podvyaz.sin", Program, "первая");

            // Копия заправки на другую деталь: без станка нельзя, со станком — те же позиции.
            var b = await k.Tech.DetailAsync(back);
            Assert.Equal("dev.threading.machine", (await Assert.ThrowsAsync<BusinessRuleException>(() => k.Tech.CopyThreadingAsync(front, back, b.RowVersion))).Code);
            await k.Tech.SetKnittingAsync(back, machineId, null, null, null, null, b.RowVersion);
            await k.Tech.CopyThreadingAsync(front, back, (await k.Tech.DetailAsync(back)).RowVersion);
            Assert.Equal(5, (await k.Tech.DetailAsync(back)).Threading.Count);

            // Станок с занятым 8-м нитеводителем нельзя сменить на 6-нитеводительный.
            await using var owner = As(org.OwnerUserId, org.OrganizationId);
            var small = await owner.Tech.CreateMachineAsync(new DevMachineValues("Shima №1", DevMachineBrand.ShimaSeiki, null, 12, 6, null, null, null));
            d = await k.Tech.DetailAsync(front);
            Assert.Equal("dev.threading.feeder", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                k.Tech.SetKnittingAsync(front, small, d.WeaveId, 180, 100m, d.Notes, d.RowVersion))).Code);

            // Дессинатор не меняет справочник станков, но пишет журнал станка.
            await Assert.ThrowsAsync<AccessDeniedException>(() => k.Tech.CreateMachineAsync(new DevMachineValues("Чужой", DevMachineBrand.Other, null, 5, 4, null, null, null)));
            await k.Tech.AddMachineNoteAsync(machineId, "перенёс, плохо работает", DevMachineStatus.Limited);
        }

        await using (var k = As(sampler, org.OrganizationId))
        {
            // Образцовщица у машины: видит техкарту, пишет факт; заправку и размеры не меняет.
            var card = await k.Tech.TechCardAsync(front);
            Assert.True(card.CanRecordFact);
            Assert.False(card.CanEditThreading);
            Assert.Equal(8, card.FeederCount);
            Assert.Equal(new[] { 8, 7, 6, 5, 4, 3, 2, 1 }, card.Feeders.Select(f => f.Feeder));
            var f8 = card.Feeders[0].Left!;
            Assert.Equal(("доп.1", "Хлопок 20/1 красный", 4, true), (f8.Yarn, f8.ItemName!, f8.Ends, f8.Paraffin));
            Assert.True(card.Feeders.Single(f => f.Feeder == 4).Right!.Elastic);
            Assert.Equal(("Ластик 2×2", 180, "40 × 30 × 30 см", DevMachineStatus.Limited), (card.Weave!, card.PlanKnitSeconds!.Value, card.PackBox!, card.MachineStatus!.Value));
            Assert.Equal(new[] { 70, 60, 50, 50 }, card.Sizes.Select(z => z.PerBox!.Value));
            Assert.Equal(22.5m, card.Sizes[3].OffMachineCm);
            Assert.Equal(3, card.Operations.Count);
            Assert.All(card.Operations, o => Assert.Null(o.Price)); // расценки — только с правом себестоимости
            Assert.Equal("podvyaz.sin", card.ProgramName);
            Assert.Contains("E7", card.ProgramParams);

            await Assert.ThrowsAsync<AccessDeniedException>(() => k.Tech.SetThreadingAsync(front, [], card.RowVersion));
            await Assert.ThrowsAsync<AccessDeniedException>(() => k.Shop.SetSizeGridAsync(modelId, ["1"], null));
            Assert.Equal("dev.share.sum", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                k.Shop.RecordFactAsync(sample, front, 195, 101m, [(main, 70m), (extra, 40m)]))).Code);
            await k.Shop.RecordFactAsync(sample, front, 195, 101m, [(main, 67m), (extra, 33m)], sharesInGrams: true);
            await k.Shop.RecordFactAsync(sample, back, 150, 60m, []);
            var facts = (await k.Tech.TechCardAsync(front)).Facts;
            var fact = Assert.Single(facts);
            Assert.Equal((195, 101m, 100m), (fact.KnitSeconds!.Value, fact.WeightGrams!.Value, fact.Shares.Sum(s => s.Percent)));
            var samples = await k.Shop.SamplesAsync(modelId);
            Assert.Equal(DevSampleStatus.Ready, samples.Samples.Single().Status); // факт по всем деталям — «Связан»
            // Замер образца по табелю мер, отклонение за допуском.
            await k.Shop.SetSampleMeasuresAsync(sample, [(samples.Samples.Single().Measures.Single().MeasureId, 17m)]);
            var measure = (await k.Shop.SamplesAsync(modelId)).Samples.Single().Measures.Single();
            Assert.Equal((16m, 17m, 1m, true), (measure.Plan!.Value, measure.Fact!.Value, measure.Deviation!.Value, measure.OutOfTolerance));
        }

        await using (var k = As(org.OwnerUserId, org.OrganizationId))
        {
            // Показатели заполнения: эскиза нет, программа только у одной детали, табель мер заполнен.
            var c = await k.Tech.CompletenessAsync(modelId);
            Assert.Equal(new[] { "Задание", "Эскиз", "Программы", "Размеры деталей" }, c.Sections.Where(s => !s.Done).Select(s => s.Name));
            Assert.Equal((6, 10, 60), (c.Done, c.Total, c.Percent));
            var card = await k.Tech.TechCardAsync(front);
            Assert.Equal(12.5m, card.Operations.Single(o => o.Name == "Вязание").Price);
            Assert.Single(await k.Tech.ProgramsAsync(machineId));
            Assert.Equal(DevMachineStatus.Limited, (await k.Tech.MachinesAsync()).Single(m => m.Id == machineId).Status);
            Assert.Equal("перенёс, плохо работает", (await k.Tech.MachineNotesAsync(machineId)).First().Text);
            Assert.Equal("dev.color.used", (await Assert.ThrowsAsync<BusinessRuleException>(() => k.Tech.RemoveColorAsync(main))).Code);
            Assert.Equal("dev.detail.used", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                k.Tech.RemoveDetailAsync(front, card.RowVersion))).Code);
            Assert.NotEmpty(await k.Tech.TechCardsAsync("Подвяз"));
            Assert.Equal(op, (await k.Shop.OperationTypesAsync()).Single(o => o.Name == "Вязание").Id);
        }

        await using var db = host.NewDb();
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM [kniterp].[dev_machine_notes] WHERE [MachineId] = {0}", machineId));
    }

    [SqlFact]
    public async Task Foreign_organization_and_shop_worker_access()
    {
        var a = await CreateOrganizationAsync();
        var b = await CreateOrganizationAsync();
        long detail, machine, model;
        await using (var k = As(a.OwnerUserId, a.OrganizationId))
        {
            machine = await k.Tech.CreateMachineAsync(new DevMachineValues("STOLL №1", DevMachineBrand.Stoll, null, 7, 8, null, null, null));
            model = await k.Models.CreateAsync(Header("Шапка"));
            detail = await k.Tech.AddDetailAsync(model, "Полотно", 1);
        }

        await using (var k = As(b.OwnerUserId, b.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => k.Tech.TechCardAsync(detail));
            await Assert.ThrowsAsync<NotFoundException>(() => k.Tech.DetailAsync(detail));
            await Assert.ThrowsAsync<NotFoundException>(() => k.Tech.MachineNotesAsync(machine));
            await Assert.ThrowsAsync<NotFoundException>(() => k.Tech.AddDetailAsync(model, "Чужая", 1));
            await Assert.ThrowsAsync<NotFoundException>(() => k.Shop.SizesAsync(model));
            await Assert.ThrowsAsync<NotFoundException>(() => k.Tech.AddMachineNoteAsync(machine, "чужой", null));
            var own = await k.Models.CreateAsync(Header("Своя"));
            var ownDetail = await k.Tech.AddDetailAsync(own, "Деталь", 1);
            // Станок чужой организации в свою деталь не выбрать.
            await Assert.ThrowsAsync<NotFoundException>(async () =>
                await k.Tech.SetKnittingAsync(ownDetail, machine, null, null, null, null, (await k.Tech.DetailAsync(ownDetail)).RowVersion));
            Assert.DoesNotContain(await k.Tech.TechCardsAsync(), r => r.DetailId == detail);
        }

        // Начальник цеха (отметка этапов, разработка — только чтение) открывает техкарту и пишет журнал станка, но не меняет модель.
        var worker = await InviteActiveAsync(a, SystemRoles.DepartmentHead);
        await using (var k = As(worker, a.OrganizationId))
        {
            Assert.Equal("Полотно", (await k.Tech.TechCardAsync(detail)).DetailName);
            Assert.False((await k.Tech.TechCardAsync(detail)).CanRecordFact);
            await k.Tech.AddMachineNoteAsync(machine, "нитеводитель 3 заедает", null);
            await Assert.ThrowsAsync<AccessDeniedException>(() => k.Tech.AddDetailAsync(model, "Нельзя", 1));
        }
    }

    /// <summary>D719: этикетки модели, детали и образца; обсуждение детали и станка цехом; настройки цеха и печати; изоляция организаций.</summary>
    [SqlFact]
    public async Task Labels_discussions_and_shop_settings()
    {
        DevObjects.Register();
        var a = await CreateOrganizationAsync();
        var b = await CreateOrganizationAsync();
        long model, detail, machine, sample;
        await using (var k = As(a.OwnerUserId, a.OrganizationId))
        {
            machine = await k.Tech.CreateMachineAsync(new DevMachineValues("STOLL №5", DevMachineBrand.Stoll, "CMS 530", 7, 8, null, null, null));
            model = await k.Models.CreateAsync(Header("Шарф"));
            detail = await k.Tech.AddDetailAsync(model, "Полотно", 2);
            await k.Shop.SetSizeGridAsync(model, ["S", "M"], null);
            sample = await k.Shop.CreateSampleAsync(model, (await k.Shop.SizesAsync(model)).Sizes[1].Id);

            var m = await k.Tech.LabelAsync(DevLabelKind.Model, model);
            Assert.StartsWith("M-", m.Code); // номер «М-…» латиницей — его кодирует Code 128
            Assert.All(m.Code, c => Assert.InRange(c, '!', '~'));
            var d = await k.Tech.LabelAsync(DevLabelKind.Detail, detail);
            Assert.Equal($"{m.Code}-D{detail}", d.Code);
            Assert.Contains(d.Lines, l => l.Key == "Количество" && l.Value == "2");
            var s = await k.Tech.LabelAsync(DevLabelKind.Sample, sample);
            Assert.EndsWith("-S1", s.Code);
            Assert.Contains(s.Lines, l => l.Key == "Размер" && l.Value == "M");
            Assert.Equal((58, 40, false), (s.WidthMm, s.HeightMm, s.OnA4));

            // Настройки: допуск 15 %, в показателе — только «Детали» и «Заправка», этикетка 43 × 25.
            Assert.Equal("dev.settings.sections", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                k.Settings.SaveShopSettingsAsync(10, [], 58, 40, false))).Code);
            Assert.Equal("dev.settings.label", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                k.Settings.SaveShopSettingsAsync(10, ["Детали"], 5, 40, false))).Code);
            await k.Settings.SaveShopSettingsAsync(15, ["Детали", "Заправка"], 43, 25, false);
            var c = await k.Tech.CompletenessAsync(model);
            Assert.Equal((1, 2), (c.Done, c.Total)); // деталь есть, заправки нет
            Assert.Equal(15, (await k.Tech.TechCardAsync(detail)).FactTolerancePercent);
            Assert.Equal(15, (await k.Shop.SamplesAsync(model)).FactTolerancePercent);
            Assert.Equal((43, 25), ((await k.Tech.LabelAsync(DevLabelKind.Detail, detail)).WidthMm, (await k.Tech.LabelAsync(DevLabelKind.Detail, detail)).HeightMm));
        }

        // Начальник цеха: этикетка детали и образца — да, модели — нет (нет права разработки); обсуждение детали и станка — да.
        var worker = await InviteActiveAsync(a, SystemRoles.DepartmentHead);
        await using (var k = As(worker, a.OrganizationId))
        {
            Assert.Equal("Полотно", (await k.Tech.LabelAsync(DevLabelKind.Detail, detail)).Title.Split(" · ")[1]);
            await k.All.Discussions.AddAsync(KnitErp.Domain.Tagging.TaggedEntity.DevDetail, detail, "нитеводитель 6 заедает", null);
            await k.All.Discussions.AddAsync(KnitErp.Domain.Tagging.TaggedEntity.DevMachine, machine, "перенёс на новое место", null);
            Assert.Single((await k.All.Discussions.ThreadAsync(KnitErp.Domain.Tagging.TaggedEntity.DevDetail, detail)).Comments);
            await Assert.ThrowsAnyAsync<Exception>(() => k.Settings.SaveShopSettingsAsync(10, ["Детали"], 58, 40, false));
        }

        // Чужая организация: ни этикеток, ни обсуждений.
        await using (var k = As(b.OwnerUserId, b.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => k.Tech.LabelAsync(DevLabelKind.Detail, detail));
            await Assert.ThrowsAsync<NotFoundException>(() => k.Tech.LabelAsync(DevLabelKind.Sample, sample));
            await Assert.ThrowsAsync<NotFoundException>(() => k.Tech.LabelAsync(DevLabelKind.Model, model));
            await Assert.ThrowsAsync<NotFoundException>(() => k.All.Discussions.ThreadAsync(KnitErp.Domain.Tagging.TaggedEntity.DevDetail, detail));
            await Assert.ThrowsAsync<NotFoundException>(() => k.All.Discussions.AddAsync(KnitErp.Domain.Tagging.TaggedEntity.DevMachine, machine, "чужой", null));
        }
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
