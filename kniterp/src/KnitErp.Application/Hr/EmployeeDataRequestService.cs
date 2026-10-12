using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Hr;
using KnitErp.Domain.Structure;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Hr;

/// <summary>Выданная ссылка анкеты: показывается кадровику один раз (в базе только хэш).</summary>
/// <param name="MailAvailable">Почта сервера настроена.</param>
/// <param name="Sent">null — письмо не отправлялось (почта не настроена или у сотрудника нет адреса); иначе — принято ли почтовым сервером.</param>
public sealed record EmployeeDataLinkDto(string Link, DateTime ExpiresAtUtc, bool MailAvailable, bool? Sent, string? Email);

/// <summary>Что видно на открытой странице анкеты: только название организации и ФИО — никаких сохранённых ПДн (ссылка могла утечь).</summary>
public sealed record EmployeeDataFormDto(string Organization, string EmployeeName, DateTime ExpiresAtUtc, AgreementText Consent);

/// <summary>Файл анкеты, как его прислал браузер.</summary>
public sealed record EmployeeDataUpload(EmployeeDocumentKind Kind, string? FileName, byte[] Content);

/// <summary>Состояние анкеты для карточки — без значений: сколько анкет ждут проверки, действует ли ссылка, последний отказ.</summary>
public sealed record EmployeeDataStatusDto(int Pending, DateTime? LinkExpiresAtUtc, bool CanIssue, bool CanReview, string? LastRejectReason);

/// <summary>Поле для сравнения: что в карточке сейчас и что прислал сотрудник.</summary>
public sealed record EmployeeDataFieldDto(string Name, string? Current, string Submitted)
{
    public bool Differs => Current != Submitted;
}

public sealed record EmployeeDataFileDto(long Id, EmployeeDocumentKind Kind, string FileName, int SizeBytes)
{
    public string KindName => EmployeeDocumentKinds.Name(Kind);
}

/// <summary>Анкета для проверки кадровиком.</summary>
public sealed record EmployeeDataReviewDto(
    long Id, DateTime SubmittedAtUtc, IReadOnlyList<EmployeeDataFieldDto> Fields, IReadOnlyList<EmployeeDataFileDto> Files, string ConsentVersion,
    string? ClientAddress, bool ConsentWillBeRecorded, bool CanDecide, byte[] RowVersion);

/// <summary>
/// Анкета сотрудника по ссылке (D125). Кадровик (право «Сотрудники: изменение» и чтение ПДн) выдаёт одноразовую ссылку на 4 часа —
/// письмом на почту сотрудника и кнопками «Копировать» / «Поделиться». Сотрудник без входа заполняет недостающие ПДн, прикладывает сканы
/// и даёт согласие на обработку (ст. 9 152-ФЗ). Присланное не попадает в карточку само: кадровик сравнивает и принимает или отклоняет.
/// Заполненность карточки (D226) считает только принятое. В журнал — только названия полей, без значений.
/// </summary>
public sealed class EmployeeDataRequestService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, IEmailSender email)
{
    public const string FormPath = "account/employee-data";

    /// <summary>Больше одного файла на вид не принимается; всего — по одному на каждый из пяти видов.</summary>
    public const int MaxFiles = 5;

    /// <summary>Новая ссылка (прежняя перестаёт действовать) и письмо сотруднику, если почта настроена и адрес известен.</summary>
    public async Task<EmployeeDataLinkDto> IssueAsync(long employeeId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.EmployeeEdit, ct);
        if (!ctx.Permissions.Has(Permissions.PersonalDataView))
        {
            await guard.DenyAsync(ctx, Permissions.PersonalDataView, ct);
        }

        var employee = await LoadInScopeAsync(ctx, employeeId, ct);
        if (employee.Status == EmploymentStatus.Dismissed)
        {
            throw new BusinessRuleException("hr.employee.dismissed", "Сотрудник уволен, карточка закрыта для изменений.");
        }

        // Ссылка — только от адреса сервера из настроек (App:PublicUrl), не от заголовка Host запроса.
        if (string.IsNullOrWhiteSpace(email.PublicUrl))
        {
            throw new BusinessRuleException("hr.employee_data.public_url",
                "Не задан адрес сервера для ссылок (App:PublicUrl). Обратитесь к администратору системы.");
        }

