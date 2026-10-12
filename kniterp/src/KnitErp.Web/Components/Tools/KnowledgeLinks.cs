using KnitErp.Application.Workspace.Knowledge;

namespace KnitErp.Web.Components.Tools;

/// <summary>
/// Кнопка «Открыть» у ответа базы знаний (D913): только экраны, доступные пользователю по правилам меню. Экран, которого нет в меню
/// (профиль, обучение), открыт всем. Скрытая кнопка — удобство, а не защита: права проверяет сервер на самой странице.
/// </summary>
public static class KnowledgeLinks
{
    public sealed record Link(string Href, string Label);

    public static IReadOnlyList<Link> Allowed(ToolContext context, IEnumerable<string> links)
    {
        var result = new List<Link>();
        foreach (var link in links.Where(l => l.Length > 0).Distinct())
        {
            var path = link.Split('?', '#')[0].Trim('/');
            var exact = context.Sections.FirstOrDefault(s => s.Href == link);
            var same = context.Sections.Where(s => s.Href.Split('?', '#')[0].Trim('/') == path).ToList();
            if (exact is not null ? !exact.Allowed : same.Count > 0 && !same.Any(s => s.Allowed))
            {
                continue;
            }

            result.Add(new Link(link, exact?.Label ?? same.FirstOrDefault()?.Label ?? ""));
        }

        return result;
    }

    /// <summary>Источники и дата проверки — для ответов и отраслевых статей.</summary>
    public static string? SourcesLine(KnowledgeDoc? doc) =>
        doc is null || doc.Kind == KnowledgeKind.Help || doc.Sources.Count == 0 ? null : string.Join("; ", doc.Sources);
}
