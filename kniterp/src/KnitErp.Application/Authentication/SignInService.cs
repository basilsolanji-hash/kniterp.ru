using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Authentication;

public enum SignInStatus
{
    Succeeded,
    Failed,
    LockedOut,

    /// <summary>Пароль верный, нужен код из приложения-аутентификатора.</summary>
    TwoFactorRequired,

    /// <summary>Пароль верный, но роль требует 2FA, а она не подключена — вход только после подключения.</summary>
    TwoFactorSetupRequired,

    /// <summary>Нет ни одной активной организации (заблокирован везде или не приглашён).</summary>
    NoOrganization,

    /// <summary>D127: пароль верный, но адрес вне разрешённых администратором сетей.</summary>
    NetworkDenied,

    /// <summary>D127: пароль верный, но сейчас вне разрешённого администратором времени работы.</summary>
    ScheduleDenied,
}

/// <summary>D127: почему сессия закрыта при проверке (для сообщения на странице входа).</summary>
public enum SessionClosure
{
    None,

    /// <summary>Обычная недействительность: выход, блокировка, смена прав, простой.</summary>
    Invalid,
    Network,
    OtherDevice,
    Schedule,
}

/// <summary>
/// D127: итог проверки сессии. EndsAtUtc — сессия действует, но закроется в этот момент (баннер с отсчётом на старом устройстве).
/// </summary>
public sealed record SessionCheck(bool Valid, DateTime? EndsAtUtc = null, SessionEndReason? EndReason = null, SessionClosure Closure = SessionClosure.None)
{
    public static readonly SessionCheck Invalid = new(false, Closure: SessionClosure.Invalid);

    public static SessionCheck Closed(SessionEndReason? reason) => new(false, Closure: reason switch
    {
        SessionEndReason.Schedule => SessionClosure.Schedule,
        SessionEndReason.OtherDevice => SessionClosure.OtherDevice,
        _ => SessionClosure.Invalid,
    });
}

/// <summary>Кто входит: штамп безопасности закрывает сессию при блокировке и смене прав.</summary>
/// <summary>Language — язык интерфейса пользователя (D60), ставится в cookie при входе.</summary>
/// <summary>SessionId — внешний идентификатор серверной записи сессии (D122): без неё или после её завершения cookie не действует.</summary>
public sealed record SessionIdentity(
    long UserId, string DisplayName, Guid SecurityStamp, long OrganizationId, bool UsedTwoFactor, string? Language = null, Guid? SessionId = null);

/// <summary>Состояние между паролем и вторым фактором. Подписано и хранится в отдельной короткой cookie.</summary>
public sealed record PendingSignIn(long UserId, Guid SecurityStamp);

/// <summary>
/// RecoveryCodesLeft — сколько резервных кодов осталось, если вход был по резервному коду.
/// DeviceToken — токен доверенного устройства для cookie (D81), если пользователь попросил доверять устройству.
/// </summary>
public sealed record SignInResult(
    SignInStatus Status, SessionIdentity? Session = null, PendingSignIn? Pending = null, int? RecoveryCodesLeft = null, string? DeviceToken = null)
{
    public static SignInResult Of(SignInStatus status) => new(status);
}

public sealed record AuthenticatorSetup(string Key, string ProvisioningUri);

public sealed record OrganizationChoice(long OrganizationId, string ShortName);

