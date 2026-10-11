using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Organizations;

public sealed record OrganizationDto(
    long Id,
    string FullName,
    string ShortName,
    string Inn,
    string? Kpp,
    bool KppVerified,
    string? ActualAddress,
    string TimeZoneId,
    string CurrencyCode,
    string? WebsiteUrl,
    bool CanEdit,
    byte[] RowVersion,
    string CountryCode = KnitErp.Domain.Organizations.Countries.Russia)
{
    public KnitErp.Domain.Organizations.CountryInfo Country => KnitErp.Domain.Organizations.Countries.Get(CountryCode);
}

public sealed record UpdateRequisitesCommand(string? ActualAddress, string TimeZoneId, byte[] RowVersion, string? WebsiteUrl = null);

public sealed record CreateOrganizationCommand(
    string FullName,
    string ShortName,
    string Inn,
    string? Kpp,
    bool KppVerified,
    string TimeZoneId,
    string OwnerEmail,
    string OwnerDisplayName,
    string CountryCode = KnitErp.Domain.Organizations.Countries.Russia);

/// <summary>OwnerSetupToken — ссылка установки пароля Владельца (null, если у него уже есть пароль).</summary>
public sealed record CreatedOrganization(long OrganizationId, long OwnerUserId, string? OwnerSetupToken);

public sealed class OrganizationService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock)
{
    public async Task<OrganizationDto> GetCurrentAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.OrganizationView, ct);
        return await LoadAsync(ctx, ctx.OrganizationId, ct);
    }

    /// <summary>Чужая организация отвечает «не найдено», даже если существует (приёмка §4.14 «б»).</summary>
    public async Task<OrganizationDto> GetByIdAsync(long organizationId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.OrganizationView, ct);
        if (organizationId != ctx.OrganizationId)
        {
            throw new NotFoundException("Организация");
        }

        return await LoadAsync(ctx, organizationId, ct);
    }

    public async Task<OrganizationDto> UpdateRequisitesAsync(UpdateRequisitesCommand cmd, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.OrganizationEdit, ct);
        var org = await db.Organizations.SingleOrDefaultAsync(o => o.Id == ctx.OrganizationId, ct)
                  ?? throw new NotFoundException("Организация");

        if (!org.RowVersion.AsSpan().SequenceEqual(cmd.RowVersion))
        {
            throw new ConcurrencyConflictException();
        }

        var changes = org.UpdateRequisites(cmd.ActualAddress, cmd.TimeZoneId, cmd.WebsiteUrl);
        foreach (var c in changes)
        {
            db.AuditEntries.Add(AuditEntry.Create(
                clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.OrganizationRequisitesChanged,
                nameof(Organization), org.Id.ToString(), c.Before, c.After, c.Field, currentUser.CorrelationId));
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConcurrencyConflictException();
        }

        return await LoadAsync(ctx, org.Id, ct);
    }

    /// <summary>
    /// Первичное создание организации с системными ролями P0 и Владельцем. Системная операция при развёртывании,
    /// поэтому выполняется без пользователя-автора.
    /// </summary>
    public async Task<CreatedOrganization> CreateWithOwnerAsync(CreateOrganizationCommand cmd, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        await using var tx = await db.BeginTransactionAsync(ct);

        var org = Organization.Create(cmd.FullName, cmd.ShortName, cmd.Inn, cmd.Kpp, cmd.KppVerified, cmd.TimeZoneId, now, cmd.CountryCode);
        db.Organizations.Add(org);

        var normalized = cmd.OwnerEmail.Trim().ToUpperInvariant();
        var owner = await db.Users.SingleOrDefaultAsync(u => u.NormalizedEmail == normalized, ct);
        if (owner is null)
        {
            owner = UserAccount.Invite(cmd.OwnerEmail, cmd.OwnerDisplayName, now);
            owner.Activate();
            db.Users.Add(owner);
        }

        var ownerToken = owner.HasPassword ? null : owner.IssueSetupToken(now);

        await db.SaveChangesAsync(ct);

        var roles = SystemRoles.Ordered.Select(code => Role.CreateSystem(org.Id, code)).ToList();
        db.Roles.AddRange(roles);
        Catalog.CatalogService.SeedDefaultUnits(db, org.Id);
        Warehousing.OperationReasonService.SeedDefaults(db, org.Id);
        Sales.SalesStageService.SeedDefaults(db, org.Id);
        LegalEntityService.SeedDefault(db, org);
        Catalog.NomenclatureService.SeedDefaultPriceType(db, org.Id);
        Finance.CashFlowItemService.SeedDefaults(db, org.Id);
        db.VatRates.AddRange(KnitErp.Domain.Catalog.VatRate.DefaultsFor(org.Id, org.CountryCode));
        db.OrganizationMembers.Add(OrganizationMember.Join(org.Id, owner.Id, now));
        await db.SaveChangesAsync(ct);

        // D110: правила автоматической смены этапа — к этапам по умолчанию (их id известны после сохранения).
        Sales.StageAutomation.SeedDefaults(db, org.Id, db.SalesOrderStages.Local.Where(st => st.OrganizationId == org.Id));
        var ownerRole = roles.Single(r => r.Code == SystemRoles.Owner);
        db.RoleAssignments.Add(RoleAssignment.ForRole(org.Id, owner.Id, ownerRole.Id, owner.Id, now, "Создание организации"));
        db.AuditEntries.Add(AuditEntry.Create(now, org.Id, null, AuditActions.OrganizationCreated, nameof(Organization),
            org.Id.ToString(), after: org.ShortName, correlationId: currentUser.CorrelationId));
        db.AuditEntries.Add(AuditEntry.Create(now, org.Id, null, AuditActions.AdminRoleGranted, nameof(UserAccount),
            owner.Id.ToString(), before: "нет доступа", after: SystemRoles.NameOf(SystemRoles.Owner),
            reason: "Создание организации", correlationId: currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);

        await tx.CommitAsync(ct);
        return new CreatedOrganization(org.Id, owner.Id, ownerToken);
    }

    private async Task<OrganizationDto> LoadAsync(AccessContext ctx, long organizationId, CancellationToken ct)
    {
        var o = await db.Organizations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == organizationId, ct)
                ?? throw new NotFoundException("Организация");
        return new OrganizationDto(o.Id, o.FullName, o.ShortName, o.Inn, o.Kpp, o.KppVerified, o.ActualAddress,
            o.TimeZoneId, o.CurrencyCode, o.WebsiteUrl, ctx.Permissions.Has(Permissions.OrganizationEdit), o.RowVersion, o.CountryCode);
    }
}