        var token = SetupTokens.Generate();
        var now = clock.UtcNow;
        var request = EmployeeDataRequest.Issue(ctx.OrganizationId, employee.Id, SetupTokens.Hash(token), ctx.UserId, now);
        await using (var tx = await db.BeginTransactionAsync(ct))
        {
            await db.LockAsync($"kniterp.hr.employee_data.{ctx.OrganizationId}.{employee.Id}", ct);
            var open = await db.EmployeeDataRequests
                .Where(r => r.OrganizationId == ctx.OrganizationId && r.EmployeeId == employee.Id && r.Status == EmployeeDataRequestStatus.Issued)
                .ToListAsync(ct);
            var cancelled = open.Count(r => r.Cancel(now));
            db.EmployeeDataRequests.Add(request);
            Audit(ctx.OrganizationId, ctx.UserId, AuditActions.EmployeeDataLinkIssued, employee.Id, $"действует до {request.ExpiresAtUtc:dd.MM.yyyy HH:mm} UTC",
                cancelled > 0 ? "прежняя ссылка отменена" : null);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        var link = $"{email.PublicUrl!.TrimEnd('/')}/{FormPath}?token={Uri.EscapeDataString(token)}";
        var address = await AddressOfAsync(ctx.OrganizationId, employee, ct);
        if (!email.IsConfigured || address is null)
        {
            return new EmployeeDataLinkDto(link, request.ExpiresAtUtc, email.IsConfigured, null, null);
        }

        var org = await db.Organizations.AsNoTracking().Where(o => o.Id == ctx.OrganizationId).Select(o => new { o.ShortName, o.TimeZoneId }).SingleAsync(ct);
        var issuer = await db.Users.AsNoTracking().Where(u => u.Id == ctx.UserId).Select(u => u.DisplayName).SingleAsync(ct);
        var until = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(request.ExpiresAtUtc, org.TimeZoneId);
        var sent = await email.SendAsync(address, $"knitERP: заполните свои данные для «{org.ShortName}»",
            Letter(employee.FirstName, employee.MiddleName, issuer, org.ShortName, link, until), ct);
        Audit(ctx.OrganizationId, ctx.UserId, AuditActions.EmployeeDataLinkEmailed, employee.Id, sent ? "письмо отправлено" : "письмо не отправлено", null);
        await db.SaveChangesAsync(ct);
        return new EmployeeDataLinkDto(link, request.ExpiresAtUtc, true, sent, address);
    }

    /// <summary>Открытая страница: действующая ссылка → организация, ФИО и текст согласия; иначе null (одинаково для любой причины).</summary>
    public async Task<EmployeeDataFormDto?> DescribeAsync(string? token, CancellationToken ct = default)
    {
        var request = await FindOpenAsync(token, tracking: false, ct);
        if (request is null)
        {
            return null;
        }

        var (organization, employeeName, consent) = await ConsentAsync(request, ct);
        return new EmployeeDataFormDto(organization, employeeName, request.ExpiresAtUtc, consent);
    }

