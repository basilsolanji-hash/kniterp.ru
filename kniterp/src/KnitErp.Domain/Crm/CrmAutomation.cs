using KnitErp.Domain.Common;

namespace KnitErp.Domain.Crm;

/// <summary>Как распределяются заявки из API, телефонии и импорта (D664).</summary>
public enum CrmAssignMode : byte
{
    /// <summary>На ответственного по умолчанию (или Владельца токена API).</summary>
    Default = 1,

    /// <summary>По очереди между сотрудниками с ролью «Менеджер продаж».</summary>
    RoundRobin = 2,
}

/// <summary>Что считать фактом плана продаж (D631).</summary>
public enum CrmPlanFact : byte
{
    /// <summary>Оплаты по заказам покупателей, созданным из сделок (по умолчанию).</summary>
    Payments = 1,

    /// <summary>Суммы выигранных сделок по дате закрытия.</summary>
    WonDeals = 2,
}

public sealed record CrmAutomationValues(
    CrmAssignMode AssignMode = CrmAssignMode.Default, long? DefaultResponsibleUserId = null, bool NextStepSuggestions = true, bool StageByEvents = true,
    int? RepeatSaleDays = 60, int StaleLeadDays = 3, CrmPlanFact PlanFact = CrmPlanFact.Payments, string? OfferTerms = null, int OfferValidDays = 14);

/// <summary>
/// Настройки автоматизации CRM организации (D660–D669, документ `docs/tz/crm-automation.md`): распределение заявок, предложения
/// следующего шага, смена этапа по событиям, задача повторной продажи, порог «лид без действий», факт плана, условия КП.
/// Одна запись на организацию; нет записи — значения по умолчанию.
/// </summary>
public sealed class CrmSettings
{
    public const int OfferTermsMaxLength = CrmOffer.TermsMaxLength;

    private CrmSettings()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public CrmAssignMode AssignMode { get; private set; } = CrmAssignMode.Default;
    public long? DefaultResponsibleUserId { get; private set; }
    public bool NextStepSuggestions { get; private set; } = true;
    public bool StageByEvents { get; private set; } = true;
    public int? RepeatSaleDays { get; private set; } = 60;
    public int StaleLeadDays { get; private set; } = 3;
    public CrmPlanFact PlanFact { get; private set; } = CrmPlanFact.Payments;
    public string? OfferTerms { get; private set; }
    public int OfferValidDays { get; private set; } = 14;

    /// <summary>Курсор распределения по очереди: id последнего назначенного менеджера.</summary>
    public long? LastAssignedUserId { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public CrmAutomationValues Values => new(AssignMode, DefaultResponsibleUserId, NextStepSuggestions, StageByEvents, RepeatSaleDays, StaleLeadDays, PlanFact,
        OfferTerms, OfferValidDays);

    public static CrmSettings Default(long organizationId) => new() { OrganizationId = organizationId };

    public string Set(CrmAutomationValues v)
    {
        if (!Enum.IsDefined(v.AssignMode) || !Enum.IsDefined(v.PlanFact))
        {
            throw new BusinessRuleException("crm.settings.value", "Неизвестное значение настройки.");
        }

        if (v.RepeatSaleDays is < 1 or > 730)
        {
            throw new BusinessRuleException("crm.settings.repeat", "Повторная продажа — через 1–730 дней или выключена.");
        }

        if (v.StaleLeadDays is < 1 or > 90)
        {
            throw new BusinessRuleException("crm.settings.stale", "Лид без действий — от 1 до 90 дней.");
        }

        if (v.OfferValidDays is < 1 or > 365)
        {
            throw new BusinessRuleException("crm.settings.offer_days", "Срок действия КП — от 1 до 365 дней.");
        }

        AssignMode = v.AssignMode;
        DefaultResponsibleUserId = v.DefaultResponsibleUserId;
        NextStepSuggestions = v.NextStepSuggestions;
        StageByEvents = v.StageByEvents;
        RepeatSaleDays = v.RepeatSaleDays;
        StaleLeadDays = v.StaleLeadDays;
        PlanFact = v.PlanFact;
        OfferTerms = DomainText.Optional(v.OfferTerms, OfferTermsMaxLength, "Условия КП");
        OfferValidDays = v.OfferValidDays;
        return $"распределение {(AssignMode == CrmAssignMode.RoundRobin ? "по очереди" : "по умолчанию")}, следующий шаг {(NextStepSuggestions ? "да" : "нет")}, "
               + $"этап по событиям {(StageByEvents ? "да" : "нет")}, повторная продажа {(RepeatSaleDays is { } r ? $"{r} дн." : "нет")}, "
               + $"лид без действий {StaleLeadDays} дн., факт плана {(PlanFact == CrmPlanFact.Payments ? "оплаты" : "выигранные сделки")}, КП {OfferValidDays} дн.";
    }

    /// <summary>Следующий по очереди из списка (по возрастанию id); пусто — null.</summary>
    public long? NextAssignee(IReadOnlyList<long> managers)
    {
        if (managers.Count == 0)
        {
            return null;
        }

        var ordered = managers.OrderBy(x => x).ToList();
        var next = ordered.FirstOrDefault(x => x > (LastAssignedUserId ?? 0));
        LastAssignedUserId = next == 0 ? ordered[0] : next;
        return LastAssignedUserId;
    }
}

/// <summary>План продаж на месяц (D631): по менеджеру и/или воронке; пусто — общий план организации.</summary>
public sealed class CrmPlan
{
    private CrmPlan()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public int Year { get; private set; }
    public int Month { get; private set; }
    public long? UserId { get; private set; }
    public long? PipelineId { get; private set; }
    public decimal Amount { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static CrmPlan Create(long organizationId, int year, int month, long? userId, long? pipelineId, decimal amount)
    {
        if (year is < 2000 or > 2100 || month is < 1 or > 12)
        {
            throw new BusinessRuleException("crm.plan.period", "Неверный месяц плана.");
        }

        var plan = new CrmPlan { OrganizationId = organizationId, Year = year, Month = month, UserId = userId, PipelineId = pipelineId };
        plan.SetAmount(amount);
        return plan;
    }

    public void SetAmount(decimal amount) =>
        Amount = CrmCurrency.Amount(amount) ?? 0;
}

public enum CrmMailingStatus : byte
{
    Draft = 1,
    Sent = 2,

