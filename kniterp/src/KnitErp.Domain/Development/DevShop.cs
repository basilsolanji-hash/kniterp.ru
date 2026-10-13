using KnitErp.Domain.Common;

namespace KnitErp.Domain.Development;

/// <summary>Состояние образца (D714). Числа хранятся в базе.</summary>
public enum DevSampleStatus : byte
{
    Knitting = 1,
    Ready = 2,
    Accepted = 3,
    Rework = 4,
    Rejected = 5,
}

/// <summary>Где физически находится образец (D723, ТЗ §8 «Образец у заказчика»). Числа хранятся в базе.</summary>
public enum DevSampleCustody : byte
{
    AtFactory = 0,
    AtCustomer = 1,
    Returned = 2,
    Kept = 3,
    Sold = 4,
}

/// <summary>
/// Образец модели (D714): «образец № 1, 2, 3» в размере сетки. Цех записывает по каждой детали фактические время вязания, вес
/// и доли цветов, конструктор — замеры по табелю мер. Решение по образцу (принят, на доработку, отклонён) — с правом изменения модели.
/// </summary>
public sealed class DevSample
{
    public const int CommentMaxLength = 500;
    public const int MaxPerModel = 50;

    private DevSample()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long ModelId { get; private set; }
    public int No { get; private set; }
    public long? SizeId { get; private set; }
    public DevSampleStatus Status { get; private set; }
    public string? Comment { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public long CreatedByUserId { get; private set; }

    /// <summary>Черновик складского списания пряжи на этот образец (D721) — второй раз не списываем, пока документ не отменён.</summary>
    public long? YarnWriteOffDocumentId { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Запомнить документ списания пряжи (D721).</summary>
    public void LinkYarnWriteOff(long documentId) => YarnWriteOffDocumentId = documentId;

    public static string StatusName(DevSampleStatus status) => status switch
    {
        DevSampleStatus.Knitting => "Вяжется",
        DevSampleStatus.Ready => "Связан",
        DevSampleStatus.Accepted => "Принят",
        DevSampleStatus.Rework => "На доработку",
        _ => "Отклонён",
    };

    public static DevSample Create(long organizationId, long modelId, int no, long? sizeId, DateTime nowUtc, long userId) => new()
    {
        OrganizationId = organizationId,
        ModelId = modelId,
        No = no,
        SizeId = sizeId,
        Status = DevSampleStatus.Knitting,
        CreatedAtUtc = nowUtc,
        CreatedByUserId = userId,
    };

    /// <summary>Причина доработки или отказа из справочника (D768); для принятого образца — пусто.</summary>
    public long? ReasonId { get; private set; }

    /// <summary>Решение по образцу: на доработку или отказ — с причиной из справочника (D768) или с текстом «что не так».</summary>
    public void Decide(DevSampleStatus status, string? comment, long? reasonId = null)
    {
        if (!Enum.IsDefined(status))
        {
            throw new BusinessRuleException("dev.sample.status", "Выберите результат образца.");
        }

        var negative = status is DevSampleStatus.Rework or DevSampleStatus.Rejected;
        if (negative && reasonId is null && string.IsNullOrWhiteSpace(comment))
        {
            throw new BusinessRuleException("dev.sample.reason", "Выберите причину доработки или отказа либо напишите, что не так.");
        }

        Status = status;
        ReasonId = negative ? reasonId : null;
        Comment = DomainText.Optional(comment, CommentMaxLength, "Комментарий");
    }

    public const int RecipientMaxLength = 200;
    public const int CustodyNoteMaxLength = 300;

    /// <summary>Где образец (D723): на фабрике, у заказчика, возвращён, оставлен у заказчика, продан.</summary>
    public DevSampleCustody Custody { get; private set; }

    /// <summary>Контрагент, которому передан образец (заказчик модели на момент передачи).</summary>
    public long? CustodyCounterpartyId { get; private set; }

    /// <summary>Кому передан: организация и контактное лицо.</summary>
    public string? CustodyRecipient { get; private set; }

    public DateOnly? HandedOn { get; private set; }
    public DateOnly? ReturnBy { get; private set; }

    /// <summary>Дата возврата, либо дата, когда образец оставлен у заказчика или продан.</summary>
    public DateOnly? CustodyClosedOn { get; private set; }

    public string? CustodyNote { get; private set; }

    public static string CustodyName(DevSampleCustody custody) => custody switch
    {
        DevSampleCustody.AtCustomer => "У заказчика",
        DevSampleCustody.Returned => "Возвращён",
        DevSampleCustody.Kept => "Оставлен у заказчика",
        DevSampleCustody.Sold => "Продан заказчику",
        _ => "На фабрике",
    };

    /// <summary>Не вернули к сроку: образец у заказчика, «вернуть до» прошло.</summary>
    public static bool IsReturnOverdue(DevSampleCustody custody, DateOnly? returnBy, DateOnly today) =>
        custody == DevSampleCustody.AtCustomer && returnBy is { } d && d < today;

    /// <summary>
    /// Передать образец заказчику (D723): связанный образец, который сейчас на фабрике (в том числе возвращённый). Кому — обязательно;
    /// «вернуть до» — не раньше даты передачи; пусто — образец отдан без срока возврата.
    /// </summary>
    public void HandToCustomer(long? counterpartyId, string? recipient, DateOnly on, DateOnly? returnBy, string? note)
    {
        if (Status == DevSampleStatus.Knitting)
        {
            throw new BusinessRuleException("dev.sample.custody.knitting", "Образец ещё вяжется — передать заказчику можно связанный образец.");
        }

        if (Custody is not (DevSampleCustody.AtFactory or DevSampleCustody.Returned))
        {
            throw new BusinessRuleException("dev.sample.custody.out", "Образец уже у заказчика или не вернётся на фабрику.");
        }

        if (returnBy is { } r && r < on)
        {
            throw new BusinessRuleException("dev.sample.custody.date", "Дата возврата не может быть раньше даты передачи.");
        }

        CustodyRecipient = DomainText.Require(recipient, RecipientMaxLength, "Кому передан образец");
        CustodyNote = DomainText.Optional(note, CustodyNoteMaxLength, "Комментарий");
        CustodyCounterpartyId = counterpartyId;
        HandedOn = on;
        ReturnBy = returnBy;
        CustodyClosedOn = null;
        Custody = DevSampleCustody.AtCustomer;
    }

    /// <summary>Закрыть передачу: образец вернули, оставили у заказчика или продали ему. Дата — не раньше передачи.</summary>
    public void CloseCustody(DevSampleCustody result, DateOnly on, string? note)
    {
        if (result is not (DevSampleCustody.Returned or DevSampleCustody.Kept or DevSampleCustody.Sold))
        {
            throw new BusinessRuleException("dev.sample.custody.result", "Выберите: возвращён, оставлен у заказчика или продан.");
        }

        if (Custody != DevSampleCustody.AtCustomer)
        {
            throw new BusinessRuleException("dev.sample.custody.not_out", "Образец не у заказчика.");
        }

        if (HandedOn is { } h && on < h)
        {
            throw new BusinessRuleException("dev.sample.custody.date", "Дата возврата не может быть раньше даты передачи.");
        }

        if (note is not null)
        {
            CustodyNote = DomainText.Optional(note, CustodyNoteMaxLength, "Комментарий");
        }

        CustodyClosedOn = on;
        Custody = result;
    }

    /// <summary>Цех записал факт по всем деталям — образец связан (если решения ещё не было).</summary>
    public void MarkKnitted()
    {
        if (Status == DevSampleStatus.Knitting)
        {
            Status = DevSampleStatus.Ready;
        }
    }
}

/// <summary>Факт по детали образца (D714): время вязания, вес и доли цветов палитры — вносит цех с телефона у машины.</summary>
public sealed class DevSampleDetail
{
    private DevSampleDetail()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long SampleId { get; private set; }
    public long DetailId { get; private set; }
    public int? KnitSeconds { get; private set; }
    public decimal? WeightGrams { get; private set; }
    public DateTime RecordedAtUtc { get; private set; }
    public long RecordedByUserId { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static DevSampleDetail Create(long organizationId, long sampleId, long detailId) =>
        new() { OrganizationId = organizationId, SampleId = sampleId, DetailId = detailId };

    public void Record(int? knitSeconds, decimal? weightGrams, DateTime nowUtc, long userId)
    {
        if (knitSeconds is < 1 or > 86_400)
        {
            throw new BusinessRuleException("dev.detail.time", "Время вязания — от 1 секунды до 24 часов.");
        }

        if (weightGrams is <= 0 or > 100_000)
        {
            throw new BusinessRuleException("dev.detail.weight", "Вес детали — больше 0 и не больше 100 кг.");
        }

        if (knitSeconds is null && weightGrams is null)
        {
            throw new BusinessRuleException("dev.sample.empty", "Внесите время вязания или вес детали.");
        }

        KnitSeconds = knitSeconds;
        WeightGrams = weightGrams is { } w ? decimal.Round(w, 2) : null;
        RecordedAtUtc = nowUtc;
        RecordedByUserId = userId;
    }
}

/// <summary>Доля цвета палитры в детали образца, %.</summary>
public sealed class DevSampleColorShare
{
    private DevSampleColorShare()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long SampleDetailId { get; private set; }
    public long ColorId { get; private set; }
    public decimal Percent { get; private set; }

    public static DevSampleColorShare Create(long organizationId, long sampleDetailId, long colorId, decimal percent) => new()
    {
        OrganizationId = organizationId,
        SampleDetailId = sampleDetailId,
        ColorId = colorId,
        Percent = decimal.Round(percent, 2),
    };

    /// <summary>Доли: каждая 0–100 %, сумма — 100 % (допуск 0,5 % на округление весов). Пустой набор допустим (однотонная деталь).</summary>
    public static void EnsureValid(IReadOnlyCollection<(long ColorId, decimal Percent)> shares)
    {
        if (shares.Count == 0)
        {
            return;
        }

        if (shares.Any(s => s.Percent is < 0 or > 100))
        {
            throw new BusinessRuleException("dev.share.range", "Доля цвета — от 0 до 100 %.");
        }

        if (shares.GroupBy(s => s.ColorId).Any(g => g.Count() > 1))
        {
            throw new BusinessRuleException("dev.share.duplicate", "Цвет указан дважды.");
        }

        var sum = shares.Sum(s => s.Percent);
        if (Math.Abs(sum - 100m) > 0.5m)
        {
            throw new BusinessRuleException("dev.share.sum", $"Сумма долей цветов — {sum:0.##} %, должна быть 100 %.");
        }
    }

    /// <summary>Доли по весу каждого цвета (граммы) — когда цех взвесил остатки бобин: доля = вес цвета / общий вес.</summary>
    public static IReadOnlyList<(long ColorId, decimal Percent)> FromWeights(IReadOnlyCollection<(long ColorId, decimal Grams)> weights)
    {
        var total = weights.Sum(w => w.Grams);
        if (total <= 0)
        {
            return [];
        }

        var shares = weights.Select(w => (w.ColorId, Percent: decimal.Round(w.Grams * 100m / total, 2))).ToList();
        // Остаток округления — в самую большую долю, чтобы сумма была ровно 100.
        var diff = 100m - shares.Sum(s => s.Percent);
        if (diff != 0 && shares.Count > 0)
        {
            var i = shares.FindIndex(s => s.Percent == shares.Max(x => x.Percent));
            shares[i] = (shares[i].ColorId, shares[i].Percent + diff);
        }

        return shares;
    }
}

/// <summary>Замер образца по точке табеля мер, см.</summary>
public sealed class DevSampleMeasure
{
    private DevSampleMeasure()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }

    /// <summary>Версия строки (D720): одновременное сохранение той же ячейки — конфликт, а не тихая перезапись.</summary>
    public byte[] RowVersion { get; private set; } = [];

    public long SampleId { get; private set; }
    public long MeasureId { get; private set; }
    public decimal Value { get; private set; }

    public static DevSampleMeasure Create(long organizationId, long sampleId, long measureId, decimal value)
    {
        var m = new DevSampleMeasure { OrganizationId = organizationId, SampleId = sampleId, MeasureId = measureId };
        m.Set(value);
        return m;
    }

    public void Set(decimal value)
    {
        DevMeasureRules.EnsureCm(value);
        Value = DevMeasureRules.Round(value)!.Value;
    }
}
