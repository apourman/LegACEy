using LegACEy.Client.PanelHost;

namespace LegACEy.Client.Tests;

public sealed class PanelFailureBudgetTests
{
    [Fact]
    public void Three_consecutive_ticks_over_50_ms_exhaust_the_budget()
    {
        var budget = new PanelFailureBudget();

        Assert.False(budget.Record(TimeSpan.FromMilliseconds(51)));
        Assert.False(budget.Record(TimeSpan.FromMilliseconds(60)));
        Assert.True(budget.Record(TimeSpan.FromMilliseconds(50.1)));
    }

    [Fact]
    public void A_fast_tick_resets_the_consecutive_slow_tick_count()
    {
        var budget = new PanelFailureBudget();

        Assert.False(budget.Record(TimeSpan.FromMilliseconds(51)));
        Assert.False(budget.Record(TimeSpan.FromMilliseconds(9)));
        Assert.False(budget.Record(TimeSpan.FromMilliseconds(51)));
    }

    [Fact]
    public void An_exception_exhausts_the_budget_immediately()
    {
        var budget = new PanelFailureBudget();

        Assert.True(budget.Record(TimeSpan.Zero, new InvalidOperationException("panel failed")));
    }
}
