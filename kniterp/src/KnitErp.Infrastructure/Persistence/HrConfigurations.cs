using KnitErp.Domain.Hr;
using KnitErp.Domain.Organizations;
using KnitErp.Domain.Access;
using KnitErp.Domain.Structure;
using KnitErp.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace KnitErp.Infrastructure.Persistence;

/// <summary>Шифрование персональных данных в колонках (D200): AES-256-GCM мастер-ключом, как секреты 2FA и интеграций.</summary>
internal static class PersonalDataColumns
{
    public static readonly ValueConverter<string?, string?> Text = new(
        v => v == null ? null : SecretCipher.Encrypt(v),
        v => v == null ? null : SecretCipher.Decrypt(v));

    public static readonly ValueConverter<DateOnly?, string?> Date = new(
        v => v == null ? null : SecretCipher.Encrypt(v.Value.ToString("yyyy-MM-dd")),
        v => v == null ? null : DateOnly.ParseExact(SecretCipher.Decrypt(v), "yyyy-MM-dd"));

    public static readonly ValueConverter<byte[], string> Bytes = new(
        v => SecretCipher.Encrypt(Convert.ToBase64String(v)),
        v => Convert.FromBase64String(SecretCipher.Decrypt(v)));

    /// <summary>Длина колонки под шифртекст: значение в UTF-8 (кириллица — 2 байта) + nonce и тег, в Base64, с префиксом.</summary>
    public static int Cipher(int plainMaxLength) => (plainMaxLength * 2 + 28) * 4 / 3 + 40;
}

