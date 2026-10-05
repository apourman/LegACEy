using Avalonia.Controls;
using LegACEy.Client.PanelHost;
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
            _ => true, _ => new System.Drawing.Rectangle(4, 8, 40, 20), _ => new Callback(() => disposals++), (_, _) => { },
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

    [Fact]
    public void Theme_failure_cleans_up_windows_and_hidden_roots() => RenderThread.Run(() =>
    {
        var host = new FakeHost { ThrowOnTheme = true };
        var ui = new ClientUiFramework(host, new FakeGameState(), new SimpleClientTheme());
        ui.OpenWindow(new WindowDefinition("sample", "Sample", 100, 80), new Border());
        ui.HideRoot(42);
        Assert.Throws<InvalidOperationException>(() => ui.SetTheme(new SimpleClientTheme()));
        Assert.Equal(2, host.Disposed);
        Assert.Throws<ObjectDisposedException>(() => ui.SetTheme(new SimpleClientTheme()));
    });

    [Fact]
    public void Inspector_hide_is_restored_on_close_and_when_switched_to_placeholder() => RenderThread.Run(() =>
    {
        var visible = true;
        var replacements = 0;
        using var inspector = new ElementInspectorControl(
            new[] { new RetailRootDescriptor("one", 1) }, _ => visible,
            _ => new System.Drawing.Rectangle(4, 8, 40, 20),
            _ => { var original = visible; visible = false; return new Callback(() => visible = original); },
            (_, _) => { }, _ => { Assert.True(visible); replacements++; return new Callback(() => replacements--); });
        inspector.Hide(1);
        Assert.False(visible);
        inspector.TakeOver(1);
        Assert.True(visible);
        Assert.Equal(1, replacements);
        inspector.Hide(1);
        Assert.Equal(0, replacements);
        Assert.False(visible);
        inspector.Dispose();
        Assert.True(visible);
    });

    [Fact]
    public void Closing_the_inspector_panel_restores_hidden_roots() => RenderThread.Run(() =>
    {
        var visible = true;
        var inspector = new ElementInspectorControl(
            new[] { new RetailRootDescriptor("one", 1) }, _ => visible,
            _ => new System.Drawing.Rectangle(4, 8, 40, 20),
            _ => { visible = false; return new Callback(() => visible = true); },
            (_, _) => { }, _ => new Callback(() => { }));
        inspector.DetachedFromVisualTree += (_, _) => inspector.Dispose();
        using var panel = AvaloniaPanel.Create(() => inspector, 320, 220);
        inspector.Hide(1);
        Assert.False(visible);
        panel.Dispose();
        Assert.True(visible);
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
        public IDisposable HideRoot(uint rootElementId) => Registration();
        public bool MoveRoot(uint rootElementId, System.Drawing.Point location) => true;
        public bool ThrowOnTheme { get; set; }
        public void ApplyTheme(IClientTheme theme)
        {
            if (ThrowOnTheme) throw new InvalidOperationException("Theme failed.");
            ThemeUpdates++;
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
