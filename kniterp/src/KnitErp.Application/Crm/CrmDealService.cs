using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Application.Sales;
using KnitErp.Application.Structure;
using KnitErp.Domain.Access;
using KnitErp.Domain.Audit;
using KnitErp.Domain.Catalog;
using KnitErp.Domain.Common;
using KnitErp.Domain.Crm;
using KnitErp.Domain.Sales;
using KnitErp.Domain.Tagging;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Crm;

/// <summary>
/// Отбор сделок. PipelineId null — первая действующая воронка (канбан) или все (таблица); Mine — только мои (ответственный
/// или наблюдатель); CloseFrom/CloseTo — плановая дата закрытия.
/// </summary>
public sealed record CrmDealFilter(
    long? PipelineId = null, long? ResponsibleUserId = null, long? SourceId = null, long? TagId = null, DateOnly? CloseFrom = null,
    DateOnly? CloseTo = null, bool Mine = false, CrmDealOutcome? Outcome = null, string? Search = null, int Page = 0, long? CounterpartyId = null);

/// <summary>Карточка сделки на канбане. Amount — null без права «Цены и суммы».</summary>
public sealed record CrmDealCardDto(
    long Id, string Number, string Title, string Counterparty, decimal? Amount, string CurrencyCode, int Probability, DateOnly? ExpectedCloseDate,
    string Responsible, long StageId, CrmDealOutcome Outcome, byte[] RowVersion);

/// <summary>Колонка канбана: этап, первые карточки, всего сделок и сумма по колонке (null без права цен).</summary>
public sealed record CrmBoardColumnDto(CrmStageDto Stage, IReadOnlyList<CrmDealCardDto> Deals, int Count, decimal? Total, decimal? Weighted);

public sealed record CrmBoardDto(long PipelineId, string Pipeline, IReadOnlyList<CrmBoardColumnDto> Columns, bool CanSeeAmounts, bool CanEdit);

public sealed record CrmDealRowDto(
    long Id, string Number, string Title, string Counterparty, string Pipeline, string Stage, string StageColor, CrmDealOutcome Outcome,
    decimal? Amount, string CurrencyCode, int Probability, DateOnly? ExpectedCloseDate, string Responsible, DateTime CreatedAtUtc)
{
    public string OutcomeName => CrmDeal.OutcomeName(Outcome);
}

public sealed record CrmDealLineDto(long ItemId, string Code, string Name, string UnitSymbol, decimal Quantity, decimal? Price, decimal? Amount);

public sealed record CrmStageHistoryDto(string? From, string To, string By, DateTime AtUtc, decimal DaysInStage);

/// <summary>Заказ покупателя базы по сделке — только чтение (D607): этап, сумма, оплата и отгрузка из модуля продаж.</summary>
public sealed record CrmLinkedOrderDto(
    long Id, string Number, DateOnly Date, SalesOrderStatus Status, SalesStageRefDto? Stage, decimal? Total, decimal? Paid, ShipmentState Shipped, decimal? Debt)
{
    public string StatusName => SalesOrder.StatusName(Status);
}

public sealed record CrmDealDto(
    long Id, string Number, CrmDealValues Values, long PipelineId, string Pipeline, long StageId, IReadOnlyList<CrmStageDto> Stages, int Probability,
    CrmDealOutcome Outcome, string? LossReason, string? LossComment, string Counterparty, string? Contact, string Responsible, string? Source,
    string? Channel, string? Project, long? LeadId, string? LeadNumber, IReadOnlyList<CrmDealLineDto> Lines, IReadOnlyList<LookupDto> Participants,
    IReadOnlyList<CrmStageHistoryDto> History, IReadOnlyList<CrmLinkedOrderDto> Orders, int HiddenOrders, string CreatedBy, DateTime CreatedAtUtc,
    DateTime? ClosedAtUtc, bool CanEdit, bool CanAll, bool CanSeeAmounts, bool CanCreateOrder, byte[] RowVersion)
{
    public string OutcomeName => CrmDeal.OutcomeName(Outcome);
    public decimal? Amount => CanSeeAmounts ? Values.Amount : null;
}

/// <summary>Предложение следующего шага после смены этапа (D622): задача с темой и сроком — пользователь подтверждает.</summary>
public sealed record CrmNextStepDto(long DealId, string Subject, int Days, DateTime DueAtUtc);

public sealed record CrmItemOptionDto(long Id, string Code, string Name, string UnitSymbol, decimal? Price);

/// <summary>
/// Сделки (D606, D607): канбан по этапам и таблица, карточка, смена этапа с историей (только вставка), позиции каталога,
/// наблюдатели, переназначение (crm.all) и «Создать заказ» — заказ покупателя базы через <see cref="SalesService"/>.
/// Подтверждённый заказ по сделке переводит её в «Выиграна» (<see cref="OnOrderConfirmedAsync"/>, вызывает модуль продаж).
/// Без crm.all видны только свои сделки и те, где пользователь наблюдатель; чужая — «не найдено».
/// </summary>
public sealed class CrmDealService(IKnitErpDbContext db, IAccessGuard guard, ICurrentUser currentUser, IClock clock, SalesService sales)
{
    public const int BoardCardsPerColumn = 50;