internal sealed class EmployeePersonalDataConfiguration : IEntityTypeConfiguration<EmployeePersonalData>
{
    public void Configure(EntityTypeBuilder<EmployeePersonalData> b)
    {
        b.ToTable("hr_personal_data", t =>
        {
            t.HasCheckConstraint("ck_hr_personal_data_gender", "[Gender] IN (0, 1, 2)");
            t.HasCheckConstraint("ck_hr_personal_data_consent",
                "([ConsentGivenOn] IS NOT NULL OR ([ConsentValidUntil] IS NULL AND [ConsentRevokedOn] IS NULL))"
                + " AND ([ConsentValidUntil] IS NULL OR [ConsentValidUntil] >= [ConsentGivenOn])"
                + " AND ([ConsentRevokedOn] IS NULL OR [ConsentRevokedOn] >= [ConsentGivenOn])");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Gender).HasConversion<byte>();
        Encrypted(b, x => x.BirthPlace, EmployeePersonalData.LongMaxLength);
        Encrypted(b, x => x.Citizenship, EmployeePersonalData.ShortMaxLength);
        Encrypted(b, x => x.PassportNumber, 20);
        Encrypted(b, x => x.PassportIssuer, EmployeePersonalData.LongMaxLength);
        b.Property(x => x.PassportIssuedOn).HasConversion(PersonalDataColumns.Date).HasMaxLength(PersonalDataColumns.Cipher(10));
        Encrypted(b, x => x.PassportDivisionCode, 7);
        Encrypted(b, x => x.Snils, 11);
        Encrypted(b, x => x.Inn, 12);
        Encrypted(b, x => x.RegistrationAddress, EmployeePersonalData.LongMaxLength);
        Encrypted(b, x => x.ActualAddress, EmployeePersonalData.LongMaxLength);
        Encrypted(b, x => x.Phone, 20);
        Encrypted(b, x => x.Education, EmployeePersonalData.LongMaxLength);
        Encrypted(b, x => x.Profession, EmployeePersonalData.ShortMaxLength);
        Encrypted(b, x => x.MaritalStatus, EmployeePersonalData.ShortMaxLength);
        Encrypted(b, x => x.FamilyMembers, EmployeePersonalData.LongMaxLength);
        b.Property(x => x.UpdatedAtUtc).HasColumnType("datetime2(3)");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Employee>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EmployeeId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_hr_personal_data_employee");
        b.HasIndex(x => new { x.OrganizationId, x.EmployeeId }).IsUnique().HasDatabaseName("ux_hr_personal_data_employee");
    }

    private static void Encrypted(EntityTypeBuilder<EmployeePersonalData> b, System.Linq.Expressions.Expression<Func<EmployeePersonalData, string?>> property, int plainMax) =>
        b.Property(property).HasConversion(PersonalDataColumns.Text).HasMaxLength(PersonalDataColumns.Cipher(plainMax));
}

internal sealed class ConsentScanConfiguration : IEntityTypeConfiguration<ConsentScan>
{
    public void Configure(EntityTypeBuilder<ConsentScan> b)
    {
        b.ToTable("hr_consent_scans", t =>
        {
            t.HasCheckConstraint("ck_hr_consent_scans_size", $"[SizeBytes] BETWEEN 1 AND {ConsentScan.MaxBytes}");
            t.HasTrigger("tr_hr_consent_scans_immutable");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.FileName).HasMaxLength(200).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(50).IsRequired();
        b.Property(x => x.Content).HasConversion(PersonalDataColumns.Bytes).HasColumnType("nvarchar(max)").IsRequired();
        b.Property(x => x.UploadedAtUtc).HasColumnType("datetime2(3)");
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Employee>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EmployeeId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_hr_consent_scans_employee");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UploadedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.EmployeeId }).HasDatabaseName("ix_hr_consent_scans_employee");
    }
}

internal sealed class StaffingPositionConfiguration : IEntityTypeConfiguration<StaffingPosition>
{
    public void Configure(EntityTypeBuilder<StaffingPosition> b)
    {
        b.ToTable("hr_staffing", t =>
        {
            t.HasCheckConstraint("ck_hr_staffing_rates", "[Rates] > 0");
            t.HasCheckConstraint("ck_hr_staffing_money", "[Salary] >= 0 AND [Allowance] >= 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Rates).HasPrecision(6, 2);
        b.Property(x => x.Salary).HasPrecision(19, 4);
        b.Property(x => x.Allowance).HasPrecision(19, 4);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Department>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.DepartmentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_hr_staffing_department");
        b.HasOne<Position>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PositionId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_hr_staffing_position");
        b.HasIndex(x => new { x.OrganizationId, x.DepartmentId, x.PositionId }).IsUnique().HasFilter("[IsArchived] = 0")
            .HasDatabaseName("ux_hr_staffing_active");
    }
}

internal sealed class PersonnelOrderConfiguration : IEntityTypeConfiguration<PersonnelOrder>
{
    public void Configure(EntityTypeBuilder<PersonnelOrder> b)
    {
        b.ToTable("hr_orders", t =>
        {
            t.HasCheckConstraint("ck_hr_orders_kind", "[Kind] IN (1, 2, 3, 4)");
            t.HasCheckConstraint("ck_hr_orders_status", "[Status] IN (1, 2, 9)");
            t.HasCheckConstraint("ck_hr_orders_posted", "([Status] = 2 AND [PostedAtUtc] IS NOT NULL AND [PostedByUserId] IS NOT NULL) OR ([Status] <> 2 AND [PostedAtUtc] IS NULL AND [PostedByUserId] IS NULL)");
            t.HasCheckConstraint("ck_hr_orders_fields",
                "([Kind] = 1 AND [PersonnelNumber] IS NOT NULL AND [LastName] IS NOT NULL AND [FirstName] IS NOT NULL AND [DepartmentId] IS NOT NULL AND [PositionId] IS NOT NULL AND [Rate] IS NOT NULL"
                + " AND ([Status] <> 2 OR [EmployeeId] IS NOT NULL))"
                + " OR ([Kind] = 2 AND [EmployeeId] IS NOT NULL AND [DepartmentId] IS NOT NULL AND [PositionId] IS NOT NULL AND [Rate] IS NOT NULL)"
                + " OR ([Kind] = 3 AND [EmployeeId] IS NOT NULL AND [LeaveKind] IS NOT NULL AND [EffectiveTo] >= [EffectiveFrom])"
                + " OR ([Kind] = 4 AND [EmployeeId] IS NOT NULL AND [DismissalGround] IS NOT NULL)");
            t.HasCheckConstraint("ck_hr_orders_rate", "[Rate] IS NULL OR ([Rate] > 0 AND [Rate] <= 1.5)");
            t.HasTrigger("tr_hr_orders_posted_immutable");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Number).HasMaxLength(20).IsRequired();
        b.Property(x => x.Kind).HasConversion<byte>();
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.LeaveKind).HasConversion<byte?>();
        b.Property(x => x.PersonnelNumber).HasMaxLength(20);
        b.Property(x => x.LastName).HasMaxLength(100);
        b.Property(x => x.FirstName).HasMaxLength(100);
        b.Property(x => x.MiddleName).HasMaxLength(100);
        b.Property(x => x.Rate).HasPrecision(4, 2);
        b.Property(x => x.Salary).HasPrecision(19, 4);
        b.Property(x => x.DismissalGround).HasMaxLength(10);
        b.Property(x => x.Basis).HasMaxLength(PersonnelOrder.BasisMaxLength);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.LeaveDays);
        b.Ignore(x => x.FullName);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Employee>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EmployeeId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_hr_orders_employee");
        b.HasOne<Department>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.DepartmentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_hr_orders_department");
        b.HasOne<Position>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PositionId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_hr_orders_position");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.PostedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_hr_orders_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.Number }).IsUnique().HasDatabaseName("ux_hr_orders_number");
        b.HasIndex(x => new { x.OrganizationId, x.EmployeeId, x.Kind }).HasDatabaseName("ix_hr_orders_employee");
    }
}

internal sealed class EmploymentRecordConfiguration : IEntityTypeConfiguration<EmploymentRecord>
{
    public void Configure(EntityTypeBuilder<EmploymentRecord> b)
    {
        b.ToTable("hr_employment_history", t =>
        {
            t.HasCheckConstraint("ck_hr_employment_history_kind", "[Kind] IN (1, 2, 3, 4)");
            t.HasCheckConstraint("ck_hr_employment_history_rate", "[Rate] > 0");
            t.HasTrigger("tr_hr_employment_history_immutable");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Kind).HasConversion<byte>();
        b.Property(x => x.LeaveKind).HasConversion<byte?>();
        b.Property(x => x.Rate).HasPrecision(4, 2);
        b.Property(x => x.Salary).HasPrecision(19, 4);
        b.Property(x => x.Note).HasMaxLength(500);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Employee>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EmployeeId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_hr_employment_history_employee");
        b.HasOne<PersonnelOrder>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.OrderId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_hr_employment_history_order");
        b.HasIndex(x => new { x.OrganizationId, x.EmployeeId, x.EffectiveFrom }).HasDatabaseName("ix_hr_employment_history_employee");
    }
}

internal sealed class WorkScheduleConfiguration : IEntityTypeConfiguration<WorkSchedule>
{
    public void Configure(EntityTypeBuilder<WorkSchedule> b)
    {
        b.ToTable("hr_work_schedules", t =>
        {
            t.HasCheckConstraint("ck_hr_work_schedules_kind", "[Kind] IN (1, 2)");
            t.HasCheckConstraint("ck_hr_work_schedules_hours", "[ShiftHours] > 0 AND [ShiftHours] <= 24 AND [NightHours] >= 0 AND [NightHours] <= [ShiftHours]");
            t.HasCheckConstraint("ck_hr_work_schedules_cycle", "[Kind] = 1 OR ([WorkDays] > 0 AND [RestDays] > 0 AND [CycleStart] IS NOT NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Name).HasMaxLength(WorkSchedule.NameMaxLength).IsRequired();
        b.Property(x => x.Kind).HasConversion<byte>();
        b.Property(x => x.ShiftHours).HasPrecision(9, 2);
        b.Property(x => x.NightHours).HasPrecision(9, 2);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_hr_work_schedules_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique().HasFilter("[IsArchived] = 0").HasDatabaseName("ux_hr_work_schedules_name");
    }
}

internal sealed class EmployeeScheduleConfiguration : IEntityTypeConfiguration<EmployeeSchedule>
{
    public void Configure(EntityTypeBuilder<EmployeeSchedule> b)
    {
        b.ToTable("hr_employee_schedules");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Employee>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EmployeeId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_hr_employee_schedules_employee");
        b.HasOne<WorkSchedule>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ScheduleId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_hr_employee_schedules_schedule");
        b.HasIndex(x => new { x.OrganizationId, x.EmployeeId, x.ValidFrom }).IsUnique().HasDatabaseName("ux_hr_employee_schedules_from");
    }
}

internal sealed class TimesheetConfiguration : IEntityTypeConfiguration<Timesheet>
{
    public void Configure(EntityTypeBuilder<Timesheet> b)
    {
        b.ToTable("hr_timesheets", t =>
        {
            t.HasCheckConstraint("ck_hr_timesheets_period", "[Month] BETWEEN 1 AND 12 AND [Year] BETWEEN 2000 AND 2100");
            t.HasCheckConstraint("ck_hr_timesheets_status", "[Status] IN (1, 2)");
            t.HasCheckConstraint("ck_hr_timesheets_closed",
                "([Status] = 2 AND [ClosedAtUtc] IS NOT NULL AND [ClosedByUserId] IS NOT NULL) OR ([Status] = 1 AND [ClosedAtUtc] IS NULL AND [ClosedByUserId] IS NULL)");
            t.HasCheckConstraint("ck_hr_timesheets_version", "([Version] = 0 AND [CorrectsTimesheetId] IS NULL) OR ([Version] > 0 AND [CorrectsTimesheetId] IS NOT NULL)");
            t.HasTrigger("tr_hr_timesheets_closed_immutable");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.From);
        b.Ignore(x => x.To);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Department>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.DepartmentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_hr_timesheets_department");
        b.HasOne<Timesheet>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CorrectsTimesheetId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_hr_timesheets_corrects");
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ClosedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_hr_timesheets_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.DepartmentId, x.Year, x.Month, x.Version }).IsUnique().HasDatabaseName("ux_hr_timesheets_period");
    }
}

internal sealed class TimesheetDayConfiguration : IEntityTypeConfiguration<TimesheetDay>
{
    public void Configure(EntityTypeBuilder<TimesheetDay> b)
    {
        b.ToTable("hr_timesheet_days", t =>
        {
            t.HasCheckConstraint("ck_hr_timesheet_days_hours",
                "[Hours] >= 0 AND [NightHours] >= 0 AND [OvertimeHours] >= 0 AND [Hours] + [OvertimeHours] <= 24 AND [NightHours] <= [Hours] + [OvertimeHours]");
            t.HasTrigger("tr_hr_timesheet_days_closed_immutable");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Code).HasMaxLength(3).IsRequired();
        b.Property(x => x.Hours).HasPrecision(9, 2);
        b.Property(x => x.NightHours).HasPrecision(9, 2);
        b.Property(x => x.OvertimeHours).HasPrecision(9, 2);
        b.HasOne<Timesheet>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.TimesheetId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_hr_timesheet_days_timesheet");
        b.HasOne<Employee>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EmployeeId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_hr_timesheet_days_employee");
        b.HasIndex(x => new { x.TimesheetId, x.EmployeeId, x.Date }).IsUnique().HasDatabaseName("ux_hr_timesheet_days");
        b.HasIndex(x => new { x.OrganizationId, x.EmployeeId, x.Date }).HasDatabaseName("ix_hr_timesheet_days_employee");
    }
}

internal sealed class PieceRateConfiguration : IEntityTypeConfiguration<PieceRate>
{
    public void Configure(EntityTypeBuilder<PieceRate> b)
    {
        b.ToTable("hr_piece_rates", t =>
        {
            t.HasCheckConstraint("ck_hr_piece_rates_price", "[Price] > 0");
            t.HasCheckConstraint("ck_hr_piece_rates_unit", "[Unit] IN (N'шт', N'кг')");
            t.HasTrigger("tr_hr_piece_rates_immutable");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.OperationName).HasMaxLength(PieceRate.NameMaxLength).IsRequired();
        b.Property(x => x.Unit).HasMaxLength(5).IsRequired();
        b.Property(x => x.Price).HasPrecision(19, 4);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganizationId, x.OperationId, x.ItemId, x.ValidFrom }).IsUnique().HasDatabaseName("ux_hr_piece_rates_from");
    }
}

internal sealed class PayrollRunConfiguration : IEntityTypeConfiguration<PayrollRun>
{
    public void Configure(EntityTypeBuilder<PayrollRun> b)
    {
        b.ToTable("hr_payroll_runs", t =>
        {
            t.HasCheckConstraint("ck_hr_payroll_runs_status", "[Status] IN (1, 2)");
            t.HasCheckConstraint("ck_hr_payroll_runs_period", "[Month] BETWEEN 1 AND 12 AND [Year] BETWEEN 2000 AND 2100");
            t.HasCheckConstraint("ck_hr_payroll_runs_approved",
                "([Status] = 2 AND [ApprovedAtUtc] IS NOT NULL AND [ApprovedByUserId] IS NOT NULL AND [TimesheetsClosed] = 1) OR ([Status] = 1 AND [ApprovedAtUtc] IS NULL AND [ApprovedByUserId] IS NULL)");
            t.HasTrigger("tr_hr_payroll_runs_approved_immutable");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Status).HasConversion<byte>();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.From);
        b.Ignore(x => x.To);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.CalculatedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.ApprovedByUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasAlternateKey(x => new { x.OrganizationId, x.Id }).HasName("ak_hr_payroll_runs_org_id");
        b.HasIndex(x => new { x.OrganizationId, x.Year, x.Month }).IsUnique().HasDatabaseName("ux_hr_payroll_runs_period");
    }
}

internal sealed class PayrollLineConfiguration : IEntityTypeConfiguration<PayrollLine>
{
    public void Configure(EntityTypeBuilder<PayrollLine> b)
    {
        b.ToTable("hr_payroll_lines", t =>
        {
            t.HasCheckConstraint("ck_hr_payroll_lines_kind", "[Kind] BETWEEN 1 AND 5");
            t.HasTrigger("tr_hr_payroll_lines_approved_immutable");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Kind).HasConversion<byte>();
        b.Property(x => x.Description).HasMaxLength(PayrollLine.DescriptionMaxLength).IsRequired();
        b.Property(x => x.Quantity).HasPrecision(18, 6);
        b.Property(x => x.Rate).HasPrecision(19, 4);
        b.Property(x => x.Amount).HasPrecision(19, 4);
        b.HasOne<PayrollRun>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.RunId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_hr_payroll_lines_run");
        b.HasOne<Employee>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EmployeeId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_hr_payroll_lines_employee");
        b.HasIndex(x => new { x.RunId, x.EmployeeId }).HasDatabaseName("ix_hr_payroll_lines_run_employee");
    }
}
