using System.Text.RegularExpressions;
using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Purchasing;
using KnitErp.Application.Sales;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Common;
using KnitErp.Domain.Finance;
using KnitErp.Domain.Organizations;
using KnitErp.Domain.Purchasing;
using KnitErp.Domain.Sales;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Finance;

public enum BankLineStatus : byte
{
    /// <summary>Будет создана оплата.</summary>
    Ready = 1,

    /// <summary>Уже загружена раньше — пропускается.</summary>
    Duplicate = 2,

    /// <summary>Контрагента с таким ИНН нет — создайте его и загрузите выписку снова.</summary>
    UnknownCounterparty = 3,

    /// <summary>Контрагент не отмечен покупателем (для поступления) или поставщиком (для списания).</summary>
    WrongRole = 4,

    /// <summary>Перевод между своими счетами, налоги, банковские комиссии — не оплата контрагента.</summary>
    Skipped = 5,

    /// <summary>Нет права на продажи (поступления) или закупки (списания).</summary>
    NoRight = 6,
}

/// <summary>Строка предпросмотра выписки: что программа поняла и что сделает.</summary>
public sealed record BankLineDto(
    int Index, DateOnly Date, string Number, BankLineDirection Direction, decimal Amount, string CounterpartyName, string? Inn, string Purpose,
    BankLineStatus Status, long? CounterpartyId, long? OrderId, string? OrderNumber, string? Note);

public sealed record BankStatementPreviewDto(string AccountLabel, DateOnly? From, DateOnly? To, IReadOnlyList<BankLineDto> Lines)
{
    public int ReadyCount => Lines.Count(l => l.Status == BankLineStatus.Ready);
}

public sealed record BankImportResultDto(int Created, int Skipped, decimal Incoming, decimal Outgoing);

