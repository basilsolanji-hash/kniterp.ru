using KnitErp.Application.Access;
using KnitErp.Application.Authentication;
using KnitErp.Application.Common;
using KnitErp.Domain.Access;
using KnitErp.Domain.Common;

namespace KnitErp.UnitTests;

/// <summary>D123: отметка повторного подтверждения — 10 минут, только своему пользователю, не для запросов по токену API.</summary>
public sealed class StepUpTests
{
    private static readonly DateTime T0 = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    private sealed class Clock : IClock
    {
        public DateTime UtcNow { get; set; } = T0;
    }

    private sealed class User : ICurrentUser
    {
        public long? UserId { get; init; } = 7;
        public long? OrganizationId { get; init; } = 1;
        public string? CorrelationId => null;
        public long? ApiTokenId { get; init; }
    }

    private static AccessContext Ctx(long userId) => new(userId, 1, EffectivePermissionSet.Empty);

    [Fact]
    public void Without_confirmation_action_is_refused_with_stepup_code()
    {
        var guard = new StepUpGuard(new StepUpState(), new User(), new Clock());
        Assert.Equal(StepUp.RequiredCode, Assert.Throws<BusinessRuleException>(() => guard.Demand(Ctx(7))).Code);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(599, true)]
    [InlineData(600, false)]
    [InlineData(3600, false)]
    [InlineData(-60, false)]
    public void Confirmation_is_valid_for_ten_minutes(int secondsLater, bool fresh)
    {
        var state = new StepUpState();
        state.Grant(7, T0);
        var clock = new Clock { UtcNow = T0.AddSeconds(secondsLater) };
        Assert.Equal(fresh, new StepUpGuard(state, new User(), clock).IsFresh(7));
    }

    [Fact]
    public void Confirmation_belongs_to_one_user_and_is_ignored_for_api_tokens()
    {
        var state = new StepUpState();
        state.Grant(7, T0);
        var clock = new Clock();
        Assert.False(new StepUpGuard(state, new User { UserId = 8 }, clock).IsFresh(8));
        Assert.Throws<BusinessRuleException>(() => new StepUpGuard(state, new User { ApiTokenId = 5 }, clock).Demand(Ctx(7)));
        new StepUpGuard(state, new User(), clock).Demand(Ctx(7));

        state.Clear();
        Assert.False(new StepUpGuard(state, new User(), clock).IsFresh(7));
    }
}
