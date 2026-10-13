using KnitErp.Domain.Common;

namespace KnitErp.Domain.Crm;

public enum CrmDealOutcome : byte
{
    Open = 1,
    Won = 2,
    Lost = 3,
}

/// <summary>Поля сделки. ResponsibleUserId null — ответственный автор (у новой) или прежний (при правке).</summary>
public sealed record CrmDealValues(
    string? Title, long CounterpartyId, long? ContactId = null, decimal Amount = 0, string? CurrencyCode = null, DateOnly? ExpectedCloseDate = null,
    long? ResponsibleUserId = null, long? ProjectId = null, long? ChannelId = null, long? SourceId = null);

/// <summary>
/// Сделка (D606) — квалифицированная возможность продажи: контрагент (обязательно), основной контакт, воронка и этап, сумма,
/// вероятность (по умолчанию из этапа), плановая дата закрытия, ответственный и наблюдатели, проект и канал базы, источник.
/// Итог «выиграна / проиграна» задаёт финальный этап воронки; проигрыш — с причиной. Каждая смена этапа — запись в историю
/// (только вставка). Сделка превращается в заказы покупателя базы (D607): связь хранит <see cref="CrmDealOrder"/>.
/// </summary>
public sealed class CrmDeal
{
    public const string NumberPrefix = "С";
    public const int TitleMaxLength = 200;
    public const int CommentMaxLength = 500;
    public const int MaxLines = 100;
    public const int MaxParticipants = 20;

    private readonly List<CrmDealLine> _lines = [];
    private readonly List<CrmDealParticipant> _participants = [];

