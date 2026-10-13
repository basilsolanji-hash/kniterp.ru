using KnitErp.Domain.Common;

namespace KnitErp.Domain.Purchasing;

/// <summary>
/// D133 (D132 п. 3): настройки закупок организации. Одна строка на организацию; пока её нет — значения по умолчанию.
/// Допуск поступления сверх заказа: по позиции заказа можно принять не больше «заказано × (1 + допуск/100)»; сверх — поступлением без заказа.
/// </summary>
public sealed class PurchaseSettings
{
    public const decimal MaxOverReceiptPercent = 10m;

    private PurchaseSettings()
    {
    }

    public long OrganizationId { get; private set; }

    /// <summary>Допуск поступления сверх заказа, % (0–10, по умолчанию 0 — сверх заказа нельзя).</summary>
    public decimal OverReceiptPercent { get; private set; }

    public long? UpdatedByUserId { get; private set; }
    public DateTime? UpdatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static PurchaseSettings Default(long organizationId) => new() { OrganizationId = organizationId };

    public void Update(decimal overReceiptPercent, long userId, DateTime nowUtc)
    {
        if (overReceiptPercent < 0 || overReceiptPercent > MaxOverReceiptPercent || decimal.Round(overReceiptPercent, 2) != overReceiptPercent)
        {
            throw new BusinessRuleException("purchase.settings.over_receipt", "Допуск поступления сверх заказа — от 0 до 10%, не больше двух знаков после запятой.");
        }

        OverReceiptPercent = overReceiptPercent;
        UpdatedByUserId = userId;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Сколько можно принять по позиции с учётом допуска (D129 + D133).</summary>
    public static decimal AllowedQuantity(decimal ordered, decimal overReceiptPercent) =>
        decimal.Round(ordered * (1 + overReceiptPercent / 100m), 6, MidpointRounding.ToZero);

    public string Describe() => $"допуск поступления сверх заказа {OverReceiptPercent:0.##}%";
}
