namespace KnitErp.Web.Components.Layout;

/// <summary>
/// D116: выбранный персонаж помощника — передаётся каскадом из MainLayout в меню профиля и чат помощника.
/// Сохранение — в профиле пользователя (PersonalToolsService), выбор действует на любом устройстве.
/// </summary>
public sealed class AssistantChoice(string persona, Func<string, Task> set)
{
    public string Persona { get; } = persona;

    public Task SetAsync(string persona) => set(persona);
}
