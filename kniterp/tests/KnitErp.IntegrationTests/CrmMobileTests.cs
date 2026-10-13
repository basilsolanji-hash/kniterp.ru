using KnitErp.Application.Catalog;
using KnitErp.Application.Common;
using KnitErp.Application.Crm;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Crm;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// Мобильный день менеджера (D690–D694): «Мой день» — просроченные и сегодняшние задачи с клиентом и телефоном, результат звонка и
/// следующий шаг одной формой; фото к сделке — загрузка, сигнатура, размер, лимит 30, архив, чужая организация и чужая сделка — 404.
/// Данные вымышленные: телефоны +7 900 000-00-xx, почты @example.org.
/// </summary>
public sealed class CrmMobileTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static readonly DateTime Morning = new(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc); // 12:00 по Москве

    [SqlFact]
    public async Task Today_lists_my_tasks_and_call_result_plans_next_step()
    {
        host.Clock.UtcNow = Morning;
        var org = await host.CreateCrmOrganizationAsync();
        var manager = await host.CrmUserAsync(org, SystemRoles.SalesManager);
        var other = await host.CrmUserAsync(org, SystemRoles.SalesManager);
        var customer = await CustomerAsync(org, "ООО «Тестовый трикотаж»");

        await using var crm = host.Crm(manager, org.OrganizationId);
        var contact = await crm.Contacts.CreateAsync(new CrmContactValues("Тестова Ольга", "Закупщик", customer, Phone: "+7 900 000-00-21"));
        var deal = await crm.Deals.CreateAsync(new CrmDealValues("Свитера к зиме", customer, contact, 30_000m));
        var lead = await crm.Leads.CreateAsync(new CrmLeadValues("Шапки оптом", ContactName: "Пробный Пётр", Phone: "+7 900 000-00-22",
            CompanyName: "ИП Пробный"));

        var call = await crm.Activities.CreateAsync(new CrmActivityCommand(CrmTarget.Deal, deal, CrmActivityKind.Call, "Обсудить образцы",
            DueLocal: new DateTime(2026, 10, 13, 18, 0, 0)));
        var leadTask = await crm.Activities.CreateAsync(new CrmActivityCommand(CrmTarget.Lead, lead, CrmActivityKind.Task, "Уточнить объём",
            DueLocal: new DateTime(2026, 10, 12, 11, 0, 0)));
        await crm.Activities.CreateAsync(new CrmActivityCommand(CrmTarget.Deal, deal, CrmActivityKind.Task, "Завтрашняя задача",
            DueLocal: new DateTime(2026, 10, 14, 10, 0, 0)));
        await crm.Activities.CreateAsync(new CrmActivityCommand(CrmTarget.Deal, deal, CrmActivityKind.Task, "Уже сделано",
            DueLocal: new DateTime(2026, 10, 13, 10, 0, 0), Done: true));
        await crm.Activities.CreateAsync(new CrmActivityCommand(CrmTarget.Deal, deal, CrmActivityKind.Call, "Чужая задача",
            DueLocal: new DateTime(2026, 10, 13, 15, 0, 0), AssigneeUserId: other));

        // «Мой день»: только мои невыполненные — просроченная по лиду и сегодняшний звонок по сделке; клиент, контакт и телефон.
        var day = await crm.Activities.TodayAsync();
        Assert.Equal(new DateOnly(2026, 10, 13), day.Date);
        var overdue = Assert.Single(day.Overdue);
        Assert.Equal((leadTask, "ИП Пробный", "Пробный Пётр", "+79000000022"), (overdue.Activity.Id, overdue.Client, overdue.ContactName, overdue.Phone));
        var today = Assert.Single(day.Today);
        Assert.Equal((call, "Тестова Ольга", "+79000000021"), (today.Activity.Id, today.ContactName, today.Phone));
        Assert.StartsWith("ООО «Тестовый трикотаж»", today.Client);
        Assert.Equal("+7 900 000-00-21", today.PhoneText);

        // Неверный срок следующего шага — отказ, ничего не записано.
        Assert.Equal("crm.call.next_days", (await Assert.ThrowsAsync<BusinessRuleException>(() => crm.Activities.CallResultAsync(
            new CrmCallResultCommand(call, false, null, 0, null, true, today.Activity.RowVersion)))).Code);
        Assert.Equal("crm.call.next_days", (await Assert.ThrowsAsync<BusinessRuleException>(() => crm.Activities.CallResultAsync(
            new CrmCallResultCommand(call, false, null, 91, null, true, today.Activity.RowVersion)))).Code);

        // Не дозвонился → звонок выполнен с результатом, следующий шаг «Перезвонить» завтра в 10:00 по Москве.
        var result = await crm.Activities.CallResultAsync(new CrmCallResultCommand(call, false, "Занято", 1, null, true, today.Activity.RowVersion));
        Assert.Equal(call, result.CallId);
        Assert.NotNull(result.NextStepId);
        // Повторное нажатие со старой версией — конфликт; с новой — «уже выполнено».
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => crm.Activities.CallResultAsync(
            new CrmCallResultCommand(call, true, null, null, null, true, today.Activity.RowVersion)));
        byte[] fresh;
        await using (var db = host.NewDb())
        {
            fresh = await db.CrmActivities.AsNoTracking().Where(a => a.Id == call).Select(a => a.RowVersion).SingleAsync();
        }

        Assert.Equal("crm.activity.done", (await Assert.ThrowsAsync<BusinessRuleException>(() => crm.Activities.CallResultAsync(
            new CrmCallResultCommand(call, true, null, null, null, true, fresh)))).Code);

        // Дозвонился по задаче другого вида → новый выполненный «Звонок», задача остаётся (не просили закрыть), следующий шаг — задача.
        var second = await crm.Activities.CallResultAsync(new CrmCallResultCommand(leadTask, true, "Нужно 500 шапок, пришлём образцы", 3,
            "Отправить образцы", false, overdue.Activity.RowVersion));
        Assert.NotEqual(leadTask, second.CallId);

        await using (var db = host.NewDb())
        {
            var done = await db.CrmActivities.AsNoTracking().SingleAsync(a => a.Id == call);
            Assert.Equal((true, "Не дозвонился: Занято", manager), (done.IsDone, done.Result, done.DoneByUserId!.Value));
            var next = await db.CrmActivities.AsNoTracking().SingleAsync(a => a.Id == result.NextStepId);
            Assert.Equal((CrmActivityKind.Call, "Перезвонить", "Занято", false, manager), (next.Kind, next.Subject, next.Text, next.IsDone, next.AssigneeUserId));
            Assert.Equal((CrmTarget.Deal, deal), (next.Target, next.TargetId));
            Assert.Equal(new DateTime(2026, 10, 14, 7, 0, 0, DateTimeKind.Utc), next.DueAtUtc);

            var leadCall = await db.CrmActivities.AsNoTracking().SingleAsync(a => a.Id == second.CallId);
            Assert.Equal((CrmActivityKind.Call, CrmTarget.Lead, lead, true), (leadCall.Kind, leadCall.Target, leadCall.TargetId, leadCall.IsDone));
            Assert.Equal("Дозвонился: Нужно 500 шапок, пришлём образцы", leadCall.Result);
            Assert.False((await db.CrmActivities.AsNoTracking().SingleAsync(a => a.Id == leadTask)).IsDone);
            var leadNext = await db.CrmActivities.AsNoTracking().SingleAsync(a => a.Id == second.NextStepId);
            Assert.Equal((CrmActivityKind.Task, "Отправить образцы"), (leadNext.Kind, leadNext.Subject));
            Assert.Equal(new DateTime(2026, 10, 16, 7, 0, 0, DateTimeKind.Utc), leadNext.DueAtUtc);
            Assert.Equal(CrmLeadStatus.InWork, (await db.CrmLeads.AsNoTracking().SingleAsync(l => l.Id == lead)).Status);

            var audit = await db.AuditEntries.AsNoTracking().Where(a => a.OrganizationId == org.OrganizationId && a.EntityType == nameof(CrmActivity)).ToListAsync();
            Assert.Contains(audit, a => a.Action == AuditActions.CrmActivityDone && a.EntityId == call.ToString() && a.After == "Не дозвонился: Занято");
            Assert.Contains(audit, a => a.Action == AuditActions.CrmActivityCreated && a.EntityId == result.NextStepId.ToString());
        }

        // После звонков: сегодняшний звонок ушёл из дня, просроченная задача по лиду осталась.
        day = await crm.Activities.TodayAsync();
        Assert.Empty(day.Today);
        Assert.Equal(leadTask, Assert.Single(day.Overdue).Activity.Id);

        // Чужая сделка: другой менеджер не видит ни задачи, ни телефона; записать звонок по чужой задаче нельзя — 404.
        await using (var foreign = host.Crm(other, org.OrganizationId))
        {
            var otherDay = await foreign.Activities.TodayAsync();
            var mine = Assert.Single(otherDay.Today); // своя задача на чужой сделке видна, но без телефона клиента
            Assert.Equal(("Чужая задача", null, null), (mine.Activity.Subject, mine.Phone, mine.Client));
            await Assert.ThrowsAsync<NotFoundException>(() => foreign.Activities.CallResultAsync(
                new CrmCallResultCommand(leadTask, true, null, null, null, true, overdue.Activity.RowVersion)));
        }
    }

    [SqlFact]
    public async Task Deal_photos_upload_limit_signature_archive_and_access()
    {
        host.Clock.UtcNow = Morning;
        var org = await host.CreateCrmOrganizationAsync();
        var manager = await host.CrmUserAsync(org, SystemRoles.SalesManager);
        var stranger = await host.CrmUserAsync(org, SystemRoles.SalesManager);
        var customer = await CustomerAsync(org, "ООО «Фото-клиент»");
        var foreignOrg = await host.CreateCrmOrganizationAsync();

        await using var crm = host.Crm(manager, org.OrganizationId);
        var deal = await crm.Deals.CreateAsync(new CrmDealValues("Пледы с логотипом", customer));

        var jpeg = Jpeg(1000);
        var id = await crm.Photos.UploadAsync(deal, jpeg, "Образец полотна");
        var list = await crm.Photos.ListAsync(deal);
        var photo = Assert.Single(list.Items);
        Assert.Equal((id, "Образец полотна", "image/jpeg", 1000, true), (photo.Id, photo.Caption, photo.ContentType, photo.SizeBytes, list.CanEdit));
        Assert.Equal($"crm/deal-photos/{id}", photo.Href);
        var (type, content) = await crm.Photos.ContentAsync(id);
        Assert.Equal("image/jpeg", type);
        Assert.Equal(jpeg, content);
        Assert.Equal("image/png", (await crm.Photos.ContentAsync(await crm.Photos.UploadAsync(deal, Png(), null))).ContentType);
        Assert.Equal("image/webp", (await crm.Photos.ContentAsync(await crm.Photos.UploadAsync(deal, WebP(), null))).ContentType);

        // Неверная сигнатура (HTML под видом фото), пустой и слишком большой файл — отказ, строк не добавилось.
        var html = "<html><script>alert(1)</script></html>"u8.ToArray();
        Assert.Equal("crm.photo.type", (await Assert.ThrowsAsync<BusinessRuleException>(() => crm.Photos.UploadAsync(deal, html, null))).Code);
        Assert.Equal("crm.photo.empty", (await Assert.ThrowsAsync<BusinessRuleException>(() => crm.Photos.UploadAsync(deal, [], null))).Code);
        Assert.Equal("crm.photo.too_large", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            crm.Photos.UploadAsync(deal, Jpeg(CrmDealPhoto.MaxBytes + 1), null))).Code);
        Assert.Equal(3, (await crm.Photos.ListAsync(deal)).Items.Count);

        // Подпись.
        await crm.Photos.SetCaptionAsync(id, "Образец полотна, синий", photo.RowVersion);
        Assert.Equal("Образец полотна, синий", (await crm.Photos.ListAsync(deal)).Items.Single(p => p.Id == id).Caption);
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => crm.Photos.SetCaptionAsync(id, "Старая версия", photo.RowVersion));

        // Лимит: 30 действующих фото на сделку.
        for (var i = 3; i < CrmDealPhoto.MaxPerDeal; i++)
        {
            await crm.Photos.UploadAsync(deal, Jpeg(100 + i), null);
        }

        Assert.Equal(0, (await crm.Photos.ListAsync(deal)).Left);
        Assert.Equal("crm.photo.limit", (await Assert.ThrowsAsync<BusinessRuleException>(() => crm.Photos.UploadAsync(deal, Jpeg(500), null))).Code);

        // Удаление — в архив: в сделке и по ссылке фото больше нет, в базе осталось; место под новое освободилось.
        var current = (await crm.Photos.ListAsync(deal)).Items.Single(p => p.Id == id);
        await crm.Photos.ArchiveAsync(id, current.RowVersion);
        Assert.DoesNotContain((await crm.Photos.ListAsync(deal)).Items, p => p.Id == id);
        await Assert.ThrowsAsync<NotFoundException>(() => crm.Photos.ContentAsync(id));
        await Assert.ThrowsAsync<NotFoundException>(() => crm.Photos.ArchiveAsync(id, current.RowVersion));
        await crm.Photos.UploadAsync(deal, Jpeg(600), "Вместо удалённого");
        Assert.Equal(CrmDealPhoto.MaxPerDeal, (await crm.Photos.ListAsync(deal)).Items.Count);

        // Менеджер без доступа к сделке (не ответственный и не участник) — 404 на всё.
        await using (var other = host.Crm(stranger, org.OrganizationId))
        {
            var visible = (await crm.Photos.ListAsync(deal)).Items[0];
            await Assert.ThrowsAsync<NotFoundException>(() => other.Photos.ListAsync(deal));
            await Assert.ThrowsAsync<NotFoundException>(() => other.Photos.ContentAsync(visible.Id));
            await Assert.ThrowsAsync<NotFoundException>(() => other.Photos.UploadAsync(deal, Jpeg(700), null));
            await Assert.ThrowsAsync<NotFoundException>(() => other.Photos.ArchiveAsync(visible.Id, visible.RowVersion));
            await Assert.ThrowsAsync<NotFoundException>(() => other.Photos.SetCaptionAsync(visible.Id, "Чужое", visible.RowVersion));
        }

        // Чужая организация — 404, даже у Владельца.
        await using (var foreign = host.Crm(foreignOrg.OwnerUserId, foreignOrg.OrganizationId))
        {
            var visible = (await crm.Photos.ListAsync(deal)).Items[0];
            await Assert.ThrowsAsync<NotFoundException>(() => foreign.Photos.ListAsync(deal));
            await Assert.ThrowsAsync<NotFoundException>(() => foreign.Photos.ContentAsync(visible.Id));
            await Assert.ThrowsAsync<NotFoundException>(() => foreign.Photos.UploadAsync(deal, Jpeg(800), null));
        }

        // Владелец своей организации (crm.all) видит фото чужой для менеджера сделки.
        await using (var owner = host.Crm(org.OwnerUserId, org.OrganizationId))
        {
            Assert.Equal(CrmDealPhoto.MaxPerDeal, (await owner.Photos.ListAsync(deal)).Items.Count);
        }

        await using var check = host.NewDb();
        var archived = await check.CrmDealPhotos.AsNoTracking().SingleAsync(p => p.Id == id);
        Assert.Equal((true, manager, jpeg.Length), (archived.IsArchived, archived.ArchivedByUserId!.Value, archived.SizeBytes));
        var audit = await check.AuditEntries.AsNoTracking().Where(a => a.OrganizationId == org.OrganizationId && a.EntityType == nameof(CrmDealPhoto)).ToListAsync();
        Assert.Equal(CrmDealPhoto.MaxPerDeal + 1, audit.Count(a => a.Action == AuditActions.CrmDealPhotoAdded));
        Assert.Single(audit, a => a.Action == AuditActions.CrmDealPhotoArchived && a.EntityId == id.ToString());
        Assert.Single(audit, a => a.Action == AuditActions.CrmDealPhotoChanged);
        // CHECK в базе: тип только из разрешённых, размер совпадает с содержимым — даже в обход приложения.
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => check.Database.ExecuteSqlRawAsync(
            "UPDATE [kniterp].[crm_deal_photos] SET [ContentType] = 'text/html' WHERE [Id] = {0}", id));
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => check.Database.ExecuteSqlRawAsync(
            "UPDATE [kniterp].[crm_deal_photos] SET [SizeBytes] = 5 WHERE [Id] = {0}", id));
    }

    private async Task<long> CustomerAsync(KnitErp.Application.Organizations.CreatedOrganization org, string name)
    {
        await using var s = host.As(org.OwnerUserId, org.OrganizationId);
        return await s.Counterparties.CreateAsync(new CounterpartyCommand($"{name} {Guid.NewGuid():N}"[..40], null, null, false, true, null));
    }

    private static byte[] Jpeg(int size)
    {
        var b = new byte[size];
        b[0] = 0xFF;
        b[1] = 0xD8;
        b[2] = 0xFF;
        b[3] = 0xE0;
        b[^2] = 0xFF;
        b[^1] = 0xD9;
        return b;
    }

    private static byte[] Png()
    {
        var b = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
        return b;
    }

    private static byte[] WebP()
    {
        var b = new byte[64];
        "RIFF"u8.ToArray().CopyTo(b, 0);
        "WEBP"u8.ToArray().CopyTo(b, 8);
        return b;
    }
}
