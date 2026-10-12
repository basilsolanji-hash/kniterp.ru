using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Hr;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Hr;

/// <summary>Документ к подписанию или напечатанный: текст с данными сотрудника и отметка о подписи, если есть.</summary>
public sealed record AgreementDocumentDto(AgreementText Text, AgreementSignatureDto? Signature)
{
    public string Title => Text.Title;
}

/// <summary>Отметка о подписи. TextMatches — совпадает ли сегодняшний текст с подписанным (данные сотрудника могли измениться).</summary>
public sealed record AgreementSignatureDto(AgreementMethod Method, DateTime SignedAtUtc, string Version, string TextHash, bool TextMatches)
{
    public string MethodName => Method == AgreementMethod.Electronic ? "простой электронной подписью" : "на бумаге собственноручно";
}

/// <summary>Состояние документов сотрудника для карточки: подписан ли каждый в действующей редакции.</summary>
public sealed record AgreementStatusDto(AgreementKind Kind, string Title, AgreementSignatureDto? Signature, bool CanMarkPaper);

/// <summary>
/// Документы перед доступом (D227): согласие на обработку ПДн, правила работы в системе с соглашением о простой электронной подписи и
/// обязательство о конфиденциальности. Тексты заполняются данными сотрудника и организации; сотрудник со своей учётной записью подписывает
/// их паролем при первом входе (пока не подпишет — вместо страниц видит документы), кадровик может отметить бумажную подпись и напечатать
/// документ. Подпись хранит версию и SHA-256 текста; таблица только на вставку.
/// </summary>
public sealed class EmployeeAgreementService(
    IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, IPasswordHasher<UserAccount> hasher)
{
    /// <summary>
    /// Документы, которые текущий пользователь должен подписать перед работой; пусто — подписывать нечего (нет карточки сотрудника,
    /// правило выключено или всё подписано в действующей редакции).
    /// </summary>
    public async Task<IReadOnlyList<AgreementDocumentDto>> PendingAsync(CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        var employeeId = await db.Employees.AsNoTracking().Where(e => e.OrganizationId == ctx.OrganizationId && e.UserId == ctx.UserId)
            .Select(e => (long?)e.Id).FirstOrDefaultAsync(ct);
        if (employeeId is null || !(await HrSettingsService.ForOrganizationAsync(db, ctx.OrganizationId, ct)).RequireAgreements)
        {
            return [];
        }

        var pending = await PendingKindsAsync(ctx.OrganizationId, employeeId.Value, ct);
        if (pending.Count == 0)
        {
            return [];
        }

        var data = await DataAsync(ctx.OrganizationId, employeeId.Value, await TodayAsync(ctx.OrganizationId, ct), ct);
        return pending.Select(k => new AgreementDocumentDto(AgreementTemplates.Render(k, data), null)).ToList();
    }

    /// <summary>Подписать все неподписанные документы простой электронной подписью: подтверждение — пароль учётной записи.</summary>
    public async Task<int> SignAsync(string? password, CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        var employee = await db.Employees.AsNoTracking().Where(e => e.OrganizationId == ctx.OrganizationId && e.UserId == ctx.UserId)
                           .Select(e => new { e.Id, e.LastName, e.FirstName, e.MiddleName }).FirstOrDefaultAsync(ct)
                       ?? throw new NotFoundException("Карточка сотрудника");
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == ctx.UserId, ct);
        if (user.PasswordHash is null || hasher.VerifyHashedPassword(user, user.PasswordHash, password ?? string.Empty) == PasswordVerificationResult.Failed)
        {
            db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.AgreementsSigned, nameof(EmployeeAgreement),
                employee.Id.ToString(), null, null, "неверный пароль — документы не подписаны", currentUser.CorrelationId));
            await db.SaveChangesAsync(ct);
            throw new BusinessRuleException("hr.agreement.password", "Неверный пароль: документы не подписаны.");
        }

        await using var tx = await db.BeginTransactionAsync(ct);
        await db.LockAsync($"kniterp.hr.agreements.{ctx.OrganizationId}.{employee.Id}", ct);
        var pending = await PendingKindsAsync(ctx.OrganizationId, employee.Id, ct);
        var data = await DataAsync(ctx.OrganizationId, employee.Id, await TodayAsync(ctx.OrganizationId, ct), ct);
        var signed = pending.Select(k => EmployeeAgreement.Sign(ctx.OrganizationId, employee.Id, AgreementTemplates.Render(k, data), AgreementMethod.Electronic,
            ctx.UserId, clock.UtcNow)).ToList();
        db.EmployeeAgreements.AddRange(signed);
        if (signed.Count > 0)
        {
            db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.AgreementsSigned, nameof(EmployeeAgreement),
                employee.Id.ToString(), null, string.Join("; ", signed.Select(s => $"{AgreementTemplates.Title(s.Kind)} ({s.Version}, {s.TextHash[..12]})")),
                "простая электронная подпись", currentUser.CorrelationId));
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return signed.Count;
    }

    /// <summary>Документы сотрудника для карточки. Видны тем, кто читает его ПДн или ведёт сотрудников.</summary>
    public async Task<IReadOnlyList<AgreementStatusDto>> StatusAsync(long employeeId, CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        await DemandCardAsync(ctx, employeeId, ct);
        var data = await DataAsync(ctx.OrganizationId, employeeId, await TodayAsync(ctx.OrganizationId, ct), ct);
        var signatures = await LatestAsync(ctx.OrganizationId, employeeId, ct);
        var canMark = ctx.Permissions.Has(Permissions.PersonalDataEdit);
        var result = new List<AgreementStatusDto>();
        foreach (var k in AgreementTemplates.All)
        {
            result.Add(new AgreementStatusDto(k, AgreementTemplates.Title(k), await SignatureAsync(ctx.OrganizationId, employeeId, k, signatures, data, ct), canMark));
        }

        return result;
    }

    /// <summary>Документ для печати с данными сотрудника (паспорт, адрес) — нужно право на ПДн; печать — в журнал.</summary>
    public async Task<AgreementDocumentDto> PrintAsync(long employeeId, AgreementKind kind, CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        var employee = await db.Employees.AsNoTracking().SingleOrDefaultAsync(e => e.Id == employeeId && e.OrganizationId == ctx.OrganizationId, ct)
                       ?? throw new NotFoundException("Сотрудник");
        if (!ctx.Permissions.Has(Permissions.PersonalDataView) && !(employee.UserId == ctx.UserId))
        {
            await guard.DenyAsync(ctx, Permissions.PersonalDataView, ct);
        }

        if (!Enum.IsDefined(kind))
        {
            throw new NotFoundException("Документ");
        }

        var signatures = await LatestAsync(ctx.OrganizationId, employeeId, ct);
        var date = signatures.TryGetValue(kind, out var s)
            ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(s.SignedAtUtc, await TimeZoneAsync(ctx.OrganizationId, ct)))
            : await TodayAsync(ctx.OrganizationId, ct);
        var data = await DataAsync(ctx.OrganizationId, employeeId, date, ct);
        var text = AgreementTemplates.Render(kind, data);
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.AgreementPrinted, nameof(EmployeeAgreement),
            employeeId.ToString(), null, AgreementTemplates.Title(kind), employee.FullName, currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
        return new AgreementDocumentDto(text, s is null ? null
            : new AgreementSignatureDto(s.Method, s.SignedAtUtc, s.Version, s.TextHash, s.Version == text.Version && s.TextHash == text.Hash));
    }

    /// <summary>Отметка кадровика: документ подписан на бумаге собственноручно (скан — в документах сотрудника).</summary>
    public async Task MarkPaperAsync(long employeeId, AgreementKind kind, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PersonalDataEdit, ct);
        var employee = await db.Employees.AsNoTracking().SingleOrDefaultAsync(e => e.Id == employeeId && e.OrganizationId == ctx.OrganizationId, ct)
                       ?? throw new NotFoundException("Сотрудник");
        var text = AgreementTemplates.Render(kind, await DataAsync(ctx.OrganizationId, employeeId, await TodayAsync(ctx.OrganizationId, ct), ct));
        db.EmployeeAgreements.Add(EmployeeAgreement.Sign(ctx.OrganizationId, employeeId, text, AgreementMethod.Paper, ctx.UserId, clock.UtcNow));
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.AgreementsSigned, nameof(EmployeeAgreement),
            employeeId.ToString(), null, $"{text.Title} ({text.Version})", $"на бумаге, отметил кадровик; {employee.FullName}", currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Подписаны ли все документы в действующей редакции — для заполненности карточки (D226).</summary>
    internal static async Task<HashSet<long>> FullySignedAsync(IKnitErpDbContext db, long organizationId, IReadOnlyCollection<long> employeeIds, CancellationToken ct)
    {
        var rows = await db.EmployeeAgreements.AsNoTracking()
            .Where(a => a.OrganizationId == organizationId && employeeIds.Contains(a.EmployeeId) && a.Version == AgreementTemplates.Version)
            .Select(a => new { a.EmployeeId, a.Kind }).Distinct().ToListAsync(ct);
        return rows.GroupBy(r => r.EmployeeId).Where(g => g.Count() == AgreementTemplates.All.Count).Select(g => g.Key).ToHashSet();
    }

    private async Task<List<AgreementKind>> PendingKindsAsync(long org, long employeeId, CancellationToken ct)
    {
        var signed = await db.EmployeeAgreements.AsNoTracking()
            .Where(a => a.OrganizationId == org && a.EmployeeId == employeeId && a.Version == AgreementTemplates.Version)
            .Select(a => a.Kind).Distinct().ToListAsync(ct);
        return AgreementTemplates.All.Where(k => !signed.Contains(k)).ToList();
    }

    private async Task<Dictionary<AgreementKind, EmployeeAgreement>> LatestAsync(long org, long employeeId, CancellationToken ct) =>
        (await db.EmployeeAgreements.AsNoTracking().Where(a => a.OrganizationId == org && a.EmployeeId == employeeId).ToListAsync(ct))
        .GroupBy(a => a.Kind).ToDictionary(g => g.Key, g => g.MaxBy(a => a.Id)!);

    private async Task<AgreementSignatureDto?> SignatureAsync(long org, long employeeId, AgreementKind kind, Dictionary<AgreementKind, EmployeeAgreement> latest,
        AgreementData today, CancellationToken ct)
    {
        if (!latest.TryGetValue(kind, out var s))
        {
            return null;
        }

        var signedOn = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(s.SignedAtUtc, await TimeZoneAsync(org, ct)));
        var text = AgreementTemplates.Render(kind, today with { Date = signedOn });
        return new AgreementSignatureDto(s.Method, s.SignedAtUtc, s.Version, s.TextHash, s.Version == text.Version && s.TextHash == text.Hash);
    }

    private async Task DemandCardAsync(AccessContext ctx, long employeeId, CancellationToken ct)
    {
        var employee = await db.Employees.AsNoTracking().SingleOrDefaultAsync(e => e.Id == employeeId && e.OrganizationId == ctx.OrganizationId, ct)
                       ?? throw new NotFoundException("Сотрудник");
        if (!ctx.Permissions.Has(Permissions.PersonalDataView) && !ctx.Permissions.Has(Permissions.EmployeeEdit) && employee.UserId != ctx.UserId)
        {
            await guard.DenyAsync(ctx, Permissions.PersonalDataView, ct);
        }
    }

    private async Task<AgreementData> DataAsync(long org, long employeeId, DateOnly date, CancellationToken ct)
    {
        var o = await db.Organizations.AsNoTracking().Where(x => x.Id == org).Select(x => new { x.FullName, x.Inn, x.ActualAddress }).SingleAsync(ct);
        var legal = await db.LegalEntities.AsNoTracking().Where(l => l.OrganizationId == org && l.IsDefault).Select(l => l.LegalAddress).FirstOrDefaultAsync(ct);
        var e = await (from x in db.Employees.AsNoTracking()
                       join p in db.Positions.AsNoTracking() on x.PositionId equals p.Id
                       where x.Id == employeeId && x.OrganizationId == org
                       select new { x.LastName, x.FirstName, x.MiddleName, x.PersonnelNumber, Position = p.Name }).SingleAsync(ct);
        var pd = await db.EmployeePersonalData.AsNoTracking().SingleOrDefaultAsync(p => p.OrganizationId == org && p.EmployeeId == employeeId, ct);
        var name = e.MiddleName is null ? $"{e.LastName} {e.FirstName}" : $"{e.LastName} {e.FirstName} {e.MiddleName}";
        return new AgreementData(o.FullName, o.Inn, legal ?? o.ActualAddress, name, e.PersonnelNumber, e.Position, pd?.PassportNumber, pd?.PassportIssuer,
            pd?.PassportIssuedOn, pd?.RegistrationAddress, date);
    }

    private async Task<string> TimeZoneAsync(long org, CancellationToken ct) =>
        await db.Organizations.AsNoTracking().Where(o => o.Id == org).Select(o => o.TimeZoneId).SingleAsync(ct);

    private async Task<DateOnly> TodayAsync(long org, CancellationToken ct) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(clock.UtcNow, await TimeZoneAsync(org, ct)));
}
