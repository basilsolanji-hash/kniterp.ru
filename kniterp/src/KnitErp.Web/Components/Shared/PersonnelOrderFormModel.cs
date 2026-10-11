using KnitErp.Domain.Hr;

namespace KnitErp.Web.Components.Shared;

/// <summary>Поля формы кадрового приказа (D204); в сервис уходят как <see cref="PersonnelOrderValues"/>.</summary>
public sealed class PersonnelOrderFormModel
{
    public PersonnelOrderKind Kind { get; set; } = PersonnelOrderKind.Hire;
    public DateOnly OrderDate { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public long EmployeeId { get; set; }
    public string? PersonnelNumber { get; set; }
    public string? LastName { get; set; }
    public string? FirstName { get; set; }
    public string? MiddleName { get; set; }
    public long DepartmentId { get; set; }
    public long PositionId { get; set; }
    public decimal Rate { get; set; } = 1m;
    public decimal? Salary { get; set; }
    public int TrialMonths { get; set; }
    public LeaveKind LeaveKind { get; set; } = LeaveKind.Annual;
    public string? DismissalGround { get; set; }
    public string? Basis { get; set; }

    public static PersonnelOrderFormModel From(PersonnelOrderValues v) => new()
    {
        Kind = v.Kind, OrderDate = v.OrderDate, EffectiveFrom = v.EffectiveFrom, EffectiveTo = v.EffectiveTo, EmployeeId = v.EmployeeId ?? 0,
        PersonnelNumber = v.PersonnelNumber, LastName = v.LastName, FirstName = v.FirstName, MiddleName = v.MiddleName,
        DepartmentId = v.DepartmentId ?? 0, PositionId = v.PositionId ?? 0, Rate = v.Rate ?? 1m, Salary = v.Salary, TrialMonths = v.TrialMonths ?? 0,
        LeaveKind = v.LeaveKind ?? LeaveKind.Annual, DismissalGround = v.DismissalGround, Basis = v.Basis,
    };

    public PersonnelOrderValues ToValues() => Kind switch
    {
        PersonnelOrderKind.Hire => new(Kind, OrderDate, EffectiveFrom, null, PersonnelNumber, LastName, FirstName, MiddleName, Id(DepartmentId),
            Id(PositionId), Rate, Salary, (byte)Math.Clamp(TrialMonths, 0, 255), Basis: Basis),
        PersonnelOrderKind.Transfer => new(Kind, OrderDate, EffectiveFrom, Id(EmployeeId), DepartmentId: Id(DepartmentId), PositionId: Id(PositionId),
            Rate: Rate, Salary: Salary, Basis: Basis),
        PersonnelOrderKind.Leave => new(Kind, OrderDate, EffectiveFrom, Id(EmployeeId), EffectiveTo: EffectiveTo, LeaveKind: LeaveKind, Basis: Basis),
        _ => new(Kind, OrderDate, EffectiveFrom, Id(EmployeeId), DismissalGround: DismissalGround, Basis: Basis),
    };

    private static long? Id(long id) => id > 0 ? id : null;
}
