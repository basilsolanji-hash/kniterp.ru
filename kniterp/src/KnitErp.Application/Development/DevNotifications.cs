using KnitErp.Application.Access;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Development;
using Microsoft.EntityFrameworkCore;

namespace KnitErp.Application.Development;

/// <summary>Счётчик для колокольчика (D796): текст, ссылка и ключ (новое число — уведомление появляется снова).</summary>
public sealed record DevAlert(string Text, string Href, bool Warning, string Key);

/// <summary>
/// Уведомления разработки (D796) — считаются при открытии колокольчика, ничего не хранится: модели на моей стадии, мои просрочки,
/// главному технологу — модели на «Себестоимости» и «Согласовании», ждущие утверждения. Два запроса к базе.
/// </summary>
public static class DevNotifications
{
    public static async Task<IReadOnlyList<DevAlert>> ComputeAsync(IKnitErpDbContext db, AccessContext ctx, DateOnly today, DateTime nowUtc, CancellationToken ct)
    {
        if (!ctx.Permissions.Has(Permissions.DevView))
        {
            return [];
        }

        var me = ctx.UserId;
        var plan = await DevStagePlan.LoadAsync(db, ctx.OrganizationId, ct);
        var models = await db.DevModels.AsNoTracking()
            .Where(m => m.OrganizationId == ctx.OrganizationId && m.Stage != DevStage.Handed && m.Stage != DevStage.Cancelled)
            .Select(m => new
            {
                m.Stage, m.StageSinceUtc, m.PlannedLaunch, m.DesignerUserId, m.DessinatorUserId, m.ConstructorUserId, m.TechnologistUserId, m.ProgrammerUserId,
            })
            .Take(2000).ToListAsync(ct);
        var mine = models.Where(m => DevStages.ResponsibleRole(m.Stage) switch
        {
            DevRole.Designer => m.DesignerUserId == me,
            DevRole.Dessinator => m.DessinatorUserId == me,
            DevRole.Technologist => m.TechnologistUserId == me,
            DevRole.Programmer => m.ProgrammerUserId == me,
            _ => false,
        }).ToList();
        var result = new List<DevAlert>();
        if (mine.Count > 0)
        {
            result.Add(new($"Модели разработки на вашей стадии: {mine.Count}", "dev/my", false, $"dev:mine:{mine.Count}"));
        }

        var late = mine.Count(m => DevModel.IsLate(m.Stage, m.PlannedLaunch, today)
                                   || DevModel.IsStageOverdue(m.Stage, m.StageSinceUtc, plan.PlanDays(m.Stage), nowUtc));
        if (late > 0)
        {
            result.Add(new($"Просрочено у вас в разработке: {late}", "dev/my", true, $"dev:late:{late}:{today:yyyyMMdd}"));
        }

        if (ctx.Permissions.Has(Permissions.DevApprove))
        {
            var waiting = models.Count(m => m.Stage is DevStage.Costing or DevStage.Approval);
            if (waiting > 0)
            {
                result.Add(new($"Модели ждут согласования и утверждения: {waiting}", "dev/models?view=table", false, $"dev:approve:{waiting}"));
            }
        }

        return result;
    }
}
