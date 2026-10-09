using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.PanelHost;
using LegACEy.Client.Themes;
using LegACEy.Plugin.Vault;
using Xunit;

namespace LegACEy.Client.Tests;

/// <summary>
/// A Vault window over a <see cref="FakeVaultServer"/>, on the real channel wire, with the channel's clock under the test's control.
/// Everything the test does happens in <see cref="Step"/>: the server answers, the channel times out or runs scheduled work, and the window draws.
/// </summary>
public class VaultFixture : IDisposable
{
    private DateTime _now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    private readonly ServerChannelClient _channel;
    private readonly VaultShellPanel _panel;

    /// <param name="items">The Vault's items; null is the fake's sample Vault.</param>
    /// <param name="art">The game art the cells draw; null draws no art.</param>
    public VaultFixture(IEnumerable<VaultItemView>? items = null, int height = VaultShellPanel.WindowHeight, IItemDragHost? dragHost = null, IGameArtSource? art = null)
    {
        Server = new FakeVaultServer(() => _now, items) { Latency = TimeSpan.FromMilliseconds(30) };
        _channel = new ServerChannelClient(Server, () => _now);
        Server.Deliver = _channel.Receive;
        Client = new VaultClient(_channel);
        VaultShellPanel? panel = null;
        Host = AvaloniaPanel.Create(() => new VaultShellWindow(panel = new VaultShellPanel(art ?? new NoArt(), Client, dragHost ?? Drag)),
            VaultShellPanel.WindowWidth, height);
        _panel = panel!;
        Settle();
        Assert.Equal(VaultConnection.Live, Client.Connection);
    }

    public FakeVaultServer Server { get; }
    public VaultClient Client { get; }
    public FakeItemDragHost Drag { get; } = new();
    public AvaloniaPanel Host { get; }
    public VaultShellWindow Window => (VaultShellWindow)Host.Content;

    /// <summary>The vault cells in reading order, filled or empty.</summary>
    public Control[] Cells => Host.Content.GetVisualDescendants().OfType<WrapPanel>().Single().Children.OfType<Control>().ToArray();

    public string[] Texts() => Host.Content.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text ?? string.Empty).ToArray();

    public Point Center(Control control) => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), Host.Content)!.Value;

    /// <summary>Presses the control at its centre with the modifiers held, and lets go there. The press lands on what the last frame drew.</summary>
    public void Press(Control control, KeyModifiers modifiers = KeyModifiers.None)
    {
        Host.Tick();
        var point = Center(control);
        Host.PointerDown(point.X, point.Y, modifiers);
        Host.PointerUp(point.X, point.Y);
    }

    /// <summary>The Vault's items in order: even numbers are gold rings, the rest steel swords.</summary>
    public static IEnumerable<VaultItemView> Vault(int count)
    {
        var deposited = new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.Zero);
        for (var number = 1; number <= count; number++)
        {
            var name = number % 2 == 0 ? "Gold Ring" : "Steel Sword";
            yield return new VaultItemView(0x80100000u + (uint)number, name, 0x2, 1, 120, "held", "Arwic Wanderer", deposited, 0x060011CF, 0, 0x06000FC7, 0, 0, 0);
        }
    }

    public void Step(TimeSpan time)
    {
        _now += time;
        Server.Pump();
        _channel.Tick();
        Host.Tick();
    }

    /// <summary>Lets the round trips, the typing settle and any push that follows them happen: twelve steps of 30 ms.</summary>
    public void Settle()
    {
        for (var step = 0; step < 12; step++) Step(TimeSpan.FromMilliseconds(30));
    }

    /// <summary>The grid's scroll viewer. The search field's text box has a viewer of its own, so tests look for the grid's.</summary>
    public static ScrollViewer GridScroller(Visual root) =>
        root.GetVisualDescendants().OfType<ScrollViewer>().Single(viewer => viewer.GetVisualAncestors().OfType<DerethSlotGrid>().Any());

    public virtual void Dispose()
    {
        _panel.Dispose();
        Host.Dispose();
    }

    private sealed class NoArt : IGameArtSource
    {
        public GameImage? ReadImage(uint id) => null;
    }
}