/// <summary>
/// D119 (А1): загрузка банковской выписки 1С «Клиент-банк». Поступления на наш расчётный счёт → оплаты покупателей, списания →
/// оплаты поставщикам. Контрагент — по ИНН; заказ — по номеру заказа или счёта в назначении платежа (один найденный — на него,
/// иначе разноска по самым старым долгам). Каждая строка — своей транзакцией с записью в журнал строк выписки: повторная загрузка
/// того же файла или пересекающегося периода дублей не создаёт. Права — как у ручного ввода оплат (продажи / закупки).
/// </summary>
public sealed class BankStatementService(
    IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, SalesService sales, PurchaseService purchases)
{
    private static readonly Regex NumberInPurpose = new(@"(?:№|N|No|номер|сч[её]т[ау]?|заказ[ау]?)\s*[:№]?\s*(?:[А-ЯA-Z]{2}-)?0*(\d{1,9})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public async Task<BankStatementPreviewDto> PreviewAsync(byte[] content, CancellationToken ct = default)
    {
        var (ctx, statement, account) = await LoadAsync(content, ct);
        return new BankStatementPreviewDto(account.Label, statement.From, statement.To, await AnalyzeAsync(ctx, statement, account, ct));
    }

    /// <summary>Создаёт оплаты по готовым строкам; selected — номера строк предпросмотра (null — все готовые).</summary>
    public async Task<BankImportResultDto> ImportAsync(byte[] content, IReadOnlyCollection<int>? selected, CancellationToken ct = default)
    {
        var (ctx, statement, account) = await LoadAsync(content, ct);
        var lines = await AnalyzeAsync(ctx, statement, account, ct);
        int created = 0, skipped = 0;
        decimal incoming = 0, outgoing = 0;
        foreach (var line in lines)
        {
            if (line.Status != BankLineStatus.Ready || (selected is not null && !selected.Contains(line.Index)))
            {
                skipped++;
                continue;
            }

            var doc = statement.Documents[line.Index];
            var fingerprint = ClientBankStatement.Fingerprint(doc);
            await using var tx = await db.BeginTransactionAsync(ct);
            try
            {
                // Повторная проверка под транзакцией: две вкладки с одной выпиской не создадут две оплаты.
                if (await db.BankStatementLines.AnyAsync(l => l.OrganizationId == ctx.OrganizationId && l.Fingerprint == fingerprint, ct))
                {
                    skipped++;
                    continue;
                }

                var comment = Trim($"Выписка банка: {line.Purpose}", CustomerPayment.CommentMaxLength);
                long paymentId;
                if (line.Direction == BankLineDirection.Incoming)
                {
                    paymentId = await sales.CreatePaymentAsync(doc.Date, line.CounterpartyId!.Value, line.OrderId, doc.Amount, comment,
                        Trim(doc.Number, CustomerPayment.DocumentNumberMaxLength), account.Id, ct);
                    if (line.OrderId is null)
                    {
                        await sales.AutoAllocatePaymentAsync(paymentId, ct);
                    }

                    incoming += doc.Amount;
                }
                else
                {
                    paymentId = await purchases.CreatePaymentAsync(doc.Date, line.CounterpartyId!.Value, line.OrderId, doc.Amount,
                        Trim(comment, SupplierPayment.CommentMaxLength), account.Id, ct);
                    if (line.OrderId is null)
                    {
                        await purchases.AutoAllocatePaymentAsync(paymentId, ct);
                    }

                    outgoing += doc.Amount;
                }

                db.BankStatementLines.Add(BankStatementLine.Create(ctx.OrganizationId, fingerprint, account.Id, line.Direction, doc.Date, doc.Number,
                    doc.Amount, line.CounterpartyId!.Value, doc.Purpose, paymentId, ctx.UserId, clock.UtcNow));
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                created++;
            }
            catch
            {
                db.DiscardChanges();
                throw;
            }
        }

        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, AuditActions.BankStatementImported, nameof(BankStatementLine),
            account.Id.ToString(), null, $"создано оплат: {created}, пропущено: {skipped}",
            $"{account.Label}: поступления {incoming:0.00}, списания {outgoing:0.00}", currentUser.CorrelationId));
        await db.SaveChangesAsync(ct);
        return new BankImportResultDto(created, skipped, incoming, outgoing);
    }

    private sealed record OwnAccount(long Id, string Account, string Label);

    private async Task<(AccessContext, BankStatement, OwnAccount)> LoadAsync(byte[] content, CancellationToken ct)
    {
        var ctx = await guard.CurrentAsync(ct);
        if (!ctx.Permissions.Has(Permissions.SalesEdit) && !ctx.Permissions.Has(Permissions.PurchaseEdit))
        {
            throw new AccessDeniedException(Permissions.SalesEdit);
        }

        await guard.DemandAsync(Permissions.PriceView, ct);
        var statement = ClientBankStatement.Parse(content);
        var accounts = await (from a in db.LegalEntityAccounts.AsNoTracking()
                              join e in db.LegalEntities.AsNoTracking() on a.LegalEntityId equals e.Id
                              where a.OrganizationId == ctx.OrganizationId && a.Kind == MoneyAccountKind.Bank && statement.Accounts.Contains(a.Account)
                              select new OwnAccount(a.Id, a.Account, e.ShortName + " · " + a.BankName + " · " + a.Account)).ToListAsync(ct);
        if (accounts.Count == 0)
        {
            throw new BusinessRuleException("bank.statement.account",
                "Расчётного счёта из выписки нет в «Счетах и кассах» организации. Добавьте его в реквизиты юрлица и загрузите выписку снова.");
        }

        if (accounts.Count > 1)
        {
            throw new BusinessRuleException("bank.statement.accounts", "В файле выписка по нескольким счетам — выгрузите её из банка по одному счёту.");
        }

        return (ctx, statement, accounts[0]);
    }

    private async Task<IReadOnlyList<BankLineDto>> AnalyzeAsync(AccessContext ctx, BankStatement statement, OwnAccount account, CancellationToken ct)
    {
        var org = ctx.OrganizationId;
        var ownAccounts = await db.LegalEntityAccounts.AsNoTracking().Where(a => a.OrganizationId == org).Select(a => a.Account).ToListAsync(ct);
        var fingerprints = statement.Documents.Select(ClientBankStatement.Fingerprint).ToList();
        var earliest = statement.Documents.Count == 0 ? DateOnly.MaxValue : statement.Documents.Min(d => d.Date);
        var loaded = (await db.BankStatementLines.AsNoTracking().Where(l => l.OrganizationId == org && l.DocumentDate >= earliest)
            .Select(l => l.Fingerprint).ToListAsync(ct)).Select(Convert.ToBase64String).ToHashSet();
        var inns = statement.Documents.SelectMany(d => new[] { d.PayerInn, d.PayeeInn }).Where(i => i is not null).Distinct().ToList();
        var counterparties = (await db.Counterparties.AsNoTracking().Where(c => c.OrganizationId == org && !c.IsArchived && inns.Contains(c.Inn))
                .Select(c => new { c.Id, c.Inn, c.Name, c.IsCustomer, c.IsSupplier }).ToListAsync(ct))
            .GroupBy(c => c.Inn!).ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.IsCustomer || c.IsSupplier).First());

        var result = new List<BankLineDto>();
        foreach (var d in statement.Documents)
        {
            var incoming = d.PayeeAccount == account.Account;
            var outgoing = d.PayerAccount == account.Account;
            var direction = incoming ? BankLineDirection.Incoming : BankLineDirection.Outgoing;
            var (name, inn) = incoming ? (d.PayerName, d.PayerInn) : (d.PayeeName, d.PayeeInn);
            BankLineDto Line(BankLineStatus status, long? cp = null, long? order = null, string? orderNumber = null, string? note = null) =>
                new(d.Index, d.Date, d.Number, direction, d.Amount, name, inn, d.Purpose, status, cp, order, orderNumber, note);

            if (!incoming && !outgoing)
            {
                result.Add(Line(BankLineStatus.Skipped, note: "документ не по этому счёту"));
                continue;
            }

            if (ownAccounts.Contains(incoming ? d.PayerAccount : d.PayeeAccount))
            {
                result.Add(Line(BankLineStatus.Skipped, note: "перевод между своими счетами — оформите в «Деньгах»"));
                continue;
            }

            if (loaded.Contains(Convert.ToBase64String(fingerprints[d.Index])))
            {
                result.Add(Line(BankLineStatus.Duplicate, note: "уже загружено"));
                continue;
            }

            if (!ctx.Permissions.Has(incoming ? Permissions.SalesEdit : Permissions.PurchaseEdit))
            {
                result.Add(Line(BankLineStatus.NoRight, note: incoming ? "нет права на оплаты покупателей" : "нет права на оплаты поставщикам"));
                continue;
            }

            if (inn is null || !counterparties.TryGetValue(inn, out var cp))
            {
                result.Add(Line(BankLineStatus.UnknownCounterparty, note: inn is null
                    ? "в выписке нет ИНН — налоги, комиссии банка и прочее оформите в «Деньгах»"
                    : "контрагента с этим ИНН нет — создайте его (реквизиты заполнятся по ИНН) и загрузите выписку снова"));
                continue;
            }

            if (incoming ? !cp.IsCustomer : !cp.IsSupplier)
            {
                result.Add(Line(BankLineStatus.WrongRole, cp.Id, note: incoming ? $"«{cp.Name}» не отмечен как покупатель" : $"«{cp.Name}» не отмечен как поставщик"));
                continue;
            }

            var (orderId, orderNumber) = incoming ? await SalesOrderAsync(org, cp.Id, d.Purpose, ct) : await PurchaseOrderAsync(org, cp.Id, d.Purpose, ct);
            result.Add(Line(BankLineStatus.Ready, cp.Id, orderId, orderNumber,
                orderNumber is null ? "разнесётся по самым старым долгам" : $"на заказ {orderNumber}"));
        }

        return result;
    }

    /// <summary>Подтверждённый заказ покупателя, чей номер (или номер его счёта) назван в назначении; ровно один — иначе null.</summary>
    private async Task<(long?, string?)> SalesOrderAsync(long org, long customerId, string purpose, CancellationToken ct)
    {
        var numbers = Numbers(purpose);
        if (numbers.Count == 0)
        {
            return (null, null);
        }

        var orders = await db.SalesOrders.AsNoTracking()
            .Where(o => o.OrganizationId == org && o.CustomerId == customerId && o.Status == SalesOrderStatus.Confirmed)
            .Select(o => new { o.Id, o.Number }).ToListAsync(ct);
        var ids = orders.Select(o => o.Id).ToList();
        var invoices = await db.CustomerInvoices.AsNoTracking().Where(i => i.OrganizationId == org && ids.Contains(i.SalesOrderId))
            .Select(i => new { i.SalesOrderId, i.Number }).ToListAsync(ct);
        var hits = orders.Where(o => numbers.Contains(Tail(o.Number))).Select(o => o.Id)
            .Concat(invoices.Where(i => numbers.Contains(Tail(i.Number))).Select(i => i.SalesOrderId)).Distinct().ToList();
        return hits.Count == 1 ? (hits[0], orders.Single(o => o.Id == hits[0]).Number) : (null, null);
    }

    private async Task<(long?, string?)> PurchaseOrderAsync(long org, long supplierId, string purpose, CancellationToken ct)
    {
        var numbers = Numbers(purpose);
        if (numbers.Count == 0)
        {
            return (null, null);
        }

        var orders = await db.PurchaseOrders.AsNoTracking()
            .Where(o => o.OrganizationId == org && o.SupplierId == supplierId && o.Status == PurchaseOrderStatus.Confirmed)
            .Select(o => new { o.Id, o.Number, o.SupplierInvoice }).ToListAsync(ct);
        var hits = orders.Where(o => numbers.Contains(Tail(o.Number)) || (o.SupplierInvoice is { } inv && numbers.Contains(Tail(inv)))).ToList();
        return hits.Count == 1 ? (hits[0].Id, hits[0].Number) : (null, null);
    }

    private static HashSet<long> Numbers(string purpose) =>
        NumberInPurpose.Matches(purpose).Select(m => long.TryParse(m.Groups[1].Value, out var n) ? n : 0).Where(n => n > 0).ToHashSet();

    /// <summary>Числовой хвост номера документа: «ЗК-00012» → 12, «15/2» → 15.</summary>
    private static long Tail(string number)
    {
        var m = Regex.Match(number, @"(\d+)");
        return m.Success && long.TryParse(m.Groups[1].Value, out var n) ? n : -1;
    }

    private static string Trim(string value, int max) => value.Length > max ? value[..max] : value;
}
