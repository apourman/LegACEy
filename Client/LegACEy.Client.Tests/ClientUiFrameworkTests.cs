using Avalonia.Controls;
using LegACEy.Client.Demo;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Tests;

public sealed class ClientUiFrameworkTests
{
    [Fact]
    public void Closing_a_window_and_ending_session_disposes_window_and_takeovers() => RenderThread.Run(() =>
    {
        var host = new FakeHost();
        using var ui = new ClientUiFramework(host, new FakeGameState(), new SimpleClientTheme());
        ui.OpenWindow(new WindowDefinition("sample", "Sample", 100, 80), new Border());
        ui.TakeOverRoot(42, new Border());
        ui.SetTheme(new SimpleClientTheme());
        Assert.Equal(1, host.ThemeUpdates);
        Assert.True(ui.CloseWindow("sample"));
        Assert.Equal(1, host.Disposed);

        ui.EndSession();

        Assert.Equal(2, host.Disposed);
        Assert.Equal(0, ui.OpenWindowCount);
        Assert.Equal(0, ui.TakeoverCount);
        Assert.Throws<ObjectDisposedException>(() => ui.OpenWindow(new WindowDefinition("later", "Later", 40, 40), new Border()));
    });

    [Fact]
    public void Opening_failure_cleans_up_prior_registrations() => RenderThread.Run(() =>
    {
        var host = new FakeHost { ThrowOnWindow = true };
        var ui = new ClientUiFramework(host, new FakeGameState(), new SimpleClientTheme());
        ui.TakeOverRoot(42, new Border());

        Assert.Throws<InvalidOperationException>(() => ui.OpenWindow(new WindowDefinition("broken", "Broken", 40, 40), new Border()));

        Assert.Equal(1, host.Disposed);
        Assert.Equal(0, ui.TakeoverCount);
    });

    [Fact]
    public void Inspector_close_disposes_each_placeholder_takeover() => RenderThread.Run(() =>
    {
        var disposals = 0;
        var inspector = new ElementInspectorControl(
            new[] { new RetailRootDescriptor("one", 1), new RetailRootDescriptor("two", 2) },
            _ => true, _ => new System.Drawing.Rectangle(4, 8, 40, 20), (_, _) => { }, (_, _) => { },
            _ => new Callback(() => disposals++));

        inspector.TakeOver(1);
        inspector.TakeOver(2);
        inspector.Dispose();

        Assert.Equal(2, disposals);
    });

    [Fact]
    public void Session_cleanup_retries_a_transient_takeover_restore_failure() => RenderThread.Run(() =>
    {
        var host = new FakeHost { FailDisposeCount = 1 };
        var ui = new ClientUiFramework(host, new FakeGameState(), new SimpleClientTheme());
        ui.TakeOverRoot(42, new Border());

        Assert.Throws<InvalidOperationException>(ui.EndSession);
        Assert.Equal(0, host.Disposed);

        ui.EndSession();

        Assert.Equal(1, host.Disposed);
        Assert.Equal(0, ui.TakeoverCount);
    });

    private sealed class FakeHost : IClientUiHost
    {
        public bool ThrowOnWindow { get; set; }
        public int Disposed { get; private set; }
        public int ThemeUpdates { get; private set; }
        public int FailDisposeCount { get; set; }
        public IDisposable OpenWindow(WindowDefinition definition, Control content, System.Drawing.Point requestedLocation)
        {
            if (ThrowOnWindow) throw new InvalidOperationException("Window host failed.");
            return Registration();
        }
        public IDisposable TakeOverRoot(uint rootElementId, Control content) => Registration();
        public bool MoveRoot(uint rootElementId, System.Drawing.Point location) => true;
        public void ApplyTheme(IClientTheme theme) => ThemeUpdates++;
        private IDisposable Registration() => new Callback(() =>
        {
            if (FailDisposeCount > 0)
            {
                FailDisposeCount--;
                throw new InvalidOperationException("Transient restore failure.");
            }
            Disposed++;
        });
    }

    private sealed class Callback : IDisposable
    {
        private readonly Action _callback;
        public Callback(Action callback) => _callback = callback;
        public void Dispose() => _callback();
    }
}
