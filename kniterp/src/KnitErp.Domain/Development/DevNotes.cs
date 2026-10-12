using System.Globalization;

namespace KnitErp.Domain.Development;

/// <summary>
/// Замечание расчёта (D767): шаблон из <see cref="DevNotes"/> и подстановки — экран переводит шаблон (be/kk/uz) и подставляет
/// названия; в журналы и комментарии документов идёт русский текст (<see cref="ToString"/>).
/// </summary>
public sealed record DevNote(string Template, IReadOnlyList<string> Args)
{
    public static DevNote Of(string template, params string[] args) => new(template, args);

    public override string ToString() => string.Format(CultureInfo.InvariantCulture, Template, Args.Cast<object>().ToArray());
}

/// <summary>Шаблоны замечаний модуля (D767) — все в одном месте, чтобы тест переводов видел каждый.</summary>
public static class DevNotes
{
    public const string NoDetails = "У модели нет деталей.";
    public const string NoWeight = "«{0}»: нет веса детали (ни факта образца, ни плана).";
    public const string NoShares = "«{0}»: нет долей цветов — внесите факт образца с долями цветов.";
    public const string NoFact = "По образцу ещё нет факта цеха.";
    public const string UnitForStock = "«{0}»: единица «{1}» — не кг и не г, количество введёт склад.";
    public const string UnitForProduction = "«{0}»: единица «{1}» — не кг и не г, норму введёт производство.";
    public const string UnitForCost = "«{0}»: единица «{1}» — не кг и не г, в расчёт не вошла.";
    public const string NoPrice = "«{0}»: нет цены ни в закупках, ни в каталоге — введите вручную.";
    public const string NoLabor = "Операция «{0}»: нет ни расценки, ни нормы и ставки за минуту — работа не учтена.";
    public const string NoOperations = "Операций технолога нет — работа не учтена.";

    public static readonly IReadOnlyList<string> All =
        [NoDetails, NoWeight, NoShares, NoFact, UnitForStock, UnitForProduction, UnitForCost, NoPrice, NoLabor, NoOperations];
}
