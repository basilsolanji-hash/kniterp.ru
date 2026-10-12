using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Crm;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Crm;

public sealed record CrmMailboxCredentials(string Host, int Port, string Login, string Password);

/// <summary>Новые письма папки «Входящие» после курсора: UIDVALIDITY папки и письма по возрастанию UID.</summary>
public sealed record CrmMailboxFetch(long UidValidity, IReadOnlyList<CrmInboundEmail> Emails);

/// <summary>
/// IMAP-клиент (D676): проверка входа и чтение новых писем. Реализация — в Infrastructure (MailKit): только TLS/STARTTLS, адрес сервера
/// не должен вести во внутреннюю сеть. Ошибка соединения или входа — CrmMailboxException с понятным текстом.
/// </summary>
public interface ICrmMailboxClient
{
    Task TestAsync(CrmMailboxCredentials credentials, CancellationToken ct = default);

    /// <param name="max">Не больше писем за раз (остальные — при следующей проверке).</param>
    Task<CrmMailboxFetch> FetchAsync(CrmMailboxCredentials credentials, long? uidValidity, long? lastUid, int max, CancellationToken ct = default);
}

public sealed class CrmMailboxException(string message) : Exception(message);

public sealed record CrmMailboxStatusDto(bool Connected, string? Host, int Port, string? Login, bool CreateLeads, string? ConnectedBy, DateTime? LastCheckAtUtc,
    string? LastError);

/// <summary>
/// Входящая почта CRM (D676): ящик организации (например, sales@) подключает crm.settings; раз в 3 минуты новые письма попадают в ленту
/// контакта, сделки или лида по адресу отправителя, незнакомые — новыми лидами (можно выключить). Письма читаются с правами подключившего:
/// отозвали права — чтение останавливается. Свои письма и автоответы пропускаются.
/// </summary>
public sealed class CrmMailboxService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, ICrmMailboxClient client)
{
    public const int CheckEveryMinutes = 3;
    public const int BatchSize = 50;

