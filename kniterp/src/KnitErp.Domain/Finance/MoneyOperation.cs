using KnitErp.Domain.Common;

namespace KnitErp.Domain.Finance;

/// <summary>Вид денежной операции (D84).</summary>
public enum MoneyOperationKind : byte
{
    /// <summary>Поступление: в кассу — приходный кассовый ордер (КО-1), на счёт — прочее поступление.</summary>
    Income = 1,

    /// <summary>Выдача: из кассы — расходный кассовый ордер (КО-2), со счёта — прочее списание.</summary>
    Expense = 2,

    /// <summary>Перемещение между своими счетами и кассами одного юрлица: сдача выручки в банк, снятие наличных, перевод между счетами.</summary>
    Transfer = 3,
}

public enum MoneyOperationStatus : byte
{
    Posted = 1,
    Cancelled = 9,
}

/// <summary>
/// Денежная операция, кроме оплат покупателей и поставщикам (D84): прочие поступления и выдачи (подотчёт, зарплата, комиссия банка,
/// проценты), перемещение денег между своими счетами и кассами. В кассе оформляются кассовыми ордерами КО-1 и КО-2
/// (Указание Банка России № 3210-У, формы — постановление Госкомстата России № 88). Не удаляется — отменяется с причиной.
/// </summary>
public sealed class MoneyOperation
{
    public const string IncomeCashPrefix = "ПКО";
    public const string ExpenseCashPrefix = "РКО";
    public const string IncomeBankPrefix = "ПБ";
    public const string ExpenseBankPrefix = "СБ";
    public const string TransferPrefix = "ПД";
    public const int PartyMaxLength = 300;
    public const int BasisMaxLength = 300;
    public const int CommentMaxLength = 1000;
    public const int ReasonMaxLength = 500;

    private MoneyOperation()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Number { get; private set; } = string.Empty;
    public DateOnly OperationDate { get; private set; }
    public MoneyOperationKind Kind { get; private set; }

    /// <summary>Счёт или касса: куда пришли деньги (поступление) или откуда ушли (выдача, перемещение).</summary>
    public long AccountId { get; private set; }

    /// <summary>Куда перемещены деньги (только перемещение).</summary>
    public long? TargetAccountId { get; private set; }

    public decimal Amount { get; private set; }

    /// <summary>«Принято от» (ПКО) или «Выдать» (РКО): ФИО или организация.</summary>
    public string? Party { get; private set; }

    /// <summary>Основание: «Возврат подотчётной суммы», «Выдача под отчёт на хозяйственные нужды».</summary>
    public string Basis { get; private set; } = string.Empty;

    public string? Comment { get; private set; }

    /// <summary>Статья движения денег (D86): у поступления — статья поступления, у выдачи — статья выплаты; у перемещения — нет.</summary>
    public long? CashFlowItemId { get; private set; }

    /// <summary>Подотчётное лицо (D86): выдача под отчёт (выдача) или возврат неиспользованного аванса (поступление).</summary>
    public long? EmployeeId { get; private set; }

    /// <summary>
    /// Срок, на который выдан аванс (D87): авансовый отчёт — не позже 3 рабочих дней после него (п. 6.3 Указания № 3210-У).
    /// Только у выдачи под отчёт; не указан — 30 дней с даты выдачи.
    /// </summary>
    public DateOnly? AdvanceDueDate { get; private set; }

    /// <summary>
    /// Кассовые ордера перемещения (D86): из кассы — РКО (CashOrderNumber), в кассу — ПКО (TargetCashOrderNumber); номер самой
    /// операции — ПД. Так сдача выручки в банк и получение наличных попадают в кассовую книгу под своими ордерами.
    /// </summary>
    public string? CashOrderNumber { get; private set; }

    public string? TargetCashOrderNumber { get; private set; }

