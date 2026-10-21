using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using LegACEy.Client.Demo;
using LegACEy.Client.PanelHost;
using LegACEy.Client.Themes;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class SettingsPanelTests
{
    [Fact]
    public void The_settings_window_switches_tabs_steps_the_UI_size_with_its_slider_and_reports_the_tab() => RenderThread.Run(() =>
    {
        var sizes = new List<int>();
        var settings = new ClientSettings(new UiSizeChoice(85, sizes.Add), null, Array.Empty<UiWindowChoice>());
        var tabs = new List<SettingsTab>();
        using var panel = AvaloniaPanel.Create(() => new SettingsPanel(settings, SettingsTab.General, tabs.Add), SettingsPanel.WindowWidth, SettingsPanel.WindowHeight);
        panel.Tick();
        var slider = panel.Content.GetVisualDescendants().OfType<DerethSlider>().Single();
        var readout = TextOf(panel, "85%");

        // The track runs from 60 to 150, so its middle is 105. Held there, the readout follows the thumb; the size is set only on release.
        var middle = CentreOf(panel, slider);
        panel.PointerDown(middle.X, middle.Y);
        panel.Tick();
        Assert.Equal("105%", readout.Text);
        Assert.Empty(sizes);
        panel.PointerUp(middle.X, middle.Y);
        Assert.Equal(new[] { 105 }, sizes);

        PressCentre(panel, TextOf(panel, "World"));
        panel.Tick();
        Assert.Equal(new[] { SettingsTab.World }, tabs);
        Assert.False(slider.IsEffectivelyVisible);

        PressCentre(panel, TextOf(panel, "General"));
        PressCentre(panel, TextOf(panel, "General"));
        panel.Tick();
        Assert.True(slider.IsEffectivelyVisible);
        Assert.Equal(new[] { SettingsTab.World, SettingsTab.General }, tabs);
    });

    [Fact]
    public void A_window_switches_between_Dereth_and_retail_and_on_retail_hides_its_options() => RenderThread.Run(() =>
    {
        var custom = true;
        var settings = new ClientSettings(null, null, new[] { new UiWindowChoice("Vitals", () => custom, value => custom = value) })
        {
            Vitals = new OptionChoice<VitalsLayout>(VitalsLayout.Stacked, _ => { })
        };
        using var panel = Tab(settings);
        Assert.True(TextOf(panel, "Layout").IsEffectivelyVisible);

        PressCentre(panel, TextOf(panel, "Retail"));
        panel.Tick();
        Assert.False(custom);
        Assert.False(TextOf(panel, "Layout").IsEffectivelyVisible);

        PressCentre(panel, TextOf(panel, "Dereth"));
        panel.Tick();
        Assert.True(custom);
        Assert.True(TextOf(panel, "Layout").IsEffectivelyVisible);
    });

    [Fact]
    public void All_windows_switches_every_window_and_shows_neither_when_they_differ() => RenderThread.Run(() =>
    {
        bool inventory = true, chat = true;
        var settings = new ClientSettings(null, null, new[]
        {
            new UiWindowChoice("Inventory", () => inventory, value => inventory = value),
            new UiWindowChoice("Chat", () => chat, value => chat = value)
        });
        using var panel = Tab(settings);
        TextBlock Label(DerethSegmented segmented, string text) =>
            segmented.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == text);
        // All windows, then a switch per window.
        var switches = panel.Content.GetVisualDescendants().OfType<DerethSegmented>()
            .Where(segmented => segmented.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "Retail")).ToList();
        var all = switches[0];

        PressCentre(panel, Label(all, "Retail"));
        panel.Tick();
        Assert.False(inventory);
        Assert.False(chat);
        Assert.Equal(1, switches[2].Selected);

        PressCentre(panel, Label(switches[1], "Dereth"));
        panel.Tick();
        Assert.True(inventory);
        Assert.Equal(-1, all.Selected);

        PressCentre(panel, Label(all, "Dereth"));
        panel.Tick();
        Assert.True(chat);
        Assert.Equal(0, all.Selected);
    });

    [Fact]
    public void A_window_on_retail_mutes_its_title_until_it_is_back_on_Dereth() => RenderThread.Run(() =>
    {
        var custom = true;
        var settings = new ClientSettings(null, null, new[] { new UiWindowChoice("Vitals", () => custom, value => custom = value) });
        using var panel = Tab(settings);
        var title = TextOf(panel, "Vitals");
        Assert.Same(DerethPalette.CreamBrush, title.Foreground);

        PressCentre(panel, TextOf(panel, "Retail"));
        panel.Tick();
        Assert.Same(DerethPalette.MutedBrush, title.Foreground);

        PressCentre(panel, TextOf(panel, "Dereth"));
        panel.Tick();
        Assert.Same(DerethPalette.CreamBrush, title.Foreground);
    });

    [Fact]
    public void Each_option_on_Dereth_calls_its_change_with_its_value() => RenderThread.Run(() =>
    {
        var changes = new List<object>();
        var settings = new ClientSettings(null, null, Windows("Toolbar", "Chat", "Vitals"))
        {
            ShortcutRow2 = new OptionChoice<bool>(true, value => changes.Add(value)),
            ChatTextSize = new OptionChoice<int>(120, value => changes.Add(value)),
            ChatFade = new OptionChoice<int>(15, value => changes.Add(value)),
            Vitals = new OptionChoice<VitalsLayout>(VitalsLayout.Stacked, value => changes.Add(value)),
        };
        using (var panel = Tab(settings))
        {
            PressCentre(panel, panel.Content.GetVisualDescendants().OfType<DerethToggle>().Single());
            PressCentre(panel, panel.Content.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "+"));
            var fadeRow = TextOf(panel, "Fade when idle").GetVisualAncestors().OfType<DockPanel>().First();
            Assert.Contains("This character", fadeRow.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text));
            PressCentre(panel, TextOf(panel, "Never"));
            PressCentre(panel, TextOf(panel, "30"));
            PressCentre(panel, TextOf(panel, "Side by side"));
        }

        // The radar's options come from its own panel, so the three cards above stay within the window's height.
        var radar = new ClientSettings(null, null, Windows("Radar"))
        {
            Radar = new OptionChoice<RadarOptions>(new RadarOptions(), value => changes.Add(value)),
        };
        using (var panel = Tab(radar))
        {
            var toggles = panel.Content.GetVisualDescendants().OfType<DerethToggle>().ToList();
            PressCentre(panel, toggles[0]);
            PressCentre(panel, toggles[1]);
            PressCentre(panel, toggles[2]);
            PressCentre(panel, TextOf(panel, "150"));
        }

        Assert.Equal(new object[]
        {
            false, 130, 0, 30, VitalsLayout.SideBySide,
            new RadarOptions(Map: false), new RadarOptions(NorthUp: true), new RadarOptions(Rings: true), new RadarOptions(Zoom: 150),
        }, changes);
    });

    [Fact]
    public void A_change_made_in_a_window_redraws_the_open_panel() => RenderThread.Run(() =>
    {
        var settings = new ClientSettings(null, null, Windows("Chat", "Radar"))
        {
            ChatTextSize = new OptionChoice<int>(120, _ => { }),
            ChatFade = new OptionChoice<int>(15, _ => { }),
            Radar = new OptionChoice<RadarOptions>(new RadarOptions(), _ => { }),
        };
        using var panel = Tab(settings);
        var ground = panel.Content.GetVisualDescendants().OfType<DerethToggle>().First();
        Assert.True(ground.On);

        // The window's own menu moves both values; the open panel draws them as they now are.
        settings.ChatTextSize = new OptionChoice<int>(140, _ => { });
        settings.ChatFade = new OptionChoice<int>(60, _ => { });
        settings.Radar = new OptionChoice<RadarOptions>(new RadarOptions(Map: false), _ => { });

        TextOf(panel, "140%");
        Assert.Equal(5, TextOf(panel, "60 s").GetVisualAncestors().OfType<DerethSegmented>().First().Selected);
        Assert.False(ground.On);
    });

    [Fact]
    public void The_update_button_checks_unless_updates_are_off() => RenderThread.Run(() =>
    {
        var checks = 0;
        foreach (var (enabled, label) in new[] { (true, "Version 0.8.1"), (false, "Version 0.8.1 (updates are off: a store install)") })
        {
            var settings = new ClientSettings(null, new UpdateChoice(label, enabled, () => checks++), Array.Empty<UiWindowChoice>());
            using var panel = AvaloniaPanel.Create(() => new SettingsPanel(settings), SettingsPanel.WindowWidth, SettingsPanel.WindowHeight);
            panel.Tick();
            Assert.Contains(panel.Content.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == label);
            PressCentre(panel, panel.Content.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Check for updates"));
        }
        Assert.Equal(1, checks);
    });

    [Fact]
    public void The_settings_window_renders_to_a_file_when_asked() => RenderThread.Run(() =>
    {
        // A look at the window in its Dereth frame, for checking borders and alignment by eye. Set LEGACEY_SETTINGS_RENDER to a folder.
        var folder = Environment.GetEnvironmentVariable("LEGACEY_SETTINGS_RENDER");
        if (folder == null) return;
        var settings = new ClientSettings(new UiSizeChoice(100, _ => { }), null, Windows("Toolbar", "Chat", "Vitals"))
        {
            ChatTextSize = new OptionChoice<int>(130, _ => { }),
            ChatFade = new OptionChoice<int>(15, _ => { }),
            ChatActiveOpacity = new OptionChoice<int>(60, _ => { }),
            ChatInactiveOpacity = new OptionChoice<int>(0, _ => { }),
        };
        foreach (var tab in new[] { SettingsTab.General, SettingsTab.Windows })
        {
            const int width = SettingsPanel.WindowWidth, height = SettingsPanel.WindowHeight;
            using var panel = AvaloniaPanel.Create(() => new DerethWindow("Settings", null, new SettingsPanel(settings, tab)), width, height);
            panel.Tick();
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(width, height));
            bitmap.Render(panel.Content);
            bitmap.Save(System.IO.Path.Combine(folder, $"settings-{tab}.png"));
        }
    });

    private static UiWindowChoice[] Windows(params string[] titles) => titles.Select(title => new UiWindowChoice(title, () => true, _ => { })).ToArray();

    private static AvaloniaPanel Tab(ClientSettings settings)
    {
        var panel = AvaloniaPanel.Create(() => new SettingsPanel(settings, SettingsTab.Windows), SettingsPanel.WindowWidth, SettingsPanel.WindowHeight);
        panel.Tick();
        return panel;
    }

    [Fact]
    public void The_world_tab_s_switch_and_strength_call_the_water_change() => RenderThread.Run(() =>
    {
        var calls = new List<(bool On, int Percent)>();
        var settings = new ClientSettings(null, null, Array.Empty<UiWindowChoice>(), new WaterOption(true, 100, (on, percent) => calls.Add((on, percent))));
        using var panel = AvaloniaPanel.Create(() => new SettingsPanel(settings, SettingsTab.World), SettingsPanel.WindowWidth, SettingsPanel.WindowHeight);
        panel.Tick();

        PressCentre(panel, panel.Content.GetVisualDescendants().OfType<DerethToggle>().Single());
        Assert.Equal(new[] { (false, 100) }, calls);

        // Three quarters along the track is 150 in steps of 5; the change comes on release.
        var slider = panel.Content.GetVisualDescendants().OfType<DerethSlider>().Single();
        var point = Avalonia.VisualExtensions.TranslatePoint(slider, new Point(slider.Bounds.Width * 0.75, slider.Bounds.Height / 2), panel.Content)!.Value;
        panel.PointerDown(point.X, point.Y);
        panel.PointerUp(point.X, point.Y);
        Assert.Equal(new[] { (false, 100), (true, 150) }, calls);
    });

    [Fact]
    public void A_water_change_moves_an_open_World_tab() => RenderThread.Run(() =>
    {
        var settings = new ClientSettings(null, null, Array.Empty<UiWindowChoice>(), new WaterOption(true, 100, (_, _) => { }));
        using var panel = AvaloniaPanel.Create(() => new SettingsPanel(settings, SettingsTab.World), SettingsPanel.WindowWidth, SettingsPanel.WindowHeight);
        panel.Tick();

        settings.Water = new WaterOption(false, 150, (_, _) => { });

        Assert.False(panel.Content.GetVisualDescendants().OfType<DerethToggle>().Single().On);
        Assert.Equal(150, panel.Content.GetVisualDescendants().OfType<DerethSlider>().Single().Value);
        Assert.Equal("150%", TextOf(panel, "150%").Text);
    });

    [Fact]
    public void The_lock_switch_calls_the_lock_change_with_the_new_state() => RenderThread.Run(() =>
    {
        var calls = new List<bool>();
        var settings = new ClientSettings(null, null, Array.Empty<UiWindowChoice>(), null, new LockUiChoice(false, true, calls.Add));
        using var panel = AvaloniaPanel.Create(() => new SettingsPanel(settings), SettingsPanel.WindowWidth, SettingsPanel.WindowHeight);
        panel.Tick();

        Assert.Equal("This character", TextOf(panel, "This character").Text);
        PressCentre(panel, panel.Content.GetVisualDescendants().OfType<DerethToggle>().Single());

        Assert.Equal(new[] { true }, calls);
    });

    [Fact]
    public void A_disabled_lock_switch_makes_no_change() => RenderThread.Run(() =>
    {
        var calls = new List<bool>();
        var settings = new ClientSettings(null, null, Array.Empty<UiWindowChoice>(), null, new LockUiChoice(false, false, calls.Add));
        using var panel = AvaloniaPanel.Create(() => new SettingsPanel(settings), SettingsPanel.WindowWidth, SettingsPanel.WindowHeight);
        panel.Tick();

        PressCentre(panel, panel.Content.GetVisualDescendants().OfType<DerethToggle>().Single());

        Assert.Empty(calls);
        Assert.False(panel.Content.GetVisualDescendants().OfType<DerethToggle>().Single().On);
    });

    [Fact]
    public void A_lock_change_from_outside_moves_the_open_lock_switch() => RenderThread.Run(() =>
    {
        var settings = new ClientSettings(null, null, Array.Empty<UiWindowChoice>(), null, new LockUiChoice(false, true, _ => { }));
        using var panel = AvaloniaPanel.Create(() => new SettingsPanel(settings), SettingsPanel.WindowWidth, SettingsPanel.WindowHeight);
        panel.Tick();

        settings.LockUi = new LockUiChoice(true, true, _ => { });

        Assert.True(panel.Content.GetVisualDescendants().OfType<DerethToggle>().Single().On);
    });

    [Fact]
    public void Reset_window_positions_asks_first_and_calls_the_reset_once_confirmed() => RenderThread.Run(() =>
    {
        var resets = 0;
        var settings = new ClientSettings(null, null, Array.Empty<UiWindowChoice>()) { ResetPositions = () => resets++ };
        using var panel = AvaloniaPanel.Create(() => new SettingsPanel(settings), SettingsPanel.WindowWidth, SettingsPanel.WindowHeight);
        panel.Tick();
        var question = TextOf(panel, "Put every window back where it first opened?");
        Assert.True(TextOf(panel, "This character").IsEffectivelyVisible);
        Assert.False(question.IsEffectivelyVisible);

        PressCentre(panel, ButtonNamed(panel, "Reset window positions"));
        panel.Tick();
        Assert.True(question.IsEffectivelyVisible);
        Assert.Equal(0, resets);

        PressCentre(panel, ButtonNamed(panel, "Cancel"));
        panel.Tick();
        Assert.False(question.IsEffectivelyVisible);
        Assert.Equal(0, resets);

        // The row shown again needs a second layout pass before its Reset has its place in the headless panel.
        PressCentre(panel, ButtonNamed(panel, "Reset window positions"));
        panel.Tick();
        panel.Tick();
        PressCentre(panel, ButtonNamed(panel, "Reset"));
        Assert.Equal(1, resets);
        Assert.False(question.IsEffectivelyVisible);
    });

    private static Button ButtonNamed(AvaloniaPanel panel, string text) =>
        panel.Content.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == text);

    private static Point CentreOf(AvaloniaPanel panel, Control control) =>
        Avalonia.VisualExtensions.TranslatePoint(control, new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), panel.Content)!.Value;

    private static void PressCentre(AvaloniaPanel panel, Control control)
    {
        var centre = CentreOf(panel, control);
        panel.PointerDown(centre.X, centre.Y);
        panel.PointerUp(centre.X, centre.Y);
    }

    private static TextBlock TextOf(AvaloniaPanel panel, string text) =>
        panel.Content.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == text);
}
