using System.Runtime.InteropServices;
using LegACEy.Client.DecalPlugin;
using LegACEy.Client.Demo;

namespace LegACEy.Client.Tests;

public sealed class GameStatePollerTests
{
    private static GameStateSnapshot Snapshot(int health) => new("Player", "Server", health, 100, 70, 100, 50, 100);

    [Fact]
    public void Entry_and_logout_frames_do_not_read_unavailable_character_stats()
    {
        var reads = 0;
        var state = new GameStatePort(Snapshot(0));
        var poller = new GameStatePoller(state, () =>
        {
            reads++;
            throw new COMException("CharacterStats.get_Health returned E_FAIL", unchecked((int)0x80004005));
        });
        var error = Record.Exception(() => poller.Poll(isSessionReady: false));
        Assert.Null(error);
        Assert.Equal(0, reads);
    }

    [Fact]
    public void Unavailable_stats_do_not_abort_rendering_and_next_frame_recovers()
    {
        var reads = 0;
        var notifications = 0;
        var initial = Snapshot(80);
        var state = new GameStatePort(initial);
        state.Changed += (_, _) => notifications++;
        var poller = new GameStatePoller(state, () =>
        {
            if (++reads == 1)
                throw new COMException("CharacterStats.get_Health returned E_FAIL", unchecked((int)0x80004005));
            return Snapshot(60);
        });
        var rendered = false;
        var error = Record.Exception(() =>
        {
            poller.Poll(isSessionReady: true);
            rendered = true;
        });
        Assert.Null(error);
        Assert.True(rendered);
        Assert.Same(initial, state.Current);
        Assert.Equal(0, notifications);
        poller.Poll(isSessionReady: true);
        Assert.Equal(60, state.Current.Health);
        Assert.Equal(1, notifications);
        poller.Poll(isSessionReady: false);
        Assert.Equal(2, reads);
    }
    [Fact]
    public void Only_character_read_COM_failures_are_treated_as_unavailable()
    {
        var state = new GameStatePort(Snapshot(80));
        var brokenRead = new GameStatePoller(state, () => throw new InvalidOperationException("Reader bug"));
        Assert.Throws<InvalidOperationException>(() => brokenRead.Poll(true));
        var poller = new GameStatePoller(state, () => Snapshot(60));
        state.Changed += (_, _) => throw new COMException("Subscriber failed");
        Assert.Throws<COMException>(() => poller.Poll(true));
    }

}
