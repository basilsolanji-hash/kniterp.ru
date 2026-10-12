using System.Globalization;

namespace KnitErp.Web.Components.Shared;

/// <summary>
/// D1136 (сессия «Продажи», запрос владельца «печать этикеток везде»): ссылка на печать этикеток позиций документа — по этикетке на
/// единицу количества (дробное и нулевое — одна этикетка), не больше 500 на позицию.
/// </summary>
public static class LabelLinks
{
    public static string For(IEnumerable<(long ItemId, decimal Quantity)> lines) =>
        "catalog/labels/print?ids=" + string.Join(',', lines.GroupBy(l => l.ItemId)
            .Select(g => $"{g.Key}*{Math.Clamp((int)Math.Ceiling(Math.Max(1, g.Sum(l => l.Quantity))), 1, 500).ToString(CultureInfo.InvariantCulture)}"));
}
