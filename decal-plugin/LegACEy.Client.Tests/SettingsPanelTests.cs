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
    public void Each_option_on_Dereth_calls_its_change_with_its_value() => RenderThread.Run(() =>
    {
        var changes = new List<object>();
        var settings = new ClientSettings(null, null, Windows("Toolbar", "Chat", "Vitals"))
        {
            ShortcutRow2 = new OptionChoice<bool>(true, value => changes.Add(value)),
            ChatTextSize = new OptionChoice<int>(120, value => changes.Add(value)),
            Vitals = new OptionChoice<VitalsLayout>(VitalsLayout.Stacked, value => changes.Add(value)),
        };
        using (var panel = Tab(settings))
        {
            PressCentre(panel, panel.Content.GetVisualDescendants().OfType<DerethToggle>().Single());
            PressCentre(panel, panel.Content.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "+"));
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
            false, 130, VitalsLayout.SideBySide,
            new RadarOptions(Ground: false), new RadarOptions(NorthUp: true), new RadarOptions(Rings: true), new RadarOptions(Zoom: 150),
        }, changes);
    });

    [Fact]
    public void A_change_made_in_a_window_redraws_the_open_panel() => RenderThread.Run(() =>
    {
        var settings = new ClientSettings(null, null, Windows("Chat", "Radar"))
        {
            ChatTextSize = new OptionChoice<int>(120, _ => { }),
            Radar = new OptionChoice<RadarOptions>(new RadarOptions(), _ => { }),
        };
        using var panel = Tab(settings);
        var ground = panel.Content.GetVisualDescendants().OfType<DerethToggle>().First();
        Assert.True(ground.On);

        // The window's own menu moves both values; the open panel draws them as they now are.
        settings.ChatTextSize = new OptionChoice<int>(140, _ => { });
        settings.Radar = new OptionChoice<RadarOptions>(new RadarOptions(Ground: false), _ => { });

        TextOf(panel, "140%");
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

    private static UiWindowChoice[] Windows(params string[] titles) => titles.Select(title => new UiWindowChoice(title, () => true, _ => { })).ToArray();

    private static AvaloniaPanel Tab(ClientSettings settings)
    {
        var panel = AvaloniaPanel.Create(() => new SettingsPanel(settings, SettingsTab.Windows), SettingsPanel.WindowWidth, SettingsPanel.WindowHeight);
        panel.Tick();
        return panel;
    }

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
