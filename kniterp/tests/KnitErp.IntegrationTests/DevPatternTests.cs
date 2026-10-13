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
/// D726–D727 (ТЗ §7): рисунок — создание, доли по сетке, колориты с пряжей, проверки утверждения, версии и файлы (только вставка),
/// «готов к программированию» и очередь программиста, копирование, привязка версии и колорита к модели, права и изоляция организаций.
/// </summary>
public sealed class DevPatternTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private long _org;

    private long Cat(DevCategory category) => DevTestLookups.Category(host, _org, category);

    private long Kind(DevPatternKind kind) => DevTestLookups.PatternKind(host, _org, kind);

    private static int _innSeed = 973_100_000;
    private static readonly byte[] Program = [0x53, 0x49, 0x4E, 0x00, 0x01, 0x02];

    private sealed record Kit(DevPatternService Patterns, DevModelService Models, DevSettingsService Settings, Services All) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => All.DisposeAsync();
    }

    private Kit As(long userId, long organizationId)
    {
        _org = organizationId; // D733: категории и виды рисунков — записи справочника этой организации
        var all = host.As(userId, organizationId);
        var user = new TestUser { UserId = userId, OrganizationId = organizationId, ClientAddress = host.ClientAddress };
        var guard = new AccessGuard(all.Db, user, host.Clock);
        return new Kit(new DevPatternService(all.Db, guard, user, host.Clock), new DevModelService(all.Db, guard, user, host.Clock),
            new DevSettingsService(all.Db, guard, user, host.Clock), all);
    }

    private DevModelHeader Header(string name) =>
        new(name, null, Cat(DevCategory.Sweater), DevAudience.Women, false, true, null, null, new DateOnly(2026, 12, 1), DevPriority.Normal, new Dictionary<DevRole, long?>());

    [SqlFact]
    public async Task Pattern_lifecycle_versions_colorways_programming_and_model_link()
    {
        var org = await CreateOrganizationAsync();
        var dessinator = await InviteActiveAsync(org, SystemRoles.DevDessinator);
        var technologist = await InviteActiveAsync(org, SystemRoles.DevTechnologist);
        long pattern, e7, e3, milk, red, model;
        await using (var k = As(org.OwnerUserId, org.OrganizationId))
        {
            e7 = await k.Settings.AddLookupAsync(DevLookupKind.MachineClass, "E7", 8);
            e3 = await k.Settings.AddLookupAsync(DevLookupKind.MachineClass, "E3 узкий", 1);
            var kg = (await k.All.Catalog.ListUnitsAsync()).Single(u => u.Code == "166").Id;
            milk = await k.All.Catalog.CreateItemAsync(new ItemCommand("ПР-1", "Хлопок молочный", ItemType.RawMaterial, kg, null));
            red = await k.All.Catalog.CreateItemAsync(new ItemCommand("ПР-2", "Хлопок красный", ItemType.RawMaterial, kg, null));
            model = await k.Models.CreateAsync(Header("Свитер с оленями"));
        }

        await using (var k = As(dessinator, org.OrganizationId))
        {
            // Дессинатор: рисунок, сетка CSV (клетка — петля), доли по файлу, колориты с пряжей.
            pattern = await k.Patterns.CreateAsync(new DevPatternValues("Олени", Kind(DevPatternKind.JacquardDouble), 4, 2, 7m, e3, 24, null, null, dessinator, null));
            var p = await k.Patterns.GetAsync(pattern);
            Assert.Equal(("Р-00001", 1, DevPatternStatus.Draft), (p.Number, p.CurrentVersion, p.Status));
            Assert.Equal("Основной", Assert.Single(p.Colorways).Name);
            Assert.Equal("dev.pattern.grid.none", (await Assert.ThrowsAsync<BusinessRuleException>(() => k.Patterns.ComputeSharesAsync(pattern))).Code);
            await k.Patterns.AddFileAsync(pattern, DevPatternFileKind.Pattern, "oleni.csv", "1,1,1,2\n1,2,2,2\n"u8.ToArray(), null);
            Assert.Equal(2, await k.Patterns.ComputeSharesAsync(pattern));
            p = await k.Patterns.GetAsync(pattern);
            Assert.Equal(new[] { 50m, 50m }, p.Colors.Select(c => c.SharePercent!.Value));
            Assert.Equal(new[] { "1", "2" }, p.Colors.Select(c => c.GridKey!).Order());
            var main = p.Colorways[0].Id;
            var red2 = await k.Patterns.AddColorwayAsync(pattern, "Красный");
            Assert.Equal("dev.pattern.colorway.duplicate", (await Assert.ThrowsAsync<BusinessRuleException>(() => k.Patterns.AddColorwayAsync(pattern, "Красный"))).Code);

            // Утвердить дессинатор не может; при утверждении — проверки ТЗ §7.
            await Assert.ThrowsAsync<AccessDeniedException>(() => k.Patterns.MoveAsync(pattern, DevPatternStatus.Approved, p.RowVersion));
            await k.Patterns.SetYarnAsync(main, p.Colors[0].Id, milk, "#F2EFE6");
            await k.Patterns.SetYarnAsync(main, p.Colors[1].Id, red, null);
            await k.Patterns.SetYarnAsync(red2, p.Colors[0].Id, red, null);
            Assert.Equal("dev.color.yarn", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                k.Patterns.SetYarnAsync(red2, p.Colors[1].Id, long.MaxValue, null))).Code);
            await k.Patterns.MoveAsync(pattern, DevPatternStatus.Review, (await k.Patterns.GetAsync(pattern)).RowVersion);
        }

        await using (var k = As(org.OwnerUserId, org.OrganizationId))
        {
            var p = await k.Patterns.GetAsync(pattern);
            string Code(BusinessRuleException ex) => ex.Code;
            Assert.Equal("dev.pattern.approve.yarn", Code(await Assert.ThrowsAsync<BusinessRuleException>(() =>
                k.Patterns.MoveAsync(pattern, DevPatternStatus.Approved, p.RowVersion))));
            await k.Patterns.SetYarnAsync(p.Colorways[1].Id, p.Colors[1].Id, milk, null);
            // Ревью 13.10: правка раскладки меняет версию строки рисунка — утверждение по устаревшей версии получает конфликт.
            await Assert.ThrowsAsync<ConcurrencyConflictException>(() => k.Patterns.MoveAsync(pattern, DevPatternStatus.Approved, p.RowVersion));
            p = await k.Patterns.GetAsync(pattern);
            Assert.Equal("dev.pattern.approve.feeders", Code(await Assert.ThrowsAsync<BusinessRuleException>(() =>
                k.Patterns.MoveAsync(pattern, DevPatternStatus.Approved, p.RowVersion)))); // 2 цвета, у класса 1 нитеводитель
            await k.Patterns.UpdateAsync(pattern, new DevPatternValues("Олени", Kind(DevPatternKind.JacquardDouble), 4, 2, 7m, e7, 26, null, null, dessinator, null), p.RowVersion);
            p = await k.Patterns.GetAsync(pattern);
            Assert.Equal("dev.pattern.approve.align", Code(await Assert.ThrowsAsync<BusinessRuleException>(() =>
                k.Patterns.MoveAsync(pattern, DevPatternStatus.Approved, p.RowVersion)))); // 26 не кратно 4
            await k.Patterns.UpdateAsync(pattern, new DevPatternValues("Олени", Kind(DevPatternKind.JacquardDouble), 4, 2, 7m, e7, 26, "центрировать по середине переда", null,
                dessinator, technologist), p.RowVersion);
            await k.Patterns.MoveAsync(pattern, DevPatternStatus.Approved, (await k.Patterns.GetAsync(pattern)).RowVersion);
            p = await k.Patterns.GetAsync(pattern);
            Assert.Equal(DevPatternStatus.Approved, p.Status);
            Assert.Equal("dev.pattern.locked", Code(await Assert.ThrowsAsync<BusinessRuleException>(() => k.Patterns.AddColorAsync(pattern, "Ещё", null, null))));
            // «Вернуть в черновик» не открывает правку утверждённой версии — только новая версия.
            await k.Patterns.MoveAsync(pattern, DevPatternStatus.Draft, p.RowVersion);
            Assert.Equal("dev.pattern.locked", Code(await Assert.ThrowsAsync<BusinessRuleException>(() => k.Patterns.AddColorAsync(pattern, "Ещё", null, null))));
            Assert.Equal("dev.pattern.locked", Code(await Assert.ThrowsAsync<BusinessRuleException>(() =>
                k.Patterns.AddFileAsync(pattern, DevPatternFileKind.Pattern, "podmena.csv", "1,1\n"u8.ToArray(), null))));
            await k.Patterns.MoveAsync(pattern, DevPatternStatus.Approved, (await k.Patterns.GetAsync(pattern)).RowVersion);
            p = await k.Patterns.GetAsync(pattern);

            // Модель — конкретная версия и колорит рисунка.
            var m = await k.Models.GetAsync(model);
            await Assert.ThrowsAsync<NotFoundException>(() => k.Patterns.LinkToModelAsync(model, pattern, 7, null, m.RowVersion));
            await k.Patterns.LinkToModelAsync(model, pattern, 1, p.Colorways[1].Id, m.RowVersion);
            var mp = await k.Patterns.ModelPatternAsync(model);
            Assert.Equal((1, "Красный"), (mp.Version!.Value, mp.Colorway!));
            Assert.Equal(1, (await k.Patterns.GetAsync(pattern)).Colorways[1].UsedInModels);
            Assert.Equal("dev.pattern.colorway.used", Code(await Assert.ThrowsAsync<BusinessRuleException>(async () =>
            {
                await k.Patterns.NewVersionAsync(pattern, "временно", null, (await k.Patterns.GetAsync(pattern)).RowVersion);
                await k.Patterns.RemoveColorwayAsync(p.Colorways[1].Id);
            })));
        }

        await using (var k = As(dessinator, org.OrganizationId))
        {
            // Новая версия: черновик, файл к версии 2, «готов к программированию» → задача программисту.
            var p = await k.Patterns.GetAsync(pattern);
            Assert.Equal((2, DevPatternStatus.Draft), (p.CurrentVersion, p.Status));
            Assert.Equal("dev.pattern.ready.file", (await Assert.ThrowsAsync<BusinessRuleException>(() => k.Patterns.ReadyForProgrammingAsync(pattern, p.RowVersion))).Code);
            await k.Patterns.AddFileAsync(pattern, DevPatternFileKind.Pattern, "oleni-v2.csv", "1,2\n2,1\n"u8.ToArray(), null);
            await k.Patterns.ReadyForProgrammingAsync(pattern, (await k.Patterns.GetAsync(pattern)).RowVersion); // загрузка файла сменила версию строки
            Assert.Equal("dev.pattern.file.type", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                k.Patterns.AddFileAsync(pattern, DevPatternFileKind.Pattern, "evil.csv", "<script>alert(1)</script>"u8.ToArray(), null))).Code);
        }

        await using (var k = As(technologist, org.OrganizationId))
        {
            // Программист (технолог): видит задачу, прикладывает программу к версии рисунка с временем вязки — задача уходит.
            Assert.Equal(pattern, Assert.Single(await k.Patterns.MyProgrammingAsync()).Id);
            Assert.Contains(await k.All.Notifications.ListAsync(), n => n.Text == "Рисунки ждут программу машины: 1");
            await Assert.ThrowsAsync<AccessDeniedException>(() => k.Patterns.AddColorAsync(pattern, "Нельзя", null, null)); // без права «рисунки»
            await k.Patterns.AddFileAsync(pattern, DevPatternFileKind.Program, "oleni.sin", Program, 720);
            Assert.Empty(await k.Patterns.MyProgrammingAsync());
            var p = await k.Patterns.GetAsync(pattern);
            var v2 = p.Versions.First();
            Assert.Equal((2, "временно"), (v2.Version, v2.Change));
            Assert.Contains(v2.Files, f => f.Kind == DevPatternFileKind.Program && f.KnitSeconds == 720);
            Assert.True(p.HasProgram);
            Assert.Single(await k.Patterns.ListAsync(new DevPatternFilter(Colors: 2, MachineClassId: e7)));
            Assert.Empty(await k.Patterns.ListAsync(new DevPatternFilter(KindId: Kind(DevPatternKind.Intarsia))));
        }

        await using (var k = As(dessinator, org.OrganizationId))
        {
            // Копия: новый номер, цвета, колориты с пряжей и файл текущей версии.
            var copy = await k.Patterns.CopyAsync(pattern);
            var c = await k.Patterns.GetAsync(copy);
            Assert.Equal(("Р-00002", "Р-00001", 2, 2), (c.Number, c.CopiedFrom!, c.Colors.Count, c.Colorways.Count));
            Assert.All(c.Colorways, cw => Assert.Equal(2, cw.Yarns.Count(y => y.ItemId is not null)));
            Assert.Equal("oleni-v2.csv", Assert.Single(c.Versions.Single().Files).FileName);
        }

        // Чужая организация — «не найдено»; версии и файлы рисунка — только вставка.
        var other = await CreateOrganizationAsync();
        await using (var k = As(other.OwnerUserId, other.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => k.Patterns.GetAsync(pattern));
            await Assert.ThrowsAsync<NotFoundException>(() => k.Patterns.AddColorAsync(pattern, "Чужой", null, null));
            var own = await k.Models.CreateAsync(Header("Своя"));
            await Assert.ThrowsAsync<NotFoundException>(async () => await k.Patterns.LinkToModelAsync(own, pattern, 1, null, (await k.Models.GetAsync(own)).RowVersion));
            Assert.Empty(await k.Patterns.ListAsync(new DevPatternFilter()));
        }

        await using var db = host.NewDb();
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM [kniterp].[dev_pattern_versions] WHERE [PatternId] = {0}", pattern));
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("UPDATE [kniterp].[dev_pattern_files] SET [FileName] = 'x' WHERE [PatternId] = {0}", pattern));
    }

    /// <summary>
    /// D729: сценарий «другой колорит» в себестоимости, рисунок в техпаспорте, повторное использование рисунков в аналитике, фото до 2000 px.
    /// </summary>
    [SqlFact]
    public async Task Colorway_cost_scenario_passport_reuse_and_photo_shrink()
    {
        var org = await CreateOrganizationAsync();
        await using var all = host.As(org.OwnerUserId, org.OrganizationId);
        _org = org.OrganizationId;
        var user = new TestUser { UserId = org.OwnerUserId, OrganizationId = org.OrganizationId, ClientAddress = host.ClientAddress };
        var guard = new AccessGuard(all.Db, user, host.Clock);
        var models = new DevModelService(all.Db, guard, user, host.Clock, new KnitErp.Infrastructure.Documents.SkiaImageShrinker());
        var tech = new DevTechService(all.Db, guard, user, host.Clock, models);
        var shop = new DevShopService(all.Db, guard, user, host.Clock, tech);
        var patterns = new DevPatternService(all.Db, guard, user, host.Clock);
        var costs = new DevCostService(all.Db, guard, user, host.Clock);
        var settings = new DevSettingsService(all.Db, guard, user, host.Clock);

        var kg = (await all.Catalog.ListUnitsAsync()).Single(u => u.Code == "166").Id;
        var milk = await all.Catalog.CreateItemAsync(new ItemCommand("ПР-М", "Хлопок молочный", ItemType.RawMaterial, kg, null));
        var red = await all.Catalog.CreateItemAsync(new ItemCommand("ПР-К", "Хлопок красный", ItemType.RawMaterial, kg, null));
        var e7 = await settings.AddLookupAsync(DevLookupKind.MachineClass, "E7", 8);
        var pattern = await patterns.CreateAsync(new DevPatternValues("Полосы", Kind(DevPatternKind.Stripes), 2, 2, null, e7, 20, null, null, null, null));
        var color = await patterns.AddColorAsync(pattern, "Фон", 100, 1);
        var p = await patterns.GetAsync(pattern);
        var main = p.Colorways[0].Id;
        var redway = await patterns.AddColorwayAsync(pattern, "Красный");
        await patterns.SetYarnAsync(main, color, milk, null);
        await patterns.SetYarnAsync(redway, color, red, null);

        // Модель: палитра с той же пряжей, деталь с весом — норма пряжи; рисунок и колорит «Основной».
        var model = await models.CreateAsync(new DevModelHeader("Шарф в полоску", null, Cat(DevCategory.Scarf), DevAudience.Unisex, false, true, null, null,
            new DateOnly(2026, 12, 1), DevPriority.Normal, new Dictionary<DevRole, long?>()));
        await tech.AddColorAsync(model, "Основной", milk, null, null);
        var detail = await tech.AddDetailAsync(model, "Полотно", 1);
        var d = await tech.DetailAsync(detail);
        await tech.SetKnittingAsync(detail, null, null, null, 200m, null, d.RowVersion);
        await patterns.LinkToModelAsync(model, pattern, 1, main, (await models.GetAsync(model)).RowVersion);

        var basic = await costs.CostingAsync(model);
        Assert.Contains(basic.Lines, l => l.ItemId == milk);
        Assert.Equal(new[] { "Основной", "Красный" }, basic.Colorways!.Select(c => c.Name));
        var other = await costs.CostingAsync(model, new DevCostScenario(ColorwayId: redway));
        Assert.Contains(other.Lines, l => l.ItemId == red && l.OriginalItemId == milk);
        Assert.DoesNotContain(other.Lines, l => l.ItemId == milk);

        // Паспорт: рисунок с раппортом и пряжей колорита.
        var passport = await new DevPassportService(all.Db, guard, host.Clock, models, tech, shop).BuildAsync(model);
        Assert.Equal(("Р-00001", 1, "Основной"), (passport.Pattern!.Number, passport.Pattern.Version, passport.Pattern.Colorway!));
        Assert.Contains("ПР-М", passport.Pattern.Colors.Single().Yarn);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(new KnitErp.Infrastructure.Documents.DevPassportPdfRenderer().Render(passport), 0, 4));

        // Ревью 13.10: новая версия рисунка меняет цвет и пряжу — паспорт модели на версии 1 печатает снимок версии 1, а не текущие данные.
        await patterns.NewVersionAsync(pattern, "другой фон", null, (await patterns.GetAsync(pattern)).RowVersion);
        var v2 = await patterns.GetAsync(pattern);
        await patterns.UpdateColorAsync(color, "Фон новый", 100, 1, v2.Colors.Single().RowVersion);
        await patterns.SetYarnAsync(main, color, red, null);
        var old = (await new DevPassportService(all.Db, guard, host.Clock, models, tech, shop).BuildAsync(model)).Pattern!;
        Assert.Equal((1, "Фон"), (old.Version, old.Colors.Single().Name));
        Assert.Contains("ПР-М", old.Colors.Single().Yarn);
        await Assert.ThrowsAnyAsync<Exception>(() => all.Db.Database.ExecuteSqlRawAsync(
            "DELETE FROM [kniterp].[dev_pattern_version_colors] WHERE [PatternId] = {0}", pattern));

        // Аналитика: рисунок во второй модели — повторное использование.
        var second = await models.CreateAsync(new DevModelHeader("Шапка в полоску", null, Cat(DevCategory.Hat), DevAudience.Unisex, false, true, null, null,
            new DateOnly(2026, 12, 1), DevPriority.Normal, new Dictionary<DevRole, long?>()));
        await patterns.LinkToModelAsync(second, pattern, 1, redway, (await models.GetAsync(second)).RowVersion);
        var report = await new DevAnalyticsService(all.Db, guard, host.Clock, SqlTestHost.Spreadsheet).ReportAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 9));
        Assert.Equal(2, Assert.Single(report.PatternReuse!).Models);

        // Фото 3000×1200 хранится уменьшенным до 2000 px по длинной стороне; маленькое — как загружено.
        byte[] Png(int w, int h)
        {
            using var bmp = new SkiaSharp.SKBitmap(w, h);
            bmp.Erase(SkiaSharp.SKColors.Teal);
            using var img = SkiaSharp.SKImage.FromBitmap(bmp);
            return img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).ToArray();
        }

        var big = await models.AddFileAsync(model, DevFileKind.Photo, "big.png", Png(3000, 1200), null);
        var small = Png(800, 600);
        var smallId = await models.AddFileAsync(model, DevFileKind.Photo, "small.png", small, null);
        var stored = (await models.GetFileAsync(big)).Content;
        using (var bmp = SkiaSharp.SKBitmap.Decode(stored))
        {
            Assert.Equal((2000, 800), (bmp.Width, bmp.Height));
        }

        Assert.Equal(small, (await models.GetFileAsync(smallId)).Content);
    }

    private async Task<CreatedOrganization> CreateOrganizationAsync()
    {
        var inn = NextValidInn();
        await using var s = host.As(null, null);
        return await s.Organizations.CreateWithOwnerAsync(new CreateOrganizationCommand(
            $"Тестовая организация {inn}", $"Тест {inn}", inn, null, false, "Europe/Moscow", $"owner-{inn}@test.local", $"Владелец {inn}"));
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
