using KnitErp.Domain.Development;

namespace KnitErp.Application.Development;

public sealed record DevMachineDto(
    long Id, string Name, DevMachineBrand Brand, string? Model, int Gauge, int FeederCount, int? WidthCm, int? Systems, string? InventoryNumber,
    DevMachineStatus Status, bool IsArchived, string Parameters, int Programs, string? LastNote, DateTime? LastNoteAtUtc, byte[] RowVersion)
{
    public string StatusName => DevMachine.StatusName(Status);
    public string BrandName => DevMachine.BrandName(Brand);

    public DevMachineValues ToValues() => new(Name, Brand, Model, Gauge, FeederCount, WidthCm, Systems, InventoryNumber);
}

public sealed record DevMachineNoteDto(string Text, DevMachineStatus? StatusAfter, DateTime AtUtc, string User)
{
    public string? StatusName => StatusAfter is { } s ? DevMachine.StatusName(s) : null;
}

/// <summary>Программа машины в библиотеке: модель, деталь, станок и его параметры на момент загрузки.</summary>
public sealed record DevProgramDto(
    long FileId, string FileName, int Version, int SizeBytes, string? MachineName, string? MachineParams, long ModelId, string ModelNumber,
    string ModelName, string? DetailName, DateTime UploadedAtUtc, string UploadedBy, long? DetailId = null);

public sealed record DevColorDto(long Id, string Label, long? ItemId, string? ItemName, string? Swatch, string? Note);

public sealed record DevDetailRowDto(
    long Id, string Name, int Quantity, string? MachineName, int? FeederCount, int ThreadingRows, bool HasProgram, int? PlanKnitSeconds,
    decimal? PlanWeightGrams);

/// <summary>Позиция заправки на одной стороне нитеводителя — как на техкарте.</summary>
public sealed record DevThreadSlotDto(long? ColorId, string Yarn, string? ItemName, string? Swatch, int Ends, bool Paraffin, string? Mode, bool Elastic, string? Label);

public sealed record DevFeederRowDto(int Feeder, DevThreadSlotDto? Left, DevThreadSlotDto? Right);

public sealed record DevSampleOptionDto(long Id, int No, string? Size, DevSampleStatus Status)
{
    public string StatusName => DevSample.StatusName(Status);
}

/// <summary>Факт образца по детали: время, вес, доли цветов, кто и когда.</summary>
public sealed record DevFactDto(
    long SampleId, long DetailId, int SampleNo, int? KnitSeconds, decimal? WeightGrams, IReadOnlyList<(string Label, decimal Percent)> Shares, DateTime RecordedAtUtc,
    string RecordedBy);

/// <summary>
/// Техкарта вязания детали (D711, D717) — то, что вязальщица открывает на телефоне или планшете у машины: общая информация,
/// заправка по нитеводителям, важные комментарии (блоки 1–3, D719). Операции и размеры — во вкладках модели, сюда не грузятся.
/// </summary>
public sealed record DevTechCardDto(
    long ModelId, string ModelNumber, string ModelName, long DetailId, string DetailName, int Quantity, long? MachineId, string? MachineName,
    string? MachineParams, int FeederCount, DevMachineStatus? MachineStatus, string? MachineLastNote, long? WeaveId, string? Weave,
    int? PlanKnitSeconds, decimal? PlanWeightGrams, string? Composition, string? Notes, IReadOnlyList<DevFeederRowDto> Feeders,
    IReadOnlyList<DevColorDto> Palette,
    long? ProgramFileId, string? ProgramName, int? ProgramVersion, string? ProgramParams, long? SketchFileId, bool CanEditThreading,
    bool CanRecordFact, bool CanSeeCost, IReadOnlyList<DevSampleOptionDto> Samples, IReadOnlyList<DevFactDto> Facts, byte[] RowVersion,
    int FactTolerancePercent = DevSettings.DefaultTolerance)
{
    public string? MachineStatusName => MachineStatus is { } s ? DevMachine.StatusName(s) : null;
}

/// <summary>Раздел показателей заполнения для карточки (D716).</summary>
public sealed record DevCompletenessDto(IReadOnlyList<DevSectionDto> Sections, int Done, int Total, int Percent);

public sealed record DevSectionDto(string Name, bool Done);

/// <summary>Условия расчёта себестоимости (D740, D742): видит `dev.cost.view`, меняет ещё и `dev.settings`.</summary>
public sealed record DevCostSettingsDto(decimal YarnWastePercent, decimal OverheadPercent, decimal? LaborRatePerMinute, decimal MarkupPercent, bool CanEdit,
    bool RequireCostForApproval = true, decimal? MinMarginPercent = null);

