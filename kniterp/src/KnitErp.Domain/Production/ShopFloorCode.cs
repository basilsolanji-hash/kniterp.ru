using System.Globalization;
using System.Text.RegularExpressions;

namespace KnitErp.Domain.Production;

/// <summary>
/// Код операции для терминала цеха: этап производственного заказа. На маршрутном листе печатается QR-кодом со ссылкой
/// <c>…/shop/op/{заказ}/{этап}</c> (камера телефона открывает её сразу) и текстом <c>ОП-{заказ}-{этап}</c> для ручного ввода
/// или сканера штрихкода, который вводит текст как клавиатура. Код не секретен: доступ проверяет сервер, чужой заказ — «не найден».
/// </summary>
public static partial class ShopFloorCode
{
    public static string Text(long orderId, ProductionStage stage) => $"ОП-{orderId}-{(int)stage}";

    public static string Path(long orderId, ProductionStage stage) => $"shop/op/{orderId}/{(int)stage}";

    /// <summary>Разбирает текст кода или ссылку из QR-кода; лишние пробелы и регистр не важны, латинские «OP» тоже принимаются.</summary>
    public static bool TryParse(string? input, out long orderId, out ProductionStage stage)
    {
        orderId = 0;
        stage = default;
        if (string.IsNullOrWhiteSpace(input) || input.Length > 300)
        {
            return false;
        }

        var m = CodePattern().Match(input.Trim());
        if (!m.Success)
        {
            m = PathPattern().Match(input.Trim());
        }

        if (!m.Success
            || !long.TryParse(m.Groups["order"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var order) || order <= 0
            || !int.TryParse(m.Groups["stage"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var s)
            || !Enum.IsDefined((ProductionStage)s))
        {
            return false;
        }

        orderId = order;
        stage = (ProductionStage)s;
        return true;
    }

    [GeneratedRegex(@"^(ОП|OP)-(?<order>\d{1,18})-(?<stage>\d{1,2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"^(https?://[^/\s]+)?/?shop/op/(?<order>\d{1,18})/(?<stage>\d{1,2})/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();
}

/// <summary>Причины, почему работа на этапе выполнена не полностью, — выбор на терминале цеха (текст пишется в отметку).</summary>
public static class ShopFloorShortfall
{
    public static readonly IReadOnlyList<string> Reasons =
    [
        "Нет пряжи или материалов",
        "Поломка оборудования",
        "Брак сырья",
        "Не хватило времени",
        "Ждал предыдущий этап",
        "Другая причина",
    ];
}
