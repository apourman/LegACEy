using Avalonia.Controls;
using LegACEy.Client.Demo;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Tests;

public sealed class ClientUiFrameworkTests
{
    [Fact]
    public void Closing_a_window_and_ending_session_disposes_the_remaining_windows() => RenderThread.Run(() =>
    {
        var host = new FakeHost();
        using var ui = new ClientUiFramework(host, new FakeGameState(), new SimpleClientTheme());
        ui.OpenWindow(new WindowDefinition("sample", "Sample", 100, 80), new Border());
        ui.OpenWindow(new WindowDefinition("second", "Second", 100, 80), new Border());
        Assert.True(ui.CloseWindow("sample"));
        Assert.Equal(1, host.Disposed);

        ui.EndSession();

        Assert.Equal(2, host.Disposed);
        Assert.Equal(0, ui.OpenWindowCount);
        Assert.Throws<ObjectDisposedException>(() => ui.OpenWindow(new WindowDefinition("later", "Later", 40, 40), new Border()));
    });

    [Fact]
    public void Opening_failure_cleans_up_prior_windows() => RenderThread.Run(() =>
    {
        var host = new FakeHost();
        var ui = new ClientUiFramework(host, new FakeGameState(), new SimpleClientTheme());
        ui.OpenWindow(new WindowDefinition("first", "First", 40, 40), new Border());
        host.ThrowOnWindow = true;

        Assert.Throws<InvalidOperationException>(() => ui.OpenWindow(new WindowDefinition("broken", "Broken", 40, 40), new Border()));

        Assert.Equal(1, host.Disposed);
        Assert.Equal(0, ui.OpenWindowCount);
    });

    [Fact]
    public void Session_cleanup_retries_a_transient_window_restore_failure() => RenderThread.Run(() =>
    {
        var host = new FakeHost();
        var ui = new ClientUiFramework(host, new FakeGameState(), new SimpleClientTheme());
        ui.OpenWindow(new WindowDefinition("sample", "Sample", 100, 80), new Border());
        host.FailDisposeCount = 1;

        Assert.Throws<InvalidOperationException>(ui.EndSession);
        Assert.Equal(0, host.Disposed);

        ui.EndSession();

        Assert.Equal(1, host.Disposed);
        Assert.Equal(0, ui.OpenWindowCount);
    });

    private sealed class FakeHost : IClientUiHost
    {
        public bool ThrowOnWindow { get; set; }
        public int Disposed { get; private set; }
        public int FailDisposeCount { get; set; }
        public IDisposable OpenWindow(WindowDefinition definition, Control content, System.Drawing.Point requestedLocation)
        {
            if (ThrowOnWindow) throw new InvalidOperationException("Window host failed.");
            return Registration();
        }
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
