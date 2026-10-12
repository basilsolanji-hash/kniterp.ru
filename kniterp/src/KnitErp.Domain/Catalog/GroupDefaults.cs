using KnitErp.Domain.Common;

namespace KnitErp.Domain.Catalog;

/// <summary>
/// Значения по умолчанию группы номенклатуры (D317): тип, единица, ставка НДС, ТН ВЭД, страна происхождения, вид маркировки.
/// Пустое поле — берётся у родительской группы (ближайшей, где задано). Новая позиция в группе получает их сама; заданное явно не меняется.
/// </summary>
public sealed class ItemGroupDefaults
{
    private ItemGroupDefaults()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public long GroupId { get; private set; }
    public ItemType? Type { get; private set; }
    public long? UnitId { get; private set; }
    public long? VatRateId { get; private set; }
    public string? TnVedCode { get; private set; }
    public string? OriginCountryCode { get; private set; }
    public string? OriginCountryName { get; private set; }

    /// <summary>null — не задано; None — «без маркировки» явно.</summary>
    public TrackingType? Tracking { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public static ItemGroupDefaults Create(long organizationId, long groupId) => new() { OrganizationId = organizationId, GroupId = groupId };

    public void Set(GroupDefaultValues v)
    {
        var tnVed = string.IsNullOrWhiteSpace(v.TnVedCode) ? null : v.TnVedCode.Trim();
        if (tnVed is not null && (tnVed.Length is not (4 or 6 or 8 or 10) || !tnVed.All(char.IsAsciiDigit)))
        {
            throw new BusinessRuleException("catalog.group_defaults.tnved", "Код ТН ВЭД — 4, 6, 8 или 10 цифр.");
        }

        var country = string.IsNullOrWhiteSpace(v.OriginCountryCode) ? null : v.OriginCountryCode.Trim();
        if (country is not null && (country.Length != 3 || !country.All(char.IsAsciiDigit)))
        {
            throw new BusinessRuleException("catalog.group_defaults.country", "Код страны — 3 цифры по ОКСМ, например 643 — Россия.");
        }

        if (v.Type is { } t && !ItemTypes.All.Contains(t))
        {
            throw new BusinessRuleException("catalog.group_defaults.type", "Неизвестный тип позиции.");
        }

        if (v.Tracking is { } tr && !Marking.All.Contains(tr))
        {
            throw new BusinessRuleException("catalog.group_defaults.tracking", "Неизвестный вид маркировки.");
        }

        Type = v.Type;
        UnitId = v.UnitId;
        VatRateId = v.VatRateId;
        TnVedCode = tnVed;
        OriginCountryCode = country;
        OriginCountryName = country is null ? null : string.IsNullOrWhiteSpace(v.OriginCountryName) ? null : v.OriginCountryName.Trim() is var n && n.Length > 100 ? n[..100] : v.OriginCountryName.Trim();
        Tracking = v.Tracking;
    }

    public GroupDefaultValues Values => new(Type, UnitId, VatRateId, TnVedCode, OriginCountryCode, OriginCountryName, Tracking);
}

/// <summary>Набор значений по умолчанию (свой у группы или итоговый с учётом родителей).</summary>
public sealed record GroupDefaultValues(
    ItemType? Type, long? UnitId, long? VatRateId, string? TnVedCode, string? OriginCountryCode, string? OriginCountryName, TrackingType? Tracking)
{
    public static readonly GroupDefaultValues Empty = new(null, null, null, null, null, null, null);

    public bool IsEmpty => this == Empty;

    /// <summary>Итог по цепочке от группы к корню: у каждого поля — первое заданное значение.</summary>
    public static GroupDefaultValues Resolve(IEnumerable<GroupDefaultValues> nearestFirst)
    {
        var r = Empty;
        foreach (var v in nearestFirst)
        {
            r = new GroupDefaultValues(r.Type ?? v.Type, r.UnitId ?? v.UnitId, r.VatRateId ?? v.VatRateId, r.TnVedCode ?? v.TnVedCode,
                r.OriginCountryCode ?? v.OriginCountryCode, r.OriginCountryCode is null ? v.OriginCountryName : r.OriginCountryName, r.Tracking ?? v.Tracking);
        }

        return r;
    }
}
