using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.VisualTree;
using LegACEy.Client.Demo;
using LegACEy.Client.DecalPlugin;
using LegACEy.Client.PanelHost;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Tests;

public sealed class RetailToolbarTests
{
    private const int Width = 600;
    private const int Height = 60;

    [Fact]
    public void Stance_shows_the_port_stance_and_a_click_asks_the_port_to_toggle() => RenderThread.Run(() =>
    {
        var port = new FakeToolbarPort();
        using var panel = Open(port, out var toolbar);
        panel.Tick();

        // Behaviour correction (spec story 14: the click changes stance as retail's button does): a click toggles, not cycles.
        Click(panel, toolbar, toolbar.StanceButton);
        Assert.Equal(1, port.StanceToggles);
        Assert.Equal(RetailStance.Melee, port.Stance);

        // A stance change made by a key or by the game shows on the next frame, as a fresh toolbar in that stance draws it.
        port.Stance = RetailStance.Missile;
        port.Raise();
        panel.Tick();
        AssertDrawn(Render(p => p.Stance = RetailStance.Missile), panel);
    });

    [Fact]
    public void Each_window_button_and_the_backpack_asks_for_its_panel_number_and_toggles_it() => RenderThread.Run(() =>
    {
        var port = new FakeToolbarPort();
        using var panel = Open(port, out var toolbar);
        panel.Tick();
        var buttons = new[] { RetailPanel.Social, RetailPanel.Magic, RetailPanel.Skills, RetailPanel.Quests, RetailPanel.WorldMap, RetailPanel.Options, RetailPanel.Inventory };

        foreach (var button in buttons)
        {
            Click(panel, toolbar, toolbar.ButtonFor(button));
            Click(panel, toolbar, toolbar.ButtonFor(button));
        }

        Assert.Equal(new[] { (12, true), (12, false), (13, true), (13, false), (11, true), (11, false), (25, true), (25, false),
            (16, true), (16, false), (10, true), (10, false), (7, true), (7, false) }, port.PanelRequests.Select(r => ((int)r.Panel, r.Open)));
    });

    [Fact]
    public void A_window_ring_follows_the_open_state_however_the_window_changed() => RenderThread.Run(() =>
    {
        var port = new FakeToolbarPort();
        using var panel = Open(port, out _);
        panel.Tick();

        // Opened by a key, or by the window's own button: the ring shows as a fresh toolbar with it open draws it.
        port.Open(RetailPanel.Skills, true);
        panel.Tick();
        AssertDrawn(Render(p => p.Open(RetailPanel.Skills, true)), panel);

        // Closed by its own close box: the ring goes.
        port.Open(RetailPanel.Skills, false);
        panel.Tick();
        AssertDrawn(Render(), panel);
    });

    [Fact]
    public void The_menu_button_opens_the_menu_and_rings_while_the_menu_is_open() => RenderThread.Run(() =>
    {
        var port = new FakeToolbarPort();
        var menuClicks = 0;
        RetailToolbar? toolbar = null;
        using var panel = AvaloniaPanel.Create(() => toolbar = new RetailToolbar(port, () =>
        {
            menuClicks++;
            // The client opens its menu, then tells the toolbar it is open.
            toolbar!.SetMenuOpen(true);
        }), Width, Height);
        panel.Tick();

        Click(panel, toolbar!, toolbar!.MenuButton);
        panel.Tick();

        Assert.Equal(1, menuClicks);
        AssertDrawn(Render(arrangeToolbar: t => t.SetMenuOpen(true)), panel);
    });

    [Fact]
    public void A_second_click_asks_to_close_a_window_the_port_only_knows_it_was_asked_to_open() => RenderThread.Run(() =>
    {
        var port = new RequestedOnlyPort();
        using var panel = Open(port, out var toolbar);
        panel.Tick();

        Click(panel, toolbar, toolbar.ButtonFor(RetailPanel.Skills));
        Click(panel, toolbar, toolbar.ButtonFor(RetailPanel.Skills));

        Assert.Equal(new[] { (11, true), (11, false) }, port.Requests.Select(r => ((int)r.Panel, r.Open)));
    });