    /// <summary>
    /// Отправка анкеты по ссылке (без входа). Организация и сотрудник — только из ссылки. Проверка — как в карточке; согласие обязательно.
    /// Анкета сохраняется на проверку, ссылка закрывается (одноразовая). Неизвестная, просроченная или использованная ссылка — один ответ.
    /// </summary>
    public async Task SubmitAsync(string? token, EmployeeDataValues values, IReadOnlyList<EmployeeDataUpload> uploads, bool consent, string? clientAddress,
        CancellationToken ct = default)
    {
        var request = await FindOpenAsync(token, tracking: true, ct) ?? throw InvalidLink();
        try
        {
            var employee = await db.Employees.AsNoTracking()
                .SingleAsync(e => e.Id == request.EmployeeId && e.OrganizationId == request.OrganizationId, ct);
            if (employee.Status == EmploymentStatus.Dismissed)
            {
                throw InvalidLink();
            }

            if (!consent)
            {
                throw new BusinessRuleException("hr.employee_data.consent",
                    "Без согласия на обработку персональных данных анкету отправить нельзя: отметьте согласие.");
            }

            var now = clock.UtcNow;
            var today = await TodayAsync(request.OrganizationId, ct);
            var clean = EmployeeDataForm.Normalize(values, employee.HiredOn, today, now);
            if (uploads.Count > MaxFiles || uploads.GroupBy(u => u.Kind).Any(g => g.Count() > 1))
            {
                throw new BusinessRuleException("hr.employee_data.files", "По одному файлу на каждый вид документа.");
            }

            var files = uploads.Select(u => EmployeeDataSubmissionFile.Create(request.OrganizationId, u.Kind, u.FileName, u.Content)).ToList();
            var filled = clean.FilledFields();
            if (filled.Count == 0 && files.Count == 0)
            {
                throw new BusinessRuleException("hr.employee_data.empty", "Заполните хотя бы одно поле или приложите документ.");
            }

            var (_, _, consentText) = await ConsentAsync(request, ct);
            await using var tx = await db.BeginTransactionAsync(ct);
            request.MarkSubmitted(now);
            var submission = EmployeeDataSubmission.Create(request, clean, consentText, clientAddress, now);
            db.EmployeeDataSubmissions.Add(submission);
            await db.SaveChangesAsync(ct); // конкурентная вторая отправка той же ссылки падает здесь (RowVersion, UNIQUE RequestId)
            foreach (var f in files)
            {
                db.EmployeeDataSubmissionFiles.Add(f.For(submission));
            }

            var docs = files.Select(f => EmployeeDocumentKinds.Name(f.Kind)).ToList();
            Audit(request.OrganizationId, null, AuditActions.EmployeeDataSubmitted, employee.Id,
                string.Join(", ", filled.Concat(docs)), $"по ссылке; согласие на обработку ПДн, редакция {consentText.Version}");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.DiscardChanges();
            throw InvalidLink();
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    /// <summary>Состояние для карточки без значений; null — не для этой роли (блок не показывается, отказа в журнале нет).</summary>
    public async Task<EmployeeDataStatusDto?> StatusAsync(long employeeId, CancellationToken ct = default)
    {
        var ctx = await guard.CurrentAsync(ct);
        var canIssue = ctx.Permissions.Has(Permissions.EmployeeEdit) && ctx.Permissions.Has(Permissions.PersonalDataView);
        var canReview = ctx.Permissions.Has(Permissions.PersonalDataView);
        if (!canIssue && !canReview)
        {
            return null;
        }

        await LoadInScopeAsync(ctx, employeeId, ct, canIssue ? Permissions.EmployeeEdit : Permissions.PersonalDataView);
        var now = clock.UtcNow;
        var pending = await db.EmployeeDataSubmissions.AsNoTracking().CountAsync(s =>
            s.OrganizationId == ctx.OrganizationId && s.EmployeeId == employeeId && s.Status == EmployeeDataSubmissionStatus.Pending, ct);
        var link = await db.EmployeeDataRequests.AsNoTracking()
            .Where(r => r.OrganizationId == ctx.OrganizationId && r.EmployeeId == employeeId && r.Status == EmployeeDataRequestStatus.Issued && r.ExpiresAtUtc > now)
            .Select(r => (DateTime?)r.ExpiresAtUtc).MaxAsync(ct);
        var rejected = await db.EmployeeDataSubmissions.AsNoTracking()
            .Where(s => s.OrganizationId == ctx.OrganizationId && s.EmployeeId == employeeId)
            .OrderByDescending(s => s.Id).Select(s => new { s.Status, s.RejectReason }).FirstOrDefaultAsync(ct);
        return new EmployeeDataStatusDto(pending, link, canIssue, canReview,
            rejected?.Status == EmployeeDataSubmissionStatus.Rejected ? rejected.RejectReason : null);
    }

    /// <summary>Анкеты, ждущие проверки, со сравнением с карточкой. Право — чтение ПДн; просмотр — в журнал.</summary>
    public async Task<IReadOnlyList<EmployeeDataReviewDto>> ReviewAsync(long employeeId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PersonalDataView, ct);
        var employee = await LoadInScopeAsync(ctx, employeeId, ct, Permissions.PersonalDataView);
        var submissions = await db.EmployeeDataSubmissions.AsNoTracking()
            .Where(s => s.OrganizationId == ctx.OrganizationId && s.EmployeeId == employeeId && s.Status == EmployeeDataSubmissionStatus.Pending)
            .OrderBy(s => s.Id).ToListAsync(ct);
        if (submissions.Count == 0)
        {
            return [];
        }

        var ids = submissions.Select(s => s.Id).ToList();
        var files = (await db.EmployeeDataSubmissionFiles.AsNoTracking()
                .Where(f => f.OrganizationId == ctx.OrganizationId && ids.Contains(f.SubmissionId))
                .Select(f => new { f.SubmissionId, Dto = new EmployeeDataFileDto(f.Id, f.Kind, f.FileName, f.SizeBytes) }).ToListAsync(ct))
            .ToLookup(f => f.SubmissionId, f => f.Dto);
        var pd = await db.EmployeePersonalData.AsNoTracking()
            .SingleOrDefaultAsync(p => p.OrganizationId == ctx.OrganizationId && p.EmployeeId == employeeId, ct);
        var today = await TodayAsync(ctx.OrganizationId, ct);
        var consentValid = pd?.ConsentOn(today) == ConsentState.Valid;
        var canDecide = ctx.Permissions.Has(Permissions.PersonalDataEdit) && ctx.Permissions.Has(Permissions.EmployeeEdit)
                        && employee.Status != EmploymentStatus.Dismissed;
        Audit(ctx.OrganizationId, ctx.UserId, AuditActions.EmployeeDataViewed, employeeId, $"анкет: {submissions.Count}", null);
        await db.SaveChangesAsync(ct);
        return submissions.Select(s => new EmployeeDataReviewDto(s.Id, s.SubmittedAtUtc, Compare(s.Values, employee.BirthDate, pd), files[s.Id].ToList(),
            s.ConsentVersion, s.ClientAddress, !consentValid, canDecide, s.RowVersion)).ToList();
    }

    /// <summary>Скан из анкеты — для сравнения до принятия. Право — чтение ПДн; просмотр — в журнал.</summary>
    public async Task<(string FileName, string ContentType, byte[] Content)> GetFileAsync(long fileId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PersonalDataView, ct);
        var file = await db.EmployeeDataSubmissionFiles.AsNoTracking().SingleOrDefaultAsync(f => f.Id == fileId && f.OrganizationId == ctx.OrganizationId, ct)
                   ?? throw new NotFoundException("Файл анкеты");
        var employeeId = await db.EmployeeDataSubmissions.AsNoTracking()
            .Where(s => s.Id == file.SubmissionId && s.OrganizationId == ctx.OrganizationId).Select(s => s.EmployeeId).SingleAsync(ct);
        await LoadInScopeAsync(ctx, employeeId, ct, Permissions.PersonalDataView);
        Audit(ctx.OrganizationId, ctx.UserId, AuditActions.EmployeeDocumentViewed, employeeId, $"Анкета — {EmployeeDocumentKinds.Name(file.Kind)}: {file.FileName}", null);
        await db.SaveChangesAsync(ct);
        return (file.FileName, file.ContentType, file.Content);
    }

