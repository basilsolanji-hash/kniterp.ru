using KnitErp.Domain.Common;

namespace KnitErp.Domain.Production;

public enum TechCardStatus : byte
{
    Draft = 1,
    Active = 2,
    Archived = 9,
}

/// <summary>
/// Технологическая карта изделия (D63): из чего и сколько уходит на партию изделия — пряжа, фурнитура, упаковка.
/// Нормы задаются на партию <see cref="OutputQuantity"/> (обычно 1 шт или 10 шт) с процентом отхода.
/// Черновик правится; «В действие» фиксирует нормы — у изделия одна действующая версия, предыдущая уходит в архив.
/// Изменить действующую нельзя: создаётся новая версия копией, чтобы прошлый выпуск оставался посчитан по старым нормам.
/// </summary>
public sealed class TechCard
{
    public const int CommentMaxLength = 1000;
    public const int MaxLines = 200;

    /// <summary>Знаков после запятой у нормы: норма на одно изделие бывает дробной (12,5 г пряжи — 0,0125 кг).</summary>
    public const int NormPrecision = 6;

    private readonly List<TechCardLine> _lines = [];

    private TechCard()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }

    /// <summary>Изделие или полуфабрикат, который получается по карте.</summary>
    public long ItemId { get; private set; }

    /// <summary>Номер версии карты у изделия: 1, 2, 3…</summary>
    public int Version { get; private set; }

    /// <summary>На какое количество изделия заданы нормы.</summary>
    public decimal OutputQuantity { get; private set; }

    public TechCardStatus Status { get; private set; }
    public string? Comment { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public long? ActivatedByUserId { get; private set; }
    public DateTime? ActivatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>
    /// Номер правки состава (аудит 10.10.2026, п. 1): растёт при любом изменении партии или строк, поэтому строка самой карты
    /// обновляется и её rowversion проверяется — старая вкладка получает конфликт, а не перезаписывает чужую правку.
    /// </summary>
    public int Revision { get; private set; }

    public IReadOnlyList<TechCardLine> Lines => _lines;

    public static TechCard Create(long organizationId, long itemId, int version, decimal outputQuantity, string? comment, long userId, DateTime nowUtc)
    {
        if (version < 1)
        {
            throw new BusinessRuleException("techcard.version", "Номер версии — с 1.");
        }

        var card = new TechCard
        {
            OrganizationId = organizationId,
            ItemId = itemId,
            Version = version,
            Status = TechCardStatus.Draft,
            CreatedByUserId = userId,
            CreatedAtUtc = nowUtc,
        };
        card.SetHeader(outputQuantity, comment);
        return card;
    }

    /// <summary>Новая версия-черновик с теми же нормами — так правят действующую карту.</summary>
    public TechCard CopyAsVersion(int version, long userId, DateTime nowUtc)
    {
        var copy = Create(OrganizationId, ItemId, version, OutputQuantity, Comment, userId, nowUtc);
        foreach (var line in _lines)
        {
            copy._lines.Add(new TechCardLine(line.ItemId, line.Quantity, line.WastePercent));
        }

        return copy;
    }

    public void SetHeader(decimal outputQuantity, string? comment)
    {
        EnsureDraft();
        if (outputQuantity <= 0)
        {
            throw new BusinessRuleException("techcard.output", "Количество изделия в партии — больше нуля.");
        }

        var text = DomainText.Optional(comment, CommentMaxLength, "Комментарий");
        OutputQuantity = outputQuantity;
        Comment = text;
        Revision++;
    }

    /// <summary>Норма материала на партию: добавляет строку или меняет существующую.</summary>
    public void SetLine(long materialItemId, decimal quantity, decimal wastePercent)
    {
        EnsureDraft();
        if (materialItemId == ItemId)
        {
            throw new BusinessRuleException("techcard.self", "Изделие не может быть материалом самого себя.");
        }

        if (quantity <= 0)
        {
            throw new BusinessRuleException("techcard.quantity", "Норма расхода — больше нуля.");
        }

        if (decimal.Round(quantity, NormPrecision) != quantity)
        {
            throw new BusinessRuleException("techcard.quantity.precision", $"Норма — не больше {NormPrecision} знаков после запятой.");
        }

        if (wastePercent is < 0 or >= 100 || decimal.Round(wastePercent, 2) != wastePercent)
        {
            throw new BusinessRuleException("techcard.waste", "Отход — от 0 до 99,99%, не больше двух знаков после запятой.");
        }

        var line = _lines.FirstOrDefault(l => l.ItemId == materialItemId);
        if (line is null)
        {
            if (_lines.Count >= MaxLines)
            {
                throw new BusinessRuleException("techcard.too_many_lines", $"В карте не больше {MaxLines} материалов.");
            }

            _lines.Add(new TechCardLine(materialItemId, quantity, wastePercent));
        }
        else
        {
            line.Set(quantity, wastePercent);
        }

        Revision++;
    }

    public void RemoveLine(long materialItemId)
    {
        EnsureDraft();
        var line = _lines.FirstOrDefault(l => l.ItemId == materialItemId)
                   ?? throw new BusinessRuleException("techcard.line_missing", "Такого материала в карте нет.");
        _lines.Remove(line);
        Revision++;
    }

    /// <summary>Ввод в действие. Предыдущую действующую версию изделия архивирует сервис в той же транзакции.</summary>
    public void Activate(long userId, DateTime nowUtc)
    {
        EnsureDraft();
        if (_lines.Count == 0)
        {
            throw new BusinessRuleException("techcard.empty", "Добавьте хотя бы один материал.");
        }

        Status = TechCardStatus.Active;
        ActivatedByUserId = userId;
        ActivatedAtUtc = nowUtc;
    }

    public void Archive()
    {
        if (Status == TechCardStatus.Archived)
        {
            throw new BusinessRuleException("techcard.archived", "Карта уже в архиве.");
        }

        Status = TechCardStatus.Archived;
    }

    /// <summary>
    /// Потребность материалов на выпуск <paramref name="quantity"/> изделий: норма × (1 + отход) × количество / партия.
    /// Округление — вверх до точности единицы материала (полграмма пряжи не бывает лишним).
    /// </summary>
    public IReadOnlyList<(long ItemId, decimal Net, decimal Gross)> Requirement(decimal quantity, Func<long, int> precisionOf)
    {
        if (quantity <= 0)
        {
            throw new BusinessRuleException("techcard.requirement.quantity", "Количество изделий — больше нуля.");
        }

        return _lines.Select(l =>
        {
            var net = l.Quantity * quantity / OutputQuantity;
            var gross = net * (1 + l.WastePercent / 100m);
            var precision = Math.Clamp(precisionOf(l.ItemId), 0, NormPrecision);
            return (l.ItemId, RoundUp(net, precision), RoundUp(gross, precision));
        }).ToList();
    }

    /// <summary>
    /// Себестоимость одного изделия по нормам с отходом (D69): Σ цена материала × норма × (1 + отход) / партия.
    /// Без округления вверх — это оценка денег, а не выдача со склада. null — цена хотя бы одного материала неизвестна.
    /// </summary>
    public decimal? UnitCost(Func<long, decimal?> costOf)
    {
        var total = 0m;
        foreach (var l in _lines)
        {
            if (costOf(l.ItemId) is not { } cost)
            {
                return null;
            }

            total += cost * l.Quantity * (1 + l.WastePercent / 100m) / OutputQuantity;
        }

        return _lines.Count == 0 ? null : total;
    }

    public static string StatusName(TechCardStatus status) => status switch
    {
        TechCardStatus.Draft => "Черновик",
        TechCardStatus.Active => "Действует",
        TechCardStatus.Archived => "В архиве",
        _ => status.ToString(),
    };

    private static decimal RoundUp(decimal value, int precision)
    {
        var factor = (decimal)Math.Pow(10, precision);
        return Math.Ceiling(value * factor) / factor;
    }

    private void EnsureDraft()
    {
        if (Status != TechCardStatus.Draft)
        {
            throw new BusinessRuleException("techcard.not_draft",
                "Менять можно только черновик. Для действующей карты создайте новую версию — нормы прошлых версий сохранятся.");
        }
    }
}

