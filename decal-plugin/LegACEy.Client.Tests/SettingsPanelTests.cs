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
    public void The_windows_tab_shows_each_window_s_choice_and_switches_it() => RenderThread.Run(() =>
    {
        var chatCustom = true;
        var settings = new ClientSettings(null, null, new[] { new UiWindowChoice("Chat", () => chatCustom, custom => chatCustom = custom) });
        using var panel = AvaloniaPanel.Create(() => new SettingsPanel(settings, SettingsTab.Windows), SettingsPanel.WindowWidth, SettingsPanel.WindowHeight);
        panel.Tick();
        Button Switch() => panel.Content.GetVisualDescendants().OfType<Button>().Single();
        Assert.Equal("Custom", Switch().Content);

        PressCentre(panel, Switch());
        panel.Tick();

        Assert.False(chatCustom);
        Assert.Equal("Native", Switch().Content);
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

        PressCentre(panel, panel.Content.GetVisualDescendants().OfType<DerethToggle>().Single());

        Assert.Equal(new[] { true }, calls);
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