    public MoneyOperationStatus Status { get; private set; }
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public long? CancelledByUserId { get; private set; }
    public DateTime? CancelledAtUtc { get; private set; }
    public string? CancelReason { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static MoneyOperation Create(
        long organizationId, string number, DateOnly date, MoneyOperationKind kind, long accountId, long? targetAccountId, decimal amount,
        string? party, string? basis, string? comment, long userId, DateTime nowUtc, long? cashFlowItemId = null, long? employeeId = null, DateOnly? advanceDueDate = null)
    {
        if (advanceDueDate is { } due && (kind != MoneyOperationKind.Expense || employeeId is null || due < date))
        {
            throw new BusinessRuleException("money.advance_due",
                kind != MoneyOperationKind.Expense || employeeId is null
                    ? "Срок аванса указывается только при выдаче под отчёт."
                    : "Срок аванса не может быть раньше даты выдачи.");
        }

        if (amount <= 0 || Money.Round(amount) != amount)
        {
            throw new BusinessRuleException("money.amount", "Сумма — больше нуля, до копеек.");
        }

        if (kind == MoneyOperationKind.Transfer)
        {
            if (targetAccountId is null)
            {
                throw new BusinessRuleException("money.transfer.target", "Укажите, куда переместить деньги.");
            }

            if (targetAccountId == accountId)
            {
                throw new BusinessRuleException("money.transfer.same", "Счёт списания и зачисления совпадают.");
            }

            if (cashFlowItemId is not null || employeeId is not null)
            {
                throw new BusinessRuleException("money.transfer.item", "Перемещение между своими счетами не относится к статьям движения денег.");
            }
        }
        else if (targetAccountId is not null)
        {
            throw new BusinessRuleException("money.target_not_allowed", "Счёт зачисления указывается только при перемещении.");
        }

        return new MoneyOperation
        {
            OrganizationId = organizationId,
            Number = number,
            OperationDate = date,
            Kind = kind,
            AccountId = accountId,
            TargetAccountId = targetAccountId,
            Amount = amount,
            Party = kind == MoneyOperationKind.Transfer ? null : DomainText.Optional(party, PartyMaxLength, "Принято от / выдать"),
            Basis = DomainText.Require(basis, BasisMaxLength, "Основание"),
            Comment = DomainText.Optional(comment, CommentMaxLength, "Комментарий"),
            CashFlowItemId = cashFlowItemId,
            EmployeeId = employeeId,
            AdvanceDueDate = advanceDueDate,
            Status = MoneyOperationStatus.Posted,
            CreatedByUserId = userId,
            CreatedAtUtc = nowUtc,
        };
    }

    public void AssignTransferCashOrders(string? fromCash, string? toCash)
    {
        if (Kind != MoneyOperationKind.Transfer)
        {
            throw new InvalidOperationException("Кассовые ордера перемещения — только у перемещения.");
        }

        CashOrderNumber = fromCash;
        TargetCashOrderNumber = toCash;
    }

    public void Cancel(long userId, string? reason, DateTime nowUtc)
    {
        if (Status == MoneyOperationStatus.Cancelled)
        {
            throw new BusinessRuleException("money.cancelled", "Операция уже отменена.");
        }

        CancelReason = DomainText.Require(reason, ReasonMaxLength, "Причина отмены");
        Status = MoneyOperationStatus.Cancelled;
        CancelledByUserId = userId;
        CancelledAtUtc = nowUtc;
    }

    /// <summary>Префикс номера: кассовые ордера нумеруются отдельно от банковских операций.</summary>
    public static string PrefixFor(MoneyOperationKind kind, bool cash) => kind switch
    {
        MoneyOperationKind.Income => cash ? IncomeCashPrefix : IncomeBankPrefix,
        MoneyOperationKind.Expense => cash ? ExpenseCashPrefix : ExpenseBankPrefix,
        _ => TransferPrefix,
    };

    public static string KindName(MoneyOperationKind kind, bool cash) => kind switch
    {
        MoneyOperationKind.Income => cash ? "Приходный кассовый ордер" : "Поступление на счёт",
        MoneyOperationKind.Expense => cash ? "Расходный кассовый ордер" : "Списание со счёта",
        _ => "Перемещение денег",
    };
}
