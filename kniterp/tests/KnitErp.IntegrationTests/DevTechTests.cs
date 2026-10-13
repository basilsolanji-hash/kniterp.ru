using KnitErp.Application.Access;
using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Development;
using KnitErp.Application.Organizations;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
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

    private sealed record Kit(DevModelService Models, DevTechService Tech, DevShopService Shop, DevSettingsService Settings, Services All,
        DevHandoffService Handoff, DevYarnService Yarn, DevCostService Costs, DevAnalyticsService Analytics, DevPassportService Passport) : IAsyncDisposable
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
        var shop = new DevShopService(all.Db, guard, user, host.Clock, tech);
        return new Kit(models, tech, shop, new DevSettingsService(all.Db, guard, user, host.Clock), all,
            new DevHandoffService(all.Db, guard, user, host.Clock, all.Catalog, all.TechCards,
                new KnitErp.Application.Catalog.ModificationService(all.Db, guard, user, host.Clock)),
            new DevYarnService(all.Db, guard, user, host.Clock, all.Documents), new DevCostService(all.Db, guard, user, host.Clock),
            new DevAnalyticsService(all.Db, guard, host.Clock, SqlTestHost.Spreadsheet), new DevPassportService(all.Db, guard, host.Clock, models, tech, shop));
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
            await k.Shop.SetDetailSizesAsync(modelId, Cells(front, [(sizes[0].Id, 16m, 14m), (sizes[1].Id, 18m, 16m), (sizes[2].Id, 20m, 18m),
                (sizes[3].Id, 22.5m, 20m)]));
            await k.Shop.SetPackingAsync(modelId, [new(sizes[0].Id, 0, 0, null, 70), new(sizes[1].Id, 0, 0, null, 60), new(sizes[2].Id, 0, 0, null, 50),
                new(sizes[3].Id, 0, 0, null, 50)], null, "40 × 30 × 30 см");
            var length = await k.Shop.AddMeasureAsync(modelId, "Длина после ВТО", 0.5m, 2m);
            await k.Shop.SetMeasureValuesAsync(modelId, [new(length, sizes[1].Id, 0, null, 16m)]);
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
            Assert.Equal(("Ластик 2×2", 180, DevMachineStatus.Limited), (card.Weave!, card.PlanKnitSeconds!.Value, card.MachineStatus!.Value));
            // Размеры и упаковка — во вкладке «Размеры», операции — во вкладке «Операции» (в техкарту вязания не входят, D719).
            var sz = await k.Shop.SizesAsync(modelId);
            Assert.Equal("40 × 30 × 30 см", sz.PackBox);
            Assert.Equal(new[] { 70, 60, 50, 50 }, sz.Sizes.Select(z => z.PerBox!.Value));
            Assert.Equal(22.5m, sz.Details.Single(d => d.DetailId == front).Values[sz.Sizes[3].Id].OffMachine);
            var ops = await k.Shop.OperationsAsync(modelId);
            Assert.Equal(3, ops.Rows.Count);
            Assert.All(ops.Rows, o => Assert.Null(o.Price ?? o.TypePrice)); // расценки — только с правом себестоимости
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
            await k.Shop.SetSampleMeasuresAsync(sample, [new(samples.Samples.Single().Measures.Single().MeasureId, 0, 0, null, 17m)]);
            var measure = (await k.Shop.SamplesAsync(modelId)).Samples.Single().Measures.Single();
            Assert.Equal((16m, 17m, 1m, true), (measure.Plan!.Value, measure.Fact!.Value, measure.Deviation!.Value, measure.OutOfTolerance));
        }

        await using (var k = As(org.OwnerUserId, org.OrganizationId))
        {
            // Показатели заполнения: эскиза нет, программа только у одной детали, табель мер заполнен.
            var c = await k.Tech.CompletenessAsync(modelId);
            Assert.Equal(new[] { "Задание", "Эскиз", "Программы", "Размеры деталей" }, c.Sections.Where(s => !s.Done).Select(s => s.Name));
            Assert.Equal((6, 10, 60), (c.Done, c.Total, c.Percent));
            Assert.Equal(12.5m, (await k.Shop.OperationsAsync(modelId)).Rows.Single(o => o.Name == "Вязание").TypePrice);
            Assert.Single(await k.Tech.ProgramsAsync(machineId));
            Assert.Equal(DevMachineStatus.Limited, (await k.Tech.MachinesAsync()).Single(m => m.Id == machineId).Status);
            Assert.Equal("перенёс, плохо работает", (await k.Tech.MachineNotesAsync(machineId)).First().Text);
            Assert.Equal("dev.color.used", (await Assert.ThrowsAsync<BusinessRuleException>(() => k.Tech.RemoveColorAsync(main))).Code);
            var detailRv = (await k.Tech.TechCardAsync(front)).RowVersion;
            Assert.Equal("dev.detail.used", (await Assert.ThrowsAsync<BusinessRuleException>(() => k.Tech.RemoveDetailAsync(front, detailRv))).Code);
            Assert.NotEmpty(await k.Tech.TechCardsAsync("Подвяз"));
            Assert.Equal(op, (await k.Shop.OperationTypesAsync()).Single(o => o.Name == "Вязание").Id);

            // D725: технический паспорт — эскиз, палитра, детали с заправкой, табель мер, операции без расценок, упаковка; PDF строится.
            var png = KnitErp.Infrastructure.Documents.PaymentQrImage.Png("эскиз", 4);
            var sketch = await k.Models.AddFileAsync(modelId, DevFileKind.Sketch, "eskiz.png", png, null);
            await k.Tech.PinSketchAsync(modelId, sketch, (await k.Models.GetAsync(modelId)).RowVersion);
            var passport = await k.Passport.BuildAsync(modelId);
            Assert.Equal(png, passport.Sketch);
            Assert.Equal(new[] { "Основной", "доп.1" }, passport.Palette.Select(c => c.Label));
            Assert.Equal(new[] { 4, 4 }, passport.Details.Select(d => d.Feeders.Count)); // 5 позиций на нитеводителях 8, 5 (обе стороны), 4, 1
            Assert.Equal(("Ластик 2×2", "podvyaz.sin, версия 1"), (passport.Details[0].Weave!, passport.Details[0].Program!));
            Assert.Equal(new[] { 14m, 16m, 18m, 20m }, passport.Sizes.Select(z => passport.Measures.Single().Values[z.Id]));
            Assert.Equal(new[] { "Вязание", "Убрать боковые нитки", "Упаковка в короба" }, passport.Operations.Select(o => o.Name));
            Assert.Equal("40 × 30 × 30 см", passport.PackBox);
            Assert.Equal(22.5m, passport.DetailSizes!.Single(d => d.DetailId == front).Values[passport.Sizes[3].Id].OffMachine);
            var (fileName, pdf) = await k.Passport.PdfAsync(modelId, new KnitErp.Infrastructure.Documents.DevPassportPdfRenderer());
            Assert.StartsWith("pasport-", fileName);
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
            if (Environment.GetEnvironmentVariable("KNITERP_E2E_ARTIFACTS") is { Length: > 0 } dir)
            {
                Directory.CreateDirectory(dir);
                await File.WriteAllBytesAsync(Path.Combine(dir, fileName), pdf); // посмотреть паспорт глазами
            }
        }

        // Паспорт без денег — открывает и образцовщица (просмотр); чужая организация — «не найдено».
        await using (var k = As(sampler, org.OrganizationId))
        {
            Assert.NotEmpty((await k.Passport.BuildAsync(modelId)).Details);
        }

        var stranger = await CreateOrganizationAsync();
        await using (var k = As(stranger.OwnerUserId, stranger.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => k.Passport.BuildAsync(modelId));
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

    private static List<DevCellEdit> Cells(long detail, IEnumerable<(long Size, decimal Off, decimal Steam)> values) =>
        values.SelectMany(v => new[] { new DevCellEdit(detail, v.Size, 0, null, v.Off), new DevCellEdit(detail, v.Size, 1, null, v.Steam) }).ToList();

    /// <summary>
    /// D720: конструктор и цех правят таблицы одновременно — ячейку, которую после загрузки экрана изменил другой, не перезаписываем
    /// (возвращаем в конфликтах с текущим значением), остальные ячейки сохраняются; чужие ячейки не затираются целой таблицей.
    /// </summary>
    [SqlFact]
    public async Task Concurrent_table_edits_do_not_overwrite_each_other()
    {
        var org = await CreateOrganizationAsync();
        var sampler = await InviteActiveAsync(org, SystemRoles.DevSampler);
        long model, detail, measure, sample;
        long s1, s2;
        await using (var k = As(org.OwnerUserId, org.OrganizationId))
        {
            model = await k.Models.CreateAsync(Header("Носки"));
            detail = await k.Tech.AddDetailAsync(model, "Паголенок", 1);
            await k.Shop.SetSizeGridAsync(model, ["23", "25"], "23");
            var sizes = (await k.Shop.SizesAsync(model)).Sizes;
            (s1, s2) = (sizes[0].Id, sizes[1].Id);
            measure = await k.Shop.AddMeasureAsync(model, "Длина следа", 0.5m, null);
            sample = await k.Shop.CreateSampleAsync(model, s1);

            // Первый пользователь: длина с машины 20 для размера 23.
            var r = await k.Shop.SetDetailSizesAsync(model, [new(detail, s1, 0, null, 20m)]);
            Assert.Equal((1, 0), (r.Saved, r.Conflicts.Count));
        }

        await using (var k = As(org.OwnerUserId, org.OrganizationId))
        {
            // Второй открыл экран раньше (видел пусто): его правка той же ячейки — конфликт, правка соседней — сохраняется.
            var r = await k.Shop.SetDetailSizesAsync(model, [new(detail, s1, 0, null, 21m), new(detail, s2, 0, null, 22m)]);
            Assert.Equal(1, r.Saved);
            Assert.Contains("сейчас 20", Assert.Single(r.Conflicts));
            var row = (await k.Shop.SizesAsync(model)).Details.Single().Values;
            Assert.Equal((20m, 22m), (row[s1].OffMachine!.Value, row[s2].OffMachine!.Value));
            // Правка «с актуальным значением» проходит; поле «после ВТО» той же строки не затирается.
            await k.Shop.SetDetailSizesAsync(model, [new(detail, s1, 1, null, 18m)]);
            Assert.Equal(1, (await k.Shop.SetDetailSizesAsync(model, [new(detail, s1, 0, 20m, 21m)])).Saved);
            row = (await k.Shop.SizesAsync(model)).Details.Single().Values;
            Assert.Equal((21m, 18m), (row[s1].OffMachine!.Value, row[s1].AfterSteam!.Value));

            // Табель мер и упаковка — так же.
            await k.Shop.SetMeasureValuesAsync(model, [new(measure, s1, 0, null, 23m)]);
            Assert.Single((await k.Shop.SetMeasureValuesAsync(model, [new(measure, s1, 0, null, 24m)])).Conflicts);
            Assert.Equal(1, (await k.Shop.SetMeasureValuesAsync(model, [new(measure, s1, 0, 23m, null)])).Saved); // очистка с актуальным значением
            Assert.Empty((await k.Shop.SizesAsync(model)).Measures.Single().Values);
            await k.Shop.SetPackingAsync(model, [new(s1, 0, 0, null, 12)], null, "30 × 20 × 20 см");
            var pack = await k.Shop.SetPackingAsync(model, [new(s1, 0, 0, null, 10)], null, "другой короб");
            Assert.Equal((0, 2), (pack.Saved, pack.Conflicts.Count));
            Assert.Equal("30 × 20 × 20 см", (await k.Shop.SizesAsync(model)).PackBox);
        }

        // Цех и конструктор пишут замеры образца: правка цеха поверх «старого» значения конструктора — конфликт.
        await using (var k = As(sampler, org.OrganizationId))
        {
            await k.Shop.SetSampleMeasuresAsync(sample, [new(measure, 0, 0, null, 22.5m)]);
        }

        await using (var k = As(org.OwnerUserId, org.OrganizationId))
        {
            var r = await k.Shop.SetSampleMeasuresAsync(sample, [new(measure, 0, 0, null, 22m)]);
            Assert.Contains("сейчас 22.5", Assert.Single(r.Conflicts));
            Assert.Equal(22.5m, (await k.Shop.SamplesAsync(model)).Samples.Single().Measures.Single().Fact);
        }
    }

    /// <summary>
    /// D750–D752, D721: передача утверждённой модели в производство (позиция, черновик техкарты с нормами пряжи по факту образца и маршрутом
    /// из операций технолога) и списание пряжи на образец черновиком склада; повтор, права, чужая организация.
    /// </summary>
    [SqlFact]
    public async Task Handoff_to_production_and_sample_yarn_write_off()
    {
        var org = await CreateOrganizationAsync();
        var other = await CreateOrganizationAsync();
        long model, sample, cotton, red, warehouse;
        await using (var k = As(org.OwnerUserId, org.OrganizationId))
        {
            var kg = (await k.All.Catalog.ListUnitsAsync()).Single(u => u.Code == "166").Id;
            cotton = await k.All.Catalog.CreateItemAsync(new ItemCommand("ПР-10", "Хлопок молочный", ItemType.RawMaterial, kg, null));
            red = await k.All.Catalog.CreateItemAsync(new ItemCommand("ПР-11", "Хлопок красный", ItemType.RawMaterial, kg, null));
            warehouse = await k.All.Warehouses.CreateWarehouseAsync("Склад пряжи", null);
            model = await k.Models.CreateAsync(Header("Шарф в полоску"));
            var main = await k.Tech.AddColorAsync(model, "Основной", cotton, null, null);
            var extra = await k.Tech.AddColorAsync(model, "доп.1", red, null, null);
            var detail = await k.Tech.AddDetailAsync(model, "Полотно", 2);
            var machine = await k.Tech.CreateMachineAsync(new DevMachineValues("STOLL №9", DevMachineBrand.Stoll, null, 7, 8, null, null, null));
            await k.Tech.SetKnittingAsync(detail, machine, null, 180, 100m, null, (await k.Tech.DetailAsync(detail)).RowVersion);
            await k.Tech.SetThreadingAsync(detail, [new DevThreadingRow(1, DevFeederSide.Left, main, null, 2, true, null, false),
                new DevThreadingRow(2, DevFeederSide.Right, extra, null, 1, false, "N", false)], (await k.Tech.DetailAsync(detail)).RowVersion);
            var sketchFile = await k.Models.AddFileAsync(model, DevFileKind.Sketch, "sharf.png",
                [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52], null);
            await k.Tech.PinSketchAsync(model, sketchFile, (await k.Models.GetAsync(model)).RowVersion);
            var steam = await k.Shop.AddOperationTypeAsync(new DevOperationValues("Парить", "шт", null, 0.1m, 3));
            await k.Shop.AddOperationAsync(model, steam, null, null, null);
            sample = await k.Shop.CreateSampleAsync(model, null);
            await k.Shop.RecordFactAsync(sample, detail, 200, 110m, [(main, 60m), (extra, 40m)]);

            // Не утверждена — передать нельзя; предпросмотр уже считает нормы: 110 г × 2 шт × 60 % / 40 %.
            var p = await k.Handoff.PreviewAsync(model);
            Assert.False(p.CanHandOff);
            Assert.Equal(new[] { 0.132m, 0.088m }, p.Lines.OrderBy(l => l.ItemCode).Select(l => l.Quantity));
            Assert.Equal(new[] { "Вязание", "ВТО", "Контроль качества" }, p.Route);
            Assert.Equal("dev.handoff.stage", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                k.Handoff.HandOffAsync(model, null, null, null, p.RowVersion))).Code);

            // Списание пряжи на образец: черновик склада, повторно — нельзя.
            var y = await k.Yarn.PreviewAsync(sample);
            Assert.Null(y.Refusal);
            Assert.Equal(2, y.Lines.Count);
            var doc = await k.Yarn.WriteOffAsync(sample, warehouse, null);
            var d = await k.All.Documents.GetAsync(doc);
            Assert.Equal((KnitErp.Domain.Warehousing.StockOperationKind.WriteOff, KnitErp.Domain.Warehousing.StockDocumentStatus.Draft), (d.Kind, d.Status));
            Assert.Equal(0.22m, d.Lines.Sum(l => l.Quantity));
            Assert.Equal("dev.yarn.done", (await Assert.ThrowsAsync<BusinessRuleException>(() => k.Yarn.WriteOffAsync(sample, warehouse, null))).Code);
            Assert.Equal(doc, (await k.Yarn.PreviewAsync(sample)).DocumentId);

            // Утвердили — передали: новая позиция, черновик техкарты с нормами и маршрутом, модель «Передано в производство».
            // D746: порог маржи 20 % — расчёт с нулевыми ценами (маржа 0) утверждается только с обоснованием.
            await k.Settings.SaveCostSettingsAsync(0, 0, null, 0, true, 20m);
            await k.Costs.SaveAsync(model, new DevCostScenario());
            var rv = (await k.Models.GetAsync(model)).RowVersion;
            Assert.Equal("dev.approve.margin", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                k.Models.MoveAsync(model, DevStage.Approved, null, rv))).Code);
            await k.Models.MoveAsync(model, DevStage.Approved, "образец для выставки, маржа не важна", (await k.Models.GetAsync(model)).RowVersion);
            p = await k.Handoff.PreviewAsync(model);
            Assert.True(p.CanHandOff, p.Refusal);
            Assert.Null(p.SuggestedSizes); // сетки нет — и размерного ряда в задании нет
            var handed = await k.Handoff.HandOffAsync(model, null, "ШФ-100", null, p.RowVersion, "S, M", "молочный, красный");
            var cardId = handed.TechCardId;
            // D753: модификации «Размер × Цвет» — 2 × 2 = 4 позиции у основной; закреплённый эскиз PNG — фото позиции.
            Assert.True(p.HasSketchPhoto);
            Assert.Equal((4, true), (handed.Modifications, handed.PhotoAdded));
            Assert.Single(await k.All.Modifications.PhotosAsync(handed.ItemId));
            Assert.Equal(4, (await k.All.Modifications.VariantsAsync(handed.ItemId)).Modifications.Count);
            var card = await k.All.TechCards.GetAsync(cardId);
            Assert.Equal(("ШФ-100", "Шарф в полоску", KnitErp.Domain.Production.TechCardStatus.Draft), (card.ItemCode, card.ItemName, card.Status));
            Assert.Equal(new[] { (cotton, 0.132m), (red, 0.088m) }, card.Lines.OrderBy(l => l.ItemId).Select(l => (l.ItemId, l.Quantity)));
            Assert.Equal(new[] { KnitErp.Domain.Production.ProductionStage.Knitting, KnitErp.Domain.Production.ProductionStage.Steaming,
                KnitErp.Domain.Production.ProductionStage.QualityControl }, card.Route!.OrderBy(r => r));
            var m = await k.Models.GetAsync(model);
            Assert.Equal(DevStage.Handed, m.Stage);
            var after = await k.Handoff.PreviewAsync(model);
            Assert.Equal((card.ItemId, cardId, false), (after.ItemId!.Value, after.TechCardId!.Value, after.CanHandOff));
            await Assert.ThrowsAsync<BusinessRuleException>(() => k.Handoff.HandOffAsync(model, null, null, null, after.RowVersion));

            // D760–D766: аналитика за период — передана 1 модель, образец связан и ждёт решения, план-факт с расчётом; выгрузка в Excel.
            var today = DateOnly.FromDateTime(host.Clock.UtcNow);
            var report = await k.Analytics.ReportAsync(today.AddDays(-30), today.AddDays(1));
            Assert.Equal((0, 1, 1), (report.InWork, report.Handed, report.SamplesWaiting));
            Assert.Equal(1m, report.SamplesPerModel);
            var pf = Assert.Single(report.PlanFact!);
            Assert.Equal((model, 0m), (pf.ModelId, pf.CalculatedCost!.Value));
            Assert.NotNull(report.AvgDevelopmentDays);
            Assert.True((await k.Analytics.ExportAsync(today.AddDays(-30), today)).Length > 1000);
            Assert.Equal("dev.analytics.period", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                k.Analytics.ReportAsync(today, today.AddDays(-1)))).Code);
            // D769: новая версия переданной модели — с техданными, ссылкой на прежнюю; вторую от той же версии сделать нельзя.
            var v2 = await k.Tech.NewVersionAsync(model);
            var m2 = await k.Models.GetAsync(v2);
            Assert.Equal((DevStage.Idea, 2, model, "Шарф в полоску"), (m2.Stage, m2.VersionNo, m2.PreviousVersionId!.Value, m2.Name));
            Assert.Equal(v2, (await k.Models.GetAsync(model)).NextVersionId);
            Assert.Equal(2, (await k.Tech.ColorsAsync(v2)).Count);
            var d2 = Assert.Single(await k.Tech.DetailsAsync(v2));
            Assert.Equal(2, d2.ThreadingRows);
            var threading2 = (await k.Tech.DetailAsync(d2.Id)).Threading;
            var colors2 = (await k.Tech.ColorsAsync(v2)).ToDictionary(c => c.Id);
            Assert.All(threading2, t => Assert.True(colors2.ContainsKey(t.ColorId!.Value))); // цвета — новой палитры
            Assert.Single((await k.Shop.OperationsAsync(v2)).Rows);
            Assert.Null((await k.Tech.DetailAsync(d2.Id)).ProgramFileId); // программы остаются у прежней версии
            Assert.Equal("dev.version.exists", (await Assert.ThrowsAsync<BusinessRuleException>(() => k.Tech.NewVersionAsync(model))).Code);
            Assert.Equal("dev.version.stage", (await Assert.ThrowsAsync<BusinessRuleException>(() => k.Tech.NewVersionAsync(v2))).Code);

            var dash = await k.Analytics.GetAsync(k.All.Db, await new AccessGuard(k.All.Db,
                new TestUser { UserId = org.OwnerUserId, OrganizationId = org.OrganizationId, ClientAddress = host.ClientAddress }, host.Clock).CurrentAsync(), today, default);
            Assert.Equal((1, 1), (dash!.InWork, dash.SamplesWaiting)); // в работе — новая версия
        }

        // Без права «утверждение» — отказ; чужая организация — «не найдено».
        var sampler = await InviteActiveAsync(org, SystemRoles.DevSampler);
        await using (var k = As(sampler, org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => k.Handoff.HandOffAsync(model, null, null, null, []));
            // Образцовщица видит аналитику без сумм плана-факта.
            var today = DateOnly.FromDateTime(host.Clock.UtcNow);
            Assert.Null((await k.Analytics.ReportAsync(today.AddDays(-30), today.AddDays(1))).PlanFact);
        }

        await using (var k = As(other.OwnerUserId, other.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => k.Handoff.PreviewAsync(model));
            await Assert.ThrowsAsync<NotFoundException>(() => k.Handoff.HandOffAsync(model, null, null, null, []));
            await Assert.ThrowsAsync<NotFoundException>(() => k.Yarn.PreviewAsync(sample));
            await Assert.ThrowsAsync<NotFoundException>(() => k.Yarn.WriteOffAsync(sample, warehouse, null));
            await Assert.ThrowsAsync<NotFoundException>(() => k.Tech.NewVersionAsync(model));
        }
    }

    /// <summary>
    /// D740–D745: себестоимость по нормам пряжи, фурнитуре и операциям; цены вручную, замена пряжи, версии только на вставку,
    /// фурнитура уходит в черновик техкарты, права на суммы и чужая организация.
    /// </summary>
    [SqlFact]
    public async Task Costing_versions_scenarios_and_access()
    {
        var org = await CreateOrganizationAsync();
        var other = await CreateOrganizationAsync();
        long model, cotton, red, tag;
        await using (var k = As(org.OwnerUserId, org.OrganizationId))
        {
            var units = await k.All.Catalog.ListUnitsAsync();
            var kg = units.Single(u => u.Code == "166").Id;
            var piece = units.Single(u => u.Code == "796").Id;
            cotton = await k.All.Catalog.CreateItemAsync(new ItemCommand("ПР-20", "Хлопок 20/1", ItemType.RawMaterial, kg, null));
            red = await k.All.Catalog.CreateItemAsync(new ItemCommand("ПР-21", "Хлопок 24/1", ItemType.RawMaterial, kg, null));
            tag = await k.All.Catalog.CreateItemAsync(new ItemCommand("ФР-1", "Бирка", ItemType.Accessory, piece, null));
            await k.Settings.SaveCostSettingsAsync(5m, 10m, 2m, 50m);
            model = await k.Models.CreateAsync(Header("Шапка бини"));
            await k.Tech.AddColorAsync(model, "Основной", cotton, null, null);
            var detail = await k.Tech.AddDetailAsync(model, "Полотно", 2);
            await k.Tech.SetKnittingAsync(detail, null, null, null, 110m, null, (await k.Tech.DetailAsync(detail)).RowVersion);
            await k.Costs.AddAccessoryAsync(model, tag, 1m);
            Assert.Equal("dev.accessory.type", (await Assert.ThrowsAsync<BusinessRuleException>(() => k.Costs.AddAccessoryAsync(model, red, 1m))).Code);
            await k.Shop.AddOperationAsync(model, await k.Shop.AddOperationTypeAsync(new DevOperationValues("Вязание", "шт", 12.5m, null, 1)), null, null, null);
            await k.Shop.AddOperationAsync(model, await k.Shop.AddOperationTypeAsync(new DevOperationValues("Парить", "шт", null, 3m, 3)), null, null, null);

            // Цен нет — замечание; вручную: 0,22 кг +5 % × 500 + бирка 10 + работа 12,50 + 3 × 2 + накладные 10 % → 158,40, цена +50 %.
            var bare = await k.Costs.CostingAsync(model);
            Assert.Contains(bare.Warnings, w => w.Template == DevNotes.NoPrice);
            var priced = new DevCostScenario(new Dictionary<long, decimal> { [cotton] = 500m, [tag] = 10m });
            var c = await k.Costs.CostingAsync(model, priced);
            Assert.Equal((115.50m, 10m, 18.50m, 14.40m, 158.40m, 237.60m), (c.Yarn, c.Accessories, c.Labor, c.Overhead, c.Cost, c.Price));
            Assert.Equal("вручную", c.Lines.First(l => l.ItemId == cotton).PriceSource);

            // Сценарий «другая пряжа»: 24/1 по 600 → пряжа 138,60.
            var replaced = await k.Costs.CostingAsync(model, priced with
            {
                Replace = new Dictionary<long, long> { [cotton] = red }, Prices = new Dictionary<long, decimal> { [red] = 600m, [tag] = 10m },
            });
            Assert.Equal((red, 138.60m), (replaced.Lines.First(l => l.Kind == DevCostLineKind.Yarn).ItemId!.Value, replaced.Yarn));

            // Версии: номера по порядку, строки сохраняются, изменить версию в базе нельзя (триггер).
            Assert.Equal(1, await k.Costs.SaveAsync(model, priced with { Note = "базовый" }));
            Assert.Equal(2, await k.Costs.SaveAsync(model, priced with { Mode = DevPriceMode.TargetPrice, TargetPrice = 200m }));
            var runs = (await k.Costs.CostingAsync(model)).Runs;
            Assert.Equal(new[] { 2, 1 }, runs.Select(r => r.Version));
            Assert.Equal((200m, 41.60m), (runs[0].Price, runs[0].Margin));
            Assert.Equal(4, (await k.Costs.RunLinesAsync(runs[1].Id)).Count);
            // D741, D751: в нормы передачи в производство идут пряжа с отходами из настроек и фурнитура модели.
            var hp = await k.Handoff.PreviewAsync(model);
            Assert.Equal((0.22m, 5m), (hp.Lines.Single(l => l.ItemId == cotton).Quantity, hp.Lines.Single(l => l.ItemId == cotton).WastePercent));
            Assert.Equal((1m, 0m), (hp.Lines.Single(l => l.ItemId == tag).Quantity, hp.Lines.Single(l => l.ItemId == tag).WastePercent));
            await using (var db = host.NewDb())
            {
                await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
                    "UPDATE kniterp.dev_cost_runs SET Note = N'подмена' WHERE Id = {0}", runs[0].Id));
                await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
                    "DELETE FROM kniterp.dev_cost_lines WHERE RunId = {0}", runs[0].Id));
            }
        }

        // D768: решение по образцу — причина из справочника; чужой вид справочника не подходит; аналитика группирует по причине.
        await using (var k = As(org.OwnerUserId, org.OrganizationId))
        {
            var loops = await k.Settings.AddLookupAsync(DevLookupKind.ReworkReason, "Петли", null);
            var weave = await k.Settings.AddLookupAsync(DevLookupKind.Weave, "Гладь", null);
            var smp = await k.Shop.CreateSampleAsync(model, null);
            var rv = (await k.Shop.SamplesAsync(model)).Samples.Single().RowVersion;
            Assert.Equal("dev.sample.reason", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                k.Shop.DecideSampleAsync(smp, DevSampleStatus.Rework, null, rv))).Code);
            await Assert.ThrowsAsync<NotFoundException>(() => k.Shop.DecideSampleAsync(smp, DevSampleStatus.Rework, null, rv, weave));
            await k.Shop.DecideSampleAsync(smp, DevSampleStatus.Rework, null, rv, loops);
            var data = await k.Shop.SamplesAsync(model);
            Assert.Equal("Петли", data.Samples.Single().Reason);
            Assert.Contains(data.Reasons!, r => r.Id == loops);
            var today = DateOnly.FromDateTime(host.Clock.UtcNow);
            Assert.Equal(("Петли", 1), (await k.Analytics.ReportAsync(today.AddDays(-1), today.AddDays(1))).ReworkReasons.Select(r => (r.Reason, r.Count)).Single());
        }

        // Без права «себестоимость» — отказ; чужая организация — «не найдено».
        var sampler = await InviteActiveAsync(org, SystemRoles.DevSampler);
        await using (var k = As(sampler, org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => k.Costs.CostingAsync(model));
        }

        await using (var k = As(other.OwnerUserId, other.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => k.Costs.CostingAsync(model));
            await Assert.ThrowsAsync<NotFoundException>(() => k.Costs.SaveAsync(model, new DevCostScenario()));
            await Assert.ThrowsAsync<NotFoundException>(() => k.Costs.AddAccessoryAsync(model, tag, 1m));
        }
    }

    /// <summary>D723: образец у заказчика — передача, срок возврата, напоминание, закрытие; права и чужая организация.</summary>
    [SqlFact]
    public async Task Sample_at_customer_is_tracked_until_returned()
    {
        var org = await CreateOrganizationAsync();
        var other = await CreateOrganizationAsync();
        long model, late, onTime, knitting;
        await using (var k = As(org.OwnerUserId, org.OrganizationId))
        {
            model = await k.Models.CreateAsync(Header("Свитер «Образец»"));
            late = await k.Shop.CreateSampleAsync(model, null);
            onTime = await k.Shop.CreateSampleAsync(model, null);
            knitting = await k.Shop.CreateSampleAsync(model, null);
            foreach (var id in new[] { late, onTime })
            {
                var rv = (await k.Shop.SamplesAsync(model)).Samples.Single(x => x.Id == id).RowVersion;
                await k.Shop.DecideSampleAsync(id, DevSampleStatus.Accepted, null, rv);
            }

            byte[] Rv(DevSamplesDto d, long id) => d.Samples.Single(x => x.Id == id).RowVersion;
            var data = await k.Shop.SamplesAsync(model);
            Assert.Equal("dev.sample.custody.knitting", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                k.Shop.HandSampleAsync(knitting, "ООО «Пример»", new DateOnly(2026, 10, 1), null, null, Rv(data, knitting)))).Code);
            Assert.Equal("dev.sample.custody.date", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                k.Shop.HandSampleAsync(late, "ООО «Пример»", new DateOnly(2026, 10, 1), new DateOnly(2026, 9, 30), null, Rv(data, late)))).Code);
            Assert.Equal("field.required", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                k.Shop.HandSampleAsync(late, " ", new DateOnly(2026, 10, 1), null, null, Rv(data, late)))).Code);

            await k.Shop.HandSampleAsync(late, "ООО «Пример», Иванова", new DateOnly(2026, 9, 20), new DateOnly(2026, 10, 1), "на выставку", Rv(data, late));
            data = await k.Shop.SamplesAsync(model);
            await k.Shop.HandSampleAsync(onTime, "ООО «Пример»", new DateOnly(2026, 10, 5), new DateOnly(2026, 11, 1), null, Rv(data, onTime));
            // Старая версия строки — конфликт, а не повторная передача.
            await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
                k.Shop.HandSampleAsync(onTime, "Повтор", new DateOnly(2026, 10, 5), null, null, Rv(data, onTime)));

            data = await k.Shop.SamplesAsync(model);
            var l = data.Samples.Single(x => x.Id == late);
            Assert.Equal((DevSampleCustody.AtCustomer, true), (l.Custody, l.ReturnOverdue));
            Assert.False(data.Samples.Single(x => x.Id == onTime).ReturnOverdue);
            Assert.Equal("dev.sample.custody.out", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                k.Shop.HandSampleAsync(late, "Ещё раз", new DateOnly(2026, 10, 2), null, null, l.RowVersion))).Code);

            var report = await k.Analytics.ReportAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 9));
            Assert.Equal(new[] { late, onTime }, report.SamplesOut!.Select(x => x.SampleId).ToArray()); // просроченный — первым
            Assert.Contains(await k.All.Notifications.ListAsync(), n => n.Text == "Образцы не вернули к сроку: 1" && n.Href == "dev/analytics#samples-out");
            var dash = await k.Analytics.GetAsync(k.All.Db, await new AccessGuard(k.All.Db,
                new TestUser { UserId = org.OwnerUserId, OrganizationId = org.OrganizationId, ClientAddress = host.ClientAddress }, host.Clock).CurrentAsync(),
                new DateOnly(2026, 10, 9), default);
            Assert.Equal((2, 1), (dash!.SamplesAtCustomer, dash.SamplesOverdue));
            Assert.Contains(await k.All.Db.AuditEntries.AsNoTracking().Where(e => e.OrganizationId == org.OrganizationId && e.Action == AuditActions.DevSampleCustody)
                .Select(e => e.After).ToListAsync(), v => v != null && v.Contains("вернуть до 01.10.2026"));

            Assert.Equal("dev.sample.custody.date", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                k.Shop.CloseSampleCustodyAsync(late, DevSampleCustody.Returned, new DateOnly(2026, 9, 1), null, l.RowVersion))).Code);
            await k.Shop.CloseSampleCustodyAsync(late, DevSampleCustody.Returned, new DateOnly(2026, 10, 8), null, l.RowVersion);
            data = await k.Shop.SamplesAsync(model);
            await k.Shop.CloseSampleCustodyAsync(onTime, DevSampleCustody.Sold, new DateOnly(2026, 10, 9), "оплачен", Rv(data, onTime));
            data = await k.Shop.SamplesAsync(model);
            Assert.Equal(DevSampleCustody.Returned, data.Samples.Single(x => x.Id == late).Custody);
            Assert.Equal("dev.sample.custody.not_out", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                k.Shop.CloseSampleCustodyAsync(onTime, DevSampleCustody.Returned, new DateOnly(2026, 10, 9), null, Rv(data, onTime)))).Code);
            Assert.Empty((await k.Analytics.ReportAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 9))).SamplesOut!);
            Assert.DoesNotContain(await k.All.Notifications.ListAsync(), n => n.Text.StartsWith("Образцы не вернули", StringComparison.Ordinal));
            // Возвращённый образец можно передать снова.
            await k.Shop.HandSampleAsync(late, "ООО «Другой»", new DateOnly(2026, 10, 9), null, null, Rv(data, late));
        }

        // Образцовщица (без права изменения) не передаёт; чужая организация — «не найдено».
        var sampler = await InviteActiveAsync(org, SystemRoles.DevSampler);
        await using (var k = As(sampler, org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => k.Shop.CloseSampleCustodyAsync(late, DevSampleCustody.Returned, new DateOnly(2026, 10, 9), null, []));
            Assert.False((await k.Shop.SamplesAsync(model)).CanHand);
        }

        await using (var k = As(other.OwnerUserId, other.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => k.Shop.HandSampleAsync(onTime, "Чужой", new DateOnly(2026, 10, 9), null, null, []));
            await Assert.ThrowsAsync<NotFoundException>(() => k.Shop.CloseSampleCustodyAsync(late, DevSampleCustody.Returned, new DateOnly(2026, 10, 9), null, []));
            Assert.Empty((await k.Analytics.ReportAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 9))).SamplesOut!);
        }
    }

    /// <summary>ТЗ §8: сравнение образцов рядом — замеры с отклонением, факт деталей против плана, общий вес, свои фото образца.</summary>
    [SqlFact]
    public async Task Samples_are_compared_side_by_side()
    {
        var org = await CreateOrganizationAsync();
        var other = await CreateOrganizationAsync();
        var png = KnitErp.Infrastructure.Documents.PaymentQrImage.Png("фото", 4);
        var start = host.Clock.UtcNow;
        long model, first, second, photo1, photo2, length;
        try
        {
            await using var k = As(org.OwnerUserId, org.OrganizationId);
            model = await k.Models.CreateAsync(Header("Шапка для сравнения"));
            var front = await k.Tech.AddDetailAsync(model, "Полотно", 1);
            var cuff = await k.Tech.AddDetailAsync(model, "Манжета", 2);
            await k.Tech.SetKnittingAsync(front, null, null, 300, 80m, null, (await k.Tech.DetailAsync(front)).RowVersion);
            await k.Tech.SetKnittingAsync(cuff, null, null, 60, 10m, null, (await k.Tech.DetailAsync(cuff)).RowVersion);
            await k.Shop.SetSizeGridAsync(model, ["S", "M"], "M");
            var sizes = (await k.Shop.SizesAsync(model)).Sizes;
            length = await k.Shop.AddMeasureAsync(model, "Длина изделия", 0.5m, null);
            var width = await k.Shop.AddMeasureAsync(model, "Ширина", 1m, null);
            await k.Shop.SetMeasureValuesAsync(model, [new(length, sizes[1].Id, 0, null, 20m), new(width, sizes[1].Id, 0, null, 18m)]);

            // Образцы № 1 и № 2; фото № 1 загружено после создания № 2 — относится к своему образцу, а не ко времени; фото модели — ни к какому.
            first = await k.Shop.CreateSampleAsync(model, sizes[1].Id);
            host.Clock.UtcNow = start.AddDays(1);
            second = await k.Shop.CreateSampleAsync(model, sizes[1].Id);
            host.Clock.UtcNow = start.AddDays(1).AddHours(1);
            photo1 = await k.Shop.AddSamplePhotoAsync(first, "obrazec1.png", png);
            photo2 = await k.Shop.AddSamplePhotoAsync(second, "obrazec2.png", png);
            await k.Models.AddFileAsync(model, DevFileKind.Photo, "model.png", png, null);

            await k.Shop.RecordFactAsync(first, front, 330, 90m, []);
            await k.Shop.RecordFactAsync(first, cuff, 70, 12m, []);
            await k.Shop.RecordFactAsync(second, front, 290, 81m, []);
            await k.Shop.SetSampleMeasuresAsync(first, [new(length, 0, 0, null, 21m), new(width, 0, 0, null, 18.5m)]);
            await k.Shop.SetSampleMeasuresAsync(second, [new(length, 0, 0, null, 20.3m)]);
            var rv = (await k.Shop.SamplesAsync(model)).Samples.Single(s => s.Id == first).RowVersion;
            await k.Shop.DecideSampleAsync(first, DevSampleStatus.Rework, "длинная", rv);

            var cmp = await k.Shop.CompareSamplesAsync(model, [second, first]);
            Assert.Equal(new[] { 1, 2 }, cmp.Samples.Select(s => s.No)); // колонки — по возрастанию номера
            Assert.Equal(new[] { "Длина изделия", "Ширина" }, cmp.MeasureNames);
            Assert.Equal(new[] { "Полотно", "Манжета" }, cmp.DetailNames);
            var (s1, s2) = (cmp.Samples[0], cmp.Samples[1]);
            Assert.Equal((DevSampleStatus.Rework, "длинная", "M"), (s1.Status, s1.Comment!, s1.Size!));
            Assert.Equal((20m, 21m, 1m, true), (s1.Measures[0].Plan!.Value, s1.Measures[0].Fact!.Value, s1.Measures[0].Deviation!.Value, s1.Measures[0].OutOfTolerance));
            Assert.Equal((0.5m, false), (s1.Measures[1].Deviation!.Value, s1.Measures[1].OutOfTolerance));
            Assert.Equal((0.3m, false), (s2.Measures[0].Deviation!.Value, s2.Measures[0].OutOfTolerance));
            Assert.Null(s2.Measures[1].Fact);
            Assert.Equal((300, 330, 80m, 90m), (s1.Details[0].PlanKnitSeconds!.Value, s1.Details[0].FactKnitSeconds!.Value, s1.Details[0].PlanWeightGrams!.Value,
                s1.Details[0].FactWeightGrams!.Value));
            Assert.Equal(2, s1.Details[1].Quantity);
            Assert.Equal(114m, s1.TotalWeightGrams); // 90 + 12 × 2 манжеты
            Assert.Equal(81m, s2.TotalWeightGrams);
            Assert.Null(s2.Details[1].FactWeightGrams);
            Assert.Equal(new[] { photo1 }, s1.PhotoFileIds);
            Assert.Equal(new[] { photo2 }, s2.PhotoFileIds);

            Assert.Equal("dev.sample.compare", (await Assert.ThrowsAsync<BusinessRuleException>(() => k.Shop.CompareSamplesAsync(model, [first]))).Code);
            Assert.Equal("dev.sample.compare", (await Assert.ThrowsAsync<BusinessRuleException>(() => k.Shop.CompareSamplesAsync(model, [first, first]))).Code);
            var otherModel = await k.Models.CreateAsync(Header("Другая модель"));
            var foreignSample = await k.Shop.CreateSampleAsync(otherModel, null);
            await Assert.ThrowsAsync<NotFoundException>(() => k.Shop.CompareSamplesAsync(model, [first, foreignSample]));
        }
        finally
        {
            host.Clock.UtcNow = start;
        }

        await using (var k = As(other.OwnerUserId, other.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => k.Shop.CompareSamplesAsync(model, [first, second]));
        }
    }

    /// <summary>
    /// ТЗ §8 (D728): образец берёт колорит и версию рисунка модели; паспорт — партия, кто вязал и собирал (активные пользователи), время сборки;
    /// время вязки и вес изделия по факту; план доработки только у «На доработку»; фото образца; чужая организация — «не найдено».
    /// </summary>
    [SqlFact]
    public async Task Sample_passport_rework_plan_and_photo()
    {
        var org = await CreateOrganizationAsync();
        var other = await CreateOrganizationAsync();
        var sampler = await InviteActiveAsync(org, SystemRoles.DevSampler);
        var foreigner = await InviteActiveAsync(other, SystemRoles.DevSampler);
        var png = KnitErp.Infrastructure.Documents.PaymentQrImage.Png("фото", 4);
        long model, colorway, sample, photo;
        await using (var k = As(org.OwnerUserId, org.OrganizationId))
        {
            var user = new TestUser { UserId = org.OwnerUserId, OrganizationId = org.OrganizationId, ClientAddress = host.ClientAddress };
            var patterns = new DevPatternService(k.All.Db, new AccessGuard(k.All.Db, user, host.Clock), user, host.Clock);
            var pattern = await patterns.CreateAsync(new DevPatternValues("Ромбы", DevPatternKind.JacquardDouble, 24, 30, null, null, null, null, null, null, null));
            colorway = await patterns.AddColorwayAsync(pattern, "Зима");
            model = await k.Models.CreateAsync(Header("Свитер с ромбами"));
            var front = await k.Tech.AddDetailAsync(model, "Перед", 1);
            var sleeve = await k.Tech.AddDetailAsync(model, "Рукав", 2);
            await patterns.LinkToModelAsync(model, pattern, 1, colorway, (await k.Models.GetAsync(model)).RowVersion);

            sample = await k.Shop.CreateSampleAsync(model, null);
            await k.Shop.RecordFactAsync(sample, front, 600, 200m, []);
            await k.Shop.RecordFactAsync(sample, sleeve, 300, 80m, []);
            var dto = (await k.Shop.SamplesAsync(model)).Samples.Single();
            Assert.Equal(("Зима", 1), (dto.Making!.Colorway!, dto.Making.PatternVersion!.Value));
            Assert.Equal(20m, dto.Making.KnitMinutes); // (600 + 300 × 2) / 60
            Assert.Equal(360m, dto.Making.WeightGrams); // 200 + 80 × 2
            Assert.Null(dto.Rework);
            Assert.Empty(dto.PhotoFileIds!);

            // Паспорт: чужой пользователь — отказ, активный образцовщицы — записан.
            Assert.Equal("dev.model.assignee", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                k.Shop.SetSampleMakingAsync(sample, "П-17", foreigner, null, 15, dto.RowVersion))).Code);
            Assert.Equal("dev.sample.assembly", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                k.Shop.SetSampleMakingAsync(sample, "П-17", sampler, null, -1, dto.RowVersion))).Code);
            await k.Shop.SetSampleMakingAsync(sample, " П-17 ", sampler, org.OwnerUserId, 15, dto.RowVersion);
            await Assert.ThrowsAsync<ConcurrencyConflictException>(() => k.Shop.SetSampleMakingAsync(sample, "П-18", null, null, null, dto.RowVersion));
            var data = await k.Shop.SamplesAsync(model);
            dto = data.Samples.Single();
            Assert.Equal(("П-17", sampler, org.OwnerUserId, 15), (dto.Making!.YarnBatch!, dto.Making.KnittedByUserId!.Value, dto.Making.AssembledByUserId!.Value,
                dto.Making.AssemblyMinutes!.Value));
            Assert.NotNull(dto.Making.KnittedBy);
            Assert.Contains(data.Users!, u => u.Id == sampler);
            Assert.DoesNotContain(data.Users!, u => u.Id == foreigner);
            Assert.Contains(await k.All.Db.AuditEntries.AsNoTracking().Where(e => e.OrganizationId == org.OrganizationId && e.Action == AuditActions.DevSampleRecorded)
                .Select(e => e.After).ToListAsync(), v => v != null && v.Contains("партия: П-17"));

            // План доработки — только у образца «На доработку».
            Assert.Equal("dev.sample.rework.status", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                k.Shop.PlanReworkAsync(sample, "Удлинить рукав", sampler, new DateOnly(2026, 11, 1), dto.RowVersion))).Code);
            await k.Shop.DecideSampleAsync(sample, DevSampleStatus.Rework, "короткий рукав", dto.RowVersion);
            dto = (await k.Shop.SamplesAsync(model)).Samples.Single();
            Assert.Equal("dev.model.assignee", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                k.Shop.PlanReworkAsync(sample, "Удлинить рукав", foreigner, null, dto.RowVersion))).Code);
            Assert.Equal("field.required", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                k.Shop.PlanReworkAsync(sample, " ", sampler, null, dto.RowVersion))).Code);
            await k.Shop.PlanReworkAsync(sample, "Удлинить рукав на 2 см", sampler, new DateOnly(2026, 11, 1), dto.RowVersion);
            dto = (await k.Shop.SamplesAsync(model)).Samples.Single();
            Assert.Equal(("Удлинить рукав на 2 см", sampler, new DateOnly(2026, 11, 1)), (dto.Rework!.What!, dto.Rework.AssigneeUserId!.Value, dto.Rework.DueDate!.Value));

            // Новый образец после смены колорита модели берёт новый колорит; у старого — прежний.
            var autumn = await patterns.AddColorwayAsync(pattern, "Осень");
            await patterns.LinkToModelAsync(model, pattern, 1, autumn, (await k.Models.GetAsync(model)).RowVersion);
            var second = await k.Shop.CreateSampleAsync(model, null);
            data = await k.Shop.SamplesAsync(model);
            Assert.Equal("Осень", data.Samples.Single(x => x.Id == second).Making!.Colorway);
            Assert.Equal("Зима", data.Samples.Single(x => x.Id == sample).Making!.Colorway);
        }

        // Образцовщица: паспорт и фото — да, план доработки — нет.
        await using (var k = As(sampler, org.OrganizationId))
        {
            var dto = (await k.Shop.SamplesAsync(model)).Samples.Single(x => x.Id == sample);
            await k.Shop.SetSampleMakingAsync(sample, "П-18", sampler, org.OwnerUserId, 20, dto.RowVersion);
            dto = (await k.Shop.SamplesAsync(model)).Samples.Single(x => x.Id == sample);
            await Assert.ThrowsAsync<AccessDeniedException>(() => k.Shop.PlanReworkAsync(sample, "Другое", null, null, dto.RowVersion));
            photo = await k.Shop.AddSamplePhotoAsync(sample, "obrazec.png", png);
            var data = await k.Shop.SamplesAsync(model);
            Assert.Equal(new[] { photo }, data.Samples.Single(x => x.Id == sample).PhotoFileIds);
            Assert.Empty(data.Samples.Single(x => x.Id != sample).PhotoFileIds!);
            var file = await k.All.Db.DevFiles.AsNoTracking().SingleAsync(f => f.Id == photo);
            Assert.Equal((sample, DevFileKind.Photo, model), (file.SampleId!.Value, file.Kind, file.ModelId));
            var cmp = await k.Shop.CompareSamplesAsync(model, data.Samples.Select(x => x.Id).ToList());
            Assert.Equal(new[] { photo }, cmp.Samples.Single(x => x.Id == sample).PhotoFileIds);
            Assert.Empty(cmp.Samples.Single(x => x.Id != sample).PhotoFileIds);
            Assert.Equal("dev.file.type", (await Assert.ThrowsAsync<BusinessRuleException>(() => k.Shop.AddSamplePhotoAsync(sample, "obrazec.sin", Program))).Code);
        }

        await using (var k = As(other.OwnerUserId, other.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => k.Shop.SetSampleMakingAsync(sample, "Чужая", null, null, null, []));
            await Assert.ThrowsAsync<NotFoundException>(() => k.Shop.PlanReworkAsync(sample, "Чужая", null, null, []));
            await Assert.ThrowsAsync<NotFoundException>(() => k.Shop.AddSamplePhotoAsync(sample, "chuzhoe.png", png));
            await Assert.ThrowsAsync<NotFoundException>(() => k.Shop.SamplesAsync(model));
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
