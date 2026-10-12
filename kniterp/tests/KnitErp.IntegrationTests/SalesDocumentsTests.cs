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
using KnitErp.Domain.Sales;
using KnitErp.Domain.Warehousing;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.IntegrationTests;

/// <summary>
/// Документы продаж (D1120–D1125): оформление (логотип, печать, подписи, QR), письма клиенту (ручные и автоматические через очередь,
/// PDF во вложениях), связанные документы, права и чужая организация.
/// </summary>
public sealed class SalesDocumentsTests(SqlTestHost host) : IClassFixture<SqlTestHost>
{
    private static int _innSeed = 813_000_000;
    private static readonly DateOnly Day = new(2026, 10, 9);

    /// <summary>Настоящий PNG 1×1 (прозрачный пиксель).</summary>
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    [SqlFact]
    public async Task Images_are_checked_by_signature_and_permission_and_decorate_invoice_with_qr()
    {
        var f = await SetUpAsync();
        long invoice;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            // Неверная сигнатура (HTML под видом PNG) и слишком большой файл — отказ; в базе ничего не появляется.
            Assert.Equal("sales.document_image.type", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.DocumentSettings.UploadImageAsync(f.Entity, SalesDocumentImageKind.Stamp, "image/png", "<html><script>alert(1)</script>"u8.ToArray()))).Code);
            Assert.Equal("sales.document_image.size", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
                s.DocumentSettings.UploadImageAsync(f.Entity, SalesDocumentImageKind.Logo, "image/png", [.. Png, .. new byte[SalesDocumentImage.MaxBytes]]))).Code);
            Assert.Empty((await s.DocumentSettings.EntityAsync(f.Entity)).Images);

            foreach (var kind in SalesDocumentImage.Kinds)
            {
                await s.DocumentSettings.UploadImageAsync(f.Entity, kind, "image/png", Png);
            }

            // Замена — та же запись; журнал — без содержимого.
            await s.DocumentSettings.UploadImageAsync(f.Entity, SalesDocumentImageKind.Stamp, "image/png", Png);
            var entity = await s.DocumentSettings.EntityAsync(f.Entity);
            Assert.Equal(4, entity.Images.Count);
            Assert.StartsWith("data:image/png;base64,", entity.Images.Single(i => i.Kind == SalesDocumentImageKind.Stamp).DataUri);

            invoice = await IssueInvoiceAsync(s, f, 3);
            var deco = await s.DocumentPrint.DecorationsAsync(SalesMailDocument.Invoice, invoice);
            Assert.NotNull(deco.Logo);
            Assert.NotNull(deco.Stamp);
            Assert.NotNull(deco.DirectorSignature);
            Assert.Equal(Png, deco.AccountantSignature!.Content);
            var print = await s.Print.InvoiceAsync(invoice);
            var vat = print.VatTotal.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal($"ST00012|Name={print.Seller.Name}|PersonalAcc=40702810938000000001|BankName=ПАО «Тестбанк»|BIC=044525225"
                         + $"|CorrespAcc=30101810400000000225|PayeeINN={print.Seller.Inn}" + (print.Seller.Kpp is { } kpp ? $"|KPP={kpp}" : "")
                         + $"|Purpose=Оплата по счёту № {print.Number} от 09.10.2026, в т.ч. НДС {vat}"
                         + $"|Sum={(long)(print.Total * 100)}", deco.PaymentQr);

            // Выключили печать и QR в счёте — на форме их нет; в УПД печать осталась.
            var flags = entity.Flags with { StampOnInvoice = false, QrOnInvoice = false, LogoOnInvoice = false };
            await s.DocumentSettings.SaveFlagsAsync(f.Entity, flags, entity.RowVersion);
            deco = await s.DocumentPrint.DecorationsAsync(SalesMailDocument.Invoice, invoice);
            Assert.Equal(SalesPrintDecorations.None, deco with { });
            Assert.Null(deco.PaymentQr);
            Assert.False((await s.DocumentSettings.EntityAsync(f.Entity)).Flags.QrOnInvoice);

            await s.DocumentSettings.DeleteImageAsync(f.Entity, SalesDocumentImageKind.Logo);
            Assert.Equal(3, (await s.DocumentSettings.EntityAsync(f.Entity)).Images.Count);
        }

        // Менеджер продаж: печатает, но не меняет оформление (нет права «Организация: изменение»).
        await using (var m = host.As(f.Manager, f.Org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() =>
                m.DocumentSettings.UploadImageAsync(f.Entity, SalesDocumentImageKind.Logo, "image/png", Png));
            await Assert.ThrowsAsync<AccessDeniedException>(() => m.DocumentSettings.DeleteImageAsync(f.Entity, SalesDocumentImageKind.Stamp));
            await Assert.ThrowsAsync<AccessDeniedException>(() => m.DocumentSettings.SaveTemplateAsync(SalesMailKind.Order, true, "Т", "Т", null));
            Assert.False((await m.DocumentSettings.GetAsync()).CanEdit);
        }

        await using (var db = host.NewDb())
        {
            var audit = await db.AuditEntries.Where(a => a.OrganizationId == f.Org.OrganizationId && a.Action == AuditActions.SalesDocumentImageChanged)
                .ToListAsync();
            Assert.Equal(6, audit.Count);
            Assert.Contains(audit, a => a.Reason!.Contains("заменено"));
            Assert.Contains(audit, a => a.Reason!.Contains("удалено"));
            Assert.DoesNotContain(audit, a => (a.After ?? "").Contains("base64"));
        }
    }

    [SqlFact]
    public async Task Auto_mail_is_queued_only_when_enabled_and_customer_has_email_without_duplicates()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);

        // Автоотправка выключена (по умолчанию) — подтверждение письма не ставит.
        var first = await ConfirmedOrderAsync(s, f, 1);
        Assert.Empty(await MailsAsync(f));

        // Включили, но у покупателя нет почты — письма нет.
        foreach (var kind in SalesDocumentSettingsService.MailKinds)
        {
            var (subject, body) = SalesMailText.Default(kind);
            await s.DocumentSettings.SaveTemplateAsync(kind, true, subject, body, null);
        }

        var stage = (await s.Stages.ListAsync()).Single(x => x.Name == "Собран");
        await s.DocumentSettings.SetMailStagesAsync([stage.Id]);
        await ConfirmedOrderAsync(s, f, 1);
        Assert.Empty(await MailsAsync(f));

        // Почта указана — письмо «заказ оформлен» с заказом во вложении; в теме — подстановки.
        Assert.Equal("sales.mail.email", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.OrderDocuments.SetCustomerMailAsync(f.Customer, "buh@", false, null))).Code);
        await s.OrderDocuments.SetCustomerMailAsync(f.Customer, "buh@shop.example", false, null);
        var order = await ConfirmedOrderAsync(s, f, 2);
        var mails = await MailsAsync(f);
        var confirmed = Assert.Single(mails);
        Assert.Equal((SalesMailKind.Order, true, $"order:{order}", "buh@shop.example"), (confirmed.Kind, confirmed.IsAuto, confirmed.DedupKey, confirmed.Recipients));
        Assert.Equal((await s.Sales.GetOrderAsync(order)).Number, confirmed.Subject.Split(' ')[1]);
        Assert.Equal((SalesMailDocument.Order, order), (confirmed.Attachments.Single().Document, confirmed.Attachments.Single().DocumentId));

        // Этап «Собран» — письмо о статусе; вернули и снова поставили «Собран» — второго письма нет. Этап не из списка — без письма.
        var other = (await s.Stages.ListAsync()).Single(x => x.Name == "В работе");
        await s.Sales.SetOrderStageAsync(order, stage.Id, (await s.Sales.GetOrderAsync(order)).RowVersion);
        await s.Sales.SetOrderStageAsync(order, other.Id, (await s.Sales.GetOrderAsync(order)).RowVersion);
        await s.Sales.SetOrderStageAsync(order, stage.Id, (await s.Sales.GetOrderAsync(order)).RowVersion);
        mails = await MailsAsync(f);
        var stageMail = Assert.Single(mails, m => m.Kind == SalesMailKind.Stage);
        Assert.Contains("Собран", stageMail.Subject);
        Assert.Empty(stageMail.Attachments);

        // Счёт и проведённая отгрузка — письма со счётом и УПД (автоматический этап «Отгружен» в список не входит).
        var invoice = await s.Invoices.CreateAsync(order, null, null);
        var ship = await s.Sales.CreateShipmentAsync(order);
        await s.Documents.PostAsync(ship, (await s.Documents.GetAsync(ship)).RowVersion);
        mails = await MailsAsync(f);
        Assert.Equal((SalesMailDocument.Invoice, invoice), mails.Single(m => m.Kind == SalesMailKind.Invoice).Attachments.Select(a => (a.Document, a.DocumentId)).Single());
        Assert.Equal((SalesMailDocument.Upd, ship), mails.Single(m => m.Kind == SalesMailKind.Upd).Attachments.Select(a => (a.Document, a.DocumentId)).Single());
        Assert.Equal(4, mails.Count);

        // «Не отправлять автоматически» — новый заказ без письма; журнал заказа виден в панели.
        var settings = await s.OrderDocuments.CustomerMailAsync(f.Customer);
        await s.OrderDocuments.SetCustomerMailAsync(f.Customer, settings.Email, true, settings.RowVersion);
        await ConfirmedOrderAsync(s, f, 1);
        Assert.Equal(4, (await MailsAsync(f)).Count);
        var panel = await s.OrderDocuments.PanelAsync(order);
        Assert.Equal(4, panel.Mails.Count);
        Assert.True(panel.CustomerNoAutoSend);
        Assert.Equal([SalesMailDocument.Order, SalesMailDocument.Invoice, SalesMailDocument.Upd], panel.Printables.Select(p => p.Document));
        _ = first;

        // Уникальность автоматического письма — и в SQL Server.
        await using var db = host.NewDb();
        var dup = SalesMailMessage.Create(f.Org.OrganizationId, SalesMailKind.Order, order, f.Customer, "a@b.ru", "Т", "Т", true, $"order:{order}",
            f.Org.OwnerUserId, host.Clock.UtcNow, []);
        db.SalesMailMessages.Add(dup);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [SqlFact]
    public async Task Dispatcher_sends_pdf_attachments_and_reports_missing_smtp()
    {
        var f = await SetUpAsync();
        long order, invoice, ship, message;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            await s.DocumentSettings.UploadImageAsync(f.Entity, SalesDocumentImageKind.Stamp, "image/png", Png);
            await s.DocumentSettings.UploadImageAsync(f.Entity, SalesDocumentImageKind.Logo, "image/png", Png);
            invoice = await IssueInvoiceAsync(s, f, 2);
            order = (await s.Invoices.GetAsync(invoice)).OrderId;
            ship = await s.Sales.CreateShipmentAsync(order);
            await s.Documents.PostAsync(ship, (await s.Documents.GetAsync(ship)).RowVersion);

            // Черновик: тема и текст по шаблону счёта, отмечен счёт; адреса покупателя нет — укажут в форме.
            var draft = await s.OrderDocuments.DraftAsync(order, SalesMailDocument.Invoice, invoice);
            Assert.Null(draft.To);
            Assert.StartsWith("Счёт на оплату СЧ-", draft.Subject);
            Assert.Equal([new SalesMailDocumentRef(SalesMailDocument.Invoice, invoice)], draft.Selected);

            // Чужой документ, неверный адрес, без документов — отказ.
            await Assert.ThrowsAsync<NotFoundException>(() => s.OrderDocuments.SendAsync(new SalesMailRequest(order, "a@shop.example", "Т", "Т",
                [new SalesMailDocumentRef(SalesMailDocument.Upd, invoice + 100_000)])));
            Assert.Equal("sales.mail.email", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.OrderDocuments.SendAsync(
                new SalesMailRequest(order, "без почты", "Т", "Т", draft.Selected)))).Code);
            Assert.Equal("sales.mail.no_documents", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.OrderDocuments.SendAsync(
                new SalesMailRequest(order, "a@shop.example", "Т", "Т", [])))).Code);

            message = await s.OrderDocuments.SendAsync(new SalesMailRequest(order, "buh@shop.example, boss@shop.example", draft.Subject, draft.Body,
            [
                new SalesMailDocumentRef(SalesMailDocument.Order, order), new SalesMailDocumentRef(SalesMailDocument.Invoice, invoice),
                new SalesMailDocumentRef(SalesMailDocument.Upd, ship),
            ]));
        }

        // Почта сервера не настроена — понятный статус «не отправлено», повтор — снова в очередь.
        host.Mail.IsConfigured = false;
        try
        {
            await using var s = host.As(null, null);
            Assert.Equal(0, await s.MailDispatcher.DeliverAsync());
        }
        finally
        {
            host.Mail.IsConfigured = true;
        }

        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            var row = (await s.OrderDocuments.PanelAsync(order)).Mails.Single(m => m.Id == message);
            Assert.Equal((SalesMailStatus.Failed, SalesMailDispatcher.NotConfigured), (row.Status, row.LastError));
            await s.OrderDocuments.RetryMailAsync(message, row.RowVersion);
        }

        await using (var s = host.As(null, null))
        {
            Assert.True(await s.MailDispatcher.DeliverAsync() >= 1);
        }

        var sent = host.Mail.SentMessages.Single(m => m.To == "buh@shop.example, boss@shop.example");
        Assert.Equal(3, sent.Attachments.Count);
        Assert.All(sent.Attachments, a =>
        {
            Assert.Equal("application/pdf", a.ContentType);
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(a.Content, 0, 4));
            Assert.EndsWith(".pdf", a.FileName);
        });
        Assert.Contains(sent.Attachments, a => a.FileName.StartsWith("УПД "));
        // Посмотреть PDF глазами: KNITERP_PDF_DUMP=<папка> — вложения сохраняются туда.
        if (Environment.GetEnvironmentVariable("KNITERP_PDF_DUMP") is { Length: > 0 } dump)
        {
            Directory.CreateDirectory(dump);
            foreach (var a in sent.Attachments)
            {
                await File.WriteAllBytesAsync(Path.Combine(dump, a.FileName), a.Content);
            }
        }
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            var row = (await s.OrderDocuments.PanelAsync(order)).Mails.Single(m => m.Id == message);
            Assert.Equal(SalesMailStatus.Sent, row.Status);
            Assert.NotNull(row.SentAtUtc);
            Assert.Equal("sales.mail.cancel", (await Assert.ThrowsAsync<BusinessRuleException>(() => s.OrderDocuments.CancelMailAsync(message, row.RowVersion))).Code);

            // PDF для скачивания — с правами пользователя.
            var pdf = await s.OrderDocuments.PdfAsync(SalesMailDocument.Order, order, SqlTestHost.PdfRenderer);
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf.Content, 0, 4));
        }

        // Кладовщик (без права «Продажи: заказы и оплаты» и цен) письмо не отправит.
        await using (var k = host.As(f.Storekeeper, f.Org.OrganizationId))
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => k.OrderDocuments.SendAsync(new SalesMailRequest(order, "a@shop.example", "Т", "Т",
                [new SalesMailDocumentRef(SalesMailDocument.Order, order)])));
            await Assert.ThrowsAsync<AccessDeniedException>(() => k.DocumentPrint.OrderAsync(order));
        }
    }

    [SqlFact]
    public async Task Documents_are_linked_and_unlinked_and_accounting_links_are_protected()
    {
        var f = await SetUpAsync();
        await using var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId);
        var order = await ConfirmedOrderAsync(s, f, 2);
        var another = await ConfirmedOrderAsync(s, f, 1);
        var ship = await s.Sales.CreateShipmentAsync(order);
        await s.Documents.PostAsync(ship, (await s.Documents.GetAsync(ship)).RowVersion);
        await s.Sales.CreatePaymentAsync(Day, f.Customer, order, 500m, "п/п 1");

        var panel = await s.OrderDocuments.PanelAsync(order);
        Assert.Contains(panel.Links, l => l.Kind == SalesLinkKind.StockDocument && l.DocumentId == ship && !l.Manual && l.Hint == SalesLinkHint.StockDocument);
        var payment = panel.Links.Single(l => l.Kind == SalesLinkKind.CustomerPayment);
        Assert.Equal(500m, payment.Amount);
        Assert.NotNull(payment.AllocationId);

        // Поиск по номеру и ручная связь с другим заказом; дубль, сам с собой и учётная связь — отказ.
        var anotherNumber = (await s.Sales.GetOrderAsync(another)).Number;
        var found = await s.OrderDocuments.SearchAsync(order, SalesLinkKind.SalesOrder, anotherNumber[^3..]);
        Assert.Contains(found, c => c.Id == another);
        Assert.DoesNotContain(found, c => c.Id == order);
        var link = await s.OrderDocuments.LinkAsync(order, SalesLinkKind.SalesOrder, another, "дозаказ");
        Assert.Equal("sales.link.exists", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.OrderDocuments.LinkAsync(order, SalesLinkKind.SalesOrder, another, null))).Code);
        Assert.Equal("sales.link.self", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.OrderDocuments.LinkAsync(order, SalesLinkKind.SalesOrder, order, null))).Code);
        Assert.Equal("sales.link.accounting", (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            s.OrderDocuments.LinkAsync(order, SalesLinkKind.StockDocument, ship, null))).Code);
        await Assert.ThrowsAsync<NotFoundException>(() => s.OrderDocuments.LinkAsync(order, SalesLinkKind.PurchaseOrder, 9_999_999, null));
        var manual = (await s.OrderDocuments.PanelAsync(order)).Links.Single(l => l.Manual);
        Assert.Equal((another, anotherNumber, "дозаказ", link), (manual.DocumentId, manual.Number, manual.Comment, manual.LinkId!.Value));

        // Отвязать ручную связь; снять разнесение оплаты — учётная связь, безопасная для снятия.
        await s.OrderDocuments.UnlinkAsync(link, manual.LinkRowVersion!);
        await s.Sales.RemovePaymentAllocationAsync(payment.AllocationId!.Value, payment.AllocationRowVersion!);
        panel = await s.OrderDocuments.PanelAsync(order);
        Assert.DoesNotContain(panel.Links, l => l.Manual);
        Assert.DoesNotContain(panel.Links, l => l.Kind == SalesLinkKind.CustomerPayment);
        // После отвязки можно связать снова; в журнале — связь и отвязка.
        await s.OrderDocuments.LinkAsync(order, SalesLinkKind.SalesOrder, another, null);
        await using var db = host.NewDb();
        Assert.Equal(2, await db.AuditEntries.CountAsync(a => a.OrganizationId == f.Org.OrganizationId && a.Action == AuditActions.SalesDocumentLinked));
        Assert.Equal(1, await db.AuditEntries.CountAsync(a => a.OrganizationId == f.Org.OrganizationId && a.Action == AuditActions.SalesDocumentUnlinked));

        // Кладовщик без права на заказы поставщику не свяжет и не найдёт их.
        await using var k = host.As(f.Storekeeper, f.Org.OrganizationId);
        await Assert.ThrowsAsync<AccessDeniedException>(() => k.OrderDocuments.LinkAsync(order, SalesLinkKind.SalesOrder, another, null));
    }

    [SqlFact]
    public async Task Foreign_organization_gets_not_found()
    {
        var f = await SetUpAsync();
        long order, invoice, message;
        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            invoice = await IssueInvoiceAsync(s, f, 1);
            order = (await s.Invoices.GetAsync(invoice)).OrderId;
            message = await s.OrderDocuments.SendAsync(new SalesMailRequest(order, "buh@shop.example", "Т", "Т",
                [new SalesMailDocumentRef(SalesMailDocument.Order, order)]));
        }

        var other = await CreateOrgAsync();
        long foreignOrder;
        await using (var o = host.As(other.OwnerUserId, other.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => o.OrderDocuments.PanelAsync(order));
            await Assert.ThrowsAsync<NotFoundException>(() => o.OrderDocuments.DraftAsync(order, SalesMailDocument.Order));
            await Assert.ThrowsAsync<NotFoundException>(() => o.OrderDocuments.SendAsync(new SalesMailRequest(order, "a@b.ru", "Т", "Т",
                [new SalesMailDocumentRef(SalesMailDocument.Order, order)])));
            await Assert.ThrowsAsync<NotFoundException>(() => o.OrderDocuments.RetryMailAsync(message, [1]));
            await Assert.ThrowsAsync<NotFoundException>(() => o.OrderDocuments.CustomerMailAsync(f.Customer));
            await Assert.ThrowsAsync<NotFoundException>(() => o.OrderDocuments.SetCustomerMailAsync(f.Customer, "a@b.ru", false, null));
            await Assert.ThrowsAsync<NotFoundException>(() => o.DocumentPrint.OrderAsync(order));
            await Assert.ThrowsAsync<NotFoundException>(() => o.DocumentPrint.DecorationsAsync(SalesMailDocument.Invoice, invoice));
            await Assert.ThrowsAsync<NotFoundException>(() => o.DocumentSettings.EntityAsync(f.Entity));
            await Assert.ThrowsAsync<NotFoundException>(() =>
                o.DocumentSettings.UploadImageAsync(f.Entity, SalesDocumentImageKind.Logo, "image/png", Png));
            await Assert.ThrowsAsync<NotFoundException>(() => o.OrderDocuments.PdfAsync(SalesMailDocument.Invoice, invoice, SqlTestHost.PdfRenderer));

            // Связать свой заказ с чужим документом нельзя: его «нет».
            var store = await o.Warehouses.CreateWarehouseAsync("Склад", null);
            var customer = await o.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Другой»", null, null, false, true, null));
            foreignOrder = await o.Sales.CreateOrderAsync(new SalesOrderHeader(Day, customer, store, null, null, true, null));
            await Assert.ThrowsAsync<NotFoundException>(() => o.OrderDocuments.LinkAsync(foreignOrder, SalesLinkKind.SalesOrder, order, null));
            Assert.Empty(await o.OrderDocuments.SearchAsync(foreignOrder, SalesLinkKind.SalesOrder, string.Empty));
        }

        await using (var s = host.As(f.Org.OwnerUserId, f.Org.OrganizationId))
        {
            await Assert.ThrowsAsync<NotFoundException>(() => s.OrderDocuments.LinkAsync(order, SalesLinkKind.SalesOrder, foreignOrder, null));
        }
    }

    private async Task<List<SalesMailMessage>> MailsAsync(Fixture f)
    {
        await using var db = host.NewDb();
        return await db.SalesMailMessages.AsNoTracking().Include(m => m.Attachments).Where(m => m.OrganizationId == f.Org.OrganizationId)
            .OrderBy(m => m.Id).ToListAsync();
    }

    private static async Task<long> ConfirmedOrderAsync(Services s, Fixture f, decimal quantity)
    {
        var order = await s.Sales.CreateOrderAsync(new SalesOrderHeader(Day, f.Customer, f.Store, null, null, true, null));
        await s.Sales.SetOrderLineAsync(order, f.Sweater, quantity, 1220m, 22m, (await s.Sales.GetOrderAsync(order)).RowVersion);
        await s.Sales.ConfirmOrderAsync(order, (await s.Sales.GetOrderAsync(order)).RowVersion);
        return order;
    }

    private static async Task<long> IssueInvoiceAsync(Services s, Fixture f, decimal quantity) =>
        await s.Invoices.CreateAsync(await ConfirmedOrderAsync(s, f, quantity), null, null);

    private sealed record Fixture(CreatedOrganization Org, long Store, long Sweater, long Customer, long Entity, long Manager, long Storekeeper);

    private async Task<Fixture> SetUpAsync()
    {
        var org = await CreateOrgAsync();
        long store, sweater, customer, entity, manager, storekeeper;
        await using (var s = host.As(org.OwnerUserId, org.OrganizationId))
        {
            var units = await s.Catalog.ListUnitsAsync();
            store = await s.Warehouses.CreateWarehouseAsync("Склад готовой продукции", null);
            sweater = await s.Catalog.CreateItemAsync(new ItemCommand("СВ-1", "Свитер", ItemType.Finished, units.Single(u => u.Symbol == "шт").Id, null));
            customer = await s.Counterparties.CreateAsync(new CounterpartyCommand("ООО «Магазин»", null, null, false, true, null));
            var e = (await s.LegalEntities.ListAsync()).Single();
            entity = e.Id;
            await s.LegalEntities.UpdateAsync(e.Id, e.Data with { LegalAddress = "г. Москва, ул. Тестовая, д. 1", DirectorName = "Иванов И. И.",
                AccountantName = "Петрова П. П." }, e.RowVersion);
            await s.LegalEntities.AddAccountAsync(e.Id, new LegalEntityAccountCommand("ПАО «Тестбанк»", "044525225", "40702810938000000001",
                "30101810400000000225"));

            // На складе 50 свитеров.
            var reason = await ReasonAsync(org.OrganizationId, StockOperationKind.Receipt);
            var receipt = await s.Documents.CreateAsync(StockOperationKind.Receipt, new StockDocumentHeader(store, null, null, reason, Day, null));
            await s.Documents.SetLineAsync(receipt, sweater, 50, (await s.Documents.GetAsync(receipt)).RowVersion);
            await s.Documents.PostAsync(receipt, (await s.Documents.GetAsync(receipt)).RowVersion);

            manager = (await s.Access.InviteAsync(new InviteUserCommand($"manager-{Guid.NewGuid():N}@test.local", "Менеджер", SystemRoles.SalesManager, null)))
                .UserId;
            storekeeper = (await s.Access.InviteAsync(new InviteUserCommand($"keeper-{Guid.NewGuid():N}@test.local", "Кладовщик", SystemRoles.SeniorStorekeeper,
                null, WarehouseId: store))).UserId;
        }

        await using (var db = host.NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == manager)).Activate();
            (await db.Users.SingleAsync(u => u.Id == storekeeper)).Activate();
            await db.SaveChangesAsync();
        }

        return new Fixture(org, store, sweater, customer, entity, manager, storekeeper);
    }

    private async Task<long?> ReasonAsync(long organizationId, StockOperationKind kind)
    {
        await using var db = host.NewDb();
        return await db.OperationReasons.Where(r => r.OrganizationId == organizationId && r.Kind == kind).Select(r => (long?)r.Id).FirstAsync();
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
