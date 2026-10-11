using KnitErp.Domain.Common;

namespace KnitErp.Domain.Access;

public enum UserStatus : byte
{
    Invited = 1,
    Active = 2,
    Archived = 4,
}

/// <summary>
/// Учётная запись человека. Не принадлежит одной организации: доступ к организациям даётся назначениями.
/// Удаления нет — только блокировка и архив (ТЗ §4.10 п.5).
/// </summary>
public sealed class UserAccount
{
    public const int EmailMaxLength = 254;
    public const int DisplayNameMaxLength = 200;

    private UserAccount()
    {
    }

    public long Id { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string NormalizedEmail { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public UserStatus Status { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>Меняется при блокировке и смене прав — по нему сервер закрывает старые сессии.</summary>
    public Guid SecurityStamp { get; private set; }

    /// <summary>Хеш пароля ASP.NET Core Identity (PBKDF2). Пароль в открытом виде не хранится.</summary>
    public string? PasswordHash { get; private set; }

    /// <summary>Неудачные попытки входа подряд; сбрасывается после успешного входа или блокировки.</summary>
    public int FailedSignInCount { get; private set; }

    public DateTime? LockoutEndUtc { get; private set; }

    public bool TwoFactorEnabled { get; private set; }

    /// <summary>Секрет приложения-аутентификатора (Base32), как в ASP.NET Core Identity.</summary>
    public string? AuthenticatorKey { get; private set; }

    /// <summary>Последний принятый шаг TOTP — один код нельзя использовать дважды.</summary>
    public long? LastTotpStep { get; private set; }

    /// <summary>SHA-256 ссылки приглашения или первичной установки пароля. Сама ссылка не хранится.</summary>
    /// <summary>Язык интерфейса (D60); null — русский.</summary>
    public string? Language { get; private set; }

    public byte[]? SetupTokenHash { get; private set; }

    public DateTime? SetupTokenExpiresAtUtc { get; private set; }

    /// <summary>Когда выдана текущая ссылка установки пароля — для ограничения повторных писем «Забыли пароль».</summary>
    public DateTime? SetupTokenIssuedAtUtc { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public bool HasPassword => PasswordHash is not null;

    public static UserAccount Invite(string email, string displayName, DateTime nowUtc)
    {
        var e = email?.Trim() ?? string.Empty;
        if (e.Length is 0 or > EmailMaxLength || e.IndexOf('@') <= 0 || e.LastIndexOf('.') < e.IndexOf('@'))
        {
            throw new BusinessRuleException("user.email.invalid", "Укажите корректный email.");
        }

        var name = displayName?.Trim() ?? string.Empty;
        if (name.Length is 0 or > DisplayNameMaxLength)
        {
            throw new BusinessRuleException("user.name.invalid", "Укажите имя пользователя (до 200 символов).");
        }

        return new UserAccount
        {
            Email = e,
            NormalizedEmail = e.ToUpperInvariant(),
            DisplayName = name,
            Status = UserStatus.Invited,
            CreatedAtUtc = nowUtc,
            SecurityStamp = Guid.NewGuid(),
        };
    }

    public void Activate()
    {
        if (Status != UserStatus.Invited)
        {
            throw new BusinessRuleException("user.status.transition", "Активировать можно только приглашённого пользователя.");
        }

        Status = UserStatus.Active;
    }

    public void SetLanguage(string? code)
    {
        var language = UiLanguages.Find(code)
                       ?? throw new BusinessRuleException("user.language", "Неизвестный язык интерфейса.");
        Language = language.Code == UiLanguages.Default ? null : language.Code;
    }

    /// <summary>Смена штампа закрывает все сессии пользователя (блокировка, смена прав).</summary>
    public void RotateSecurityStamp() => SecurityStamp = Guid.NewGuid();

    /// <summary>
    /// Выдаёт одноразовую ссылку установки пароля на <see cref="SignInPolicy.InvitationLifetime"/>.
    /// Предыдущая ссылка перестаёт действовать. Возвращает токен для ссылки — он показывается один раз.
    /// </summary>
    public string IssueSetupToken(DateTime nowUtc)
    {
        if (Status == UserStatus.Archived || HasPassword)
        {
            throw new BusinessRuleException("auth.invitation.not_needed", "Пользователь уже установил пароль, приглашение не нужно.");
        }

        return NewSetupToken(nowUtc, SignInPolicy.InvitationLifetime);
    }

    /// <summary>
    /// Ссылка смены пароля (D81): по почте «Забыли пароль» или от администратора. Только для активной учётной записи
    /// с паролем; прежний пароль действует, пока по ссылке не задан новый. Второй фактор ссылка не отменяет.
    /// </summary>
    public string IssuePasswordResetToken(DateTime nowUtc, TimeSpan lifetime)
    {
        if (Status != UserStatus.Active || !HasPassword)
        {
            throw new BusinessRuleException("auth.reset.not_active", "Смена пароля по ссылке — только для активного пользователя с паролем.");
        }

        return NewSetupToken(nowUtc, lifetime);
    }

    /// <summary>Недавно выдана ссылка — повторное письмо не отправляется.</summary>
    public bool ResetRecentlyIssued(DateTime nowUtc) =>
        SetupTokenIssuedAtUtc is { } issued && nowUtc - issued < SignInPolicy.PasswordResetResendInterval;

    private string NewSetupToken(DateTime nowUtc, TimeSpan lifetime)
    {
        var token = SetupTokens.Generate();
        SetupTokenHash = SetupTokens.Hash(token);
        SetupTokenExpiresAtUtc = nowUtc + lifetime;
        SetupTokenIssuedAtUtc = nowUtc;
        return token;
    }

    /// <summary>
    /// Аварийное восстановление (D06): ссылка установки нового пароля даже при заданном пароле. Только команда сервера —
    /// в интерфейсе её нет. Прежний пароль действует, пока по ссылке не задан новый.
    /// </summary>
    public string IssueRecoveryToken(DateTime nowUtc)
    {
        if (Status == UserStatus.Archived)
        {
            throw new BusinessRuleException("auth.recovery.archived", "Учётная запись в архиве.");
        }

        var token = NewSetupToken(nowUtc, SignInPolicy.InvitationLifetime);
        FailedSignInCount = 0;
        LockoutEndUtc = null;
        return token;
    }

    /// <summary>Установка пароля по ссылке приглашения. Приглашённый пользователь становится активным.</summary>
    public void CompleteSetup(string token, string passwordHash, DateTime nowUtc)
    {
        if (SetupTokenHash is null || !SetupTokens.Matches(token, SetupTokenHash))
        {
            throw new BusinessRuleException("auth.invitation.invalid", "Ссылка приглашения недействительна.");
        }

        if (SetupTokenExpiresAtUtc is not { } expires || expires <= nowUtc)
        {
            throw new BusinessRuleException("auth.invitation.expired", "Срок приглашения истёк. Попросите администратора отправить новое.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
        SetupTokenHash = null;
        SetupTokenExpiresAtUtc = null;
        SetupTokenIssuedAtUtc = null;
        FailedSignInCount = 0;
        LockoutEndUtc = null;
        if (Status == UserStatus.Invited)
        {
            Activate();
        }

        RotateSecurityStamp();
    }

    /// <summary>Пересчёт хеша при смене параметров PBKDF2 — пароль тот же, сессии не закрываются.</summary>
    public void UpgradePasswordHash(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
    }

    public bool IsLockedOut(DateTime nowUtc) => LockoutEndUtc is { } end && end > nowUtc;

    /// <summary>Неудачная попытка входа (пароль или код 2FA). Возвращает true, если учётная запись только что заблокирована.</summary>
    public bool RegisterFailedSignIn(DateTime nowUtc)
    {
        FailedSignInCount++;
        if (FailedSignInCount < SignInPolicy.MaxFailedAttempts)
        {
            return false;
        }

        FailedSignInCount = 0;
        LockoutEndUtc = nowUtc + SignInPolicy.LockoutDuration;
        return true;
    }

    public void RegisterSuccessfulSignIn()
    {
        FailedSignInCount = 0;
        LockoutEndUtc = null;
    }

    /// <summary>Новый секрет аутентификатора, пока 2FA не подтверждена кодом. Включённую 2FA так не перезаписать.</summary>
    public string BeginAuthenticatorSetup()
    {
        if (TwoFactorEnabled)
        {
            throw new BusinessRuleException("auth.2fa.already_enabled", "Двухфакторная аутентификация уже включена.");
        }

        AuthenticatorKey ??= Totp.NewKey();
        return AuthenticatorKey;
    }

    /// <summary>Проверяет код TOTP и запоминает его шаг. Повтор уже принятого кода отклоняется.</summary>
    public bool VerifyTotp(string? code, DateTime nowUtc)
    {
        if (AuthenticatorKey is null || Totp.FindMatchingStep(AuthenticatorKey, code, nowUtc) is not { } step)
        {
            return false;
        }

        if (LastTotpStep is { } last && step <= last)
        {
            return false;
        }

        LastTotpStep = step;
        return true;
    }

    /// <summary>Включает 2FA после проверки первого кода. Старые сессии закрываются.</summary>
    public bool ConfirmTwoFactor(string? code, DateTime nowUtc)
    {
        if (TwoFactorEnabled)
        {
            throw new BusinessRuleException("auth.2fa.already_enabled", "Двухфакторная аутентификация уже включена.");
        }

        if (!VerifyTotp(code, nowUtc))
        {
            return false;
        }

        TwoFactorEnabled = true;
        RotateSecurityStamp();
        return true;
    }

    /// <summary>Сброс 2FA (потерян телефон). При следующем входе пользователь подключит аутентификатор заново.</summary>
    public void ResetTwoFactor()
    {
        TwoFactorEnabled = false;
        AuthenticatorKey = null;
        LastTotpStep = null;
        RotateSecurityStamp();
    }
}

public enum MembershipStatus : byte
{
    Active = 1,
    Blocked = 2,
}

/// <summary>
/// Участие пользователя в организации. Блокировка действует в пределах организации:
/// у одного человека может быть несколько организаций (ORG 01), и администратор одной не блокирует его в другой.
/// </summary>
public sealed class OrganizationMember
{
    private OrganizationMember()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long UserId { get; private set; }
    public MembershipStatus Status { get; private set; }
    public DateTime JoinedAtUtc { get; private set; }
    public DateTime? BlockedAtUtc { get; private set; }
    public long? BlockedByUserId { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static OrganizationMember Join(long organizationId, long userId, DateTime nowUtc) => new()
    {
        OrganizationId = organizationId,
        UserId = userId,
        Status = MembershipStatus.Active,
        JoinedAtUtc = nowUtc,
    };

    public void Block(long byUserId, DateTime nowUtc)
    {
        if (Status == MembershipStatus.Blocked)
        {
            throw new BusinessRuleException("member.already_blocked", "Пользователь уже заблокирован.");
        }

        Status = MembershipStatus.Blocked;
        BlockedAtUtc = nowUtc;
        BlockedByUserId = byUserId;
    }

    public void Unblock()
    {
        if (Status != MembershipStatus.Blocked)
        {
            throw new BusinessRuleException("member.not_blocked", "Пользователь не заблокирован.");
        }

        Status = MembershipStatus.Active;
        BlockedAtUtc = null;
        BlockedByUserId = null;
    }
}

/// <summary>Роль организации — шаблон набора прав (ТЗ §4.6 п.4).</summary>
public sealed class Role
{
    private readonly List<RolePermission> _permissions = [];

    private Role()
    {
    }

    public const int NameMaxLength = 150;
    public const int DescriptionMaxLength = 300;
    public const string CustomPrefix = "custom.";

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public bool IsSystem { get; private set; }

    /// <summary>D112: описание своей роли — для кого она и что даёт.</summary>
    public string? Description { get; private set; }

    /// <summary>D112: своя роль в архиве не выдаётся; выданные назначения нужно сначала сменить.</summary>
    public bool IsArchived { get; private set; }

    public byte[] RowVersion { get; private set; } = [];
    public IReadOnlyCollection<RolePermission> Permissions => _permissions;

    /// <summary>
    /// Административная роль: системные Владелец и Администратор, а своя — если даёт управление пользователями (D112):
    /// такую роль создаёт, меняет и выдаёт только Владелец.
    /// </summary>
    public bool IsAdministrative => IsSystem
        ? SystemRoles.IsAdministrative(Code)
        : _permissions.Any(p => p.Level.Grants() && KnitErp.Domain.Access.Permissions.IsAdministrative(p.PermissionCode));

    /// <summary>
    /// Своя роль организации (D112): название, описание и набор прав «да». Право выдачи административных прав в свою роль не входит —
    /// оно только у Владельца.
    /// </summary>
    public static Role CreateCustom(long organizationId, string? name, string? description, IEnumerable<string> permissionCodes)
    {
        var role = new Role { OrganizationId = organizationId, Code = CustomPrefix + Guid.NewGuid().ToString("N")[..16] };
        role.Update(name, description, permissionCodes);
        return role;
    }

    public void Update(string? name, string? description, IEnumerable<string> permissionCodes)
    {
        if (IsSystem)
        {
            throw new BusinessRuleException("access.role.system", "Системную роль менять нельзя — создайте свою на её основе.");
        }

        if (IsArchived)
        {
            throw new BusinessRuleException("access.role.archived", "Роль в архиве. Сначала верните её.");
        }

        Name = DomainText.Require(name, NameMaxLength, "Название роли");
        var d = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        Description = d is { Length: > DescriptionMaxLength }
            ? throw new BusinessRuleException("access.role.description", $"Описание — не длиннее {DescriptionMaxLength} символов.")
            : d;
        var codes = permissionCodes.Distinct(StringComparer.Ordinal).ToList();
        if (codes.FirstOrDefault(c => !KnitErp.Domain.Access.Permissions.IsKnown(c)) is { } unknown)
        {
            throw new BusinessRuleException("access.permission.unknown", $"Неизвестное право: {unknown}.");
        }

        if (codes.Contains(KnitErp.Domain.Access.Permissions.AdminGrant))
        {
            throw new BusinessRuleException("access.role.admin_grant", "Выдача административных прав остаётся только у Владельца и в свою роль не входит.");
        }

        if (codes.Count == 0)
        {
            throw new BusinessRuleException("access.role.empty", "Отметьте хотя бы одно право.");
        }

        _permissions.RemoveAll(p => !codes.Contains(p.PermissionCode));
        foreach (var code in codes.Where(c => _permissions.All(p => p.PermissionCode != c)))
        {
            _permissions.Add(new RolePermission(code, PermissionLevel.Full));
        }
    }

    public void Archive()
    {
        if (IsSystem)
        {
            throw new BusinessRuleException("access.role.system", "Системную роль нельзя отправить в архив.");
        }

        IsArchived = true;
    }

    public void Restore() => IsArchived = false;

    /// <summary>Системная роль P0, наполненная из матрицы ТЗ §4.8.</summary>
    public static Role CreateSystem(long organizationId, string code)
    {
        var role = new Role
        {
            OrganizationId = organizationId,
            Code = code,
            Name = SystemRoles.NameOf(code),
            IsSystem = true,
        };

        foreach (var (permission, level) in RoleMatrixP0.PermissionsOf(code))
        {
            role._permissions.Add(new RolePermission(permission, level));
        }

        return role;
    }

    /// <summary>
    /// Включить или выключить право системной роли по настройке организации (D216: «Кадровик закрывает табель»).
    /// Только для прав, которые настройка переключает явно; матрица P0 остаётся источником значений по умолчанию.
    /// </summary>
    public bool Toggle(string permissionCode, PermissionLevel level, bool enabled)
    {
        var existing = _permissions.FirstOrDefault(p => p.PermissionCode == permissionCode);
        if (enabled && existing is null)
        {
            _permissions.Add(new RolePermission(permissionCode, level));
            return true;
        }

        if (!enabled && existing is not null)
        {
            _permissions.Remove(existing);
            return true;
        }

        return false;
    }
}

public sealed class RolePermission
{
    private RolePermission()
    {
    }

    public RolePermission(string permissionCode, PermissionLevel level)
    {
        PermissionCode = permissionCode;
        Level = level;
    }

    public long RoleId { get; private set; }
    public string PermissionCode { get; private set; } = string.Empty;
    public PermissionLevel Level { get; private set; }
}

/// <summary>
/// Назначение прав (ТЗ §4.11 KA3654): роль или индивидуальное право, разрешение или запрет, область и срок.
/// </summary>
public sealed class RoleAssignment
{
    public const int ReasonMaxLength = 500;

    private RoleAssignment()
    {
    }

    public Guid Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long UserId { get; private set; }
    public long? RoleId { get; private set; }
    public string? PermissionCode { get; private set; }
    public bool IsDeny { get; private set; }
    public long? DepartmentId { get; private set; }
    public long? WarehouseId { get; private set; }
    public DateTime ValidFromUtc { get; private set; }
    public DateTime? ValidToUtc { get; private set; }
    public long GrantedByUserId { get; private set; }
    public DateTime GrantedAtUtc { get; private set; }
    public string? Reason { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }
    public long? RevokedByUserId { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public bool IsActiveAt(DateTime nowUtc) =>
        RevokedAtUtc is null && ValidFromUtc <= nowUtc && (ValidToUtc is null || nowUtc < ValidToUtc);

    public static RoleAssignment ForRole(
        long organizationId, long userId, long roleId, long grantedBy, DateTime nowUtc,
        string? reason, DateTime? validToUtc = null, long? departmentId = null, long? warehouseId = null)
    {
        var a = new RoleAssignment
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            UserId = userId,
            RoleId = roleId,
            DepartmentId = departmentId,
            WarehouseId = warehouseId,
            ValidFromUtc = nowUtc,
            ValidToUtc = validToUtc,
            GrantedByUserId = grantedBy,
            GrantedAtUtc = nowUtc,
            Reason = NormalizeReason(reason),
        };
        GrantPolicy.EnsureValidPeriod(a.ValidFromUtc, a.ValidToUtc);
        return a;
    }

    public static RoleAssignment ForPermission(
        long organizationId, long userId, string permissionCode, bool isDeny, long grantedBy, DateTime nowUtc,
        string? reason, DateTime? validToUtc = null, long? departmentId = null, long? warehouseId = null)
    {
        if (!Permissions.IsKnown(permissionCode))
        {
            throw new BusinessRuleException("access.permission.unknown", $"Неизвестное право: {permissionCode}.");
        }

        var a = new RoleAssignment
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            UserId = userId,
            PermissionCode = permissionCode,
            IsDeny = isDeny,
            DepartmentId = departmentId,
            WarehouseId = warehouseId,
            ValidFromUtc = nowUtc,
            ValidToUtc = validToUtc,
            GrantedByUserId = grantedBy,
            GrantedAtUtc = nowUtc,
            Reason = NormalizeReason(reason),
        };
        GrantPolicy.EnsureValidPeriod(a.ValidFromUtc, a.ValidToUtc);
        GrantPolicy.EnsureReason(isDeny || Permissions.IsAdministrative(permissionCode), a.Reason);
        return a;
    }

    public void Revoke(long byUserId, DateTime nowUtc)
    {
        if (RevokedAtUtc is not null)
        {
            throw new BusinessRuleException("access.assignment.revoked", "Назначение уже отозвано.");
        }

        RevokedAtUtc = nowUtc;
        RevokedByUserId = byUserId;
    }

    private static string? NormalizeReason(string? reason)
    {
        var r = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (r is { Length: > ReasonMaxLength })
        {
            throw new BusinessRuleException("access.reason.too_long", $"Причина длиннее {ReasonMaxLength} символов.");
        }

        return r;
    }
}
