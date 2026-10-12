using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Authentication;

/// <summary>
/// D123: повторное подтверждение (step-up) опасных действий администратора. После ввода пароля (и кода из приложения, если
/// подключена 2FA — та же проверка, что у смены пароля, D115) сессия получает отметку «недавно подтверждено» на 10 минут.
/// Отметка хранится только на сервере — в объекте области DI (в Blazor это цепь вкладки, в HTTP-запросе — сам запрос),
/// клиент её не видит и подделать не может. Перезагрузка вкладки, выход и токен API отметки не имеют.
/// </summary>
public static class StepUp
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public const string RequiredCode = "auth.stepup.required";

    public const string CancelledCode = "auth.stepup.cancelled";

    // Код и текст — литералами в new BusinessRuleException(...): так сообщение попадает в словари перевода.
    public static BusinessRuleException Required() =>
        new BusinessRuleException("auth.stepup.required", "Подтвердите действие паролем или кодом из приложения.");

    public static BusinessRuleException Cancelled() =>
        new BusinessRuleException("auth.stepup.cancelled", "Действие не выполнено: подтверждение отменено.");
}

/// <summary>Отметка «недавно подтверждено» одной сессии (области DI). Только на сервере.</summary>
public sealed class StepUpState
{
    private readonly Lock _gate = new();
    private long? _userId;
    private DateTime? _verifiedAtUtc;

    internal void Grant(long userId, DateTime nowUtc)
    {
        lock (_gate)
        {
            (_userId, _verifiedAtUtc) = (userId, nowUtc);
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            (_userId, _verifiedAtUtc) = (null, null);
        }
    }

    /// <summary>Отметка того же пользователя и не старше 10 минут. Часы назад (at &gt; now) — недействительна.</summary>
    internal bool IsValid(long userId, DateTime nowUtc)
    {
        lock (_gate)
        {
            return _userId == userId && _verifiedAtUtc is { } at && at <= nowUtc && nowUtc - at < StepUp.Lifetime;
        }
    }
}

/// <summary>Проверка отметки перед опасным действием. Вызывается после проверки прав: без права — отказ, а не окно подтверждения.</summary>
public sealed class StepUpGuard(StepUpState state, ICurrentUser currentUser, IClock clock)
{
    public bool IsFresh(long userId) => currentUser.ApiTokenId is null && state.IsValid(userId, clock.UtcNow);

    /// <summary>Нет свежего подтверждения — BusinessRuleException «auth.stepup.required»; экран показывает окно и повторяет действие.</summary>
    public void Demand(AccessContext ctx)
    {
        // Запрос по токену API подтвердить нечем: опасные действия — только из браузера.
        if (!IsFresh(ctx.UserId))
        {
            throw StepUp.Required();
        }
    }
}

/// <summary>
/// Ввод пароля (и кода) для отметки. Свой DbContext на операцию: окно подтверждения открыто поверх страницы, у которой
/// в контексте могли остаться изменения отклонённого действия — их нельзя сохранить заодно с журналом.
/// </summary>
public sealed class StepUpService(IKnitErpDbContextFactory factory, IPasswordHasher<UserAccount> hasher, ICurrentUser currentUser, IClock clock,
    StepUpState state)
{
    public async Task<bool> TwoFactorRequiredAsync(CancellationToken ct = default)
    {
        var userId = currentUser.UserId ?? throw new NotFoundException("Пользователь");
        await using var db = factory.Create();
        return await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.TwoFactorEnabled).SingleAsync(ct);
    }

    public async Task ConfirmAsync(string? password, string? code, CancellationToken ct = default)
    {
        if (currentUser.ApiTokenId is not null)
        {
            throw StepUp.Required();
        }

        await using var db = factory.Create();
        UserAccount user;
        try
        {
            user = await IdentityConfirmation.ConfirmAsync(db, hasher, currentUser, clock, password, code,
                AuditActions.StepUpFailed, "неверный пароль при повторном подтверждении", "неверный код 2FA при повторном подтверждении", ct);
        }
        catch (BusinessRuleException)
        {
            state.Clear();
            throw;
        }

        state.Grant(user.Id, clock.UtcNow);
        await SecurityEvents.AuditAsync(db, currentUser, clock, user, AuditActions.StepUpConfirmed,
            $"повторное подтверждение на {StepUp.Lifetime.TotalMinutes:0} мин", ct);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Проверка текущего пароля и (при 2FA) кода — общая для D115 (смена пароля, выход везде) и D123 (повторное подтверждение).
/// Неверный пароль или код — неудачная попытка входа (общий счётчик блокировки) и запись в журнал.
/// </summary>
internal static class IdentityConfirmation
{
    public static async Task<UserAccount> ConfirmAsync(IKnitErpDbContext db, IPasswordHasher<UserAccount> hasher, ICurrentUser currentUser, IClock clock,
        string? password, string? code, string failureAction, string wrongPasswordReason, string wrongCodeReason, CancellationToken ct)
    {
        var userId = currentUser.UserId ?? throw new NotFoundException("Пользователь");
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId && u.Status == UserStatus.Active, ct)
                   ?? throw new NotFoundException("Пользователь");
        var now = clock.UtcNow;
        if (user.IsLockedOut(now))
        {
            throw new BusinessRuleException("auth.locked", "Вход временно заблокирован после неудачных попыток. Попробуйте позже.");
        }

        var passwordOk = user.PasswordHash is not null
                         && hasher.VerifyHashedPassword(user, user.PasswordHash, password ?? string.Empty) != PasswordVerificationResult.Failed;
        var codeOk = !user.TwoFactorEnabled || user.VerifyTotp(code, now);
        if (passwordOk && codeOk)
        {
            return user;
        }

        user.RegisterFailedSignIn(now);
        await SecurityEvents.AuditAsync(db, currentUser, clock, user, failureAction, passwordOk ? wrongCodeReason : wrongPasswordReason, ct);
        await db.SaveChangesAsync(ct);
        if (user.TwoFactorEnabled)
        {
            throw new BusinessRuleException("auth.confirm.failed", "Неверный текущий пароль или код из приложения.");
        }

        throw new BusinessRuleException("auth.confirm.failed", "Неверный текущий пароль.");
    }
}