    [Fact]
    public void With_nothing_selected_use_and_examine_do_nothing() => RenderThread.Run(() =>
    {
        var port = new FakeToolbarPort();
        using var panel = Open(port, out var toolbar);
        panel.Tick();

        Click(panel, toolbar, toolbar.UseButton);
        Click(panel, toolbar, toolbar.ExamineButton);

        Assert.Equal(0, port.UseCount);
        Assert.Equal(0, port.ExamineCount);
    });

    [Fact]
    public void Use_and_examine_act_on_the_selected_object() => RenderThread.Run(() =>
    {
        var port = new FakeToolbarPort { Selection = new RetailSelection(0x50000001, "Ivory Quarter Staff", SelectionKind.ItemWithMana, 0.82f) };
        using var panel = Open(port, out var toolbar);
        panel.Tick();

        Click(panel, toolbar, toolbar.UseButton);
        Click(panel, toolbar, toolbar.ExamineButton);

        Assert.Equal(1, port.UseCount);
        Assert.Equal(1, port.ExamineCount);
    });

    [Fact]
    public void The_plaque_follows_the_selection_through_nothing_creature_mana_item_and_stack() => RenderThread.Run(() =>
    {
        var creature = new RetailSelection(0x80000001, "Drudge Skulker", SelectionKind.Creature, 0.64f);
        var hurt = creature with { Meter = 0.3f };
        var staff = new RetailSelection(0x50000002, "Ivory Quarter Staff", SelectionKind.ItemWithMana, 0.82f);
        var pyreals = new RetailSelection(0x50000003, "Pyreals", SelectionKind.Stack);
        var port = new FakeToolbarPort();
        using var panel = Open(port, out _);
        panel.Tick();

        // Each state is shown as a fresh toolbar in that state draws it, so a change made anywhere reaches the plaque on the next frame.
        foreach (var selection in new RetailSelection?[] { creature, hurt, staff, pyreals, null })
        {
            port.Selection = selection;
            port.Raise();
            panel.Tick();
            AssertDrawn(Render(p => p.Selection = selection), panel);
        }
    });

    [Fact]
    public void Health_and_mana_are_read_from_the_server_events_by_object_id()
    {
        var meters = new SelectionMeters();

        meters.Receive(0xF7B0, GameEvent(0x01C0, 0x80000001, 0.25f));
        meters.Receive(0xF7B0, GameEvent(0x0264, 0x50000002, 0.82f, success: 1));

        Assert.Equal(0.25f, meters.Health(0x80000001));
        Assert.Null(meters.Health(0x50000002));
        Assert.Equal(0.82f, meters.Mana(0x50000002));
        Assert.Null(meters.Mana(0x80000001));
    }

