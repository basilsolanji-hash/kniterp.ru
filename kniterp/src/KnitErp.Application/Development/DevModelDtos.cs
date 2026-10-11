using KnitErp.Domain.Development;

namespace KnitErp.Application.Development;

/// <summary>Отбор моделей: коллекция, стадия, исполнитель, заказчик, только просроченные, поиск по номеру и названию.</summary>
public sealed record DevModelFilter(
    long? CollectionId = null, DevStage? Stage = null, long? AssigneeUserId = null, long? CustomerId = null, bool OverdueOnly = false,
    string? Search = null, bool IncludeFinal = false);

public sealed record DevModelRowDto(
    long Id, string Number, string Name, DevStage Stage, string? CollectionName, DevCategory Category, string? CustomerName, DevPriority Priority,
    DateOnly? PlannedLaunch, DateTime StageSinceUtc, bool IsLate, bool IsStageOverdue, string? ResponsibleName)
{
    public string StageName => DevStages.Name(Stage);
    public string CategoryName => DevModel.CategoryName(Category);
    public string PriorityName => DevModel.PriorityName(Priority);
    public bool IsOverdue => IsLate || IsStageOverdue;
}

public sealed record DevModelPageDto(IReadOnlyList<DevModelRowDto> Rows, int Total, int Page, int PageSize);

public sealed record DevBoardColumnDto(DevStage Stage, int PlanDays, IReadOnlyList<DevModelRowDto> Models)
{
    public string StageName => DevStages.Name(Stage);
}

public sealed record DevOptionDto(long Id, string Name);

/// <summary>Списки для формы модели и отборов.</summary>
public sealed record DevModelOptionsDto(
    IReadOnlyList<DevOptionDto> Collections, IReadOnlyList<DevOptionDto> Customers, IReadOnlyList<DevOptionDto> Users, bool CanEdit);

public sealed record DevAssigneeDto(DevRole Role, long? UserId, string? Name)
{
    public string RoleName => DevModel.RoleName(Role);
}

public sealed record DevStageEntryDto(DevStage? From, DevStage To, DateTime AtUtc, string User, string? Comment, int? DaysInPrevious)
{
    public string ToName => DevStages.Name(To);
    public string? FromName => From is { } f ? DevStages.Name(f) : null;
}

public sealed record DevFileDto(long Id, DevFileKind Kind, string FileName, int Version, int SizeBytes, bool IsImage, string? Comment,
    DateTime UploadedAtUtc, string UploadedBy)
{
    public string KindName => DevFile.KindName(Kind);
}

/// <summary>Карточка модели. Целевые суммы задания — только с правом «Разработка: себестоимость и цена» (иначе null).</summary>
public sealed record DevModelDto(
    long Id, string Number, string Name, long? CollectionId, string? CollectionName, DevCategory Category, DevAudience Audience, bool ChildSafety,
    bool HasPattern, long? CustomerId, string? CustomerName, long? DealId, DateOnly? PlannedLaunch, DevPriority Priority, DevStage Stage,
    DateTime StageSinceUtc, int StagePlanDays, string? CancelReason, IReadOnlyList<DevAssigneeDto> Assignees, DevBrief Brief, bool CanSeeCost,
    IReadOnlyList<DevStageEntryDto> History, IReadOnlyList<DevFileDto> Files, IReadOnlyList<DevStage> MoveTargets, DevStage? NextStage,
    bool CanEdit, bool CanApprove, bool IsLate, bool IsStageOverdue, string CreatedBy, DateTime CreatedAtUtc, byte[] RowVersion)
{
    public string StageName => DevStages.Name(Stage);
    public string CategoryName => DevModel.CategoryName(Category);
    public string AudienceName => DevModel.AudienceName(Audience);
    public string PriorityName => DevModel.PriorityName(Priority);

    public DevModelHeader ToHeader() => new(Name, CollectionId, Category, Audience, ChildSafety, HasPattern, CustomerId, DealId, PlannedLaunch, Priority,
        Assignees.ToDictionary(a => a.Role, a => a.UserId));
}

/// <summary>Строка «Моих задач разработки»: модель, мои роли в ней, отвечаю ли я за текущую стадию, срок.</summary>
public sealed record DevMyTaskDto(
    long ModelId, string Number, string Name, DevStage Stage, IReadOnlyList<DevRole> MyRoles, bool ResponsibleNow, DateOnly? StageDue,
    DateOnly? PlannedLaunch, bool IsOverdue, DevPriority Priority)
{
    public string StageName => DevStages.Name(Stage);
}

public sealed record DevStageSettingDto(DevStage Stage, int PlanDays, bool IsEnabled, bool CanDisable)
{
    public string StageName => DevStages.Name(Stage);
}

public sealed record DevLookupDto(long Id, DevLookupKind Kind, string Name, int? Number, bool IsArchived, byte[] RowVersion);