/// <summary>Материал карты: норма на партию изделия и процент отхода (обрезки, распуск, брак вязки).</summary>
public sealed class TechCardLine
{
    private TechCardLine()
    {
    }

    internal TechCardLine(long itemId, decimal quantity, decimal wastePercent)
    {
        ItemId = itemId;
        Set(quantity, wastePercent);
    }

    public long Id { get; private set; }
    public long TechCardId { get; private set; }
    public long ItemId { get; private set; }
    public decimal Quantity { get; private set; }
    public decimal WastePercent { get; private set; }

    internal void Set(decimal quantity, decimal wastePercent)
    {
        Quantity = quantity;
        WastePercent = wastePercent;
    }
}

/// <summary>
/// Многоуровневая спецификация (D90, дополнение «сборка из полуфабрикатов»): полуфабрикат с действующей техкартой раскрывается
/// до материалов его карты — так видно, сколько пряжи нужно на изделие через все уровни. Циклы (изделие в своём же составе)
/// не раскрываются — техкарты их не допускают, а здесь это защита от зацикливания.
/// </summary>
public static class BillOfMaterials
{
    public const int MaxDepth = 10;

    /// <summary>
    /// Потребность в материалах нижнего уровня на <paramref name="quantity"/> изделий: строки карты, для которых есть действующая
    /// карта в <paramref name="cards"/>, заменяются их потребностью с отходом. Округление вверх — по точности единицы на каждом уровне.
    /// </summary>
    public static IReadOnlyDictionary<long, decimal> Explode(
        TechCard card, decimal quantity, IReadOnlyDictionary<long, TechCard> cards, Func<long, int> precisionOf)
    {
        var result = new Dictionary<long, decimal>();
        Walk(card, quantity, 0, [card.ItemId]);
        return result;

        void Walk(TechCard current, decimal qty, int depth, HashSet<long> path)
        {
            foreach (var (itemId, _, gross) in current.Requirement(qty, precisionOf))
            {
                if (depth < MaxDepth && cards.TryGetValue(itemId, out var sub) && !path.Contains(itemId))
                {
                    Walk(sub, gross, depth + 1, [.. path, itemId]);
                }
                else
                {
                    result[itemId] = result.GetValueOrDefault(itemId) + gross;
                }
            }
        }
    }

    /// <summary>Входит ли <paramref name="productId"/> в состав <paramref name="materialId"/> на любом уровне (по картам <paramref name="linesOf"/>).</summary>
    public static bool Contains(long materialId, long productId, Func<long, IReadOnlyCollection<long>> linesOf)
    {
        var seen = new HashSet<long>();
        var queue = new Queue<long>([materialId]);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == productId)
            {
                return true;
            }

            if (seen.Add(current))
            {
                foreach (var next in linesOf(current))
                {
                    queue.Enqueue(next);
                }
            }
        }

        return false;
    }
}
