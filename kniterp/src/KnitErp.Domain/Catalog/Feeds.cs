using System.Security.Cryptography;
using System.Text;
using KnitErp.Domain.Common;
using KnitErp.Domain.Integrations;

namespace KnitErp.Domain.Catalog;

/// <summary>
/// Фид каталога в формате YML (D314, сессия 4) для сайта, Яндекс Маркета и других площадок, которые читают YML по ссылке.
/// Ссылка содержит секретный ключ (32 случайных байта): он показывается один раз, в базе — только SHA-256 и первые символы.
/// Фид открывает цены выбранного вида и остатки — поэтому создаёт его Владелец или Администратор с правом видеть цены;
/// выключенный фид отвечает 404. Отбор: вид цены (обязателен), склад (пусто — все), группа с подгруппами, «только в наличии».
/// </summary>
public sealed class CatalogFeed
{
    public const int NameMaxLength = 100;
    public const int MaxPerOrganization = 10;
    public const string KeyPrefix = "kf_";
    public const int PrefixLength = 9;

    private CatalogFeed()
    {
    }

    public long Id { get; private set; }
    public long OrganizationId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Prefix { get; private set; } = string.Empty;
    public byte[] KeyHash { get; private set; } = [];
    public long PriceTypeId { get; private set; }
    public long? WarehouseId { get; private set; }
    public long? GroupId { get; private set; }
    public bool OnlyAvailable { get; private set; }
    public bool IsActive { get; private set; } = true;
    public long CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? LastRequestedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static (CatalogFeed Feed, string Key) Create(long organizationId, string? name, long priceTypeId, long? warehouseId, long? groupId,
        bool onlyAvailable, long userId, DateTime nowUtc)
    {
        var key = KeyPrefix + ApiToken.Base64Url(RandomNumberGenerator.GetBytes(32));
        var feed = new CatalogFeed
        {
            OrganizationId = organizationId,
            Name = DomainText.Require(name, NameMaxLength, "Название фида"),
            Prefix = key[..PrefixLength],
            KeyHash = Hash(key),
            PriceTypeId = priceTypeId,
            WarehouseId = warehouseId,
            GroupId = groupId,
            OnlyAvailable = onlyAvailable,
            CreatedByUserId = userId,
            CreatedAtUtc = nowUtc,
        };
        return (feed, key);
    }

    /// <summary>Ключ из адреса: только печатные символы base64url с префиксом, иначе запрос не доходит до базы.</summary>
    public static bool LooksLikeKey(string? key) =>
        key is { Length: > PrefixLength and < 80 } k && k.StartsWith(KeyPrefix, StringComparison.Ordinal)
                                                     && k.Skip(KeyPrefix.Length).All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    public void SetActive(bool active) => IsActive = active;

    /// <summary>Отметка запроса — не чаще раза в минуту, чтобы частый опрос площадкой не писал в базу на каждый запрос.</summary>
    public bool Touch(DateTime nowUtc)
    {
        if (LastRequestedAtUtc is { } last && nowUtc - last < TimeSpan.FromMinutes(1))
        {
            return false;
        }

        LastRequestedAtUtc = nowUtc;
        return true;
    }
}