    public async Task<CrmOptionsDto> OptionsAsync(CancellationToken ct = default) =>
        await CrmCommon.OptionsAsync(db, await guard.DemandAsync(Permissions.CrmView, ct), ct);

    /// <summary>
    /// Канбан воронки: колонки этапов, в каждой — первые <see cref="BoardCardsPerColumn"/> сделок (новые сверху), всего и сумма.
    /// Запросы — проекции на сервере: этапы, итоги по колонкам, карточки (ТЗ CRM §8, не больше 5 запросов к CRM).
    /// </summary>
    public async Task<CrmBoardDto> BoardAsync(CrmDealFilter filter, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmView, ct);
        await CrmSettingsService.EnsureDefaultsAsync(db, ctx.OrganizationId, ct);
        var stages = await (from s in db.CrmStages.AsNoTracking()
                            join p in db.CrmPipelines.AsNoTracking() on s.PipelineId equals p.Id
                            where s.OrganizationId == ctx.OrganizationId && (filter.PipelineId == null ? !p.IsArchived : p.Id == filter.PipelineId)
                            select new { Stage = s, Pipeline = p.Name, p.SortOrder })
            .ToListAsync(ct);
        if (stages.Count == 0)
        {
            throw new NotFoundException("Воронка");
        }

        var first = stages.OrderBy(s => s.SortOrder).ThenBy(s => s.Stage.PipelineId).First();
        var pipelineId = filter.PipelineId ?? first.Stage.PipelineId;
        var pipeline = stages.First(s => s.Stage.PipelineId == pipelineId).Pipeline;
        var q = Filtered(ctx, filter with { PipelineId = pipelineId });
        var amounts = ctx.Permissions.Has(Permissions.PriceView);
        var totals = await q.GroupBy(d => d.StageId)
            .Select(g => new { StageId = g.Key, Count = g.Count(), Sum = g.Sum(d => d.Amount), Weighted = g.Sum(d => d.Amount * d.Probability / 100m) })
            .ToDictionaryAsync(x => x.StageId, ct);
        // Первые карточки каждой колонки одним запросом: сделка попадает, если новее неё в той же колонке меньше лимита.
        var cards = await (from d in q.Where(d => q.Count(x => x.StageId == d.StageId && x.Id > d.Id) < BoardCardsPerColumn)
                           join c in db.Counterparties.AsNoTracking() on d.CounterpartyId equals c.Id
                           join u in db.Users.AsNoTracking() on d.ResponsibleUserId equals u.Id
                           select new CrmDealCardDto(d.Id, d.Number, d.Title, c.Name, d.Amount, d.CurrencyCode, d.Probability, d.ExpectedCloseDate,
                               u.DisplayName, d.StageId, d.Outcome, d.RowVersion))
            .ToListAsync(ct);
        var columns = CrmSettingsService.Ordered(stages.Where(s => s.Stage.PipelineId == pipelineId).Select(s => s.Stage))
            .Where(s => !s.IsArchived || totals.ContainsKey(s.Id))
            .Select(s =>
            {
                var t = totals.GetValueOrDefault(s.Id);
                return new CrmBoardColumnDto(
                    new CrmStageDto(s.Id, s.PipelineId, s.Name, s.Color, s.Probability, s.Kind, s.IsArchived, t?.Count ?? 0, s.RowVersion),
                    cards.Where(c => c.StageId == s.Id).OrderByDescending(c => c.Id).Select(c => amounts ? c : c with { Amount = null }).ToList(),
                    t?.Count ?? 0, amounts ? t?.Sum ?? 0 : null, amounts ? Money.Round(t?.Weighted ?? 0) : null);
            })
            .ToList();
        return new CrmBoardDto(pipelineId, pipeline, columns, amounts, ctx.Permissions.Has(Permissions.CrmEdit));
    }

    public async Task<CrmPage<CrmDealRowDto>> ListAsync(CrmDealFilter filter, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmView, ct);
        var q = Filtered(ctx, filter);
        var total = await q.CountAsync(ct);
        var (page, skip) = CrmCommon.Paging(filter.Page);
        var amounts = ctx.Permissions.Has(Permissions.PriceView);
        var rows = await (from d in q
                          join c in db.Counterparties.AsNoTracking() on d.CounterpartyId equals c.Id
                          join u in db.Users.AsNoTracking() on d.ResponsibleUserId equals u.Id
                          join s in db.CrmStages.AsNoTracking() on d.StageId equals s.Id
                          join p in db.CrmPipelines.AsNoTracking() on d.PipelineId equals p.Id
                          orderby d.Id descending
                          select new CrmDealRowDto(d.Id, d.Number, d.Title, c.Name, p.Name, s.Name, s.Color, d.Outcome, amounts ? d.Amount : null,
                              d.CurrencyCode, d.Probability, d.ExpectedCloseDate, u.DisplayName, d.CreatedAtUtc))
            .Skip(skip).Take(CrmCommon.PageSize).ToListAsync(ct);
        return new CrmPage<CrmDealRowDto>(rows, total, page, CrmCommon.PageSize);
    }

    public async Task<CrmDealDto> GetAsync(long id, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmView, ct);
        var d = await CrmAccess.Deals(db, ctx).AsNoTracking().Include(x => x.Lines).Include(x => x.Participants)
                    .SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Сделка");
        var amounts = ctx.Permissions.Has(Permissions.PriceView);
        var stages = await db.CrmStages.AsNoTracking().Where(s => s.PipelineId == d.PipelineId).ToListAsync(ct);
        var pipeline = await db.CrmPipelines.AsNoTracking().Where(p => p.Id == d.PipelineId).Select(p => p.Name).SingleAsync(ct);
        var userIds = d.Participants.Select(p => p.UserId).Append(d.ResponsibleUserId).Append(d.CreatedByUserId).ToList();
        var history = await db.CrmDealStageHistory.AsNoTracking().Where(h => h.OrganizationId == ctx.OrganizationId && h.DealId == id)
            .OrderByDescending(h => h.ChangedAtUtc).ThenByDescending(h => h.Id).Take(200).ToListAsync(ct);
        userIds.AddRange(history.Select(h => h.ChangedByUserId));
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        long?[] refIds = [d.SourceId, d.LossReasonId];
        var refs = await db.CrmReferences.AsNoTracking().Where(r => refIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        long?[] lookupIds = [d.ChannelId, d.ProjectId];
        var lookups = await db.Lookups.AsNoTracking().Where(l => lookupIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var counterparty = await db.Counterparties.AsNoTracking().Where(c => c.Id == d.CounterpartyId).Select(c => c.Name).SingleAsync(ct);
        var contact = d.ContactId is { } cid ? await db.CrmContacts.AsNoTracking().Where(c => c.Id == cid).Select(c => c.FullName).SingleOrDefaultAsync(ct) : null;
        var lead = d.LeadId is { } lid ? await db.CrmLeads.AsNoTracking().Where(l => l.Id == lid).Select(l => l.Number).SingleOrDefaultAsync(ct) : null;
        var itemIds = d.Lines.Select(l => l.ItemId).ToList();
        var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id))
            .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new { i.Id, i.Code, i.Name, u.Symbol }).ToDictionaryAsync(x => x.Id, ct);
        var stageNames = stages.ToDictionary(s => s.Id, s => s.Name);
        var (orders, hidden) = await LinkedOrdersAsync(ctx, id, ct);
        var values = new CrmDealValues(d.Title, d.CounterpartyId, d.ContactId, amounts ? d.Amount : 0, d.CurrencyCode, d.ExpectedCloseDate,
            d.ResponsibleUserId, d.ProjectId, d.ChannelId, d.SourceId);
        return new CrmDealDto(d.Id, d.Number, values, d.PipelineId, pipeline, d.StageId,
            CrmSettingsService.Ordered(stages.Where(s => !s.IsArchived || s.Id == d.StageId))
                .Select(s => new CrmStageDto(s.Id, s.PipelineId, s.Name, s.Color, s.Probability, s.Kind, s.IsArchived, 0, s.RowVersion)).ToList(),
            d.Probability, d.Outcome, Name(refs, d.LossReasonId), d.LossComment, counterparty, contact, users[d.ResponsibleUserId],
            Name(refs, d.SourceId), Name(lookups, d.ChannelId), Name(lookups, d.ProjectId), d.LeadId, lead,
            d.Lines.Select(l => new CrmDealLineDto(l.ItemId, items[l.ItemId].Code, items[l.ItemId].Name, items[l.ItemId].Symbol, l.Quantity,
                amounts ? l.Price : null, amounts ? l.Amount : null)).ToList(),
            d.Participants.Select(p => new LookupDto(p.UserId, users.GetValueOrDefault(p.UserId) ?? "—")).ToList(),
            history.Select(h => new CrmStageHistoryDto(h.FromStageId is { } f ? stageNames.GetValueOrDefault(f) : null, stageNames.GetValueOrDefault(h.ToStageId) ?? "—",
                users.GetValueOrDefault(h.ChangedByUserId) ?? "—", h.ChangedAtUtc, h.DaysInStage)).ToList(),
            orders, hidden, users[d.CreatedByUserId], d.CreatedAtUtc, d.ClosedAtUtc,
            ctx.Permissions.Has(Permissions.CrmEdit), CrmAccess.SeesAll(ctx), amounts,
            ctx.Permissions.Has(Permissions.CrmEdit) && ctx.Permissions.Has(Permissions.SalesEdit) && amounts && d.Outcome != CrmDealOutcome.Lost,
            d.RowVersion);
    }

    /// <summary>Новая сделка без лида — на первом рабочем этапе выбранной (или первой) воронки.</summary>
    public async Task<long> CreateAsync(CrmDealValues values, long? pipelineId = null, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmEdit, ct);
        await CrmSettingsService.EnsureDefaultsAsync(db, ctx.OrganizationId, ct);
        values = WithoutHiddenAmount(ctx, values, 0);
        await ValidateAsync(ctx, values, null, ct);
        var stage = await FirstStageAsync(ctx, pipelineId, ct);
        var currency = await CrmCommon.CurrencyAsync(db, ctx, ct);
        if (values.ExpectedCloseDate is null)
        {
            values = values with { ExpectedCloseDate = await CrmEvents.DefaultCloseDateAsync(db, ctx.OrganizationId, clock.UtcNow, ct) };
        }

        CrmDeal.Create(ctx.OrganizationId, "С-0", values, stage, currency, ctx.UserId, clock.UtcNow); // проверка полей до выдачи номера
        await using var tx = await db.BeginTransactionAsync(ct);
        var number = await DocumentNumbers.NextAsync(db, ctx.OrganizationId, CrmDeal.NumberPrefix, ct);
        var (deal, entry) = CrmDeal.Create(ctx.OrganizationId, number, values, stage, currency, ctx.UserId, clock.UtcNow);
        db.CrmDeals.Add(deal);
        await db.SaveChangesAsync(ct);
        entry.AttachTo(deal);
        db.CrmDealStageHistory.Add(entry);
        Audit(ctx, AuditActions.CrmDealCreated, deal.Id, null, deal.Describe(), stage.Name);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return deal.Id;
    }

    public async Task UpdateAsync(long id, CrmDealValues values, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, deal) = await LoadForEditAsync(id, rowVersion, ct);
        values = WithoutHiddenAmount(ctx, values, deal.Amount);
        await ValidateAsync(ctx, values, deal, ct);
        var before = deal.Describe();
        try
        {
            deal.Update(values);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }

        Audit(ctx, AuditActions.CrmDealChanged, id, before, deal.Describe(), null);
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Смена этапа (канбан, кнопки «←/→», лента этапов). Проигрыш — с причиной. Запись в историю этапов и журнал.</summary>
    public async Task<CrmNextStepDto?> MoveAsync(long id, long stageId, byte[] rowVersion, long? lossReasonId = null, string? lossComment = null,
        CancellationToken ct = default)
    {
        var (ctx, deal) = await LoadForEditAsync(id, rowVersion, ct);
        var stage = await db.CrmStages.AsNoTracking().SingleOrDefaultAsync(s => s.Id == stageId && s.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("Этап воронки");
        await CrmCommon.EnsureReferenceAsync(db, ctx, lossReasonId, null, CrmReferenceKind.LossReason, ct);
        var from = await db.CrmStages.AsNoTracking().Where(s => s.Id == deal.StageId).Select(s => s.Name).SingleAsync(ct);
        CrmDealStageHistory entry;
        try
        {
            entry = deal.MoveTo(stage, ctx.UserId, clock.UtcNow, lossReasonId, lossComment);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }

        db.CrmDealStageHistory.Add(entry);
        Audit(ctx, AuditActions.CrmDealStageChanged, id, from, stage.Name, deal.LossComment ?? deal.Number);
        await using var tx = await db.BeginTransactionAsync(ct);
        await CrmEvents.AfterStageChangedAsync(db, ctx, deal, stage, clock.UtcNow, currentUser.CorrelationId, ct);
        await db.SaveOrConflictAsync(ct);
        await tx.CommitAsync(ct);
        return await NextStepAsync(ctx, deal, stage, ct);
    }

    /// <summary>D622: правило следующего шага этапа, если оно есть, включено в настройках и у сделки нет открытой задачи с той же темой.</summary>
    private async Task<CrmNextStepDto?> NextStepAsync(AccessContext ctx, CrmDeal deal, CrmStage stage, CancellationToken ct)
    {
        if (stage.NextStepDays is not { } days || stage.NextStepSubject is not { } subject || !(await CrmEvents.SettingsAsync(db, ctx.OrganizationId, ct)).NextStepSuggestions
            || await db.CrmActivities.AnyAsync(a => a.OrganizationId == ctx.OrganizationId && a.Target == CrmTarget.Deal && a.TargetId == deal.Id && !a.IsDone
                                                    && a.Subject == subject, ct))
        {
            return null;
        }

        var tz = await CrmEvents.TimeZoneAsync(db, ctx.OrganizationId, ct);
        return new CrmNextStepDto(deal.Id, subject, days, CrmEvents.DueInDays(clock.UtcNow, tz, days));
    }

    public async Task SetProbabilityAsync(long id, int probability, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, deal) = await LoadForEditAsync(id, rowVersion, ct);
        var before = deal.Probability;
        deal.SetProbability(probability);
        Audit(ctx, AuditActions.CrmDealChanged, id, $"{before} %", $"{deal.Probability} %", $"{deal.Number}: вероятность");
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Переназначить ответственного — только руководитель (crm.all, D600).</summary>
    public async Task ReassignAsync(long id, long userId, byte[] rowVersion, CancellationToken ct = default)
    {
        await guard.DemandAsync(Permissions.CrmAll, ct);
        var (ctx, deal) = await LoadForEditAsync(id, rowVersion, ct);
        await CrmCommon.EnsureResponsibleAsync(db, ctx, userId, deal.ResponsibleUserId, ct);
        var before = deal.ResponsibleUserId;
        deal.Reassign(userId);
        deal.RemoveParticipant(userId);
        Audit(ctx, AuditActions.CrmDealReassigned, id, before.ToString(), userId.ToString(), deal.Number);
        await db.SaveOrConflictAsync(ct);
    }

    public async Task AddParticipantAsync(long id, long userId, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, deal) = await LoadForEditAsync(id, rowVersion, ct);
        if (!await db.OrganizationMembers.AnyAsync(m => m.OrganizationId == ctx.OrganizationId && m.UserId == userId && m.Status == MembershipStatus.Active, ct))
        {
            throw new NotFoundException("Сотрудник");
        }

        if (deal.AddParticipant(userId))
        {
            Audit(ctx, AuditActions.CrmDealChanged, id, null, $"наблюдатель {userId}", deal.Number);
            await db.SaveOrConflictAsync(ct);
        }
    }

    public async Task RemoveParticipantAsync(long id, long userId, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, deal) = await LoadForEditAsync(id, rowVersion, ct);
        if (deal.RemoveParticipant(userId))
        {
            Audit(ctx, AuditActions.CrmDealChanged, id, $"наблюдатель {userId}", null, deal.Number);
            await db.SaveOrConflictAsync(ct);
        }
    }

    /// <summary>Позиция каталога в сделке — для будущего заказа (D607). Цены видит и вводит тот, у кого право «Цены и суммы».</summary>
    public async Task SetLineAsync(long id, long itemId, decimal quantity, decimal price, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, deal) = await LoadForEditAsync(id, rowVersion, ct);
        await guard.DemandAsync(Permissions.PriceView, ct);
        var item = await db.Items.AsNoTracking().Where(i => i.Id == itemId && i.OrganizationId == ctx.OrganizationId)
                       .Join(db.Units.AsNoTracking(), i => i.UnitId, u => u.Id, (i, u) => new { i.Code, i.IsArchived, u.Symbol, u.Precision })
                       .SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Номенклатура");
        if (item.IsArchived)
        {
            throw new BusinessRuleException("catalog.archived", $"Позиция «{item.Code}» в архиве.");
        }

        if (decimal.Round(quantity, item.Precision) != quantity)
        {
            throw new BusinessRuleException("stock.precision", $"Количество в «{item.Symbol}» — не больше {item.Precision} знаков после запятой.");
        }

        try
        {
            deal.SetLine(itemId, quantity, price);
        }
        catch
        {
            db.DiscardChanges();
            throw;
        }

        Audit(ctx, AuditActions.CrmDealChanged, id, null, $"{item.Code}: {Quantities.Format(quantity)} {item.Symbol} × {price:0.####}", $"{deal.Number}: позиция");
        await db.SaveOrConflictAsync(ct);
    }

    public async Task RemoveLineAsync(long id, long itemId, byte[] rowVersion, CancellationToken ct = default)
    {
        var (ctx, deal) = await LoadForEditAsync(id, rowVersion, ct);
        deal.RemoveLine(itemId);
        Audit(ctx, AuditActions.CrmDealChanged, id, $"позиция №{itemId}", "удалена", deal.Number);
        await db.SaveOrConflictAsync(ct);
    }

    /// <summary>Позиции каталога для строки сделки: поиск по коду, названию и артикулу, до 20 совпадений.</summary>
    public async Task<IReadOnlyList<CrmItemOptionDto>> ItemsAsync(string? search, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmView, ct);
        if (!ctx.Permissions.Has(Permissions.CatalogView) || string.IsNullOrWhiteSpace(search))
        {
            return [];
        }

        var text = search.Trim();
        var priceType = ctx.Permissions.Has(Permissions.PriceView)
            ? await db.PriceTypes.AsNoTracking().Where(t => t.OrganizationId == ctx.OrganizationId && t.IsDefault).Select(t => (long?)t.Id).FirstOrDefaultAsync(ct)
            : null;
        return await (from i in db.Items.AsNoTracking()
                      join u in db.Units.AsNoTracking() on i.UnitId equals u.Id
                      where i.OrganizationId == ctx.OrganizationId && !i.IsArchived
                            && (i.Code.Contains(text) || i.Name.Contains(text) || (i.Article != null && i.Article.Contains(text)))
                      orderby i.Code
                      select new CrmItemOptionDto(i.Id, i.Code, i.Name, u.Symbol,
                          priceType == null ? null : db.ItemPrices.Where(p => p.ItemId == i.Id && p.PriceTypeId == priceType).Select(p => (decimal?)p.Price).FirstOrDefault()))
            .Take(20).ToListAsync(ct);
    }

    /// <summary>Покупатели для сделки: поиск по названию и ИНН, до 20.</summary>
    public async Task<IReadOnlyList<LookupDto>> CounterpartiesAsync(string? search, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmView, ct);
        var text = search?.Trim() ?? "";
        return await db.Counterparties.AsNoTracking()
            .Where(c => c.OrganizationId == ctx.OrganizationId && !c.IsArchived && (text == "" || c.Name.Contains(text) || c.Inn == text))
            .OrderByDescending(c => c.IsCustomer).ThenBy(c => c.Name).Take(20).Select(c => new LookupDto(c.Id, c.Name)).ToListAsync(ct);
    }

    /// <summary>Контакты контрагента — для основного контакта сделки.</summary>
    public async Task<IReadOnlyList<LookupDto>> ContactsOfAsync(long counterpartyId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmView, ct);
        return await CrmAccess.Contacts(db, ctx).AsNoTracking().Where(c => c.CounterpartyId == counterpartyId && !c.IsArchived)
            .OrderBy(c => c.FullName).Take(100).Select(c => new LookupDto(c.Id, c.FullName)).ToListAsync(ct);
    }

    /// <summary>Склады для «Создать заказ».</summary>
    public async Task<IReadOnlyList<LookupDto>> WarehousesAsync(CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.SalesEdit, ct);
        return await db.Warehouses.AsNoTracking().Where(w => w.OrganizationId == ctx.OrganizationId && !w.IsArchived)
            .OrderBy(w => w.Name).Select(w => new LookupDto(w.Id, w.Name)).ToListAsync(ct);
    }

    /// <summary>
    /// «Создать заказ» (D607): заказ покупателя базы через <see cref="SalesService"/> — покупатель, склад, проект, канал,
    /// ответственный и строки по позициям сделки (цены с НДС, ставка — из позиции каталога). Права продаж проверяет сам
    /// модуль продаж. Всё в одной транзакции со связью «сделка — заказ». Проигранной сделке заказ не создаётся.
    /// </summary>
    public async Task<long> CreateOrderAsync(long id, long warehouseId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmEdit, ct);
        var deal = await CrmAccess.Deals(db, ctx).AsNoTracking().Include(d => d.Lines).SingleOrDefaultAsync(d => d.Id == id, ct)
                   ?? throw new NotFoundException("Сделка");
        return await CreateOrderCoreAsync(ctx, deal, warehouseId, deal.Lines.Select(l => (l.ItemId, l.Quantity, l.Price)).ToList(), null, ct);
    }

    /// <summary>
    /// «Заказ по КП» (D625): строки и цены — из принятого КП (снимок), а не из текущих позиций сделки. Дальше заказ подтверждается
    /// и передаётся в производство из карточки сделки.
    /// </summary>
    public async Task<long> CreateOrderFromOfferAsync(long offerId, long warehouseId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmEdit, ct);
        var offer = await db.CrmOffers.AsNoTracking().Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == offerId && o.OrganizationId == ctx.OrganizationId, ct)
                    ?? throw new NotFoundException("КП");
        var deal = await CrmAccess.Deals(db, ctx).AsNoTracking().SingleOrDefaultAsync(d => d.Id == offer.DealId, ct) ?? throw new NotFoundException("КП");

        // D129 (аудит 12.10.2026, п. 7): повторное «Заказ по КП» (двойное нажатие, вторая вкладка) возвращает уже созданный
        // и не отменённый заказ, а не второй такой же; проверка — под блокировкой КП. Просроченное КП в заказ не превращается.
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.LockAsync($"kniterp.crm.offer.{offer.Id}", ct);
        var existing = await (from l in db.CrmDealOrders.AsNoTracking()
                              where l.OrganizationId == ctx.OrganizationId && l.OfferId == offer.Id
                              join o in db.SalesOrders.AsNoTracking() on l.SalesOrderId equals o.Id
                              where o.Status != SalesOrderStatus.Cancelled
                              orderby o.Id descending
                              select (long?)o.Id).FirstOrDefaultAsync(ct);
        if (existing is { } orderId)
        {
            return orderId;
        }

        var today = await LocalTodayAsync(ctx, ct);
        if (offer.ValidUntil < today)
        {
            var validUntil = offer.ValidUntil.ToString("dd.MM.yyyy");
            throw new BusinessRuleException("crm.offer.expired",
                $"КП {offer.Number} действовало до {validUntil} — срок истёк. Составьте новое КП с актуальными ценами.");
        }

        var created = await CreateOrderCoreAsync(ctx, deal, warehouseId, offer.Lines.Select(l => (l.ItemId, l.Quantity, l.Price)).ToList(), offer.Number, ct,
            offer.Id);
        await tx.CommitAsync(ct);
        return created;
    }

    private async Task<DateOnly> LocalTodayAsync(AccessContext ctx, CancellationToken ct)
    {
        var tz = await db.Organizations.AsNoTracking().Where(o => o.Id == ctx.OrganizationId).Select(o => o.TimeZoneId).SingleAsync(ct);
        return CrmEvents.Today(clock.UtcNow, tz);
    }

    /// <summary>Подтвердить заказ покупателя по сделке, не уходя из карточки (права — модуля продаж). Сделка станет «Выиграна» (D607).</summary>
    public async Task ConfirmOrderAsync(long id, long salesOrderId, CancellationToken ct = default)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmView, ct);
        _ = await CrmAccess.Deals(db, ctx).AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException("Сделка");
        if (!await db.CrmDealOrders.AnyAsync(l => l.OrganizationId == ctx.OrganizationId && l.DealId == id && l.SalesOrderId == salesOrderId, ct))
        {
            throw new NotFoundException("Заказ покупателя");
        }

        var order = await sales.GetOrderAsync(salesOrderId, ct);
        await sales.ConfirmOrderAsync(salesOrderId, order.RowVersion, ct);
    }

    private async Task<long> CreateOrderCoreAsync(AccessContext ctx, CrmDeal deal, long warehouseId, IReadOnlyList<(long ItemId, decimal Quantity, decimal Price)> lines,
        string? offerNumber, CancellationToken ct, long? offerId = null)
    {
        var id = deal.Id;
        if (deal.Outcome == CrmDealOutcome.Lost)
        {
            throw new BusinessRuleException("crm.deal.lost", "Сделка проиграна — заказ по ней не создаётся.");
        }

        var options = await sales.GetOptionsAsync(ct: ct);
        var today = await LocalTodayAsync(ctx, ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var orderId = await sales.CreateOrderAsync(new SalesOrderHeader(today, deal.CounterpartyId, warehouseId, deal.ExpectedCloseDate is { } d && d >= today ? d : null,
            null, true, offerNumber is null ? $"Из сделки {deal.Number}" : $"Из сделки {deal.Number} по {offerNumber}"), ct);
        var order = await sales.GetOrderAsync(orderId, ct);
        await sales.SetOrderDetailsAsync(orderId, new SalesOrderDetails(null, deal.ProjectId, deal.ChannelId, null, deal.ResponsibleUserId),
            new Dictionary<long, string?>(), order.RowVersion, ct);
        foreach (var line in lines)
        {
            var vat = options.Items.FirstOrDefault(i => i.Id == line.ItemId)?.VatPercent;
            await sales.SetOrderLineAsync(orderId, line.ItemId, line.Quantity, line.Price, vat, (await sales.GetOrderAsync(orderId, ct)).RowVersion, ct);
        }

        db.CrmDealOrders.Add(CrmDealOrder.Create(ctx.OrganizationId, id, orderId, ctx.UserId, clock.UtcNow, offerId));
        // D683: связь и в заказе базы (SalesOrder.CrmDealId, D117) — карточка заказа показывает сделку в «Связанных документах».
        (await db.SalesOrders.SingleAsync(o => o.Id == orderId && o.OrganizationId == ctx.OrganizationId, ct)).LinkCrmDeal(id);
        Audit(ctx, AuditActions.CrmDealOrderCreated, id, null, order.Number, offerNumber ?? deal.Number);
        await db.SaveChangesAsync(ct);
        // Б2 (D665): создан заказ — сделка «Согласование» (только вперёд).
        var tracked = await db.CrmDeals.SingleAsync(d => d.Id == id, ct);
        await CrmEvents.AdvanceToAsync(db, ctx, tracked, "Согласование", clock.UtcNow, currentUser.CorrelationId, $"создан заказ {order.Number}", ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return orderId;
    }

    /// <summary>
    /// D607: событие модуля продаж «заказ покупателя подтверждён». Если заказ создан из открытой сделки — сделка переходит
    /// на этап «выигрыш» своей воронки (с записью в историю и журнал). Вызывается в транзакции подтверждения; права —
    /// у того, кто подтверждает заказ (это автоматическое следствие, а не правка сделки).
    /// </summary>
    public static async Task OnOrderConfirmedAsync(IKnitErpDbContext db, AccessContext ctx, long orderId, DateTime nowUtc, string? correlationId, CancellationToken ct)
    {
        var dealId = await db.CrmDealOrders.Where(l => l.OrganizationId == ctx.OrganizationId && l.SalesOrderId == orderId)
            .Select(l => (long?)l.DealId).SingleOrDefaultAsync(ct);
        if (dealId is not { } id)
        {
            return;
        }

        var deal = await db.CrmDeals.SingleAsync(d => d.Id == id && d.OrganizationId == ctx.OrganizationId, ct);
        if (deal.Outcome != CrmDealOutcome.Open)
        {
            return;
        }

        var won = await db.CrmStages.AsNoTracking().SingleAsync(s => s.PipelineId == deal.PipelineId && s.Kind == CrmStageKind.Won, ct);
        var from = await db.CrmStages.AsNoTracking().Where(s => s.Id == deal.StageId).Select(s => s.Name).SingleAsync(ct);
        db.CrmDealStageHistory.Add(deal.MoveTo(won, ctx.UserId, nowUtc));
        var order = await db.SalesOrders.AsNoTracking().Where(o => o.Id == orderId).Select(o => o.Number).SingleAsync(ct);
        db.AuditEntries.Add(AuditEntry.Create(nowUtc, ctx.OrganizationId, ctx.UserId, AuditActions.CrmDealStageChanged, nameof(CrmDeal), id.ToString(),
            from, won.Name, $"заказ {order} подтверждён", correlationId));
        await CrmEvents.AfterStageChangedAsync(db, ctx, deal, won, nowUtc, correlationId, ct);
    }

    /// <summary>Заказы по сделке из модуля продаж — только с правом «Продажи: просмотр»; суммы скрывает сам модуль продаж (D91).</summary>
    private async Task<(IReadOnlyList<CrmLinkedOrderDto> Orders, int Hidden)> LinkedOrdersAsync(AccessContext ctx, long dealId, CancellationToken ct)
    {
        var ids = await db.CrmDealOrders.AsNoTracking().Where(l => l.OrganizationId == ctx.OrganizationId && l.DealId == dealId)
            .OrderBy(l => l.Id).Select(l => l.SalesOrderId).ToListAsync(ct);
        if (ids.Count == 0 || !ctx.Permissions.Has(Permissions.SalesView))
        {
            return ([], ids.Count);
        }

        var list = new List<CrmLinkedOrderDto>();
        foreach (var orderId in ids)
        {
            var o = await sales.GetOrderAsync(orderId, ct);
            list.Add(new CrmLinkedOrderDto(o.Id, o.Number, o.OrderDate, o.Status, o.Stage, o.Total, o.Paid, o.Shipped, o.Debt));
        }

        return (list, 0);
    }

    private IQueryable<CrmDeal> Filtered(AccessContext ctx, CrmDealFilter f)
    {
        var q = CrmAccess.Deals(db, ctx).AsNoTracking();
        if (f.PipelineId is { } pipeline)
        {
            q = q.Where(d => d.PipelineId == pipeline);
        }

        if (f.Mine)
        {
            q = q.Where(d => d.ResponsibleUserId == ctx.UserId || d.Participants.Any(p => p.UserId == ctx.UserId));
        }

        if (f.ResponsibleUserId is { } responsible)
        {
            q = q.Where(d => d.ResponsibleUserId == responsible);
        }

        if (f.SourceId is { } source)
        {
            q = q.Where(d => d.SourceId == source);
        }

        if (f.CounterpartyId is { } counterparty)
        {
            q = q.Where(d => d.CounterpartyId == counterparty);
        }

        if (f.TagId is { } tagId)
        {
            q = q.Where(d => db.TagLinks.Any(t => t.TagId == tagId && t.Entity == TaggedEntity.CrmDeal && t.EntityId == d.Id));
        }

        if (f.CloseFrom is { } from)
        {
            q = q.Where(d => d.ExpectedCloseDate >= from);
        }

        if (f.CloseTo is { } to)
        {
            q = q.Where(d => d.ExpectedCloseDate <= to);
        }

        if (f.Outcome is { } outcome)
        {
            q = q.Where(d => d.Outcome == outcome);
        }

        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var text = f.Search.Trim();
            q = q.Where(d => d.Number.Contains(text) || d.Title.Contains(text)
                             || db.Counterparties.Any(c => c.Id == d.CounterpartyId && (c.Name.Contains(text) || c.Inn == text)));
        }

        return q;
    }

    private async Task<CrmStage> FirstStageAsync(AccessContext ctx, long? pipelineId, CancellationToken ct)
    {
        var pipeline = pipelineId is { } pid
            ? await db.CrmPipelines.AsNoTracking().SingleOrDefaultAsync(p => p.Id == pid && p.OrganizationId == ctx.OrganizationId, ct) ?? throw new NotFoundException("Воронка")
            : await db.CrmPipelines.AsNoTracking().Where(p => p.OrganizationId == ctx.OrganizationId && !p.IsArchived)
                  .OrderBy(p => p.SortOrder).ThenBy(p => p.Id).FirstAsync(ct);
        pipeline.EnsureActive();
        return await db.CrmStages.AsNoTracking().Where(s => s.PipelineId == pipeline.Id && !s.IsArchived && s.Kind == CrmStageKind.Open)
                   .OrderBy(s => s.SortOrder).ThenBy(s => s.Id).FirstOrDefaultAsync(ct)
               ?? throw new BusinessRuleException("crm.stage.last_open", "В воронке нет рабочих этапов.");
    }

    private async Task ValidateAsync(AccessContext ctx, CrmDealValues v, CrmDeal? current, CancellationToken ct)
    {
        await CrmCommon.CounterpartyAsync(db, ctx, v.CounterpartyId, current?.CounterpartyId, ct);
        if (v.ContactId is { } contactId && contactId != current?.ContactId
            && !await CrmAccess.Contacts(db, ctx).AnyAsync(c => c.Id == contactId && !c.IsArchived, ct))
        {
            throw new NotFoundException("Контакт");
        }

        await CrmCommon.EnsureReferenceAsync(db, ctx, v.SourceId, current?.SourceId, CrmReferenceKind.Source, ct);
        await CrmCommon.EnsureLookupAsync(db, ctx, v.ChannelId, current?.ChannelId, LookupKind.SalesChannel, ct);
        await CrmCommon.EnsureLookupAsync(db, ctx, v.ProjectId, current?.ProjectId, LookupKind.Project, ct);
        await CrmCommon.EnsureResponsibleAsync(db, ctx, v.ResponsibleUserId, current?.ResponsibleUserId ?? ctx.UserId, ct);
    }

    private static CrmDealValues WithoutHiddenAmount(AccessContext ctx, CrmDealValues v, decimal current) =>
        ctx.Permissions.Has(Permissions.PriceView) ? v : v with { Amount = current };

    private async Task<(AccessContext, CrmDeal)> LoadForEditAsync(long id, byte[] rowVersion, CancellationToken ct)
    {
        var ctx = await guard.DemandAsync(Permissions.CrmEdit, ct);
        var deal = await CrmAccess.Deals(db, ctx).Include(d => d.Lines).Include(d => d.Participants).SingleOrDefaultAsync(d => d.Id == id, ct)
                   ?? throw new NotFoundException("Сделка");
        return (ctx, deal.EnsureVersion(deal.RowVersion, rowVersion));
    }

    private static string? Name(Dictionary<long, string> map, long? id) => id is { } v && map.TryGetValue(v, out var n) ? n : null;

    private void Audit(AccessContext ctx, string action, long id, string? before, string? after, string? reason) =>
        db.AuditEntries.Add(AuditEntry.Create(clock.UtcNow, ctx.OrganizationId, ctx.UserId, action, nameof(CrmDeal), id.ToString(),
            before, after, reason, currentUser.CorrelationId));
}