/// <summary>
/// Изменённая ячейка таблицы (D720): строка (деталь, точка, размер для упаковки), столбец (размер; 0 — нет), поле (0 — длина с машины или
/// значение, 1 — после ВТО, 2 — ширина), значение, которое пользователь видел (<see cref="Was"/>), и новое. Экран шлёт только изменённые ячейки.
/// </summary>
public sealed record DevCellEdit(long RowId, long ColumnId, int Field, decimal? Was, decimal? Value);

/// <summary>Итог сохранения таблицы: сколько ячеек записано и какие пропущены — их успел изменить другой пользователь.</summary>
public sealed record DevSaveResult(int Saved, IReadOnlyList<string> Conflicts);

/// <summary>Настройки цеха и печати (D719); Sections.Done — раздел входит в показатель заполнения.</summary>
public sealed record DevShopSettingsDto(int FactTolerancePercent, IReadOnlyList<DevSectionDto> Sections, int LabelWidthMm, int LabelHeightMm, bool LabelsOnA4,
    bool CanEdit);

/// <summary>Размер сетки модели; <paramref name="WeightGrams"/> — вес изделия этого размера, г (ТЗ §8).</summary>
public sealed record DevSizeDto(long Id, string Name, bool IsBase, int? PerBox, decimal? WeightGrams = null);

/// <summary>Точка шаблона табеля мер категории (ТЗ §9): допуск ± см и шаг градации (+ см на размер).</summary>
public sealed record DevCategoryMeasureDto(long Id, DevCategory Category, string Name, decimal Tolerance, decimal? Step, int SortOrder);

public sealed record DevMeasureDto(long Id, string Name, decimal Tolerance, decimal? Step, IReadOnlyDictionary<long, decimal> Values);

public sealed record DevDetailSizeRowDto(long DetailId, string DetailName, IReadOnlyDictionary<long, (decimal? OffMachine, decimal? AfterSteam, decimal? Width)> Values);

/// <summary>Раздел «Размеры»: сетка, размеры деталей, табель мер, короб.</summary>
public sealed record DevSizesDto(
    IReadOnlyList<DevSizeDto> Sizes, IReadOnlyList<DevDetailSizeRowDto> Details, IReadOnlyList<DevMeasureDto> Measures, string? PackBox, bool CanEdit,
    IReadOnlyList<string> MeasurePointSuggestions);

public sealed record DevSampleMeasureDto(long MeasureId, string Name, decimal? Plan, decimal Tolerance, decimal? Fact, decimal? Deviation, bool OutOfTolerance);

public sealed record DevSampleDto(
    long Id, int No, long? SizeId, string? Size, DevSampleStatus Status, string? Comment, DateTime CreatedAtUtc, string CreatedBy,
    IReadOnlyList<(string Detail, DevFactDto? Fact, int? PlanKnitSeconds, decimal? PlanWeightGrams)> Details,
    IReadOnlyList<DevSampleMeasureDto> Measures, byte[] RowVersion, string? Reason = null, DevSampleCustody Custody = DevSampleCustody.AtFactory,
    string? CustodyRecipient = null, DateOnly? HandedOn = null, DateOnly? ReturnBy = null, DateOnly? CustodyClosedOn = null, string? CustodyNote = null,
    bool ReturnOverdue = false, DevSampleMakingDto? Making = null, DevSampleReworkDto? Rework = null, IReadOnlyList<long>? PhotoFileIds = null)
{
    public string StatusName => DevSample.StatusName(Status);
    public string CustodyName => DevSample.CustodyName(Custody);
}

/// <summary>
/// Паспорт образца (ТЗ §8, D728): колорит и версия рисунка (с модели при создании), партия пряжи, кто вязал и собирал, время сборки;
/// время вязки (минуты, сумма факта деталей × количество в изделии) и фактический вес изделия считаются по факту цеха.
/// </summary>
public sealed record DevSampleMakingDto(string? Colorway, int? PatternVersion, string? YarnBatch, long? KnittedByUserId, string? KnittedBy,
    long? AssembledByUserId, string? AssembledBy, int? AssemblyMinutes, decimal? KnitMinutes, decimal? WeightGrams);

/// <summary>План доработки образца «На доработку» (ТЗ §8): что меняем, кто, срок.</summary>
public sealed record DevSampleReworkDto(string? What, long? AssigneeUserId, string? Assignee, DateOnly? DueDate);