    /// <summary>Запланирована (D675): уйдёт в назначенное время с правами запланировавшего.</summary>
    Scheduled = 3,

    /// <summary>В очереди (D675): получатели зафиксированы, письма уходят пачками фоновой задачей.</summary>
    Sending = 4,
}

/// <summary>
/// Рассылка (D615): тема и текст письма контактам с согласием на рекламу (38-ФЗ ст. 18). Получатели фиксируются при отправке;
/// в каждое письмо добавляется ссылка отписки. Отправленная рассылка не меняется.
/// </summary>
public sealed class CrmMailing
{
    public const int SubjectMaxLength = 200;
    public const int TextMaxLength = 8000;
    public const int MaxRecipients = 2000;

    private readonly List<CrmMailingRecipient> _recipients = [];

    private CrmMailing()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Subject { get; private set; } = string.Empty;
    public string Text { get; private set; } = string.Empty;

    /// <summary>Отбор получателей: тег контакта (null — все с согласием).</summary>
    public long? TagId { get; private set; }

    public CrmMailingStatus Status { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? SentAtUtc { get; private set; }
    public DateTime? ScheduledAtUtc { get; private set; }

    /// <summary>Кто поставил в очередь или запланировал: письма уходят с его правами (отозвали crm.mailing — рассылка встаёт).</summary>
    public long? QueuedByUserId { get; private set; }

    public byte[] RowVersion { get; private set; } = [];
    public IReadOnlyList<CrmMailingRecipient> Recipients => _recipients;

    /// <summary>Запланировать (D675): не раньше чем через 5 минут и не позже чем через 90 дней.</summary>
    public void Schedule(DateTime atUtc, long userId, DateTime nowUtc)
    {
        EnsureDraft();
        if (atUtc < nowUtc.AddMinutes(5) || atUtc > nowUtc.AddDays(90))
        {
            throw new BusinessRuleException("crm.mailing.schedule", "Время отправки — не раньше чем через 5 минут и не позже чем через 90 дней.");
        }

        (Status, ScheduledAtUtc, QueuedByUserId) = (CrmMailingStatus.Scheduled, atUtc, userId);
    }

    public void Unschedule()
    {
        if (Status != CrmMailingStatus.Scheduled)
        {
            throw new BusinessRuleException("crm.mailing.not_scheduled", "Рассылка не запланирована.");
        }

        (Status, ScheduledAtUtc, QueuedByUserId) = (CrmMailingStatus.Draft, null, null);
    }

    /// <summary>Поставить в очередь: получатели уже добавлены; дальше письма уходят пачками.</summary>
    public void Queue(long userId)
    {
        if (Status is not (CrmMailingStatus.Draft or CrmMailingStatus.Scheduled))
        {
            throw new BusinessRuleException("crm.mailing.sent", "Рассылка уже отправлена — менять её нельзя.");
        }

        if (_recipients.Count == 0)
        {
            throw new BusinessRuleException("crm.mailing.empty", "Нет получателей: у контактов нет согласия на рассылки или почты.");
        }

        Status = CrmMailingStatus.Sending;
        QueuedByUserId ??= userId;
    }

    /// <summary>Очередь пуста — рассылка завершена.</summary>
    public void Complete(DateTime nowUtc)
    {
        if (Status != CrmMailingStatus.Sending)
        {
            throw new BusinessRuleException("crm.mailing.not_sending", "Рассылка не в очереди.");
        }

        (Status, SentAtUtc) = (CrmMailingStatus.Sent, nowUtc);
    }

    public static CrmMailing Create(long organizationId, string? subject, string? text, long? tagId, long userId, DateTime nowUtc)
    {
        var m = new CrmMailing { OrganizationId = organizationId, Status = CrmMailingStatus.Draft, CreatedByUserId = userId, CreatedAtUtc = nowUtc };
        m.Update(subject, text, tagId);
        return m;
    }

    public void Update(string? subject, string? text, long? tagId)
    {
        EnsureDraft();
        Subject = DomainText.Require(subject, SubjectMaxLength, "Тема");
        Text = DomainText.Require(text, TextMaxLength, "Текст");
        TagId = tagId;
    }

    public CrmMailingRecipient AddRecipient(long contactId, string email)
    {
        if (Status is not (CrmMailingStatus.Draft or CrmMailingStatus.Scheduled))
        {
            throw new BusinessRuleException("crm.mailing.sent", "Рассылка уже отправлена — менять её нельзя.");
        }

        if (_recipients.Count >= MaxRecipients)
        {
            throw new BusinessRuleException("crm.mailing.too_many", $"В одной рассылке не больше {MaxRecipients} получателей.");
        }

        var r = new CrmMailingRecipient(contactId, email);
        _recipients.Add(r);
        return r;
    }

    private void EnsureDraft()
    {
        if (Status != CrmMailingStatus.Draft)
        {
            throw new BusinessRuleException(Status == CrmMailingStatus.Scheduled ? "crm.mailing.scheduled" : "crm.mailing.sent",
                Status == CrmMailingStatus.Scheduled ? "Рассылка запланирована — снимите её с расписания, чтобы изменить." : "Рассылка уже отправлена — менять её нельзя.");
        }
    }
}

public sealed class CrmMailingRecipient
{
    private CrmMailingRecipient()
    {
    }