    private CrmDeal()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Number { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public long CounterpartyId { get; private set; }
    public long? ContactId { get; private set; }
    public long PipelineId { get; private set; }
    public long StageId { get; private set; }
    public decimal Amount { get; private set; }
    public string CurrencyCode { get; private set; } = "RUB";
    public int Probability { get; private set; }
    public DateOnly? ExpectedCloseDate { get; private set; }
    public long ResponsibleUserId { get; private set; }
    public long? ProjectId { get; private set; }
    public long? ChannelId { get; private set; }
    public long? SourceId { get; private set; }
    public long? LeadId { get; private set; }
    public CrmDealOutcome Outcome { get; private set; }
    public long? LossReasonId { get; private set; }
    public string? LossComment { get; private set; }

    /// <summary>Когда сделка пришла на текущий этап — из этого считается время на этапе в истории.</summary>
    public DateTime StageEnteredAtUtc { get; private set; }

    public DateTime? ClosedAtUtc { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public IReadOnlyList<CrmDealLine> Lines => _lines;
    public IReadOnlyList<CrmDealParticipant> Participants => _participants;

    /// <summary>Сумма, взвешенная по вероятности, — для прогноза воронки.</summary>
    public decimal WeightedAmount => Money.Round(Amount * Probability / 100m);

    public static (CrmDeal Deal, CrmDealStageHistory Entry) Create(
        long organizationId, string number, CrmDealValues values, CrmStage stage, string currencyFallback, long userId, DateTime nowUtc, long? leadId = null)
    {
        if (stage.IsFinal || stage.IsArchived)
        {
            throw new BusinessRuleException("crm.deal.first_stage", "Новая сделка начинается с рабочего этапа воронки.");
        }

        var deal = new CrmDeal
        {
            OrganizationId = organizationId,
            Number = number,
            PipelineId = stage.PipelineId,
            StageId = stage.Id,
            Probability = stage.Probability,
            Outcome = CrmDealOutcome.Open,
            StageEnteredAtUtc = nowUtc,
            CreatedByUserId = userId,
            CreatedAtUtc = nowUtc,
            ResponsibleUserId = values.ResponsibleUserId ?? userId,
            LeadId = leadId,
        };
        deal.Apply(values, currencyFallback);
        return (deal, CrmDealStageHistory.Create(deal, null, stage.Id, userId, nowUtc, 0));
    }

    public void Update(CrmDealValues values)
    {
        EnsureOpen();
        Apply(values, CurrencyCode);
        if (values.ResponsibleUserId is { } responsible)
        {
            ResponsibleUserId = responsible;
        }
    }

    /// <summary>Ответственный меняется отдельно: чужую сделку переназначает только руководитель (право crm.all — проверяет сервис).</summary>
    public void Reassign(long userId) => ResponsibleUserId = userId;

    /// <summary>
    /// Переход на этап своей воронки — вперёд, назад или на финальный. Вероятность берётся из этапа; итог — из вида этапа;
    /// проигрыш требует причину. Возвращает запись истории (только вставка).
    /// </summary>
    public CrmDealStageHistory MoveTo(CrmStage stage, long userId, DateTime nowUtc, long? lossReasonId = null, string? lossComment = null)
    {
        if (stage.PipelineId != PipelineId || stage.OrganizationId != OrganizationId)
        {
            throw new BusinessRuleException("crm.deal.other_pipeline", "Этап из другой воронки.");
        }

        if (stage.IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", $"Этап «{stage.Name}» в архиве.");
        }

        if (stage.Id == StageId)
        {
            throw new BusinessRuleException("crm.deal.same_stage", "Сделка уже на этом этапе.");
        }

        if (stage.Kind == CrmStageKind.Lost && lossReasonId is null)
        {
            throw new BusinessRuleException("crm.deal.loss_reason", "Укажите причину проигрыша.");
        }

        var days = Math.Round((decimal)(nowUtc - StageEnteredAtUtc).TotalDays, 2);
        var entry = CrmDealStageHistory.Create(this, StageId, stage.Id, userId, nowUtc, Math.Max(0, days));
        StageId = stage.Id;
        StageEnteredAtUtc = nowUtc;
        Probability = stage.Probability;
        Outcome = stage.Kind switch
        {
            CrmStageKind.Won => CrmDealOutcome.Won,
            CrmStageKind.Lost => CrmDealOutcome.Lost,
            _ => CrmDealOutcome.Open,
        };
        LossReasonId = stage.Kind == CrmStageKind.Lost ? lossReasonId : null;
        LossComment = stage.Kind == CrmStageKind.Lost ? DomainText.Optional(lossComment, CommentMaxLength, "Комментарий") : null;
        ClosedAtUtc = stage.IsFinal ? nowUtc : null;
        return entry;
    }

    /// <summary>Вероятность вручную, 0–100 %: у открытой сделки менеджер может уточнить оценку этапа.</summary>
    public void SetProbability(int probability)
    {
        EnsureOpen();
        Probability = probability is >= 0 and <= 100
            ? probability
            : throw new BusinessRuleException("crm.stage.probability", "Вероятность — от 0 до 100 %.");
    }

    /// <summary>Позиция каталога: новая или замена. Если в сделке есть позиции, её сумма — их сумма (D607).</summary>
    public void SetLine(long itemId, decimal quantity, decimal price)
    {
        EnsureOpen();
        Quantities.EnsurePositive(quantity);
        if (price < 0 || decimal.Round(price, 4) != price)
        {
            throw new BusinessRuleException("sales.price", "Цена — не меньше нуля, не больше четырёх знаков после запятой.");
        }

        var line = _lines.FirstOrDefault(l => l.ItemId == itemId);
        if (line is null)
        {
            if (_lines.Count >= MaxLines)
            {
                throw new BusinessRuleException("crm.deal.too_many_lines", $"В сделке не больше {MaxLines} позиций.");
            }

            line = new CrmDealLine(OrganizationId, itemId);
            _lines.Add(line);
        }

        line.Set(quantity, price);
        Amount = _lines.Sum(l => l.Amount);
    }

    public void RemoveLine(long itemId)
    {
        EnsureOpen();
        var line = _lines.FirstOrDefault(l => l.ItemId == itemId) ?? throw new BusinessRuleException("crm.deal.line_missing", "Такой позиции в сделке нет.");
        _lines.Remove(line);
        if (_lines.Count > 0)
        {
            Amount = _lines.Sum(l => l.Amount);
        }
    }

    /// <summary>Наблюдатель видит сделку без права crm.all (D600).</summary>
    public bool AddParticipant(long userId)
    {
        if (userId == ResponsibleUserId || _participants.Any(p => p.UserId == userId))
        {
            return false;
        }

        if (_participants.Count >= MaxParticipants)
        {
            throw new BusinessRuleException("crm.deal.too_many_participants", $"Наблюдателей не больше {MaxParticipants}.");
        }

        _participants.Add(new CrmDealParticipant(userId));
        return true;
    }

    public bool RemoveParticipant(long userId) => _participants.RemoveAll(p => p.UserId == userId) > 0;

    public void EnsureOpen()
    {
        if (Outcome != CrmDealOutcome.Open)
        {
            throw new BusinessRuleException("crm.deal.closed", "Сделка закрыта. Чтобы изменить её, верните на рабочий этап.");
        }
    }

    public static string OutcomeName(CrmDealOutcome outcome) => outcome switch
    {
        CrmDealOutcome.Open => "В работе",
        CrmDealOutcome.Won => "Выиграна",
        CrmDealOutcome.Lost => "Проиграна",
        _ => outcome.ToString(),
    };

    public string Describe() =>
        $"{Number} «{Title}», сумма {Amount:0.00} {CurrencyCode}, вероятность {Probability} %, закрытие {ExpectedCloseDate?.ToString("dd.MM.yyyy") ?? "—"}";

    private void Apply(CrmDealValues v, string currencyFallback)
    {
        Title = DomainText.Require(v.Title, TitleMaxLength, "Название");
        CounterpartyId = v.CounterpartyId;
        ContactId = v.ContactId;
        if (_lines.Count == 0)
        {
            Amount = CrmCurrency.Amount(v.Amount) ?? 0;
        }

        CurrencyCode = CrmCurrency.Ensure(v.CurrencyCode, currencyFallback);
        ExpectedCloseDate = v.ExpectedCloseDate;
        ProjectId = v.ProjectId;
        ChannelId = v.ChannelId;
        SourceId = v.SourceId;
    }
}

/// <summary>Позиция каталога в сделке: из них «Создать заказ» составит строки заказа покупателя.</summary>
public sealed class CrmDealLine
{
    private CrmDealLine()
    {
    }

    internal CrmDealLine(long organizationId, long itemId) => (OrganizationId, ItemId) = (organizationId, itemId);

    public long Id { get; private set; }

    /// <summary>Организация — для составных внешних ключей на сделку и номенклатуру (D682).</summary>
    public long OrganizationId { get; private set; }

    public long DealId { get; private set; }
    public long ItemId { get; private set; }
    public decimal Quantity { get; private set; }
    public decimal Price { get; private set; }
    public decimal Amount { get; private set; }

    internal void Set(decimal quantity, decimal price)
    {
        Quantity = quantity;
        Price = price;
        Amount = Money.Round(quantity * price);
    }
}

/// <summary>Наблюдатель сделки — видит её, как ответственный, но без права менять ответственного.</summary>
public sealed class CrmDealParticipant
{
    private CrmDealParticipant()
    {
    }

    internal CrmDealParticipant(long userId) => UserId = userId;

    public long Id { get; private set; }
    public long DealId { get; private set; }
    public long UserId { get; private set; }
}

/// <summary>
/// История этапов сделки (D602) — только вставка (триггер в базе): кто, когда, с какого этапа на какой и сколько дней сделка
/// провела на прежнем этапе. По ней строятся отчёты скорости и конверсии.
/// </summary>
public sealed class CrmDealStageHistory
{
    private CrmDealStageHistory()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long DealId { get; private set; }
    public long? FromStageId { get; private set; }
    public long ToStageId { get; private set; }
    public long ChangedByUserId { get; private set; }
    public DateTime ChangedAtUtc { get; private set; }
    public decimal DaysInStage { get; private set; }

    /// <summary>Сделка ещё может быть не сохранена (id = 0) — тогда сервис проставляет DealId после сохранения.</summary>
    internal static CrmDealStageHistory Create(CrmDeal deal, long? fromStageId, long toStageId, long userId, DateTime nowUtc, decimal days) =>
        new()
        {
            OrganizationId = deal.OrganizationId,
            DealId = deal.Id,
            FromStageId = fromStageId,
            ToStageId = toStageId,
            ChangedByUserId = userId,
            ChangedAtUtc = nowUtc,
            DaysInStage = days,
        };

    public void AttachTo(CrmDeal deal)
    {
        if (DealId == 0)
        {
            DealId = deal.Id;
        }
    }
}

/// <summary>
/// Связь сделки с заказом покупателя базы (D607): заказ создан из сделки кнопкой «Создать заказ». Хранится в CRM, а не полем
/// SalesOrder.CrmDealId, — так модуль продаж не меняется; один заказ принадлежит не больше чем одной сделке.
/// </summary>
public sealed class CrmDealOrder
{
    private CrmDealOrder()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long DealId { get; private set; }
    public long SalesOrderId { get; private set; }

    /// <summary>КП, по которому создан заказ («Заказ по КП», D625); повторное нажатие возвращает этот заказ (D129).</summary>
    public long? OfferId { get; private set; }

    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static CrmDealOrder Create(long organizationId, long dealId, long salesOrderId, long userId, DateTime nowUtc, long? offerId = null) =>
        new()
        {
            OrganizationId = organizationId, DealId = dealId, SalesOrderId = salesOrderId, OfferId = offerId, CreatedByUserId = userId,
            CreatedAtUtc = nowUtc,
        };
}