    /// <summary>A game event as the server sends it: the 0xF7B0 header, the character, a sequence number, the event type, then the body.</summary>
    private static byte[] GameEvent(uint eventType, uint objectId, float value, uint? success = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(0xF7B0u);
            writer.Write(0x50000001u);
            writer.Write(0u);
            writer.Write(eventType);
            writer.Write(objectId);
            writer.Write(value);
            if (success is { } flag) writer.Write(flag);
        }
        return stream.ToArray();
    }

    /// <summary>A port that reads a panel as open only after it was asked to open it, as the native port does for the panels it cannot read yet.</summary>
    private sealed class RequestedOnlyPort : IRetailToolbarPort
    {
        private readonly HashSet<RetailPanel> _open = new();

        public RetailStance Stance => RetailStance.Peace;
        public RetailSelection? Selection => null;
        public List<(RetailPanel Panel, bool Open)> Requests { get; } = new();

        public event Action? Changed;

        public void ToggleStance() { }

        public void UseSelected() { }

        public void ExamineSelected() { }

        public bool IsPanelOpen(RetailPanel panel) => _open.Contains(panel);

        public void SetPanelOpen(RetailPanel panel, bool open)
        {
            Requests.Add((panel, open));
            if (open) _open.Add(panel);
            else _open.Remove(panel);
            Changed?.Invoke();
        }
    }

    /// <summary>A fake of retail's toolbar: it records what the toolbar asks, and applies a panel change at once, as retail does.</summary>
    private sealed class FakeToolbarPort : IRetailToolbarPort
    {
        private readonly HashSet<RetailPanel> _open = new();

        public RetailStance Stance { get; set; } = RetailStance.Peace;
        public int StanceToggles { get; private set; }
        public List<(RetailPanel Panel, bool Open)> PanelRequests { get; } = new();
        public RetailSelection? Selection { get; set; }
        public int UseCount { get; private set; }
        public int ExamineCount { get; private set; }

        public event Action? Changed;

        // Retail's toggle: peace goes to the default stance (melee here) and anything else to peace.
        public void ToggleStance()
        {
            StanceToggles++;
            Stance = Stance == RetailStance.Peace ? RetailStance.Melee : RetailStance.Peace;
        }

        public void UseSelected() => UseCount++;

        public void ExamineSelected() => ExamineCount++;

        public bool IsPanelOpen(RetailPanel panel) => _open.Contains(panel);

        public void SetPanelOpen(RetailPanel panel, bool open)
        {
            PanelRequests.Add((panel, open));
            Open(panel, open);
        }

        /// <summary>Retail opens or closes a panel, by a click, a key or a close box, and tells the toolbar.</summary>
        public void Open(RetailPanel panel, bool open)
        {
            if (open) _open.Add(panel);
            else _open.Remove(panel);
            Raise();
        }

        public void Raise() => Changed?.Invoke();
    }

    /// <summary>A toolbar in the given state, drawn once: what a player sees after a change must equal this.</summary>
    private static byte[] Render(Action<FakeToolbarPort>? arrange = null, Action<RetailToolbar>? arrangeToolbar = null)
    {
        var port = new FakeToolbarPort();
        arrange?.Invoke(port);
        using var panel = Open(port, out var toolbar);
        arrangeToolbar?.Invoke(toolbar);
        panel.Tick();
        return panel.Frame.Pixels.ToArray();
    }

    /// <summary>The panel's frame must show what a fresh toolbar in the same state draws. A mismatch names the pixels that differ.</summary>
    private static void AssertDrawn(byte[] fresh, AvaloniaPanel panel)
    {
        var actual = panel.Frame.Pixels;
        if (fresh.AsSpan().SequenceEqual(actual)) return;
        var stride = panel.Frame.Stride / 4;
        var differing = new List<(int X, int Y)>();
        for (var i = 0; i + 4 <= fresh.Length; i += 4)
            if (!fresh.AsSpan(i, 4).SequenceEqual(actual.AsSpan(i, 4))) differing.Add(((i / 4) % stride, (i / 4) / stride));
        Assert.Fail($"{differing.Count} pixels differ, from ({differing.Min(p => p.X)},{differing.Min(p => p.Y)}) to ({differing.Max(p => p.X)},{differing.Max(p => p.Y)}).");
    }

    private static AvaloniaPanel Open(IRetailToolbarPort port, out RetailToolbar toolbar)
    {
        RetailToolbar? created = null;
        var panel = AvaloniaPanel.Create(() => created = new RetailToolbar(port, () => { }), Width, Height);
        toolbar = created!;
        return panel;
    }

    /// <summary>A press and release at the middle of a medallion, where the player clicks.</summary>
    private static void Click(AvaloniaPanel panel, RetailToolbar toolbar, DerethMedallion button)
    {
        var centre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), toolbar)!.Value;
        panel.PointerDown(centre.X, centre.Y);
        panel.PointerUp(centre.X, centre.Y);
        // The pointer moves on, as a player's does, so the button is not left in its hover art.
        panel.PointerLeave();
    }
}
