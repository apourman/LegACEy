using System;
using System.Drawing;
using System.IO;
using Avalonia.Controls;
using LegACEy.Client.Demo;
using LegACEy.Client.PanelHost;
using LegACEy.Client.Themes;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class DerethAttackBarWindowTests
{
    // The mockup's layout in window DIPs: the groove's left end and length, and the plaques' and gems' centres.
    private const double GrooveLeft = 46, GrooveWidth = 168, GrooveY = 19;
    private const double PlaqueX = 292;
    private static readonly double[] PlaqueY = { 15, 29, 43 };
    private static readonly double[] GemX = { 16, 90, 164 };
    private const double GemY = 42;
    // Just inside the groove's click area, which runs 4 DIPs past each end of the bar: a click there is past the bar's end.
    private const double GrooveHitLeft = 44, GrooveHitRight = 216;

    /// <summary>A panel holding the window at its design size, at 1x, with the window's content as its root.</summary>
    private static AvaloniaPanel Panel(DerethAttackBarWindow view, double scale = 1) =>
        AvaloniaPanel.Create(() => view, (int)Math.Round(DerethAttackBarArt.DesignWidth * scale), (int)Math.Round(DerethAttackBarArt.DesignHeight * scale), scale);

    /// <summary>A player's click at a point of the window, in DIPs.</summary>
    private static void Click(AvaloniaPanel panel, double x, double y)
    {
        panel.Tick();
        panel.PointerDown(x, y);
        panel.PointerUp(x, y);
        panel.Tick();
    }

    /// <summary>The fraction of the groove at window x, as a click there sets it.</summary>
    private static double FractionAt(double fraction) => GrooveLeft + GrooveWidth * fraction;

    [Fact]
    public void A_click_on_the_bar_sets_the_set_point_at_that_fraction_and_clamps_it() => RenderThread.Run(() =>
    {
        var port = new FakeAttackBarPort { SetPoint = 0.2 };
        var view = new DerethAttackBarWindow(port, () => { });
        using var panel = Panel(view);

        Click(panel, FractionAt(0.75), GrooveY);
        Assert.Equal(0.75, port.SetPoint, 3);
        Assert.Equal(0.75, view.Face.SetPoint, 3);
        Assert.Equal("Power 75%", view.BarTooltip);

        // The groove's hit area runs a little past either end of the bar, and the set point stops at the bar's ends, 0 to 1 as retail clamps it.
        Click(panel, GrooveHitRight, GrooveY);
        Assert.Equal(1, port.SetPoint, 3);
        Click(panel, GrooveHitLeft, GrooveY);
        Assert.Equal(0, port.SetPoint, 3);
    });

    [Fact]
    public void A_click_on_a_plaque_sets_the_height_and_lights_that_plaque() => RenderThread.Run(() =>
    {
        var port = new FakeAttackBarPort { Height = AttackHeight.Medium };
        var view = new DerethAttackBarWindow(port, () => { });
        using var panel = Panel(view);
        Assert.Equal(1, view.Face.LitPlaque);

        Click(panel, PlaqueX, PlaqueY[2]);
        Assert.Equal(AttackHeight.Low, port.Height);
        Assert.Equal(2, view.Face.LitPlaque);

        Click(panel, PlaqueX, PlaqueY[0]);
        Assert.Equal(AttackHeight.High, port.Height);
        Assert.Equal(0, view.Face.LitPlaque);
    });

    [Fact]
    public void A_click_on_a_toggle_flips_its_option_and_its_gem_follows() => RenderThread.Run(() =>
    {
        var port = new FakeAttackBarPort { AutoRepeatAttack = false, AutoTarget = true, ViewCombatTarget = false };
        var view = new DerethAttackBarWindow(port, () => { });
        using var panel = Panel(view);

        Click(panel, GemX[0], GemY);
        Assert.True(port.AutoRepeatAttack);
        Assert.True(view.Face.RepeatAttacks);

        Click(panel, GemX[1], GemY);
        Assert.False(port.AutoTarget);
        Assert.False(view.Face.AutoTarget);

        Click(panel, GemX[2], GemY);
        Assert.True(port.ViewCombatTarget);
        Assert.True(view.Face.KeepInView);
    });

    [Fact]
    public void The_charge_fill_follows_the_port_and_empties_when_the_attack_ends() => RenderThread.Run(() =>
    {
        var port = new FakeAttackBarPort();
        var view = new DerethAttackBarWindow(port, () => { });

        port.Charge = 0.6;
        port.Publish();
        Assert.Equal(0.6, view.Face.Charge, 3);

        port.Charge = 0;
        port.Publish();
        Assert.Equal(0, view.Face.Charge, 3);
    });

    [Fact]
    public void Melee_reads_Power_and_missile_reads_Accuracy() => RenderThread.Run(() =>
    {
        var port = new FakeAttackBarPort { Stance = RetailStance.Melee, SetPoint = 0.55 };
        var view = new DerethAttackBarWindow(port, () => { });
        Assert.False(view.Face.Accuracy);
        Assert.Equal("Power 55%", view.BarTooltip);

        port.Stance = RetailStance.Missile;
        port.Publish();
        Assert.True(view.Face.Accuracy);
        Assert.Equal("Accuracy 55%", view.BarTooltip);
    });

    [Theory]
    [InlineData(RetailStance.Peace)]
    [InlineData(RetailStance.Magic)]
    public void Peace_and_magic_hide_the_window_and_melee_and_missile_show_it(RetailStance stance) => RenderThread.Run(() =>
    {
        var port = new FakeAttackBarPort { Stance = RetailStance.Melee };
        var view = new DerethAttackBarWindow(port, () => { });
        Assert.True(view.Shown);

        port.Stance = stance;
        port.Publish();
        Assert.False(view.Shown);

        port.Stance = RetailStance.Missile;
        port.Publish();
        Assert.True(view.Shown);
    });

    [Fact]
    public void A_change_made_outside_the_window_shows_in_it() => RenderThread.Run(() =>
    {
        // A retail key sets the height, the power and an option, and the stance changes to missile; the game says so, and the window follows.
        var port = new FakeAttackBarPort { Height = AttackHeight.Medium, SetPoint = 0.55, AutoTarget = true };
        var view = new DerethAttackBarWindow(port, () => { });

        port.Height = AttackHeight.Low;
        port.SetPoint = 0.2;
        port.AutoTarget = false;
        port.Publish();

        Assert.Equal(2, view.Face.LitPlaque);
        Assert.Equal(0.2, view.Face.SetPoint, 3);
        Assert.Equal("Power 20%", view.BarTooltip);
        Assert.False(view.Face.AutoTarget);

        port.Stance = RetailStance.Missile;
        port.Publish();

        Assert.True(view.Face.Accuracy);
        Assert.Equal("Accuracy 20%", view.BarTooltip);
    });

    [Fact]
    public void A_still_window_draws_no_new_frame_and_a_change_draws_one() => RenderThread.Run(() =>
    {
        var port = new FakeAttackBarPort();
        var view = new DerethAttackBarWindow(port, () => { });
        using var panel = Panel(view);
        panel.Tick();
        panel.Tick();

        port.Publish();
        Assert.False(panel.Tick());

        port.SetPoint = 0.3;
        port.Publish();
        Assert.True(panel.Tick());
    });

    [Fact]
    public void A_press_on_the_plate_moves_the_window_unless_retail_s_UI_is_locked() => RenderThread.Run(() =>
    {
        var moves = 0;
        var port = new FakeAttackBarPort();
        var view = new DerethAttackBarWindow(port, () => moves++);
        using var panel = Panel(view);
        panel.Tick();

        // The top band of the plate, away from the bar and the plaques, is the window's to drag.
        panel.PointerDown(150, 3);
        panel.PointerUp(150, 3);
        Assert.Equal(1, moves);

        port.UiLocked = true;
        panel.PointerDown(150, 3);
        panel.PointerUp(150, 3);
        Assert.Equal(1, moves);
    });

    [Fact]
    public void An_edge_drag_gives_the_bar_more_width_or_height_on_its_own_unless_the_UI_is_locked() => RenderThread.Run(() =>
    {
        var port = new FakeAttackBarPort { Stance = RetailStance.Melee };
        var view = new DerethAttackBarWindow(port, () => { });
        using var panel = Panel(view);
        panel.Tick();
        panel.PointerMove(150, 3);
        panel.Tick();
        Assert.True(view.CornersVisible);

        var windows = new WindowManager(new Size(1600, 1000), new MemoryWindowPositionStore(), "Thistledown", "Alice");
        var window = windows.Open(DerethAttackBarWindow.DefinitionOf(() => port.UiLocked), new Point(100, 100));
        Drag(windows, new Point(window.Bounds.Right - 2, window.Bounds.Top + 29), 160, 0);
        Assert.Equal(new Size(480, 58), window.Size);
        Drag(windows, new Point(window.Bounds.Left + 200, window.Bounds.Bottom - 2), 0, 40);
        Assert.Equal(new Size(480, 98), window.Size);
        Drag(windows, new Point(window.Bounds.Right - 2, window.Bounds.Bottom - 2), 2000, 2000);
        Assert.Equal(new Size(960, 150), window.Size);
        Drag(windows, new Point(window.Bounds.Right - 2, window.Bounds.Bottom - 2), -2000, -2000);
        Assert.Equal(new Size(280, 58), window.Size);

        port.UiLocked = true;
        Drag(windows, new Point(window.Bounds.Right - 2, window.Bounds.Top + 29), 160, 0);
        Assert.Equal(new Size(280, 58), window.Size);
        panel.PointerMove(400, 400);
        panel.PointerMove(150, 3);
        panel.Tick();
        Assert.False(view.CornersVisible);
    });

    [Fact]
    public void A_wider_bar_is_longer_and_its_plaques_follow_the_right_edge_with_text_at_its_size() => RenderThread.Run(() =>
    {
        var port = new FakeAttackBarPort();
        var view = new DerethAttackBarWindow(port, () => { });
        using var panel = AvaloniaPanel.Create(() => view, 480, 98, 1);

        // The groove is 160 DIPs longer, so its middle is 80 further right.
        Click(panel, GrooveLeft + (GrooveWidth + 160) / 2, 31);
        Assert.Equal(0.5, port.SetPoint, 2);
        // The plaques moved 160 right with the edge; the low one is lower in the taller window.
        Click(panel, PlaqueX + 160, 72);
        Assert.Equal(AttackHeight.Low, port.Height);
        // The gems spread: the third sits a further 2 × 160 / 3 DIPs right, and the row is lower.
        Click(panel, GemX[2] + 2 * 160 / 3.0, GemY + 30);
        Assert.True(port.ViewCombatTarget);
    });

    /// <summary>A player's drag from <paramref name="from"/> by (<paramref name="dx"/>, <paramref name="dy"/>).</summary>
    private static void Drag(WindowManager windows, Point from, int dx, int dy)
    {
        windows.Press(from);
        windows.Move(new Point(from.X + dx, from.Y + dy));
        windows.Release();
    }

    // Looked at by a person, not asserted: LEGACEY_ATTACK_RENDER names the folder the PNGs are written to.
    [Fact]
    public void The_attack_bar_renders_to_pngs_when_asked() => RenderThread.Run(() =>
    {
        var folder = Environment.GetEnvironmentVariable("LEGACEY_ATTACK_RENDER");
        if (folder == null) return;
        Directory.CreateDirectory(folder);
        foreach (var scale in new[] { 1.0, 2.0 })
            foreach (var (name, stance) in new[] { ("melee", RetailStance.Melee), ("missile", RetailStance.Missile) })
            {
                // Mid-charge: the attack is building, short of the set point, with the medium height lit and two options on, as the mockup.
                var port = new FakeAttackBarPort
                {
                    Stance = stance, SetPoint = 0.75, Charge = 0.45, Height = AttackHeight.Medium, AutoRepeatAttack = true, AutoTarget = true
                };
                Render(Path.Combine(folder, $"{name}-{scale:0}x.png"), port, scale);
            }
        // Stretched: wider, taller, and both.
        foreach (var (width, height) in new[] { (480, 58), (320, 98), (560, 120) })
        {
            var port = new FakeAttackBarPort { SetPoint = 0.75, Charge = 0.45, Height = AttackHeight.Medium, AutoRepeatAttack = true, AutoTarget = true };
            var view = new DerethAttackBarWindow(port, () => { });
            using var panel = AvaloniaPanel.Create(() => view, width, height, 1);
            panel.Tick();
            PanelPng.Save(panel, Path.Combine(folder, $"stretched-{width}x{height}.png"));
        }
    });

    private static void Render(string path, FakeAttackBarPort port, double scale)
    {
        var view = new DerethAttackBarWindow(port, () => { });
        using var panel = Panel(view, scale);
        panel.Tick();
        PanelPng.Save(panel, path);
    }

    /// <summary>The game as the attack bar sees it, changed by the test as a player's clicks and the game's own changes would.</summary>
    private sealed class FakeAttackBarPort : IAttackBarPort
    {
        public RetailStance Stance { get; set; } = RetailStance.Melee;
        public double SetPoint { get; set; } = 0.55;
        public AttackHeight Height { get; set; } = AttackHeight.Medium;
        public double Charge { get; set; }
        public bool AutoRepeatAttack { get; set; }
        public bool AutoTarget { get; set; }
        public bool ViewCombatTarget { get; set; }
        public bool UiLocked { get; set; }

        public event EventHandler? Changed;

        /// <summary>Tells the window the game changed, as the port does when a value moves from outside.</summary>
        public void Publish() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
