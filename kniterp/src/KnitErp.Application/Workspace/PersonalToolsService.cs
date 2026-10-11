using System.Globalization;
using System.Text.Json;
using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Common;
using KnitErp.Domain.Workspace;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Workspace;

public sealed record PanelSettings(IReadOnlyList<string> Pinned, int Visible);

/// <summary>Задача или событие календаря. Date/Time — местные дата и время организации; без даты — просто задача.</summary>
public sealed record TaskItem(string Id, string Title, DateOnly? Date, TimeOnly? Time, bool Remind, bool Done, string? Note);

public sealed record NoteItem(string Id, string Text, DateTime UpdatedAtUtc);

public sealed record LinkItem(string Id, string Title, string Url);

public sealed record FavoriteItem(string Title, string Href);

/// <summary>Что показывать в календаре (D83): праздники и дни рождения коллег. Личная настройка.</summary>
public sealed record CalendarSettings(bool ShowHolidays = true, bool ShowBirthdays = true);

public enum CalendarEventKind
{
    Holiday,
    Birthday,
}

/// <summary>Событие календаря, которое не задача: праздник или день рождения коллеги (без года и возраста).</summary>
public sealed record CalendarEventDto(DateOnly Date, CalendarEventKind Kind, string Title);

/// <summary>
/// Личные данные инструментов: панель, задачи и календарь, заметки, ссылки, избранное, город погоды.
/// Каждый пользователь видит и меняет только свои данные в своей организации. Каждая операция — свой DbContext:
/// инструменты работают поверх открытой страницы, а DbContext вкладки занят ею (CLAUDE.md).
/// Одновременная правка с двух устройств: сохраняется последняя (личные данные одного человека, допущение D47).
/// </summary>
public sealed class PersonalToolsService(IKnitErpDbContextFactory factory, ICurrentUser currentUser, IClock clock)
{
    public const int MaxTasks = 500;
    public const int MaxNotes = 100;
    public const int MaxLinks = 30;
    public const int MaxFavorites = 30;
    public const int TitleMaxLength = 200;
    public const int NoteMaxLength = 5000;

    private const string PanelKind = "panel";
    private const string TasksKind = "tasks";
    private const string NotesKind = "notes";
    private const string LinksKind = "links";
    private const string FavoritesKind = "favorites";
    private const string WeatherKind = "weather";
    private const string DismissedKind = "notifications-dismissed";
    private const string CalendarKind = "calendar";

    /// <summary>Сколько скрытых уведомлений помнить: старые ключи вытесняются (их ситуации давно сменились).</summary>
    public const int MaxDismissed = 300;

    /// <summary>Настройка главного экрана; её же читает <see cref="Organizations.DashboardService"/> вместе с данными экрана.</summary>
    public const string DashboardKind = "dashboard";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // --- Панель быстрого доступа -------------------------------------------------------------

    /// <summary>Язык интерфейса (D60) — в профиле, действует на любом устройстве после входа.</summary>
    public async Task SetLanguageAsync(string code, CancellationToken ct = default)
    {
        var userId = currentUser.UserId ?? throw new NotFoundException("Пользователь");
        await using var db = factory.Create();
        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        user.SetLanguage(code);
        await db.SaveChangesAsync(ct);
    }

    public async Task<PanelSettings> GetPanelAsync(CancellationToken ct = default)
    {
        var stored = await LoadAsync<PanelSettings>(PanelKind, ct);
        var untouched = stored is null || (stored.Pinned ?? []).SequenceEqual(ToolCatalog.LegacyDefaultPinned);
        return untouched ? new PanelSettings(ToolCatalog.DefaultPinned, ToolCatalog.DefaultVisible) : Clean(stored!);
    }

    public async Task<PanelSettings> SavePanelAsync(PanelSettings settings, CancellationToken ct = default)
    {
        var clean = Clean(settings);
        await SaveAsync(PanelKind, clean, ct);
        return clean;
    }

