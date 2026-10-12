using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Hr;
using KnitErp.Domain.Structure;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Hr;

public sealed record PersonalDataDto(
    long EmployeeId,
    string FullName,
    PersonalDataValues Values,
    DateOnly? BirthDate,
    DateOnly? ConsentGivenOn,
    DateOnly? ConsentValidUntil,
    DateOnly? ConsentRevokedOn,
    ConsentState Consent,
    IReadOnlyList<ConsentScanDto> Scans,
    bool CanEdit,
    byte[]? RowVersion)
{
    public string ConsentName => EmployeePersonalData.ConsentName(Consent);

    /// <summary>Без действующего согласия — предупреждение на карточке (D201).</summary>
    public bool ConsentWarning => Consent != ConsentState.Valid;
}

public sealed record ConsentScanDto(long Id, string FileName, int SizeBytes, DateTime UploadedAtUtc, EmployeeDocumentKind Kind = EmployeeDocumentKind.Consent)
{
    public string KindName => EmployeeDocumentKinds.Name(Kind);
}

/// <summary>Заполненность карточек списка (D226): по сотруднику и сводка «чего не хватает» по всем видимым.</summary>
public sealed record CompletenessListDto(IReadOnlyDictionary<long, EmployeeCompleteness> Employees)
{
    public int Count(string item) => Employees.Values.Count(c => c.Missing.Contains(item));
}

/// <summary>Состояние согласия без самих персональных данных — для карточки сотрудника, чтения ПДн не требует.</summary>
public sealed record ConsentSummaryDto(ConsentState State, DateOnly? ValidUntil, bool CanViewPersonalData, bool CanEditPersonalData)
{
    public string Name => EmployeePersonalData.ConsentName(State);
}

/// <summary>Данные для печати личной карточки Т-2.</summary>
public sealed record PersonalCardT2Dto(
    string Organization,
    string? Okpo,
    string PersonnelNumber,
    string LastName,
    string FirstName,
    string? MiddleName,
    DateOnly? BirthDate,
    DateOnly HiredOn,
    DateOnly? DismissedOn,
    string Department,
    string Position,
    PersonalDataValues Values,
    DateOnly PrintedOn);