    /// <summary>
    /// Принять анкету: заполненные поля заменяют значения карточки, сканы становятся документами сотрудника; если действующего согласия
    /// нет — отмечается согласие из анкеты (дата отправки). Права — изменение ПДн и сотрудников.
    /// </summary>
    public async Task AcceptAsync(long submissionId, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PersonalDataEdit, ct);
        if (!ctx.Permissions.Has(Permissions.EmployeeEdit))
        {
            await guard.DenyAsync(ctx, Permissions.EmployeeEdit, ct);
        }

        var submission = await LoadSubmissionAsync(ctx, submissionId, rowVersion, ct);
        var employee = await db.Employees.SingleAsync(e => e.Id == submission.EmployeeId && e.OrganizationId == ctx.OrganizationId, ct);
        try
        {
            await EnsureInScopeAsync(ctx, employee, Permissions.EmployeeEdit, ct);
            if (employee.Status == EmploymentStatus.Dismissed)
            {
                throw new BusinessRuleException("hr.employee.dismissed", "Сотрудник уволен, карточка закрыта для изменений.");
            }

            var now = clock.UtcNow;
            var org = await db.Organizations.AsNoTracking().Where(o => o.Id == ctx.OrganizationId).Select(o => o.TimeZoneId).SingleAsync(ct);
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(now, org));
            var values = submission.Values;
            var changed = new List<string>();
            if (values.BirthDate is { } birth && employee.SetBirthday(birth, employee.ShareBirthday, today) is not null)
            {
                changed.Add("Дата рождения");
            }

