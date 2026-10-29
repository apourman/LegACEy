using Avalonia.Controls;
using LegACEy.Client.PanelHost;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Tests;

public sealed class GameMessageBandsTests
{
    [Fact]
    public void Messages_stack_newest_first_up_to_three_a_repeat_on_its_own_row_and_each_runs_out_on_its_own() => RenderThread.Run(() =>
    {
        var now = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        var bands = new DerethMessageBands(() => now);
        Assert.False(bands.IsVisible);

        bands.Show("You can't move that!");
        now += TimeSpan.FromSeconds(1);
        bands.Show("The chest is locked!");
        Assert.True(bands.IsVisible);
        Assert.Equal(new[] { "The chest is locked!", "You can't move that!" }, Lines(bands));

        // A repeat gets its own row, as retail's floating lines stack.
        now += TimeSpan.FromSeconds(1);
        bands.Show("You can't move that!");
        Assert.Equal(new[] { "You can't move that!", "The chest is locked!", "You can't move that!" }, Lines(bands));

        // Past three, the oldest goes.
        bands.Show("You are too fatigued to attack!");
        bands.Show("You have used a portal too recently.");
        Assert.Equal(new[] { "You have used a portal too recently.", "You are too fatigued to attack!", "You can't move that!" }, Lines(bands));

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

    [Fact]
    public void While_placed_a_sample_band_stays_on_top_at_the_text_size_and_messages_show_under_it() => RenderThread.Run(() =>
    {
        var now = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        var bands = new DerethMessageBands(() => now) { TextScale = 1.5 };
        bands.Moving = true;
        Assert.True(bands.IsVisible);
        foreach (var text in new[] { "One", "Two", "Three", "Four" }) bands.Show(text);
        Assert.Equal(new[] { DerethMessageBands.SampleText, "Four", "Three", "Two" }, Lines(bands));
        Assert.Equal(17 * 1.5, ((TextBlock)((Decorator)bands.Children[0]).Child!).FontSize);

        // The messages run out; the sample stays until placing ends.
        now += TimeSpan.FromSeconds(5);
        bands.Tick();
        Assert.Equal(new[] { DerethMessageBands.SampleText }, Lines(bands));
        bands.TextScale = 1;
        Assert.Equal(17, ((TextBlock)((Decorator)bands.Children[0]).Child!).FontSize);
        bands.Moving = false;
        Assert.False(bands.IsVisible);
    });

    [Fact]
    public void The_bands_render_to_pngs_when_asked() => RenderThread.Run(() =>
    {
        // Looked at by a person, not asserted: LEGACEY_MESSAGES_RENDER names the folder the PNGs are written to, a burst and its smoke
        // two seconds on.
        var folder = Environment.GetEnvironmentVariable("LEGACEY_MESSAGES_RENDER");
        if (folder == null) return;
        Directory.CreateDirectory(folder);
        var now = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        var bands = new DerethMessageBands(() => now);
        for (var i = 0; i < 3; i++) bands.Show("You're too loaded down to jump");
        using var panel = AvaloniaPanel.Create(() => bands, 560, 200, 1);
        // Fitted as the game fits its surface, so the smoke's edge is the surface's.
        bands.Measure(Avalonia.Size.Infinity);
        panel.Resize((int)Math.Ceiling(bands.DesiredSize.Width), (int)Math.Ceiling(bands.DesiredSize.Height));
        panel.Tick();
        PanelPng.Save(panel, Path.Combine(folder, "burst.png"));
        now += TimeSpan.FromSeconds(2);
        bands.Tick();
        panel.Tick();
        PanelPng.Save(panel, Path.Combine(folder, "burst-2s.png"));
    });

    private static string[] Lines(DerethMessageBands bands) => bands.Children
        .Select(band => ((TextBlock)((Decorator)band).Child!).Text!)
        .ToArray();
}