/// <summary>Образец у заказчика (D723) — строка списка «Образцы у заказчиков».</summary>
public sealed record DevSampleOutDto(long SampleId, int No, long ModelId, string Number, string ModelName, string Recipient, DateOnly HandedOn, DateOnly? ReturnBy,
    bool Overdue);

public sealed record DevSamplesDto(IReadOnlyList<DevSampleDto> Samples, IReadOnlyList<DevSizeDto> Sizes, bool CanEdit, bool CanRecord,
    int FactTolerancePercent = DevSettings.DefaultTolerance, IReadOnlyList<DevOptionDto>? Reasons = null, string? CustomerName = null, DateOnly? Today = null,
    bool CanHand = false, bool CanCloseCustody = false, IReadOnlyList<DevOptionDto>? Users = null);

/// <summary>Деталь в сравнении образцов: факт вязания и веса против плана детали.</summary>
public sealed record DevSampleCompareDetailDto(long DetailId, string Detail, int Quantity, int? PlanKnitSeconds, int? FactKnitSeconds,
    decimal? PlanWeightGrams, decimal? FactWeightGrams);

/// <summary>Образец в сравнении (ТЗ §8): статус, размер, решение, детали, общий вес, замеры и фото образца.</summary>
public sealed record DevSampleCompareItemDto(long Id, int No, string? Size, DevSampleStatus Status, string? Reason, string? Comment, DateTime CreatedAtUtc,
    IReadOnlyList<DevSampleCompareDetailDto> Details, decimal? TotalWeightGrams, IReadOnlyList<DevSampleMeasureDto> Measures,
    IReadOnlyList<long> PhotoFileIds)
{
    public string StatusName => DevSample.StatusName(Status);
}

/// <summary>Сравнение образцов модели рядом: колонки — образцы (по возрастанию №), строки — точки замера и детали в одном порядке у всех.</summary>
public sealed record DevSampleCompareDto(long ModelId, IReadOnlyList<DevSampleCompareItemDto> Samples, IReadOnlyList<string> MeasureNames,
    IReadOnlyList<string> DetailNames);

public sealed record DevOperationTypeDto(long Id, string Name, string Unit, decimal? Price, decimal? NormMinutes, byte? ProductionStage, bool IsArchived,
    byte[] RowVersion);

public sealed record DevModelOperationDto(long Id, int Seq, long OperationTypeId, string Name, string Unit, string? Note, decimal? NormMinutes,
    decimal? Price, decimal? TypeNorm, decimal? TypePrice);

public sealed record DevOperationsDto(IReadOnlyList<DevModelOperationDto> Rows, IReadOnlyList<DevOperationTypeDto> Types, decimal TotalMinutes,
    decimal? TotalCost, bool CanEdit, bool CanSeeCost);

/// <summary>Строка списка техкарт вязания для цеха.</summary>
public sealed record DevTechCardRowDto(long DetailId, long ModelId, string ModelNumber, string ModelName, DevStage Stage, string DetailName, string? MachineName,
    bool HasProgram, int ThreadingRows)
{
    public string StageName => DevStages.Name(Stage);
}

/// <summary>Техзадание детали для редактора дессинатора: значения, заправка, справочники для выбора, программы детали.</summary>
public sealed record DevDetailEditDto(
    long Id, long ModelId, string ModelNumber, string ModelName, string Name, int Quantity, long? MachineId, long? WeaveId, int? PlanKnitSeconds,
    decimal? PlanWeightGrams, string? Notes, IReadOnlyList<DevThreadingRow> Threading, IReadOnlyList<DevMachineDto> Machines,
    IReadOnlyList<DevOptionDto> Weaves, IReadOnlyList<DevColorDto> Palette, IReadOnlyList<DevOptionDto> OtherDetails, IReadOnlyList<DevProgramDto> Programs,
    long? ProgramFileId, bool CanEditThreading, bool CanEdit, byte[] RowVersion);

/// <summary>Что печатаем на этикетке модуля (D719): модель (папка, пакет образцов), деталь (пачка в цеху), образец (бирка к связанному образцу).</summary>
public enum DevLabelKind : byte
{
    Model = 1,
    Detail = 2,
    Sample = 3,
}

/// <summary>Одна этикетка: код для штрихкода Code 128 (только ASCII), заголовок и строки «подпись — значение».</summary>
public sealed record DevLabelDto(string Code, string Title, string Subtitle, IReadOnlyList<DevLabelLineDto> Lines, string Back,
    int WidthMm = DevSettings.DefaultLabelWidth, int HeightMm = DevSettings.DefaultLabelHeight, bool OnA4 = false);

public sealed record DevLabelLineDto(string Key, string Value);