    /// <summary>
    /// Неизвестные, повторные и постоянные (поиск, поддержка — они на панели всегда) инструменты отбрасываются,
    /// число видимых — в допустимых пределах.
    /// </summary>
    public static PanelSettings Clean(PanelSettings s)
    {
        var pinned = (s.Pinned ?? []).Where(id => ToolCatalog.Find(id) is not null && !ToolCatalog.Fixed.Contains(id))
            .Distinct().Take(ToolCatalog.MaxPinned).ToList();
        return new PanelSettings(pinned, Math.Clamp(s.Visible, 0, ToolCatalog.MaxVisible));
    }

    // --- Скрытые уведомления ------------------------------------------------------------------

    /// <summary>Ключи уведомлений, которые пользователь убрал («✕», «Очистить все»).</summary>
    public async Task<IReadOnlySet<string>> GetDismissedNotificationsAsync(CancellationToken ct = default) =>
        (await LoadAsync<List<string>>(DismissedKind, ct) ?? []).ToHashSet(StringComparer.Ordinal);

    public async Task DismissNotificationsAsync(IEnumerable<string> keys, CancellationToken ct = default)
    {
        var list = await LoadAsync<List<string>>(DismissedKind, ct) ?? [];
        foreach (var key in keys.Where(k => !string.IsNullOrWhiteSpace(k) && k.Length <= 200).Distinct(StringComparer.Ordinal))
        {
            list.Remove(key);
            list.Add(key);
        }

        await SaveAsync(DismissedKind, list.Skip(Math.Max(0, list.Count - MaxDismissed)).ToList(), ct);
    }

    /// <summary>«Показать скрытые»: список скрытых очищается.</summary>
    public Task RestoreNotificationsAsync(CancellationToken ct = default) => SaveAsync(DismissedKind, new List<string>(), ct);

    // --- Помощник (D116) ------------------------------------------------------------------------

    private const string AssistantKind = "assistant";

    private sealed record AssistantSettings(string? Persona);

    /// <summary>Кто помогает пользователю: Ника, Книт или без персонажа. Хранится в профиле — одинаково на любом устройстве.</summary>
    public async Task<string> GetAssistantPersonaAsync(CancellationToken ct = default) =>
        AssistantPersonas.Clean((await LoadAsync<AssistantSettings>(AssistantKind, ct))?.Persona);

    public async Task<string> SetAssistantPersonaAsync(string? persona, CancellationToken ct = default)
    {
        var clean = AssistantPersonas.Clean(persona);
        await SaveAsync(AssistantKind, new AssistantSettings(clean), ct);
        return clean;
    }

    // --- Главный экран ------------------------------------------------------------------------

    public async Task<DashboardLayout> GetDashboardLayoutAsync(CancellationToken ct = default) =>
        DashboardBlocks.Clean(await LoadAsync<DashboardLayout>(DashboardKind, ct));

    public async Task<DashboardLayout> SaveDashboardLayoutAsync(DashboardLayout layout, CancellationToken ct = default)
    {
        var clean = DashboardBlocks.Clean(layout);
        await SaveAsync(DashboardKind, clean, ct);
        return clean;
    }

    /// <summary>«Сбросить по умолчанию»: личная запись удаляется — дальше экран следует порядку по умолчанию.</summary>
    public async Task<DashboardLayout> ResetDashboardLayoutAsync(CancellationToken ct = default)
    {
        await using var db = factory.Create();
        var ctx = await new AccessGuard(db, currentUser, clock).CurrentAsync(ct);
        var row = await db.UserToolData
            .SingleOrDefaultAsync(d => d.OrganizationId == ctx.OrganizationId && d.UserId == ctx.UserId && d.Kind == DashboardKind, ct);
        if (row is not null)
        {
            db.UserToolData.Remove(row);
            await db.SaveChangesAsync(ct);
        }

        return DashboardBlocks.Default;
    }

    // --- Задачи и календарь ------------------------------------------------------------------