    public async Task<CrmMailboxStatusDto> StatusAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmView, ct);
        var settings = ctx.Permissions.Has(Permissions.CrmSettings);
        var m = await (from x in db.CrmMailboxes.AsNoTracking().Where(x => x.OrganizationId == ctx.OrganizationId)
                       join u in db.Users.AsNoTracking() on x.ConnectedByUserId equals u.Id
                       select new { x.Password, x.Host, x.Port, x.Login, x.CreateLeads, u.DisplayName, x.LastCheckAtUtc, x.LastError }).SingleOrDefaultAsync(ct);
        return m is not { Password: not null }
            ? new CrmMailboxStatusDto(false, null, 993, null, true, null, null, null)
            : new CrmMailboxStatusDto(true, m.Host, m.Port, m.Login, m.CreateLeads, m.DisplayName, m.LastCheckAtUtc, settings ? m.LastError : null);
    }

    public async Task ConnectAsync(string? host, int port, string? login, string? password, bool createLeads, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmSettings, ct);
        await guard.DemandAsync(Permissions.CrmEdit, ct);
        var box = await db.CrmMailboxes.SingleOrDefaultAsync(x => x.OrganizationId == ctx.OrganizationId, ct);
        var before = box is { IsConnected: true } ? $"{box.Login} @ {box.Host}" : null;
        // Проверка — на отдельном объекте: неверные реквизиты не портят сохранённое подключение и не оставляют в контексте недописанную строку.
        var probe = CrmMailbox.Create(ctx.OrganizationId);
        probe.Connect(host, port, login, password, createLeads, ctx.UserId, clock.UtcNow);
        try
        {
            await client.TestAsync(new CrmMailboxCredentials(probe.Host, probe.Port, probe.Login, probe.Password!), ct);
        }
        catch (CrmMailboxException ex)
        {
            throw new BusinessRuleException("crm.mailbox.connect", ex.Message);
        }

        if (box is null)
        {
            box = CrmMailbox.Create(ctx.OrganizationId);
            db.CrmMailboxes.Add(box);
        }

        box.Connect(host, port, login, password, createLeads, ctx.UserId, clock.UtcNow);

        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CrmSettingsChanged, nameof(CrmMailbox),
            ctx.OrganizationId.ToString(), before, $"{box.Login} @ {box.Host}, новые лиды: {(createLeads ? "да" : "нет")}", "Входящая почта", currentUser.CorrelationId));
        await db.SaveOrConflictAsync(ct);
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmSettings, ct);
        var box = await db.CrmMailboxes.SingleOrDefaultAsync(x => x.OrganizationId == ctx.OrganizationId, ct);
        if (box is not { IsConnected: true })
        {
            return;
        }

        box.Disconnect();
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.CrmSettingsChanged, nameof(CrmMailbox),
            ctx.OrganizationId.ToString(), $"{box.Login} @ {box.Host}", "отключён", "Входящая почта", currentUser.CorrelationId));
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Проверить сейчас (кнопка в настройках): без ожидания трёх минут. Возвращает число новых писем.</summary>
    public async Task<int> CheckNowAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmSettings, ct);
        var id = await db.CrmMailboxes.AsNoTracking().Where(x => x.OrganizationId == ctx.OrganizationId && x.Password != null).Select(x => (long?)x.Id)
                     .SingleOrDefaultAsync(ct) ?? throw new BusinessRuleException("crm.mailbox.not_connected", "Почтовый ящик не подключён.");
        return await PollAsync(db, clock, client, id, ct);
    }

    /// <summary>Фоновая проверка всех ящиков, у которых подошло время (D676).</summary>
    public static async Task<int> PollDueAsync(IKnitErpDbContext db, IClock clock, ICrmMailboxClient client, CancellationToken ct = default)
    {
        var border = clock.UtcNow.AddMinutes(-CheckEveryMinutes);
        var ids = await db.CrmMailboxes.AsNoTracking().Where(x => x.Password != null && (x.LastCheckAtUtc == null || x.LastCheckAtUtc <= border))
            .OrderBy(x => x.LastCheckAtUtc).Select(x => x.Id).Take(20).ToListAsync(ct);
        var total = 0;
        foreach (var id in ids)
        {
            total += await PollAsync(db, clock, client, id, ct);
        }

        return total;
    }

    private static async Task<int> PollAsync(IKnitErpDbContext db, IClock clock, ICrmMailboxClient client, long id, CancellationToken ct)
    {
        var box = await db.CrmMailboxes.SingleAsync(x => x.Id == id, ct);
        var user = new MailboxUser(box.ConnectedByUserId, box.OrganizationId);
        var intake = new CrmIntakeService(db, new AccessGuard(db, user, clock), user, clock);
        CrmMailboxFetch fetch;
        try
        {
            fetch = await client.FetchAsync(new CrmMailboxCredentials(box.Host, box.Port, box.Login, box.Password!), box.UidValidity, box.LastUid, BatchSize, ct);
        }
        catch (CrmMailboxException ex)
        {
            box.Failed(ex.Message, clock.UtcNow);
            await db.SaveChangesAsync(ct);
            return 0;
        }

        var added = 0;
        long? last = null;
        foreach (var email in fetch.Emails.OrderBy(e => e.Uid))
        {
            last = email.Uid;
            if (string.Equals(CrmText.NormalizeEmail(email.FromEmail), CrmText.NormalizeEmail(box.Login), StringComparison.OrdinalIgnoreCase))
            {
                continue; // своё письмо (копия отправленного)
            }

            try
            {
                if (await intake.RegisterEmailAsync(email, box.CreateLeads, ct) is { Duplicate: false })
                {
                    added++;
                }
            }
            catch (AccessDeniedException)
            {
                box.Failed("У подключившего ящик больше нет прав на CRM — переподключите ящик.", clock.UtcNow);
                await db.SaveChangesAsync(ct);
                return added;
            }
            catch (BusinessRuleException)
            {
                // Письмо, которое нельзя разобрать (пустой адрес, слишком длинные поля), не останавливает чтение остальных.
            }
        }

        box.Checked(fetch.UidValidity, last, clock.UtcNow);
        await db.SaveChangesAsync(ct);
        return added;
    }

    private sealed class MailboxUser(long userId, long organizationId) : ICurrentUser
    {
        public long? UserId => userId;
        public long? OrganizationId => organizationId;
        public string? CorrelationId => "crm-mailbox";
    }
}