    internal CrmMailingRecipient(long contactId, string email)
    {
        ContactId = contactId;
        Email = email;
    }

    public long Id { get; private set; }
    public long MailingId { get; private set; }
    public long ContactId { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public bool Delivered { get; private set; }

    /// <summary>Когда была попытка отправки; null — письмо ещё в очереди.</summary>
    public DateTime? AttemptedAtUtc { get; private set; }

    public void SetDelivered(bool delivered, DateTime nowUtc) => (Delivered, AttemptedAtUtc) = (delivered, nowUtc);

    /// <summary>Удаление ПДн по запросу (D677): адрес получателя стирается, факт отправки остаётся.</summary>
    public void ErasePersonalData() => Email = CrmContact.ErasedName;
}

/// <summary>Объект CRM для внешней ссылки импорта.</summary>
public enum CrmExternalEntity : byte
{
    Lead = 1,
    Contact = 2,
    Deal = 3,
}

/// <summary>
/// Внешний идентификатор (D646): запись из amoCRM, Битрикс24 или другой системы ↔ объект CRM. Повторный импорт того же файла
/// обновляет найденные, а не создаёт дубли.
/// </summary>
public sealed class CrmExternalLink
{
    public const int SystemMaxLength = 30;
    public const int ExternalIdMaxLength = 100;

    private CrmExternalLink()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string System { get; private set; } = string.Empty;
    public CrmExternalEntity Entity { get; private set; }
    public string ExternalId { get; private set; } = string.Empty;
    public long EntityId { get; private set; }

    public static CrmExternalLink Create(long organizationId, string system, CrmExternalEntity entity, string externalId, long entityId) =>
        new()
        {
            OrganizationId = organizationId, System = DomainText.Require(system, SystemMaxLength, "Система"), Entity = entity,
            ExternalId = DomainText.Require(externalId, ExternalIdMaxLength, "Внешний id"), EntityId = entityId,
        };
}