    public async Task<IReadOnlyList<TaskItem>> GetTasksAsync(CancellationToken ct = default) =>
        await LoadAsync<List<TaskItem>>(TasksKind, ct) ?? [];

    public async Task<IReadOnlyList<TaskItem>> SaveTaskAsync(TaskItem item, CancellationToken ct = default)
    {
        var title = Text(item.Title, TitleMaxLength, "Название");
        var note = string.IsNullOrWhiteSpace(item.Note) ? null : Text(item.Note, NoteMaxLength, "Описание");
        if (item.Remind && item.Date is null)
        {
            throw new BusinessRuleException("workspace.remind_date", "Для напоминания укажите дату.");
        }

        var list = (await GetTasksAsync(ct)).ToList();
        var clean = item with { Id = NewIdIfEmpty(item.Id), Title = title, Note = note, Time = item.Date is null ? null : item.Time };
        var index = list.FindIndex(t => t.Id == clean.Id);
        if (index >= 0)
        {
            list[index] = clean;
        }
        else
        {
            if (list.Count >= MaxTasks)
            {
                throw new BusinessRuleException("workspace.limit", $"Не больше {MaxTasks} задач — удалите выполненные.");
            }

            list.Add(clean);
        }

        await SaveAsync(TasksKind, list, ct);
        return list;
    }

    public async Task<IReadOnlyList<TaskItem>> SetTaskDoneAsync(string id, bool done, CancellationToken ct = default)
    {
        var list = (await GetTasksAsync(ct)).Select(t => t.Id == id ? t with { Done = done } : t).ToList();
        await SaveAsync(TasksKind, list, ct);
        return list;
    }

    public async Task<IReadOnlyList<TaskItem>> DeleteTaskAsync(string id, CancellationToken ct = default)
    {
        var list = (await GetTasksAsync(ct)).Where(t => t.Id != id).ToList();
        await SaveAsync(TasksKind, list, ct);
        return list;
    }

    // --- Календарь: праздники и дни рождения ------------------------------------------------

    public async Task<CalendarSettings> GetCalendarSettingsAsync(CancellationToken ct = default) =>
        await LoadAsync<CalendarSettings>(CalendarKind, ct) ?? new CalendarSettings();

    public Task SaveCalendarSettingsAsync(CalendarSettings settings, CancellationToken ct = default) => SaveAsync(CalendarKind, settings, ct);

    /// <summary>
    /// Праздники страны организации и дни рождения коллег за период (не больше года), по личной настройке. День рождения — только
    /// у работающих сотрудников этой организации, которые дали согласие; год рождения не показывается (152-ФЗ).
    /// </summary>
    public async Task<IReadOnlyList<CalendarEventDto>> GetCalendarEventsAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (to < from || to.DayNumber - from.DayNumber > 400)
        {
            throw new BusinessRuleException("workspace.calendar_range", "Период календаря — не больше года.");
        }

        var settings = await GetCalendarSettingsAsync(ct);
        await using var db = factory.Create();
        var ctx = await new AccessGuard(db, currentUser, clock).CurrentAsync(ct);
        var result = new List<CalendarEventDto>();
        if (settings.ShowHolidays)
        {
            var country = await db.Organizations.AsNoTracking().Where(o => o.Id == ctx.OrganizationId).Select(o => o.CountryCode).SingleAsync(ct);
            result.AddRange(ProductionCalendar.Holidays(country, from, to).Select(h => new CalendarEventDto(h.Date, CalendarEventKind.Holiday, h.Name)));
        }

        if (settings.ShowBirthdays)
        {
            var people = await db.Employees.AsNoTracking()
                .Where(e => e.OrganizationId == ctx.OrganizationId && e.ShareBirthday && e.BirthDate != null
                            && e.Status != KnitErp.Domain.Structure.EmploymentStatus.Dismissed)
                .Select(e => new { e.LastName, e.FirstName, e.BirthDate }).ToListAsync(ct);
            result.AddRange(people.SelectMany(p => ProductionCalendar.Anniversaries(p.BirthDate!.Value, from, to)
                .Select(d => new CalendarEventDto(d, CalendarEventKind.Birthday, $"{p.FirstName} {p.LastName}"))));
        }