            var pd = await db.EmployeePersonalData.SingleOrDefaultAsync(p => p.OrganizationId == ctx.OrganizationId && p.EmployeeId == employee.Id, ct);
            var isNew = pd is null;
            pd ??= EmployeePersonalData.Create(ctx.OrganizationId, employee.Id, now);
            string? consent = null;
            // Допущение D125: отметка в анкете — не ПЭП, поэтому согласие попадает в карточку только после проверки кадровиком.
            if (pd.ConsentOn(today) != ConsentState.Valid)
            {
                var given = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(submission.SubmittedAtUtc, org));
                pd.GiveConsent(given > today ? today : given, null, today, now);
                consent = $"дано {given:dd.MM.yyyy} в анкете по ссылке, до отзыва";
            }

            changed.AddRange(pd.Update(values.MergeInto(Current(pd)), today, now));
            if (isNew)
            {
                db.EmployeePersonalData.Add(pd);
            }

            var files = await db.EmployeeDataSubmissionFiles.AsNoTracking()
                .Where(f => f.OrganizationId == ctx.OrganizationId && f.SubmissionId == submission.Id).OrderBy(f => f.Id).ToListAsync(ct);
            foreach (var f in files)
            {
                db.ConsentScans.Add(ConsentScan.Create(ctx.OrganizationId, employee.Id, f.FileName, f.ContentType, f.Content, now, ctx.UserId, f.Kind));
                Audit(ctx.OrganizationId, ctx.UserId, AuditActions.EmployeeDocumentAdded, employee.Id,
                    $"{EmployeeDocumentKinds.Name(f.Kind)}: {f.FileName}, {f.SizeBytes / 1024 + 1} КБ", "из анкеты сотрудника");
            }

            submission.Accept(ctx.UserId, now);
            if (consent is not null)
            {
                Audit(ctx.OrganizationId, ctx.UserId, AuditActions.ConsentChanged, employee.Id, consent, employee.FullName);
            }

            if (changed.Count > 0)
            {
                Audit(ctx.OrganizationId, ctx.UserId, AuditActions.PersonalDataChanged, employee.Id, string.Join(", ", changed), "из анкеты сотрудника");
            }

            Audit(ctx.OrganizationId, ctx.UserId, AuditActions.EmployeeDataAccepted, employee.Id,
                changed.Count == 0 && files.Count == 0 ? "без изменений" : string.Join(", ", changed.Concat(files.Select(f => EmployeeDocumentKinds.Name(f.Kind)))),
                null);
            await using var tx = await db.BeginTransactionAsync(ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.DiscardChanges();
            throw new ConcurrencyConflictException();
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("ux_hr_personal_data_employee", StringComparison.Ordinal) == true)
        {
            db.DiscardChanges();
            throw new ConcurrencyConflictException();
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    /// <summary>Отклонить анкету с причиной: в карточку ничего не попадает. Право — изменение ПДн.</summary>
    public async Task RejectAsync(long submissionId, string? reason, byte[] rowVersion, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.PersonalDataEdit, ct);
        var submission = await LoadSubmissionAsync(ctx, submissionId, rowVersion, ct);
        try
        {
            var employee = await db.Employees.AsNoTracking().SingleAsync(e => e.Id == submission.EmployeeId && e.OrganizationId == ctx.OrganizationId, ct);
            await EnsureInScopeAsync(ctx, employee, Permissions.PersonalDataEdit, ct);
            submission.Reject(ctx.UserId, reason, clock.UtcNow);
            Audit(ctx.OrganizationId, ctx.UserId, AuditActions.EmployeeDataRejected, employee.Id, null, PersonalDataMask.Mask(submission.RejectReason!));
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.DiscardChanges();
            throw new ConcurrencyConflictException();
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }
    }

    internal static string Letter(string firstName, string? middleName, string issuer, string organization, string link, DateTime untilLocal) =>
        $"""
         Здравствуйте, {firstName}{(middleName is null ? "" : " " + middleName)}!

         {issuer} из «{organization}» просит вас заполнить свои данные для кадрового учёта: дату рождения, паспорт, адреса регистрации
         и проживания, телефон, личную почту, СНИЛС и ИНН. По желанию можно приложить фото или скан паспорта, регистрации, СНИЛС, ИНН
         и свою фотографию.

         Откройте ссылку — входить в систему не нужно:
         {link}

         Ссылка личная и одноразовая, действует до {untilLocal:dd.MM.yyyy HH:mm} (время организации). Не пересылайте её никому:
         по ней можно отправить данные от вашего имени. Отправленные данные проверит кадровик.
         Если вы не ждали этого письма, просто не открывайте ссылку.
         """;

    private static BusinessRuleException InvalidLink() => new(EmployeeDataForm.InvalidLinkCode, EmployeeDataForm.InvalidLinkMessage);

    /// <summary>
    /// Действующая ссылка по токену: поиск по SHA-256 (индекс), затем сравнение хэша за постоянное время. Неизвестная, просроченная,
    /// использованная или отменённая — null, без различий.
    /// </summary>
    private async Task<EmployeeDataRequest?> FindOpenAsync(string? token, bool tracking, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 40 or > 100)
        {
            return null;
        }

        var hash = SetupTokens.Hash(token);
        var query = tracking ? db.EmployeeDataRequests : db.EmployeeDataRequests.AsNoTracking();
        var request = await query.SingleOrDefaultAsync(r => r.TokenHash == hash, ct);
        return request is not null && SetupTokens.Matches(token, request.TokenHash) && request.IsOpen(clock.UtcNow) ? request : null;
    }

    private async Task<(string Organization, string EmployeeName, AgreementText Consent)> ConsentAsync(EmployeeDataRequest request, CancellationToken ct)
    {
        var o = await db.Organizations.AsNoTracking().Where(x => x.Id == request.OrganizationId)
            .Select(x => new { x.FullName, x.ShortName, x.Inn, x.ActualAddress }).SingleAsync(ct);
        var legal = await db.LegalEntities.AsNoTracking().Where(l => l.OrganizationId == request.OrganizationId && l.IsDefault)
            .Select(l => l.LegalAddress).FirstOrDefaultAsync(ct);
        var e = await (from x in db.Employees.AsNoTracking()
                       join p in db.Positions.AsNoTracking() on x.PositionId equals p.Id
                       where x.Id == request.EmployeeId && x.OrganizationId == request.OrganizationId
                       select new { x.LastName, x.FirstName, x.MiddleName, x.PersonnelNumber, Position = p.Name }).SingleAsync(ct);
        var name = e.MiddleName is null ? $"{e.LastName} {e.FirstName}" : $"{e.LastName} {e.FirstName} {e.MiddleName}";
        // Текст согласия D227 — без сохранённых паспорта и адреса (на открытой странице их не показываем): эти строки — пустые линии.
        var text = AgreementTemplates.Render(AgreementKind.PersonalDataConsent, new AgreementData(o.FullName, o.Inn, legal ?? o.ActualAddress, name,
            e.PersonnelNumber, e.Position, null, null, null, null, DateOnly.FromDateTime(request.IssuedAtUtc)));
        return (o.ShortName, name, text);
    }

    /// <summary>Адрес письма: личная почта из ПДн, иначе почта учётной записи сотрудника.</summary>
    private async Task<string?> AddressOfAsync(long org, Employee employee, CancellationToken ct)
    {
        var personal = await db.EmployeePersonalData.AsNoTracking().Where(p => p.OrganizationId == org && p.EmployeeId == employee.Id)
            .Select(p => p.PersonalEmail).SingleOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(personal))
        {
            return personal;
        }

        return employee.UserId is { } userId
            ? await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Email).SingleOrDefaultAsync(ct)
            : null;
    }

    private async Task<EmployeeDataSubmission> LoadSubmissionAsync(AccessContext ctx, long id, byte[] rowVersion, CancellationToken ct)
    {
        var submission = await db.EmployeeDataSubmissions.SingleOrDefaultAsync(s => s.Id == id && s.OrganizationId == ctx.OrganizationId, ct)
                         ?? throw new NotFoundException("Анкета сотрудника");
        return submission.RowVersion.AsSpan().SequenceEqual(rowVersion) ? submission : throw new ConcurrencyConflictException();
    }

    /// <summary>Сотрудник своей организации и в области права; иначе «не найдено».</summary>
    private async Task<Employee> LoadInScopeAsync(AccessContext ctx, long id, CancellationToken ct, string permission = Permissions.EmployeeEdit)
    {
        var employee = await db.Employees.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id && e.OrganizationId == ctx.OrganizationId, ct)
                       ?? throw new NotFoundException("Сотрудник");
        await EnsureInScopeAsync(ctx, employee, permission, ct);
        return employee;
    }

    private async Task EnsureInScopeAsync(AccessContext ctx, Employee employee, string permission, CancellationToken ct)
    {
        // Право на ПДн выдаётся на всю организацию (как в PersonalDataService); область подразделений — у права на сотрудников.
        if (permission is Permissions.PersonalDataView or Permissions.PersonalDataEdit)
        {
            return;
        }

        var visible = await Structure.StructureScope.VisibleDepartmentsAsync(db, ctx, permission, ct);
        if (visible is not null && !visible.Contains(employee.DepartmentId))
        {
            throw new NotFoundException("Сотрудник");
        }
    }

    private static PersonalDataValues Current(EmployeePersonalData p) => new(
        p.Gender, p.BirthPlace, p.Citizenship, p.PassportNumber, p.PassportIssuer, p.PassportIssuedOn, p.PassportDivisionCode,
        p.Snils, p.Inn, p.RegistrationAddress, p.ActualAddress, p.Phone, p.Education, p.Profession, p.MaritalStatus, p.FamilyMembers, p.PersonalEmail);

    private static IReadOnlyList<EmployeeDataFieldDto> Compare(EmployeeDataValues v, DateOnly? birthDate, EmployeePersonalData? pd)
    {
        var list = new List<EmployeeDataFieldDto>();
        Add("Дата рождения", birthDate?.ToString("dd.MM.yyyy"), v.BirthDate?.ToString("dd.MM.yyyy"));
        Add("Паспорт: серия и номер", pd?.PassportNumber, v.PassportNumber);
        Add("Кем выдан", pd?.PassportIssuer, v.PassportIssuer);
        Add("Дата выдачи", pd?.PassportIssuedOn?.ToString("dd.MM.yyyy"), v.PassportIssuedOn?.ToString("dd.MM.yyyy"));
        Add("Код подразделения", pd?.PassportDivisionCode, v.PassportDivisionCode);
        Add("Адрес регистрации", pd?.RegistrationAddress, v.RegistrationAddress);
        Add("Фактический адрес", pd?.ActualAddress, v.ActualAddress);
        Add("Телефон", pd?.Phone, v.Phone);
        Add("Личная почта", pd?.PersonalEmail, v.PersonalEmail);
        Add("СНИЛС", pd?.Snils is { } s ? PersonIds.FormatSnils(s) : null, v.Snils is { } n ? PersonIds.FormatSnils(n) : null);
        Add("ИНН", pd?.Inn, v.Inn);
        return list;

        void Add(string name, string? current, string? submitted)
        {
            if (submitted is not null)
            {
                list.Add(new EmployeeDataFieldDto(name, current, submitted));
            }
        }
    }

    private async Task<DateOnly> TodayAsync(long org, CancellationToken ct)
    {
        var tz = await db.Organizations.AsNoTracking().Where(o => o.Id == org).Select(o => o.TimeZoneId).SingleAsync(ct);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(clock.UtcNow, tz));
    }

    private void Audit(long org, long? userId, string action, long employeeId, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, org, userId, action, nameof(Employee), employeeId.ToString(), null, after, reason,
            currentUser.CorrelationId));
}
