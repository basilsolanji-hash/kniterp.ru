namespace KnitErp.Application.Workspace;

/// <summary>
/// Персонаж помощника (D116): Ника (по умолчанию), Книт или без персонажа — только чат в панели инструментов.
/// Выбор влияет на образ и обращение в ответах; знания и права помощника одинаковы.
/// </summary>
public static class AssistantPersonas
{
    public const string Nika = "nika";
    public const string Knit = "knit";
    public const string None = "none";

    public static readonly IReadOnlyList<string> All = [Nika, Knit, None];

    public static string Clean(string? persona) => persona is Nika or Knit or None ? persona : Nika;

    /// <summary>Имя персонажа — для обращения в ответах ИИ; без персонажа — нейтральный «помощник».</summary>
    public static string? Name(string persona) => persona switch
    {
        Nika => "Ника",
        Knit => "Книт",
        _ => null,
    };
}
