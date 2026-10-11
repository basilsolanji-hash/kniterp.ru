using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnitErp.Application.Common;
using KnitErp.Domain.Integrations;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Integrations;

/// <summary>Отправка одного запроса подписчику: код ответа или текст ошибки (сеть, тайм-аут, запрещённый адрес).</summary>
public interface IWebhookSender
{
    Task<(int? StatusCode, string? Error)> SendAsync(string url, IReadOnlyDictionary<string, string> headers, string body, CancellationToken ct);
}

/// <summary>
/// Рассылка событий (D88). Источник — журнал аудита: запись о действии сохраняется в той же транзакции, что и само действие,
/// поэтому событие не теряется и не появляется для отменённого действия (outbox). Шаг 1 — поставить в доставку события после
/// позиции подписки; идентификатор события детерминирован (подписка + запись журнала), повторная постановка дубля не создаёт.
/// Шаг 2 — отправить подошедшие доставки: подпись HMAC-SHA256, при ошибке — повтор с паузой (WebhookDelivery.Backoff).
/// Порядок: Sequence — номер записи журнала, получатель упорядочивает по нему и отбрасывает дубли по X-KnitErp-Delivery.
/// </summary>
public sealed class WebhookDispatcher(IKnitErpDbContext db, IWebhookSender sender, IClock clock)
{
    public const int BatchSize = 200;
    public const int DeliverBatch = 50;

    /// <summary>Поставить новые события в доставку. Возвращает число поставленных.</summary>
    public async Task<int> EnqueueAsync(CancellationToken ct = default)
    {
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.LockAsync("kniterp.webhooks.enqueue", ct);
        var subscriptions = await db.WebhookSubscriptions.Where(w => w.IsActive).ToListAsync(ct);
        var actions = WebhookEvents.ByAuditAction.Keys.ToList();
        var now = clock.UtcNow;
        var added = 0;
        foreach (var w in subscriptions)
        {
            var entries = await db.AuditEntries.AsNoTracking()
                .Where(a => a.Id > w.LastAuditId && a.OrganizationId == w.OrganizationId)
                .OrderBy(a => a.Id).Take(BatchSize)
                .Select(a => new { a.Id, a.Action, a.EntityType, a.EntityId, a.Reason, a.OccurredAtUtc })
                .ToListAsync(ct);
            if (entries.Count == 0)
            {
                continue;
            }

            var wanted = w.EventList.ToHashSet(StringComparer.Ordinal);
            foreach (var a in entries.Where(e => actions.Contains(e.Action)))
            {
                var type = WebhookEvents.ByAuditAction[a.Action].Event;
                if (!wanted.Contains(type))
                {
                    continue;
                }

                var eventId = EventId(w.Id, a.Id);
                db.WebhookDeliveries.Add(WebhookDelivery.Create(w.OrganizationId, w.Id, eventId, type, a.Id,
                    Payload(eventId, type, a.Id, a.OccurredAtUtc, a.EntityType, a.EntityId, a.Reason), now));
                added++;
            }

            w.Advance(entries[^1].Id);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return added;
    }

    /// <summary>Отправить подошедшие доставки (по порядку). Возвращает число успешных.</summary>
    public async Task<int> DeliverAsync(CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var due = await db.WebhookDeliveries
            .Where(d => d.Status == WebhookDeliveryStatus.Pending && d.NextAttemptAtUtc <= now)
            .OrderBy(d => d.Id).Take(DeliverBatch).ToListAsync(ct);
        if (due.Count == 0)
        {
            return 0;
        }

        var ids = due.Select(d => d.SubscriptionId).Distinct().ToList();
        var subscriptions = await db.WebhookSubscriptions.AsNoTracking().Where(w => ids.Contains(w.Id)).ToDictionaryAsync(w => w.Id, ct);
        var ok = 0;
        foreach (var d in due)
        {
            var w = subscriptions[d.SubscriptionId];
            if (!w.IsActive)
            {
                d.FailedAttempt(null, "Подписка выключена.", clock.UtcNow);
                continue;
            }

            var timestamp = new DateTimeOffset(clock.UtcNow).ToUnixTimeSeconds();
            var headers = new Dictionary<string, string>
            {
                ["X-KnitErp-Event"] = d.EventType,
                ["X-KnitErp-Delivery"] = d.EventId.ToString(),
                ["X-KnitErp-Timestamp"] = timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["X-KnitErp-Signature"] = "v1=" + WebhookDelivery.Sign(w.Secret, timestamp, d.Payload),
            };
            var (status, error) = await sender.SendAsync(w.Url, headers, d.Payload, ct);
            if (status is >= 200 and < 300)
            {
                d.Succeeded(status.Value, clock.UtcNow);
                ok++;
            }
            else
            {
                d.FailedAttempt(status, error ?? $"Ответ {status}", clock.UtcNow);
            }

            await db.SaveChangesAsync(ct);
        }

        await db.SaveChangesAsync(ct);
        return ok;
    }

    /// <summary>Тело события: id, тип, порядковый номер, время и ссылка на объект (сами данные получатель берёт через API).</summary>
    public static string Payload(Guid eventId, string type, long sequence, DateTime occurredAtUtc, string? entity, string? entityId, string? summary) =>
        JsonSerializer.Serialize(new
        {
            id = eventId,
            type,
            sequence,
            occurredAt = DateTime.SpecifyKind(occurredAtUtc, DateTimeKind.Utc),
            data = entity is null ? null : new { entity, id = entityId, summary },
        });

    /// <summary>Детерминированный идентификатор события: одна запись журнала даёт подписке ровно одно событие.</summary>
    public static Guid EventId(long subscriptionId, long auditId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"kniterp-webhook:{subscriptionId}:{auditId}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
