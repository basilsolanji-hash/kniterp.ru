using KnitErp.Application.Sales;
using KnitErp.Domain.Common;
using KnitErp.Domain.Sales;

namespace KnitErp.UnitTests;

/// <summary>D1120–D1125: строка QR платежа (ГОСТ Р 56042-2014), подстановки писем, адреса, изображения и связи документов.</summary>
public sealed class SalesDocumentsRulesTests
{
    private static readonly PaymentQrData Full = new("ООО «Ромашка | Трикотаж»", "4070 2810 9380 0000 0001", "ПАО «Тестбанк»", "044525225",
        "30101810400000000225", "7707083893", "773601001", "Оплата по счёту № СЧ-000001 от 09.10.2026, в т.ч. НДС 11000.00", 61_000.5m);

    [Fact]
    public void Payment_qr_has_gost_header_required_fields_and_sum_in_kopecks()
    {
        var qr = PaymentQr.Build(Full);
        Assert.Equal(
            "ST00012|Name=ООО «Ромашка Трикотаж»|PersonalAcc=40702810938000000001|BankName=ПАО «Тестбанк»|BIC=044525225"
            + "|CorrespAcc=30101810400000000225|PayeeINN=7707083893|KPP=773601001"
            + "|Purpose=Оплата по счёту № СЧ-000001 от 09.10.2026, в т.ч. НДС 11000.00|Sum=6100050",
            qr);
    }

    [Fact]
    public void Payment_qr_is_not_built_without_required_requisites()
    {
        Assert.Null(PaymentQr.Build(Full with { PersonalAcc = null }));
        Assert.Null(PaymentQr.Build(Full with { Bic = "12345" }));
        Assert.Null(PaymentQr.Build(Full with { CorrespAcc = "" }));
        Assert.Null(PaymentQr.Build(Full with { BankName = " " }));
        Assert.Null(PaymentQr.Build(Full with { Sum = 0 }));
        // Необязательные реквизиты (КПП у ИП) просто не попадают в строку.
        Assert.DoesNotContain("KPP=", PaymentQr.Build(Full with { Kpp = null }));
    }

    [Fact]
    public void Mail_template_placeholders_are_substituted()
    {
        var text = SalesMailText.Render("Заказ {Номер} от {Дата}: {Этап}, {Сумма} — {Покупатель} / {Организация}", "ЗК-000007", new DateOnly(2026, 10, 9),
            "ООО «Магазин»", 1234.5m, "RUB", "Собран", "ООО «Фабрика»");
        Assert.Equal("Заказ ЗК-000007 от 09.10.2026: Собран, 1 234,50 RUB — ООО «Магазин» / ООО «Фабрика»", text.Replace(' ', ' ').Replace(' ', ' '));
    }

    [Fact]
    public void Recipients_are_validated_and_normalized()
    {
        Assert.Equal("buh@shop.example, boss@shop.example", SalesMailText.NormalizeRecipients(" Buh@Shop.example; boss@shop.example,buh@shop.example "));
        Assert.Null(SalesMailText.NormalizeRecipients("  "));
        Assert.Equal("sales.mail.email", Assert.Throws<BusinessRuleException>(() => SalesMailText.NormalizeRecipients("not-an-email")).Code);
        Assert.Equal("sales.mail.email", Assert.Throws<BusinessRuleException>(() => SalesMailText.NormalizeRecipients("a@b.c<script>")).Code);
        Assert.Equal("sales.mail.recipients", Assert.Throws<BusinessRuleException>(() =>
            SalesMailText.NormalizeRecipients("a@x.ru,b@x.ru,c@x.ru,d@x.ru,e@x.ru,f@x.ru")).Code);
    }

    [Fact]
    public void Document_image_is_checked_by_signature_and_size()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0];
        var image = SalesDocumentImage.Create(1, 1, SalesDocumentImageKind.Stamp, "image/png", png, DateTime.UtcNow, 1);
        Assert.Equal(("image/png", 10), (image.ContentType, image.SizeBytes));
        // HTML со скриптом под видом PNG, PDF вместо картинки, слишком большой файл — отказ.
        Assert.Equal("sales.document_image.type", Assert.Throws<BusinessRuleException>(() =>
            SalesDocumentImage.Create(1, 1, SalesDocumentImageKind.Stamp, "image/png", "<svg onload=alert(1)>"u8.ToArray(), DateTime.UtcNow, 1)).Code);
        Assert.Equal("sales.document_image.type", Assert.Throws<BusinessRuleException>(() =>
            SalesDocumentImage.Create(1, 1, SalesDocumentImageKind.Logo, "application/pdf", "%PDF-1.7"u8.ToArray(), DateTime.UtcNow, 1)).Code);
        Assert.Equal("sales.document_image.size", Assert.Throws<BusinessRuleException>(() =>
            SalesDocumentImage.Create(1, 1, SalesDocumentImageKind.Logo, "image/png", [.. png, .. new byte[SalesDocumentImage.MaxBytes]], DateTime.UtcNow, 1)).Code);
    }

    [Fact]
    public void Mail_message_retries_with_backoff_then_fails()
    {
        var now = new DateTime(2026, 10, 9, 5, 0, 0, DateTimeKind.Utc);
        var m = SalesMailMessage.Create(1, SalesMailKind.Order, 1, 1, "a@b.ru", "Тема", "Текст", false, null, 1, now, [(SalesMailDocument.Order, 1L)]);
        m.MarkFailed("нет связи", now, permanent: false);
        Assert.Equal((SalesMailStatus.Queued, now.AddMinutes(1)), (m.Status, m.NextAttemptAtUtc));
        for (var i = 0; i < SalesMailMessage.MaxAttempts - 1; i++)
        {
            m.MarkFailed("нет связи", now, permanent: false);
        }

        Assert.Equal(SalesMailStatus.Failed, m.Status);
        m.Retry(now);
        Assert.Equal((SalesMailStatus.Queued, 0), (m.Status, m.Attempts));
        m.MarkSent(now);
        Assert.Equal("sales.mail.cancel", Assert.Throws<BusinessRuleException>(m.Cancel).Code);
    }

    [Fact]
    public void Link_rules_forbid_self_and_automatic_kinds()
    {
        Assert.Equal("sales.link.self", Assert.Throws<BusinessRuleException>(() =>
            SalesDocumentLink.Create(1, 5, SalesLinkKind.SalesOrder, 5, null, 1, DateTime.UtcNow)).Code);
        Assert.Equal("sales.link.kind", Assert.Throws<BusinessRuleException>(() =>
            SalesDocumentLink.Create(1, 5, SalesLinkKind.CrmDeal, 7, null, 1, DateTime.UtcNow)).Code);
        var link = SalesDocumentLink.Create(1, 5, SalesLinkKind.PurchaseOrder, 7, "  под заказ  ", 1, DateTime.UtcNow);
        Assert.Equal("под заказ", link.Comment);
        link.Remove(1, DateTime.UtcNow);
        Assert.Equal("sales.link.removed", Assert.Throws<BusinessRuleException>(() => link.Remove(1, DateTime.UtcNow)).Code);
    }
}