        return result.OrderBy(e => e.Date).ThenBy(e => e.Kind).ThenBy(e => e.Title).ToList();
    }

    // --- Заметки -----------------------------------------------------------------------------

    public async Task<IReadOnlyList<NoteItem>> GetNotesAsync(CancellationToken ct = default) =>
        (await LoadAsync<List<NoteItem>>(NotesKind, ct) ?? []).OrderByDescending(n => n.UpdatedAtUtc).ToList();

    public async Task<IReadOnlyList<NoteItem>> SaveNoteAsync(string? id, string? text, CancellationToken ct = default)
    {
        var body = Text(text, NoteMaxLength, "Заметка");
        var list = (await GetNotesAsync(ct)).ToList();
        var note = new NoteItem(NewIdIfEmpty(id), body, clock.UtcNow);
        var index = list.FindIndex(n => n.Id == note.Id);
        if (index >= 0)
        {
            list[index] = note;
        }
        else
        {
            if (list.Count >= MaxNotes)
            {
                throw new BusinessRuleException("workspace.limit", $"Не больше {MaxNotes} заметок.");
            }

            list.Insert(0, note);
        }

        await SaveAsync(NotesKind, list, ct);
        return list.OrderByDescending(n => n.UpdatedAtUtc).ToList();
    }

    public async Task<IReadOnlyList<NoteItem>> DeleteNoteAsync(string id, CancellationToken ct = default)
    {
        var list = (await GetNotesAsync(ct)).Where(n => n.Id != id).ToList();
        await SaveAsync(NotesKind, list, ct);
        return list;
    }

    // --- Ссылки ------------------------------------------------------------------------------

    public async Task<IReadOnlyList<LinkItem>> GetLinksAsync(CancellationToken ct = default) =>
        await LoadAsync<List<LinkItem>>(LinksKind, ct) ?? [];

    public async Task<IReadOnlyList<LinkItem>> AddLinkAsync(string? title, string? url, CancellationToken ct = default)
    {
        var list = (await GetLinksAsync(ct)).ToList();
        if (list.Count >= MaxLinks)
        {
            throw new BusinessRuleException("workspace.limit", $"Не больше {MaxLinks} ссылок.");
        }

        var href = Domain.Organizations.Organization.NormalizeWebsite(url)
                   ?? throw new BusinessRuleException("field.required", "Укажите адрес ссылки.");
        list.Add(new LinkItem(NewIdIfEmpty(null), Text(string.IsNullOrWhiteSpace(title) ? new Uri(href).Host : title, TitleMaxLength, "Название"), href));
        await SaveAsync(LinksKind, list, ct);
        return list;
    }

    public async Task<IReadOnlyList<LinkItem>> DeleteLinkAsync(string id, CancellationToken ct = default)
    {
        var list = (await GetLinksAsync(ct)).Where(l => l.Id != id).ToList();
        await SaveAsync(LinksKind, list, ct);
        return list;
    }

    // --- Избранные разделы -------------------------------------------------------------------

    public async Task<IReadOnlyList<FavoriteItem>> GetFavoritesAsync(CancellationToken ct = default) =>
        await LoadAsync<List<FavoriteItem>>(FavoritesKind, ct) ?? [];

    /// <summary>Добавить или убрать раздел. Href — только адрес внутри системы, внешние ссылки — в «Моих ссылках».</summary>
    public async Task<IReadOnlyList<FavoriteItem>> ToggleFavoriteAsync(string? title, string? href, CancellationToken ct = default)
    {
        var path = (href ?? string.Empty).Trim().TrimStart('/');
        if (path.Contains("://", StringComparison.Ordinal) || path.StartsWith('/') || path.Length > 300)
        {
            throw new BusinessRuleException("workspace.favorite", "В избранное добавляются только разделы системы.");
        }

        var list = (await GetFavoritesAsync(ct)).ToList();
        var existing = list.FindIndex(f => f.Href == path);
        if (existing >= 0)
        {
            list.RemoveAt(existing);
        }
        else
        {
            if (list.Count >= MaxFavorites)
            {
                throw new BusinessRuleException("workspace.limit", $"Не больше {MaxFavorites} избранных разделов.");
            }

            list.Add(new FavoriteItem(Text(title, TitleMaxLength, "Название"), path));
        }

        await SaveAsync(FavoritesKind, list, ct);
        return list;
    }

    // --- Погода ------------------------------------------------------------------------------

    public async Task<string?> GetWeatherCityAsync(CancellationToken ct = default) =>
        (await LoadAsync<Dictionary<string, string>>(WeatherKind, ct))?.GetValueOrDefault("city");

    public async Task SaveWeatherCityAsync(string? city, CancellationToken ct = default) =>
        await SaveAsync(WeatherKind, new Dictionary<string, string> { ["city"] = Text(city, 100, "Город") }, ct);

    // --- Хранение ----------------------------------------------------------------------------

    private async Task<T?> LoadAsync<T>(string kind, CancellationToken ct)
    {
        await using var db = factory.Create();
        var ctx = await new AccessGuard(db, currentUser, clock).CurrentAsync(ct);
        var row = await db.UserToolData.AsNoTracking()
            .SingleOrDefaultAsync(d => d.OrganizationId == ctx.OrganizationId && d.UserId == ctx.UserId && d.Kind == kind, ct);
        if (row is null)
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(row.Json, Json);
        }
        catch (JsonException)
        {
            // Повреждённые данные не должны ломать панель: инструмент начинает с пустого состояния.
            return default;
        }
    }

    private async Task SaveAsync<T>(string kind, T value, CancellationToken ct)
    {
        await using var db = factory.Create();
        var ctx = await new AccessGuard(db, currentUser, clock).CurrentAsync(ct);
        var json = JsonSerializer.Serialize(value, Json);
        var row = await db.UserToolData
            .SingleOrDefaultAsync(d => d.OrganizationId == ctx.OrganizationId && d.UserId == ctx.UserId && d.Kind == kind, ct);
        if (row is null)
        {
            db.UserToolData.Add(UserToolData.Create(ctx.OrganizationId, ctx.UserId, kind, json, clock.UtcNow));
        }
        else
        {
            row.Update(json, clock.UtcNow);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Параллельное сохранение с другого устройства: побеждает последнее (D47) — пишем ещё раз поверх.
            await using var retry = factory.Create();
            var again = await retry.UserToolData
                .SingleOrDefaultAsync(d => d.OrganizationId == ctx.OrganizationId && d.UserId == ctx.UserId && d.Kind == kind, ct);
            if (again is null)
            {
                retry.UserToolData.Add(UserToolData.Create(ctx.OrganizationId, ctx.UserId, kind, json, clock.UtcNow));
            }
            else
            {
                again.Update(json, clock.UtcNow);
            }

            await retry.SaveChangesAsync(ct);
        }
    }

    private static string NewIdIfEmpty(string? id) =>
        string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N")[..12] : id.Trim()[..Math.Min(id.Trim().Length, 40)];

    private static string Text(string? value, int max, string field)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v))
        {
            throw new BusinessRuleException("field.required", $"Поле «{field}» обязательно.");
        }

        return v.Length <= max ? v : throw new BusinessRuleException("field.too_long", $"Поле «{field}» длиннее {max} символов.");
    }

    /// <summary>Сегодня по времени организации — для календаря и напоминаний.</summary>
    public static DateOnly Today(DateTime utcNow, string timeZoneId) =>
        DateOnly.FromDateTime(TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var tz)
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), tz)
            : utcNow);

    public static string FormatDate(DateOnly date) => date.ToString("d MMMM", CultureInfo.GetCultureInfo("ru-RU"));
}