/// <summary>
/// Вход в систему: пароль, второй фактор, приглашение, блокировка после неудачных попыток.
/// Каждый вход и каждая неудача пишутся в журнал (журнал входов, MVP 1.0).
/// </summary>
public sealed class SignInService(
    IKnitErpDbContext db,
    IPasswordHasher<UserAccount> hasher,
    ICurrentUser currentUser,
    IClock clock,
    IEmailSender? email = null)
{
    public const string Issuer = "knitERP";

    // Хеш-заглушка: для неизвестного email тратим столько же времени, сколько на проверку настоящего пароля.
    // Считается тем же хешером, что и настоящие пароли (D115: те же параметры PBKDF2), — время ответа не выдаёт адрес.
    private static string? _dummyHash;

    private string DummyHash => _dummyHash ??= hasher.HashPassword(null!, SetupTokens.Generate());

    /// <param name="deviceToken">Токен доверенного устройства из cookie браузера (D81): действующий — код 2FA не спрашивается.</param>
    public async Task<SignInResult> PasswordSignInAsync(string? email, string? password, string? deviceToken = null, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var normalized = (email ?? string.Empty).Trim().ToUpperInvariant();
        var user = normalized.Length == 0 ? null : await db.Users.SingleOrDefaultAsync(u => u.NormalizedEmail == normalized, ct);

        if (user is null || user.PasswordHash is null || user.Status != UserStatus.Active)
        {
            hasher.VerifyHashedPassword(null!, DummyHash, password ?? string.Empty);
            if (user is not null)
            {
                await AuditAsync(user, AuditActions.SignInFailed, user.PasswordHash is null ? "пароль не установлен" : "учётная запись не активна", ct);
            }
            else
            {
                db.AuditEntries.Add(AuditEntry.Create(now, null, null, AuditActions.SignInFailed, nameof(UserAccount), null,
                    reason: WithAddress("неизвестный email"), correlationId: currentUser.CorrelationId));
            }

            await db.SaveChangesAsync(ct);
            return SignInResult.Of(SignInStatus.Failed);
        }

        // D115: блокировка после неудачных попыток не действует на доверенное устройство владельца (OWASP «device cookies»):
        // подбирающий пароль с чужого адреса не может закрыть вход самому сотруднику. Пароль всё равно проверяется.
        var trusted = user.TwoFactorEnabled ? await TrustedDeviceAsync(user, deviceToken, ct) : null;
        if (user.IsLockedOut(now) && trusted is null)
        {
            await AuditAsync(user, AuditActions.SignInFailed, "вход заблокирован после неудачных попыток", ct);
            await db.SaveChangesAsync(ct);
            return SignInResult.Of(SignInStatus.LockedOut);
        }

        var verification = hasher.VerifyHashedPassword(user, user.PasswordHash, password ?? string.Empty);
        if (verification == PasswordVerificationResult.Failed)
        {
            return await FailAsync(user, "неверный пароль", ct);
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.UpgradePasswordHash(hasher.HashPassword(user, password!));
        }

        var organizations = await ActiveOrganizationIdsAsync(user.Id, ct);
        if (organizations.Count == 0)
        {
            await AuditAsync(user, AuditActions.SignInFailed, "нет активной организации", ct);
            await db.SaveChangesAsync(ct);
            return SignInResult.Of(SignInStatus.NoOrganization);
        }

        // D127: ограничения входа (сети, время) проверяются после верного пароля и до второго фактора — неудачей входа это не считается.
        var (organizationId, denied) = await ChooseOrganizationAsync(user, organizations, ct);
        if (organizationId is null)
        {
            await db.SaveChangesAsync(ct);
            return SignInResult.Of(denied);
        }

        if (user.TwoFactorEnabled)
        {
            if (trusted is { } device)
            {
                device.MarkUsed(now);
                return await SucceedAsync(user, organizationId.Value, usedTwoFactor: true, ct, "пароль и доверенное устройство");
            }

            await db.SaveChangesAsync(ct);
            return new SignInResult(SignInStatus.TwoFactorRequired, Pending: new PendingSignIn(user.Id, user.SecurityStamp));
        }

        if (await RequiresTwoFactorAsync(user.Id, ct))
        {
            await db.SaveChangesAsync(ct);
            return new SignInResult(SignInStatus.TwoFactorSetupRequired, Pending: new PendingSignIn(user.Id, user.SecurityStamp));
        }

        return await SucceedAsync(user, organizationId.Value, usedTwoFactor: false, ct);
    }

    /// <summary>
    /// Второй шаг входа: код из приложения-аутентификатора или одноразовый резервный код (D06).
    /// trustDeviceLabel — «Доверять этому устройству» (D81): название браузера для списка устройств; null — не доверять.
    /// </summary>
    public async Task<SignInResult> TwoFactorSignInAsync(PendingSignIn pending, string? code, string? trustDeviceLabel = null, CancellationToken ct = default)
    {
        var user = await LoadPendingAsync(pending, ct);
        if (user is null || !user.TwoFactorEnabled)
        {
            return SignInResult.Of(SignInStatus.Failed);
        }

        if (user.IsLockedOut(clock.UtcNow))
        {
            return SignInResult.Of(SignInStatus.LockedOut);
        }

        int? recoveryLeft = null;
        if (RecoveryCode.LooksLikeCode(code))
        {
            var hash = RecoveryCode.Hash(code);
            var now = clock.UtcNow;

            // D115: код гасится одним условным UPDATE — два одновременных входа с одним кодом не пройдут оба.
            var used = await db.RecoveryCodes.Where(r => r.UserId == user.Id && r.CodeHash == hash && r.UsedAtUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.UsedAtUtc, now), ct);
            if (used == 0)
            {
                return await FailAsync(user, "неверный или использованный резервный код", ct);
            }

            recoveryLeft = await db.RecoveryCodes.CountAsync(r => r.UserId == user.Id && r.UsedAtUtc == null, ct);
            await AuditAsync(user, AuditActions.RecoveryCodeUsed, $"осталось кодов: {recoveryLeft}", ct);
        }
        else if (!user.VerifyTotp(code, clock.UtcNow))
        {
            return await FailAsync(user, "неверный код 2FA", ct);
        }

        var organizations = await ActiveOrganizationIdsAsync(user.Id, ct);
        if (organizations.Count == 0)
        {
            await db.SaveChangesAsync(ct);
            return SignInResult.Of(SignInStatus.NoOrganization);
        }

        var (organizationId, denied) = await ChooseOrganizationAsync(user, organizations, ct);
        if (organizationId is null)
        {
            await db.SaveChangesAsync(ct);
            return SignInResult.Of(denied);
        }

        string? deviceToken = null;
        if (trustDeviceLabel is not null)
        {
            deviceToken = await TrustDeviceAsync(user, trustDeviceLabel, ct);
        }

        return await SucceedAsync(user, organizationId.Value, usedTwoFactor: true, ct) with { RecoveryCodesLeft = recoveryLeft, DeviceToken = deviceToken };
    }

    /// <summary>Действующее доверенное устройство этого пользователя по токену из cookie или null.</summary>
    private async Task<TrustedDevice?> TrustedDeviceAsync(UserAccount user, string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
        {
            return null;
        }

        var hash = SetupTokens.Hash(token);
        var device = await db.TrustedDevices.SingleOrDefaultAsync(d => d.TokenHash == hash && d.UserId == user.Id, ct);
        return device is not null && device.IsActive(user.SecurityStamp, clock.UtcNow) ? device : null;
    }

    /// <summary>
    /// Новое доверенное устройство (D81). Больше <see cref="SignInPolicy.MaxTrustedDevices"/> — самые давние теряют доверие.
    /// </summary>
    private async Task<string> TrustDeviceAsync(UserAccount user, string label, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var (device, token) = TrustedDevice.Create(user.Id, user.SecurityStamp, label, now);
        var active = (await db.TrustedDevices.Where(d => d.UserId == user.Id && d.RevokedAtUtc == null).ToListAsync(ct))
            .Where(d => d.IsActive(user.SecurityStamp, now)).OrderByDescending(d => d.CreatedAtUtc).ToList();
        foreach (var old in active.Skip(SignInPolicy.MaxTrustedDevices - 1))
        {
            old.Revoke(now);
        }

        db.TrustedDevices.Add(device);
        await AuditAsync(user, AuditActions.DeviceTrusted, $"{device.Label}, до {device.ExpiresAtUtc:yyyy-MM-dd} UTC", ct);
        return token;
    }

    /// <summary>Секрет для приложения-аутентификатора. Доступно только после верного пароля.</summary>
    public async Task<AuthenticatorSetup> BeginTwoFactorSetupAsync(PendingSignIn pending, CancellationToken ct = default)
    {
        var user = await LoadPendingAsync(pending, ct) ?? throw new NotFoundException("Пользователь");
        var key = user.BeginAuthenticatorSetup();
        await db.SaveChangesAsync(ct);
        return new AuthenticatorSetup(key, Totp.ProvisioningUri(Issuer, user.Email, key));
    }

    /// <summary>Подтверждение 2FA первым кодом и вход. Неверный код считается неудачной попыткой входа.</summary>
    public async Task<SignInResult> CompleteTwoFactorSetupAsync(PendingSignIn pending, string? code, CancellationToken ct = default)
    {
        var user = await LoadPendingAsync(pending, ct);
        if (user is null || user.TwoFactorEnabled || user.AuthenticatorKey is null)
        {
            return SignInResult.Of(SignInStatus.Failed);
        }

        if (user.IsLockedOut(clock.UtcNow))
        {
            return SignInResult.Of(SignInStatus.LockedOut);
        }

        if (!user.ConfirmTwoFactor(code, clock.UtcNow))
        {
            return await FailAsync(user, "неверный код при подключении 2FA", ct);
        }

        await AuditAsync(user, AuditActions.TwoFactorEnabled, "приложение-аутентификатор подключено", ct);
        if (email is not null)
        {
            // D115: подключение 2FA — по паролю; если пароль украден, письмо предупредит владельца.
            SecurityEvents.Notify(email, user, "Вход с кодом knitERP подключён",
                "К вашей учётной записи подключено приложение-аутентификатор: теперь вход — по паролю и коду из него.");
        }
        var organizations = await ActiveOrganizationIdsAsync(user.Id, ct);
        if (organizations.Count == 0)
        {
            await db.SaveChangesAsync(ct);
            return SignInResult.Of(SignInStatus.NoOrganization);
        }

        var (organizationId, denied) = await ChooseOrganizationAsync(user, organizations, ct);
        if (organizationId is null)
        {
            await db.SaveChangesAsync(ct);
            return SignInResult.Of(denied);
        }

        return await SucceedAsync(user, organizationId.Value, usedTwoFactor: true, ct);
    }

    /// <summary>Установка пароля по ссылке приглашения. После неё пользователь входит обычным способом.</summary>
    public async Task AcceptInvitationAsync(string? token, string? password, string? passwordConfirmation, CancellationToken ct = default)
    {
        var user = await FindBySetupTokenAsync(token, ct)
                   ?? throw new BusinessRuleException("auth.invitation.invalid", "Ссылка приглашения недействительна.");

        if (!string.Equals(password, passwordConfirmation, StringComparison.Ordinal))
        {
            throw new BusinessRuleException("auth.password.mismatch", "Пароли не совпадают.");
        }

        SignInPolicy.EnsurePasswordAcceptable(password, user.Email);
        user.CompleteSetup(token!, hasher.HashPassword(user, password!), clock.UtcNow);
        await AuditAsync(user, AuditActions.InvitationAccepted, "пароль установлен", ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Email приглашения для страницы установки пароля или null, если ссылка недействительна или истекла.</summary>
    public async Task<string?> DescribeInvitationAsync(string? token, CancellationToken ct = default)
    {
        var user = await FindBySetupTokenAsync(token, ct);
        return user?.SetupTokenExpiresAtUtc > clock.UtcNow ? user.Email : null;
    }

    /// <summary>
    /// Сессия ещё действительна: штамп не менялся, учётная запись активна, участие в организации не заблокировано,
    /// серверная запись сессии (D122) есть, принадлежит этому пользователю и не завершена. Cookie без записи — недействительна.
    /// Вызывается при каждом запросе не реже раза в минуту; время последней активности пишется не чаще раза в 5 минут.
    /// </summary>
    public async Task<bool> ValidateSessionAsync(long userId, long organizationId, Guid securityStamp, Guid? sessionId, CancellationToken ct = default) =>
        (await CheckSessionAsync(userId, organizationId, securityStamp, sessionId, currentUser.ClientAddress, full: true, ct)).Valid;

    /// <summary>
    /// D127: проверка сессии. Быстрая (full = false, раз в 10 с) читает только запись сессии: завершена ли, наступил ли момент закрытия.
    /// Полная (раз в минуту и при смене адреса) — ещё пользователя, участие и ограничения входа: адрес вне разрешённых сетей закрывает
    /// сессию сразу, конец времени работы — с предупреждением за 30 с. Наступивший момент закрытия завершает сессию.
    /// </summary>
    /// <param name="clientAddress">Адрес клиента; null — неизвестен (вкладка Blazor), тогда сети проверит следующий HTTP-запрос.</param>
    public async Task<SessionCheck> CheckSessionAsync(long userId, long organizationId, Guid securityStamp, Guid? sessionId, string? clientAddress,
        bool full, CancellationToken ct = default)
    {
        if (sessionId is not { } publicId)
        {
            return SessionCheck.Invalid;
        }

        var now = clock.UtcNow;
        var row = await db.UserSessions.AsNoTracking().Where(x => x.PublicId == publicId)
            .Select(x => new { x.Id, x.UserId, x.SecurityStamp, x.LastSeenAtUtc, x.RevokedAtUtc, x.EndsAtUtc, x.EndReason }).SingleOrDefaultAsync(ct);
        if (row is null || row.UserId != userId || row.RevokedAtUtc is not null || row.SecurityStamp != securityStamp
            || now >= row.LastSeenAtUtc + SignInPolicy.SessionIdleLimit)
        {
            return SessionCheck.Invalid;
        }

        if (row.EndsAtUtc is { } ends && now >= ends)
        {
            // D127: момент закрытия наступил — сессия завершается; cookie и вкладка больше не принимаются.
            await db.UserSessions.Where(x => x.Id == row.Id && x.RevokedAtUtc == null)
                .ExecuteUpdateAsync(x => x.SetProperty(v => v.RevokedAtUtc, now), ct);
            return SessionCheck.Closed(row.EndReason);
        }

        if (!full)
        {
            return new SessionCheck(true, row.EndsAtUtc, row.EndReason);
        }

        var valid = await db.Users.AnyAsync(u => u.Id == userId && u.Status == UserStatus.Active && u.SecurityStamp == securityStamp, ct)
                    && await db.OrganizationMembers.AnyAsync(m => m.UserId == userId && m.OrganizationId == organizationId
                                                                  && m.Status == MembershipStatus.Active, ct);
        if (!valid)
        {
            return SessionCheck.Invalid;
        }

        var (endsAt, reason) = (row.EndsAtUtc, row.EndReason);
        var verdict = await SignInRestrictionRules.EvaluateAsync(db, userId, organizationId, clientAddress, enforceUnknownAddress: false, now, ct);
        if (verdict.Denial == RestrictionDenial.Network)
        {
            // Адрес вне разрешённых сетей — сразу, без предупреждения: запись журнала одна (условный UPDATE).
            var closed = await db.UserSessions.Where(x => x.Id == row.Id && x.RevokedAtUtc == null)
                .ExecuteUpdateAsync(x => x.SetProperty(v => v.RevokedAtUtc, now), ct);
            if (closed > 0)
            {
                db.AuditEntries.Add(AuditEntry.Create(now, organizationId, userId, AuditActions.SignInRestricted, nameof(UserAccount),
                    userId.ToString(), after: "сессия закрыта",
                    reason: WithAddress(SignInRestrictionRules.Describe(RestrictionDenial.Network), clientAddress), correlationId: currentUser.CorrelationId));
                await db.SaveChangesAsync(ct);
            }

            return new SessionCheck(false, Closure: SessionClosure.Network);
        }

        if (SignInRestrictionRules.ScheduleEnd(verdict, now) is { } scheduleEnd && (endsAt is null || endsAt > scheduleEnd))
        {
            var marked = await db.UserSessions
                .Where(x => x.Id == row.Id && x.RevokedAtUtc == null && (x.EndsAtUtc == null || x.EndsAtUtc > scheduleEnd))
                .ExecuteUpdateAsync(x => x.SetProperty(v => v.EndsAtUtc, scheduleEnd).SetProperty(v => v.EndReason, SessionEndReason.Schedule), ct);
            if (marked > 0)
            {
                (endsAt, reason) = (scheduleEnd, SessionEndReason.Schedule);
                db.AuditEntries.Add(AuditEntry.Create(now, organizationId, userId, AuditActions.SessionEnding, nameof(UserAccount),
                    userId.ToString(), after: $"закроется {scheduleEnd:yyyy-MM-dd HH:mm:ss} UTC",
                    reason: "окончание разрешённого времени работы", correlationId: currentUser.CorrelationId));
                await db.SaveChangesAsync(ct);
            }
        }

        if (now - row.LastSeenAtUtc >= SignInPolicy.SessionTouchInterval)
        {
            // Условный UPDATE: одновременные запросы одной сессии не пишут время по нескольку раз и не конфликтуют по rowversion.
            var stale = now - SignInPolicy.SessionTouchInterval;
            await db.UserSessions.Where(x => x.Id == row.Id && x.RevokedAtUtc == null && x.LastSeenAtUtc <= stale)
                .ExecuteUpdateAsync(x => x.SetProperty(v => v.LastSeenAtUtc, now), ct);
        }

        return new SessionCheck(true, endsAt, reason);
    }

    public async Task<IReadOnlyList<OrganizationChoice>> ListOrganizationsAsync(long userId, CancellationToken ct = default) =>
        await db.OrganizationMembers.AsNoTracking()
            .Where(m => m.UserId == userId && m.Status == MembershipStatus.Active)
            .Join(db.Organizations.AsNoTracking().Where(o => !o.IsArchived), m => m.OrganizationId, o => o.Id, (m, o) => o)
            .OrderBy(o => o.ShortName)
            .Select(o => new OrganizationChoice(o.Id, o.ShortName))
            .ToListAsync(ct);

    /// <summary>Переход в другую организацию того же пользователя. Чужая организация — «не найдено».</summary>
    public async Task<SessionIdentity> SwitchOrganizationAsync(SessionIdentity session, long organizationId, CancellationToken ct = default)
    {
        if (!await ValidateSessionAsync(session.UserId, session.OrganizationId, session.SecurityStamp, session.SessionId, ct)
            || !(await ListOrganizationsAsync(session.UserId, ct)).Any(o => o.OrganizationId == organizationId))
        {
            throw new NotFoundException("Организация");
        }

        // D127: ограничения принадлежат участию в организации — в организацию с ограничением пускают по тем же правилам, что при входе.
        var now = clock.UtcNow;
        var verdict = await SignInRestrictionRules.EvaluateAsync(db, session.UserId, organizationId, currentUser.ClientAddress,
            enforceUnknownAddress: true, now, ct);
        if (verdict.Denial != RestrictionDenial.None)
        {
            db.AuditEntries.Add(AuditEntry.Create(now, organizationId, session.UserId, AuditActions.SignInRestricted, nameof(UserAccount),
                session.UserId.ToString(), after: "смена организации отклонена", reason: WithAddress(SignInRestrictionRules.Describe(verdict.Denial)),
                correlationId: currentUser.CorrelationId));
            await db.SaveChangesAsync(ct);
            throw SignInRestrictionRules.Refusal(verdict.Denial);
        }

        if (session.SessionId is { } current)
        {
            await ApplySessionRestrictionsAsync(session.UserId, organizationId, verdict, current, ct);
            if (SignInRestrictionRules.ScheduleEnd(verdict, now) is { } scheduleEnd)
            {
                await db.UserSessions.Where(x => x.PublicId == current && x.RevokedAtUtc == null && (x.EndsAtUtc == null || x.EndsAtUtc > scheduleEnd))
                    .ExecuteUpdateAsync(x => x.SetProperty(v => v.EndsAtUtc, scheduleEnd).SetProperty(v => v.EndReason, SessionEndReason.Schedule), ct);
            }
        }

        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, organizationId, session.UserId, AuditActions.SignedIn,
            nameof(UserAccount), session.UserId.ToString(), after: "смена организации", correlationId: currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
        return session with { OrganizationId = organizationId };
    }

    /// <summary>Выход: запись в журнал, серверная сессия (D122) завершается — её cookie больше не примут, даже если её скопировали.</summary>
    public async Task RecordSignOutAsync(long userId, long organizationId, Guid? sessionId = null, CancellationToken ct = default)
    {
        if (sessionId is { } publicId)
        {
            var now = clock.UtcNow;
            await db.UserSessions.Where(x => x.PublicId == publicId && x.UserId == userId && x.RevokedAtUtc == null)
                .ExecuteUpdateAsync(x => x.SetProperty(v => v.RevokedAtUtc, now), ct);
        }

        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, organizationId, userId, AuditActions.SignedOut,
            nameof(UserAccount), userId.ToString(), correlationId: currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// D127: первая активная организация, где ограничения разрешают вход (адрес, время). Нет такой — отказ с причиной первой
    /// организации и запись в журнал каждой организации, где вход запрещён.
    /// </summary>
    private async Task<(long? OrganizationId, SignInStatus Denied)> ChooseOrganizationAsync(UserAccount user, List<long> organizations, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var denials = new List<(long OrganizationId, RestrictionDenial Denial)>();
        foreach (var organizationId in organizations)
        {
            var verdict = await SignInRestrictionRules.EvaluateAsync(db, user.Id, organizationId, currentUser.ClientAddress, enforceUnknownAddress: true, now, ct);
            if (verdict.Denial == RestrictionDenial.None)
            {
                return (organizationId, SignInStatus.Succeeded);
            }

            denials.Add((organizationId, verdict.Denial));
        }

        foreach (var (organizationId, denial) in denials)
        {
            db.AuditEntries.Add(AuditEntry.Create(now, organizationId, user.Id, AuditActions.SignInRestricted, nameof(UserAccount),
                user.Id.ToString(), after: "вход отклонён", reason: WithAddress(SignInRestrictionRules.Describe(denial)), correlationId: currentUser.CorrelationId));
        }

        return (null, denials[0].Denial == RestrictionDenial.Network ? SignInStatus.NetworkDenied : SignInStatus.ScheduleDenied);
    }

    /// <summary>
    /// D127: «одно устройство» — другие действующие сессии пользователя закроются через 30 с (на старом устройстве — предупреждение
    /// с отсчётом), запись в журнал организации.
    /// </summary>
    private async Task ApplySessionRestrictionsAsync(long userId, long organizationId, RestrictionVerdict verdict, Guid keepSessionId, CancellationToken ct)
    {
        if (verdict.Restriction?.SingleDevice != true)
        {
            return;
        }

        var now = clock.UtcNow;
        var endsAt = now + UserSession.EndWarning;
        var marked = await db.UserSessions
            .Where(x => x.UserId == userId && x.PublicId != keepSessionId && x.RevokedAtUtc == null && (x.EndsAtUtc == null || x.EndsAtUtc > endsAt))
            .ExecuteUpdateAsync(x => x.SetProperty(v => v.EndsAtUtc, endsAt).SetProperty(v => v.EndReason, SessionEndReason.OtherDevice), ct);
        if (marked > 0)
        {
            db.AuditEntries.Add(AuditEntry.Create(now, organizationId, userId, AuditActions.SessionEnding, nameof(UserAccount), userId.ToString(),
                after: $"прежние сессии ({marked}) закроются {endsAt:yyyy-MM-dd HH:mm:ss} UTC",
                reason: WithAddress("вход с другого устройства (одно устройство)"), correlationId: currentUser.CorrelationId));
        }
    }

    private async Task<SignInResult> FailAsync(UserAccount user, string reason, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var lockedNow = user.RegisterFailedSignIn(now);
        await AuditAsync(user, AuditActions.SignInFailed, reason, ct);

        // Параллельные попытки подбора меняют одну строку users: при конфликте rowversion перечитываем
        // счётчик и засчитываем попытку заново, чтобы ни одна неудача не потерялась.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(ct);
                break;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < 5)
            {
                foreach (var entry in ex.Entries)
                {
                    await entry.ReloadAsync(ct);
                }

                lockedNow = !user.IsLockedOut(now) && user.RegisterFailedSignIn(now);
            }
        }

        if (lockedNow)
        {
            await AuditAsync(user, AuditActions.LockedOut,
                $"{SignInPolicy.MaxFailedAttempts} неудачных попыток подряд, вход закрыт до {user.LockoutEndUtc:yyyy-MM-dd HH:mm} UTC", ct);
            await db.SaveChangesAsync(ct);
        }

        return SignInResult.Of(user.IsLockedOut(now) ? SignInStatus.LockedOut : SignInStatus.Failed);
    }

    private async Task<SignInResult> SucceedAsync(UserAccount user, long organizationId, bool usedTwoFactor, CancellationToken ct, string? via = null)
    {
        user.RegisterSuccessfulSignIn();
        // D122: каждая выдача cookie — своя серверная сессия; в cookie — только её GUID.
        var session = UserSession.Create(user.Id, user.SecurityStamp, currentUser.UserAgent, currentUser.ClientAddress, clock.UtcNow);
        // D127: «одно устройство» — прежние сессии закроются через 30 с; конец времени работы близко — и эта сессия тоже.
        var verdict = await SignInRestrictionRules.EvaluateAsync(db, user.Id, organizationId, currentUser.ClientAddress, enforceUnknownAddress: true,
            clock.UtcNow, ct);
        if (SignInRestrictionRules.ScheduleEnd(verdict, clock.UtcNow) is { } scheduleEnd)
        {
            session.ScheduleEnd(scheduleEnd, SessionEndReason.Schedule);
        }

        await ApplySessionRestrictionsAsync(user.Id, organizationId, verdict, session.PublicId, ct);
        db.UserSessions.Add(session);
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, organizationId, user.Id, AuditActions.SignedIn, nameof(UserAccount),
            user.Id.ToString(), after: via ?? (usedTwoFactor ? "пароль и 2FA" : "пароль"), reason: WithAddress(null), correlationId: currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
        return new SignInResult(SignInStatus.Succeeded,
            new SessionIdentity(user.Id, user.DisplayName, user.SecurityStamp, organizationId, usedTwoFactor, user.Language, session.PublicId));
    }

    private async Task<UserAccount?> LoadPendingAsync(PendingSignIn pending, CancellationToken ct) =>
        await db.Users.SingleOrDefaultAsync(u => u.Id == pending.UserId && u.SecurityStamp == pending.SecurityStamp
                                                 && u.Status == UserStatus.Active, ct);

    private async Task<UserAccount?> FindBySetupTokenAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
        {
            return null;
        }

        var hash = SetupTokens.Hash(token);
        return await db.Users.SingleOrDefaultAsync(u => u.SetupTokenHash == hash && u.Status != UserStatus.Archived, ct);
    }

    private async Task<List<long>> ActiveOrganizationIdsAsync(long userId, CancellationToken ct) =>
        await db.OrganizationMembers
            .Where(m => m.UserId == userId && m.Status == MembershipStatus.Active)
            .Join(db.Organizations.Where(o => !o.IsArchived), m => m.OrganizationId, o => o.Id, (m, o) => o.Id)
            .OrderBy(id => id)
            .ToListAsync(ct);

    /// <summary>
    /// Владелец и Администратор входят только со вторым фактором — в любой своей организации,
    /// иначе их сессия в другой организации обходила бы требование.
    /// </summary>
    private async Task<bool> RequiresTwoFactorAsync(long userId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var roleCodes = await db.RoleAssignments
            .Where(a => a.UserId == userId && a.RoleId != null && a.RevokedAtUtc == null
                        && a.ValidFromUtc <= now && (a.ValidToUtc == null || a.ValidToUtc > now))
            .Join(db.Roles, a => a.RoleId, r => (long?)r.Id, (a, r) => r.Code)
            .Distinct()
            .ToListAsync(ct);
        return roleCodes.Any(SignInPolicy.RequiresTwoFactor);
    }

    /// <summary>
    /// Событие входа пишется в журнал каждой организации пользователя: так его видят администратор и аудитор,
    /// а сотрудник видит свои входы в «своих» записях.
    /// </summary>
    private async Task AuditAsync(UserAccount user, string action, string reason, CancellationToken ct)
    {
        var orgIds = await db.OrganizationMembers.Where(m => m.UserId == user.Id).Select(m => (long?)m.OrganizationId).ToListAsync(ct);
        if (orgIds.Count == 0)
        {
            orgIds.Add(null);
        }

        foreach (var orgId in orgIds)
        {
            db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, orgId, user.Id, action, nameof(UserAccount),
                user.Id.ToString(), reason: WithAddress(reason), correlationId: currentUser.CorrelationId));
        }
    }

    /// <summary>Адрес, с которого пришёл запрос, — в причину записи журнала входов (поля журнала не меняются, D56).</summary>
    private string? WithAddress(string? reason) => WithAddress(reason, currentUser.ClientAddress);

    private static string? WithAddress(string? reason, string? address) => address is { } ip
        ? reason is null ? $"адрес {ip}" : $"{reason} · адрес {ip}"
        : reason;
}
