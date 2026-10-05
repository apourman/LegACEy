using System.Globalization;
using Avalonia.Controls;
using LegACEy.Client.DecalPlugin;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.PanelHost;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Tests;

public sealed class ScrollProfileTests
{
    [Fact]
    public void Interval_retains_a_brief_stall_and_resets_without_reusing_old_peaks()
    {
        var profile = new ScrollProfile();
        profile.Record("list", "wheel-input", 1);
        profile.Record("list", "wheel-input", 19);
        profile.Record("list", "wheel-input", 1);
        profile.Record("list", "wheel-input", double.NaN);
        var snapshot = profile.Snapshot(42, 1, DateTime.UnixEpoch);
        Assert.Contains("pid=42", snapshot);
        Assert.Contains("count=3 totalMs=21.000 avgMs=7.000 maxMs=19.000 ge8ms=1 ge16ms=1", snapshot);
        profile.Record("list", "wheel-input", 2);
        var next = profile.Snapshot(42, 1, DateTime.UnixEpoch);
        Assert.Contains("count=1 totalMs=2.000 avgMs=2.000 maxMs=2.000", next);
        Assert.DoesNotContain("maxMs=19", next);
    }

    [Fact]
    public void Diagnostic_format_is_stable_in_a_comma_decimal_locale()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var profile = new ScrollProfile();
            profile.Record("list", "panel-tick", 1.5);
            Assert.Contains("avgMs=1.500", profile.Snapshot(42, 1.5, DateTime.UnixEpoch));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void Writer_flushes_queued_capture_on_disposal()
    {
        var path = Path.Combine(Path.GetTempPath(), "scroll-profile-" + Guid.NewGuid() + ".log");
        try
        {
            using (var writer = new ScrollProfileLog(path))
            {
                writer.Enqueue("capture=start\n");
                writer.Enqueue("capture=stop\n");
            }
            Assert.Equal("capture=start\ncapture=stop\n", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Real_scrolling_records_input_and_render_stages_without_stale_idle_pixel_samples() => RenderThread.Run(() =>
    {
        using var panel = AvaloniaPanel.Create(() => new PerformanceListPanel(new NoArt()), 360, 320);
        var observations = new List<(string Stage, double Milliseconds)>();
        panel.TimingObserver = (stage, elapsed) => observations.Add((stage, elapsed));
        panel.MouseWheel(40, 80, 0, -120);
        Assert.True(panel.Tick());
        Assert.Null(panel.LastError);
        Assert.Contains(observations, value => value.Stage == "wheel-input");
        Assert.Contains(observations, value => value.Stage == "render-timer");
        Assert.Contains(observations, value => value.Stage == "pixel-diff-copy");
        Assert.All(observations, value => Assert.True(double.IsFinite(value.Milliseconds) && value.Milliseconds >= 0));
        panel.Tick(); // Settle any deferred layout before checking a fully idle tick.
        observations.Clear();
        Assert.False(panel.Tick());
        Assert.Contains(observations, value => value.Stage == "panel-tick");
        Assert.DoesNotContain(observations, value => value.Stage == "pixel-diff-copy");
        Assert.Null(panel.LastError);
    });

    [Fact]
    public void A_broken_diagnostic_observer_does_not_disable_panel_input_or_rendering() => RenderThread.Run(() =>
    {
        using var panel = AvaloniaPanel.Create(() => new Button(), 100, 100);
        panel.TimingObserver = (_, _) => throw new IOException("Diagnostic sink failed");
        panel.MouseWheel(40, 40, 0, -1);
        panel.Tick();
        Assert.Null(panel.LastError);
    });

    private sealed class NoArt : IGameArtSource
    {
        public GameImage? ReadImage(uint id) => null;
    }

    [Fact]
    public void Repeated_themed_scrolling_can_produce_a_stage_capture_at_game_panel_size() => RenderThread.Run(() =>
    {
        var portalPath = Environment.GetEnvironmentVariable("LEGACEY_PORTAL_DAT");
        using var dat = string.IsNullOrEmpty(portalPath) ? null : new PortalDat(portalPath);
        IGameArtSource art = dat is null ? new NoArt() : dat;
        using var panel = AvaloniaPanel.Create(() =>
            new ThemeWindowChrome(art, "performance-list", new PerformanceListPanel(art)), 620, 460);
        panel.ApplyTheme(new AcClientTheme(art));
        for (var index = 0; index < 10; index++) panel.Tick();
        var profile = new ScrollProfile();
        var phase = "idle";
        panel.TimingObserver = (stage, elapsed) => profile.Record(phase, stage, elapsed);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        for (var index = 0; index < 60; index++) panel.Tick();
        var idle = profile.Snapshot(Environment.ProcessId, timer.Elapsed.TotalSeconds, DateTime.UtcNow);
        phase = "scroll";
        timer.Restart();
        for (var index = 0; index < 120; index++)
        {
            panel.MouseWheel(80, 100, 0, index < 60 || index >= 90 ? -1 : 1);
            panel.Tick();
            Assert.Null(panel.LastError);
        }
        var scrolling = profile.Snapshot(Environment.ProcessId, timer.Elapsed.TotalSeconds, DateTime.UtcNow);
        Assert.Contains("stage=wheel-input count=120", scrolling);
        Assert.Contains("stage=pixel-diff-copy", scrolling);
        Assert.DoesNotContain("stage=pixel-diff-copy", idle);
        var output = Environment.GetEnvironmentVariable("LEGACEY_SCROLL_PROFILE_OUTPUT");
        if (!string.IsNullOrEmpty(output)) File.WriteAllText(output, idle + scrolling);
    });
}
