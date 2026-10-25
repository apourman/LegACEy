using Avalonia.Controls;
using Avalonia.Controls.Documents;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Tests;

public sealed class GameMessageBandsTests
{
    [Fact]
    public void Messages_stack_newest_first_up_to_three_repeats_count_up_and_each_runs_out_on_its_own() => RenderThread.Run(() =>
    {
        var now = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        var bands = new DerethMessageBands(() => now);
        Assert.False(bands.IsVisible);

        bands.Show("You can't move that!");
        now += TimeSpan.FromSeconds(1);
        bands.Show("The chest is locked!");
        Assert.True(bands.IsVisible);
        Assert.Equal(new[] { "The chest is locked!", "You can't move that!" }, Lines(bands));

        // A repeat moves to the top, counts up and starts its time again.
        now += TimeSpan.FromSeconds(1);
        bands.Show("You can't move that!");
        bands.Show("You can't move that!");
        Assert.Equal(new[] { "You can't move that!  ×3", "The chest is locked!" }, Lines(bands));

        // Past three, the oldest goes.
        bands.Show("You are too fatigued to attack!");
        bands.Show("You have used a portal too recently.");
        Assert.Equal(new[] { "You have used a portal too recently.", "You are too fatigued to attack!", "You can't move that!  ×3" }, Lines(bands));

        // Each runs out four seconds after it was last shown; the last one gone hides the stack.
        now += TimeSpan.FromSeconds(3.9);
        bands.Tick();
        Assert.Equal(3, Lines(bands).Length);
        Assert.True(bands.Children[0].Opacity < 1);
        now += TimeSpan.FromSeconds(0.2);
        bands.Tick();
        Assert.Empty(Lines(bands));
        Assert.False(bands.IsVisible);
    });

    private static string[] Lines(DerethMessageBands bands) => bands.Children
        .Select(band => string.Concat(((TextBlock)((Border)band).Child!).Inlines!.OfType<Run>().Select(run => run.Text)))
        .ToArray();
}
