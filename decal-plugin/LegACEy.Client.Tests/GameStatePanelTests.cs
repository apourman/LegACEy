using LegACEy.Client.Demo;
using LegACEy.Client.PanelHost;

namespace LegACEy.Client.Tests;

public sealed class GameStatePanelTests
{
    [Fact]
    public void Publishing_changed_vitals_notifies_subscribers_and_changes_live_panel_pixels() => RenderThread.Run(() =>
    {
        var state = new FakeGameState();
        var notifications = 0;
        state.Changed += (_, _) => notifications++;
        using var panel = AvaloniaPanel.Create(() => new LiveGameDataPanel(state), 320, 220);
        var before = panel.Frame.Pixels.ToArray();

        state.Set(new GameStateSnapshot("Preview Character", "Thistledown", 19, 100, 64, 100, 37, 100));
        Assert.True(panel.Tick());

        Assert.Equal(1, notifications);
        Assert.False(before.SequenceEqual(panel.Frame.Pixels));
    });

    [Fact]
    public void Fake_state_advance_changes_values_and_raises_change_notification()
    {
        var state = new FakeGameState();
        var original = state.Current;
        var notifications = 0;
        state.Changed += (_, _) => notifications++;

        state.Advance();

        Assert.NotEqual(original, state.Current);
        Assert.Equal(1, notifications);
    }
}
