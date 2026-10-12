using KnitErp.Domain.Common;

namespace KnitErp.Domain.Access;

/// <summary>Проверки выдачи прав (ТЗ §4.12 KA3657, приёмка §4.14 «д»).</summary>
public static class GrantPolicy
{
    /// <summary>
    /// Нельзя выдать право выше своего (D128): по каждому коду уровень не выше своего (Ч, затем О, затем Д), право на всю организацию —
    /// только при своём праве на всю организацию («Д», для «Ч» — «Ч» или «Д»), право в области (подразделение, склад) — только в
    /// пределах своей области. Права «П» и «С» в роли не дают доступа и не проверяются. <paramref name="scope"/> — область
    /// назначения; null — вся организация (особые права, определение своей роли).
    /// </summary>
    public static void EnsureNoEscalation(
        EffectivePermissionSet grantor, IEnumerable<(string PermissionCode, PermissionLevel Level)> granted, GrantScope? scope = null)
    {
        // Допущение D23: у Владельца (право выдачи административных прав) нет части прав из-за разделения обязанностей
        // (например, создание начальных остатков), но он должен назначать любые роли организации.
        if (grantor.Has(Permissions.AdminGrant))
        {
            return;
        }

        scope ??= GrantScope.Organization;
        foreach (var (code, level) in granted)
        {
            if (!level.Grants())
            {
                continue;
            }

            var own = grantor.LevelOf(code);
            if (own == PermissionLevel.None)
            {
                throw Escalation(code);
            }

            if (own == PermissionLevel.Full)
            {
                continue;
            }

            if (scope.IsUnspecified)
            {
                // Подсказка формы до выбора области: «Ч» не даёт выдать больше чтения; «О» можно выдать в своей области.
                if (own == PermissionLevel.ReadOnly && level != PermissionLevel.ReadOnly)
                {
                    throw Escalation(code);
                }

                continue;
            }

            if (scope.IsOrganizationWide)
            {
                // Вся организация: «Ч» — только при своём «Ч»; остальное — только при своём «Д» (проверено выше).
                if (own == PermissionLevel.ReadOnly && level == PermissionLevel.ReadOnly)
                {
                    continue;
                }

                throw own == PermissionLevel.Scoped
                    ? new BusinessRuleException(
                        "access.escalation.scope",
                        $"Нельзя выдать право на всю организацию: «{Permissions.Describe(code)}» у вас только в своей области.")
                    : Escalation(code);
            }

            // Назначение с областью делает право «своей областью» (EffectivePermissionSet.Compute): нужно своё «О», и область
            // назначения — внутри своей (подразделение — своё или вложенное, склад — свой).
            if (own != PermissionLevel.Scoped)
            {
                throw Escalation(code);
            }

            var mine = grantor.ScopeOf(code);
            var departmentOk = scope.DepartmentId is null || scope.DepartmentLineage.Any(mine.DepartmentIds.Contains);
            var warehouseOk = scope.WarehouseId is not { } w || mine.WarehouseIds.Contains(w);
            if (!departmentOk || !warehouseOk)
            {
                throw new BusinessRuleException(
                    "access.escalation.scope",
                    $"Нельзя выдать право за пределами своей области: «{Permissions.Describe(code)}».");
            }
        }
    }

    private static BusinessRuleException Escalation(string code) =>
        new BusinessRuleException("access.escalation", $"Нельзя выдать право выше своего: «{Permissions.Describe(code)}».");

    /// <summary>Выдача административных прав — только Владелец (ТЗ §4.8 KA3634).</summary>
    public static void EnsureCanGrantAdministrative(EffectivePermissionSet grantor, bool isAdministrative)
    {
        if (isAdministrative && !grantor.Has(Permissions.AdminGrant))
        {
            throw new BusinessRuleException(
                "access.admin_grant_denied",
                "Выдача административных прав — только Владелец организации.");
        }
    }

    public static void EnsureReason(bool required, string? reason)
    {
        if (required && string.IsNullOrWhiteSpace(reason))
        {
            throw new BusinessRuleException(
                "access.reason.required",
                "Причина обязательна при выдаче административных прав и при запрете.");
        }
    }

    public static void EnsureValidPeriod(DateTime validFromUtc, DateTime? validToUtc)
    {
        if (validToUtc is { } to && to <= validFromUtc)
        {
            throw new BusinessRuleException("access.period.invalid", "Срок окончания должен быть позже начала.");
        }
    }

    /// <summary>Нельзя оставить организацию без активного Владельца.</summary>
    public static void EnsureOwnerRemains(int activeOwnersAfterChange)
    {
        if (activeOwnersAfterChange < 1)
        {
            throw new BusinessRuleException(
                "access.last_owner",
                "Нельзя оставить организацию без активного Владельца.");
        }
    }
}

/// <summary>
/// Область назначения для проверки выдачи (D128). DepartmentLineage — подразделение назначения и все его предки: область выдающего
/// «своё подразделение» включает вложенные (D29), поэтому назначение в подразделение внутри своего допустимо.
/// </summary>
public sealed record GrantScope(long? DepartmentId, long? WarehouseId, IReadOnlyCollection<long> DepartmentLineage)
{
    public static GrantScope Organization { get; } = new(null, null, []);

    /// <summary>Область ещё не выбрана (список ролей в форме): проверяются только уровни.</summary>
    public static GrantScope Unspecified { get; } = new(null, null, []) { IsUnspecified = true };

    public bool IsUnspecified { get; private init; }

    public bool IsOrganizationWide => !IsUnspecified && DepartmentId is null && WarehouseId is null;
}
