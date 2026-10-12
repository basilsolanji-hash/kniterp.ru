using KnitErp.Domain.Common;
using KnitErp.Domain.Crm;

namespace KnitErp.UnitTests;

/// <summary>D674–D676: шаблоны писем, HTML-версия письма, расписание рассылки, адрес почтового ящика.</summary>
public sealed class CrmMailRulesTests
{
    [Fact]
    public void Template_renders_known_variables_and_empties_missing_values()
    {
        var values = new Dictionary<string, string?> { ["Имя"] = "Анна", ["Компания"] = null };
        Assert.Equal("Анна из  — {{Другое}}", CrmEmailTemplate.Render("{{Имя}} из {{ Компания }} — {{Другое}}", values));
        Assert.True(CrmEmailTemplate.HasVariables("Здравствуйте, {{Имя}}"));
        Assert.False(CrmEmailTemplate.HasVariables("Здравствуйте"));
    }

    [Fact]
    public void Template_rejects_unknown_variable()
    {
        var ex = Assert.Throws<BusinessRuleException>(() => CrmEmailTemplate.Create(1, "Т", "Тема {{Имя}}", "Текст {{Отчество}}", 1, DateTime.UtcNow));
        Assert.Equal("crm.template.variable", ex.Code);
    }

    [Fact]
    public void Html_escapes_user_text_and_links_only_http()
    {
        var html = CrmEmailHtml.FromText("<script>alert(1)</script>\nСм. https://example.org/a?b=1&c=2.\n\njavascript:alert(1)", "Отписаться: https://erp.test/x");
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("<a href=\"https://example.org/a?b=1&amp;c=2\"", html);
        Assert.DoesNotContain("href=\"javascript", html);
        Assert.Contains("<br>", html);
        Assert.Contains("href=\"https://erp.test/x\"", html);
    }

    [Fact]
    public void Mailing_schedule_window_and_queue_rules()
    {
        var now = new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);
        var m = CrmMailing.Create(1, "Тема", "Текст", null, 1, now);
        Assert.Throws<BusinessRuleException>(() => m.Schedule(now.AddMinutes(2), 1, now));
        Assert.Throws<BusinessRuleException>(() => m.Schedule(now.AddDays(91), 1, now));
        m.Schedule(now.AddHours(1), 1, now);
        Assert.Equal("crm.mailing.scheduled", Assert.Throws<BusinessRuleException>(() => m.Update("x", "y", null)).Code);
        Assert.Equal("crm.mailing.empty", Assert.Throws<BusinessRuleException>(() => m.Queue(1)).Code);
        m.AddRecipient(10, "a@example.org");
        m.Queue(1);
        Assert.Equal(CrmMailingStatus.Sending, m.Status);
        Assert.Throws<BusinessRuleException>(() => m.AddRecipient(11, "b@example.org"));
        m.Recipients[0].SetDelivered(true, now);
        m.Complete(now);
        Assert.Equal((CrmMailingStatus.Sent, now), (m.Status, m.SentAtUtc));
    }

    [Theory]
    [InlineData("imap.example.org", true)]
    [InlineData("mail-1.example.org", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("localhost", false)]
    [InlineData("imap.example.org:993", false)]
    [InlineData("imap..example.org", false)]
    [InlineData("http://imap.example.org", false)]
    public void Mailbox_host_is_a_plain_host_name(string host, bool valid) => Assert.Equal(valid, CrmMailbox.ValidHost(host));

    [Fact]
    public void Mailbox_cursor_resets_when_folder_or_account_changes()
    {
        var box = CrmMailbox.Create(1);
        box.Connect("imap.example.org", 993, "sales@example.org", "p", true, 1, DateTime.UtcNow);
        box.Checked(5, 100, DateTime.UtcNow);
        box.Checked(5, null, DateTime.UtcNow);
        Assert.Equal((5L, 100L), (box.UidValidity!.Value, box.LastUid!.Value));
        box.Checked(6, null, DateTime.UtcNow);
        Assert.Null(box.LastUid);
        box.Checked(6, 3, DateTime.UtcNow);
        box.Connect("imap.example.org", 993, "other@example.org", "p", true, 1, DateTime.UtcNow);
        Assert.Null(box.LastUid);
        Assert.Throws<BusinessRuleException>(() => box.Connect("imap.example.org", 110, "x", "p", true, 1, DateTime.UtcNow));
        box.Disconnect();
        Assert.False(box.IsConnected);
    }
}