/// <summary>
/// Персональные данные сотрудников (Т-2, 152-ФЗ; D200, D201). Чтение — право «Персональные данные сотрудников»
/// (своя карточка — уровень «С»), изменение — отдельное право. Каждое чтение, печать и просмотр скана пишутся
/// в журнал аудита: кто и когда смотрел. В журнал не попадают сами значения — только названия полей.
/// </summary>
public sealed class PersonalDataService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    /// <summary>Состояние согласия для карточки: видно всем, кто видит карточку, — это не персональные данные.</summary>
    public async Task<ConsentSummaryDto> SummaryAsync(long employeeId, CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        var employee = await LoadEmployeeAsync(ctx, employeeId, forView: true, ct);
        var pd = await db.EmployeePersonalData.AsNoTracking()
            .Where(p => p.OrganizationId == ctx.OrganizationId && p.EmployeeId == employeeId)
            .Select(p => new { p.ConsentGivenOn, p.ConsentValidUntil, p.ConsentRevokedOn }).SingleOrDefaultAsync(ct);
        var today = await TodayAsync(ctx, ct);
        var state = EmployeePersonalData.StateOf(pd?.ConsentGivenOn, pd?.ConsentValidUntil, pd?.ConsentRevokedOn, today);
        return new ConsentSummaryDto(state, pd?.ConsentValidUntil, CanRead(ctx, employee), ctx.Permissions.Has(Permissions.PersonalDataEdit));
    }

    /// <summary>Чтение персональных данных. Пишет в журнал «кто смотрел» на каждый вызов.</summary>
    public async Task<PersonalDataDto> GetAsync(long employeeId, CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        var employee = await LoadEmployeeAsync(ctx, employeeId, forView: true, ct);
        if (!CanRead(ctx, employee))
        {
            await guard.DenyAsync(ctx, Permissions.PersonalDataView, ct);
        }

        var pd = await db.EmployeePersonalData.AsNoTracking()
            .SingleOrDefaultAsync(p => p.OrganizationId == ctx.OrganizationId && p.EmployeeId == employeeId, ct);
        var scans = await db.ConsentScans.AsNoTracking()
            .Where(s => s.OrganizationId == ctx.OrganizationId && s.EmployeeId == employeeId)
            .OrderByDescending(s => s.Id)
            .Select(s => new ConsentScanDto(s.Id, s.FileName, s.SizeBytes, s.UploadedAtUtc, s.Kind)).ToListAsync(ct);
        var today = await TodayAsync(ctx, ct);
        Audit(ctx, AuditActions.PersonalDataViewed, employeeId, null, employee.FullName, null);
        await db.SaveChangesAsync(ct);
        return new PersonalDataDto(employeeId, employee.FullName, pd is null ? new PersonalDataValues() : ToValues(pd), employee.BirthDate,
            pd?.ConsentGivenOn, pd?.ConsentValidUntil, pd?.ConsentRevokedOn,
            pd?.ConsentOn(today) ?? ConsentState.Missing, scans, ctx.Permissions.Has(Permissions.PersonalDataEdit), pd?.RowVersion);
    }

    /// <summary>Сохранение персональных данных. rowVersion — null для первой записи.</summary>
    /// <returns>Новая версия записи.</returns>
    public async Task<byte[]> SaveAsync(long employeeId, PersonalDataValues values, byte[]? rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PersonalDataEdit, ct);
        var employee = await LoadEmployeeAsync(ctx, employeeId, forView: false, ct);
        var today = await TodayAsync(ctx, ct);
        if ((await HrSettingsService.ForOrganizationAsync(db, ctx.OrganizationId, ct)).BlockPersonalDataWithoutConsent) // D216
        {
            var consent = await db.EmployeePersonalData.AsNoTracking().Where(p => p.OrganizationId == ctx.OrganizationId && p.EmployeeId == employeeId)
                .Select(p => new { p.ConsentGivenOn, p.ConsentValidUntil, p.ConsentRevokedOn }).SingleOrDefaultAsync(ct);
            if (EmployeePersonalData.StateOf(consent?.ConsentGivenOn, consent?.ConsentValidUntil, consent?.ConsentRevokedOn, today) != ConsentState.Valid)
            {
                throw new Domain.Common.BusinessRuleException("hr.personal_data.no_consent",
                    "В организации персональные данные вводятся только при действующем согласии: сначала отметьте согласие сотрудника.");
            }
        }

        IReadOnlyList<string> changed = [];
        var pd = await EditAsync(ctx, employee, rowVersion, p => changed = p.Update(values, today, clock.UtcNow), ct);
        if (changed.Count > 0)
        {
            Audit(ctx, AuditActions.PersonalDataChanged, employeeId, null, string.Join(", ", changed), employee.FullName);
        }

        await SaveAsync(ct);
        return pd.RowVersion;
    }

    /// <summary>Отметка согласия на обработку ПДн (ст. 9 152-ФЗ).</summary>
    public async Task<byte[]> GiveConsentAsync(long employeeId, DateOnly givenOn, DateOnly? validUntil, byte[]? rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PersonalDataEdit, ct);
        var employee = await LoadEmployeeAsync(ctx, employeeId, forView: false, ct);
        var today = await TodayAsync(ctx, ct);
        var pd = await EditAsync(ctx, employee, rowVersion, p => p.GiveConsent(givenOn, validUntil, today, clock.UtcNow), ct);
        Audit(ctx, AuditActions.ConsentChanged, employeeId, null,
            validUntil is { } u ? $"дано {givenOn:dd.MM.yyyy}, до {u:dd.MM.yyyy}" : $"дано {givenOn:dd.MM.yyyy}, до отзыва", employee.FullName);
        await SaveAsync(ct);
        return pd.RowVersion;
    }

    public async Task<byte[]> RevokeConsentAsync(long employeeId, DateOnly revokedOn, byte[]? rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PersonalDataEdit, ct);
        var employee = await LoadEmployeeAsync(ctx, employeeId, forView: false, ct);
        var pd = await EditAsync(ctx, employee, rowVersion, p => p.RevokeConsent(revokedOn, clock.UtcNow), ct);
        Audit(ctx, AuditActions.ConsentChanged, employeeId, null, $"отозвано {revokedOn:dd.MM.yyyy}", employee.FullName);
        await SaveAsync(ct);
        return pd.RowVersion;
    }

    public Task<long> AddConsentScanAsync(long employeeId, string fileName, string contentType, byte[] content, CancellationToken ct = default) =>
        AddDocumentAsync(employeeId, EmployeeDocumentKind.Consent, fileName, contentType, content, ct);

    /// <summary>Документ сотрудника (D226): скан согласия, паспорта, регистрации или фотография. Право — изменение ПДн; загрузка — в журнал.</summary>
    public async Task<long> AddDocumentAsync(long employeeId, EmployeeDocumentKind kind, string fileName, string contentType, byte[] content,
        CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PersonalDataEdit, ct);
        var employee = await LoadEmployeeAsync(ctx, employeeId, forView: false, ct);
        var scan = ConsentScan.Create(ctx.OrganizationId, employeeId, fileName, contentType, content, clock.UtcNow, ctx.UserId, kind);
        db.ConsentScans.Add(scan);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        Audit(ctx, kind == EmployeeDocumentKind.Consent ? AuditActions.ConsentScanAdded : AuditActions.EmployeeDocumentAdded, employeeId, null,
            $"{EmployeeDocumentKinds.Name(kind)}: {scan.FileName}, {scan.SizeBytes / 1024 + 1} КБ", employee.FullName);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return scan.Id;
    }

    /// <summary>
    /// Заполненность карточки сотрудника (D226). Видна тем, кто читает его ПДн или ведёт сотрудников: это только факт наличия, без значений.
    /// </summary>
    public async Task<EmployeeCompleteness> CompletenessAsync(long employeeId, CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        var employee = await LoadEmployeeAsync(ctx, employeeId, forView: true, ct);
        if (!CanRead(ctx, employee) && !ctx.Permissions.Has(Permissions.EmployeeEdit))
        {
            await guard.DenyAsync(ctx, Permissions.PersonalDataView, ct);
        }

        return (await CompletenessOfAsync(ctx.OrganizationId, [employee.Id], ct))[employee.Id];
    }

    /// <summary>D226: заполненность карточек видимых сотрудников — для списка и сводки «чего не хватает». Право — ПДн или ведение сотрудников.</summary>
    public async Task<CompletenessListDto?> CompletenessListAsync(CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        var permission = ctx.Permissions.Has(Permissions.PersonalDataView) ? Permissions.PersonalDataView
            : ctx.Permissions.Has(Permissions.EmployeeEdit) ? Permissions.EmployeeEdit : null;
        if (permission is null)
        {
            return null; // показатель не для этой роли — блок просто не показывается
        }

        var visible = await Structure.StructureScope.VisibleDepartmentsAsync(db, ctx, permission, ct);
        var ids = (await db.Employees.AsNoTracking().Where(e => e.OrganizationId == ctx.OrganizationId && e.Status != EmploymentStatus.Dismissed)
                .Select(e => new { e.Id, e.DepartmentId }).ToListAsync(ct))
            .Where(e => visible is null || visible.Contains(e.DepartmentId)).Select(e => e.Id).ToList();
        return new CompletenessListDto(await CompletenessOfAsync(ctx.OrganizationId, ids, ct));
    }

    private async Task<Dictionary<long, EmployeeCompleteness>> CompletenessOfAsync(long org, IReadOnlyCollection<long> ids, CancellationToken ct)
    {
        var tz = await db.Organizations.AsNoTracking().Where(o => o.Id == org).Select(o => o.TimeZoneId).SingleAsync(ct);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(clock.UtcNow, tz));
        var births = await db.Employees.AsNoTracking().Where(e => e.OrganizationId == org && ids.Contains(e.Id))
            .Select(e => new { e.Id, e.BirthDate }).ToDictionaryAsync(e => e.Id, e => e.BirthDate != null, ct);
        var pd = await db.EmployeePersonalData.AsNoTracking().Where(p => p.OrganizationId == org && ids.Contains(p.EmployeeId))
            .Select(p => new
            {
                p.EmployeeId,
                Passport = p.PassportNumber != null && p.PassportIssuer != null && p.PassportIssuedOn != null,
                Snils = p.Snils != null, Inn = p.Inn != null, Address = p.RegistrationAddress != null, Phone = p.Phone != null,
                p.ConsentGivenOn, p.ConsentValidUntil, p.ConsentRevokedOn,
            }).ToDictionaryAsync(p => p.EmployeeId, ct);
        var documents = (await db.ConsentScans.AsNoTracking().Where(s => s.OrganizationId == org && ids.Contains(s.EmployeeId))
                .Select(s => new { s.EmployeeId, s.Kind }).Distinct().ToListAsync(ct))
            .ToLookup(s => s.EmployeeId, s => s.Kind);
        var scheduled = (await db.EmployeeSchedules.AsNoTracking().Where(s => s.OrganizationId == org && ids.Contains(s.EmployeeId))
            .Select(s => s.EmployeeId).Distinct().ToListAsync(ct)).ToHashSet();
        var hired = (await db.EmploymentHistory.AsNoTracking()
            .Where(h => h.OrganizationId == org && ids.Contains(h.EmployeeId) && h.Kind == PersonnelOrderKind.Hire && h.OrderId != null)
            .Select(h => h.EmployeeId).Distinct().ToListAsync(ct)).ToHashSet();
        var agreed = await EmployeeAgreementService.FullySignedAsync(db, org, ids, ct); // D227
        return ids.ToDictionary(id => id, id =>
        {
            var p = pd.GetValueOrDefault(id);
            var docs = documents[id].ToHashSet();
            return EmployeeCompleteness.Of(births.GetValueOrDefault(id), p?.Passport ?? false, p?.Snils ?? false, p?.Inn ?? false,
                p?.Address ?? false, p?.Phone ?? false,
                p is not null && EmployeePersonalData.StateOf(p.ConsentGivenOn, p.ConsentValidUntil, p.ConsentRevokedOn, today) == ConsentState.Valid,
                docs.Contains(EmployeeDocumentKind.Consent), docs.Contains(EmployeeDocumentKind.Passport), docs.Contains(EmployeeDocumentKind.Registration),
                docs.Contains(EmployeeDocumentKind.Photo), scheduled.Contains(id), hired.Contains(id), docs.Contains(EmployeeDocumentKind.Contract),
                agreed.Contains(id));
        });
    }

    /// <summary>Скан согласия для просмотра. Просмотр — в журнал.</summary>
    public async Task<(string FileName, string ContentType, byte[] Content)> GetConsentScanAsync(long scanId, CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        var scan = await db.ConsentScans.AsNoTracking().SingleOrDefaultAsync(s => s.Id == scanId && s.OrganizationId == ctx.OrganizationId, ct)
                   ?? throw new NotFoundException("Скан согласия");
        var employee = await LoadEmployeeAsync(ctx, scan.EmployeeId, forView: true, ct);
        if (!CanRead(ctx, employee))
        {
            await guard.DenyAsync(ctx, Permissions.PersonalDataView, ct);
        }

        Audit(ctx, scan.Kind == EmployeeDocumentKind.Consent ? AuditActions.ConsentScanViewed : AuditActions.EmployeeDocumentViewed, scan.EmployeeId, null,
            $"{EmployeeDocumentKinds.Name(scan.Kind)}: {scan.FileName}", employee.FullName);
        await db.SaveChangesAsync(ct);
        return (scan.FileName, scan.ContentType, scan.Content);
    }

    /// <summary>Личная карточка Т-2 (постановление Госкомстата России от 05.01.2004 № 1). Печать — в журнал.</summary>
    public async Task<PersonalCardT2Dto> CardT2Async(long employeeId, CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        var employee = await LoadEmployeeAsync(ctx, employeeId, forView: true, ct);
        if (!CanRead(ctx, employee))
        {
            await guard.DenyAsync(ctx, Permissions.PersonalDataView, ct);
        }

        var org = await db.Organizations.AsNoTracking().Where(o => o.Id == ctx.OrganizationId).Select(o => o.FullName).SingleAsync(ct);
        var names = await (from d in db.Departments.AsNoTracking()
                           from p in db.Positions.AsNoTracking()
                           where d.Id == employee.DepartmentId && p.Id == employee.PositionId
                           select new { Department = d.Name, Position = p.Name }).SingleAsync(ct);
        var pd = await db.EmployeePersonalData.AsNoTracking()
            .SingleOrDefaultAsync(p => p.OrganizationId == ctx.OrganizationId && p.EmployeeId == employeeId, ct);
        Audit(ctx, AuditActions.PersonalDataPrinted, employeeId, null, employee.FullName, null);
        await db.SaveChangesAsync(ct);
        return new PersonalCardT2Dto(org, null, employee.PersonnelNumber, employee.LastName, employee.FirstName, employee.MiddleName,
            employee.BirthDate, employee.HiredOn, employee.DismissedOn, names.Department, names.Position,
            pd is null ? new PersonalDataValues() : ToValues(pd), await TodayAsync(ctx, ct));
    }

    /// <summary>Читать ПДн можно с правом организации или свою карточку с уровнем «С».</summary>
    private static bool CanRead(AccessContext ctx, Employee employee) =>
        ctx.Permissions.Has(Permissions.PersonalDataView)
        || (ctx.Permissions.HasOwnOnly(Permissions.PersonalDataView) && employee.UserId == ctx.UserId);

    /// <summary>
    /// Сотрудник своей организации и видимый пользователю (область подразделений или своя карточка); иначе «не найдено».
    /// Для изменения ПДн область карточки не проверяется: право на ПДн выдаётся на всю организацию.
    /// </summary>
    private async Task<Employee> LoadEmployeeAsync(AccessContext ctx, long id, bool forView, CancellationToken ct)
    {
        var employee = await db.Employees.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id && e.OrganizationId == ctx.OrganizationId, ct)
                       ?? throw new NotFoundException("Сотрудник");
        if (!forView || ctx.Permissions.Has(Permissions.PersonalDataView) || ctx.Permissions.Has(Permissions.PersonalDataEdit))
        {
            return employee;
        }

        if (employee.UserId == ctx.UserId)
        {
            return employee;
        }

        if (!ctx.Permissions.Has(Permissions.EmployeeView))
        {
            throw new NotFoundException("Сотрудник");
        }

        var visible = await Structure.StructureScope.VisibleDepartmentsAsync(db, ctx, Permissions.EmployeeView, ct);
        return visible is null || visible.Contains(employee.DepartmentId) ? employee : throw new NotFoundException("Сотрудник");
    }

    /// <summary>
    /// Изменение записи ПДн. Новая запись попадает в контекст только после успешной проверки, а при отказе изменения
    /// забываются: у вкладки Blazor один DbContext, и отклонённое значение не должно уйти в базу со следующим сохранением.
    /// </summary>
    private async Task<EmployeePersonalData> EditAsync(
        AccessContext ctx, Employee employee, byte[]? rowVersion, Action<EmployeePersonalData> change, CancellationToken ct)
    {
        if (employee.Status == EmploymentStatus.Dismissed)
        {
            throw new Domain.Common.BusinessRuleException("hr.employee.dismissed", "Сотрудник уволен, карточка закрыта для изменений.");
        }

        var pd = await db.EmployeePersonalData.SingleOrDefaultAsync(p => p.OrganizationId == ctx.OrganizationId && p.EmployeeId == employee.Id, ct);
        var isNew = pd is null;
        if (pd is null)
        {
            if (rowVersion is { Length: > 0 })
            {
                throw new ConcurrencyConflictException();
            }

            pd = EmployeePersonalData.Create(ctx.OrganizationId, employee.Id, clock.UtcNow);
        }
        else if (rowVersion is null || !pd.RowVersion.AsSpan().SequenceEqual(rowVersion))
        {
            throw new ConcurrencyConflictException();
        }

        try
        {
            change(pd);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }

        if (isNew)
        {
            db.EmployeePersonalData.Add(pd);
        }

        return pd;
    }

    /// <summary>Сохранение; при конфликте версий или повторной первой записи (другая вкладка успела раньше) — «данные изменены».</summary>
    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException
                                            || ex.InnerException?.Message.Contains("ux_hr_personal_data_employee", StringComparison.Ordinal) == true)
        {
            db.DiscardChanges();
            throw new ConcurrencyConflictException();
        }
    }

    private async Task<DateOnly> TodayAsync(AccessContext ctx, CancellationToken ct)
    {
        var tz = await db.Organizations.AsNoTracking().Where(o => o.Id == ctx.OrganizationId).Select(o => o.TimeZoneId).SingleAsync(ct);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(clock.UtcNow, tz));
    }

    private static PersonalDataValues ToValues(EmployeePersonalData p) => new(
        p.Gender, p.BirthPlace, p.Citizenship, p.PassportNumber, p.PassportIssuer, p.PassportIssuedOn, p.PassportDivisionCode,
        p.Snils, p.Inn, p.RegistrationAddress, p.ActualAddress, p.Phone, p.Education, p.Profession, p.MaritalStatus, p.FamilyMembers);

    private void Audit(AccessContext ctx, string action, long employeeId, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(Employee), employeeId.ToString(),
            before, after, reason, currentUser.CorrelationId));
}
