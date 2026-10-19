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
        var steps = new List<int>();
        var settings = new ClientSettings(new UiSizeChoice(85, steps.Add), null, Array.Empty<UiWindowChoice>());
        var tabs = new List<SettingsTab>();
        using var panel = AvaloniaPanel.Create(() => new SettingsPanel(settings, SettingsTab.General, tabs.Add), SettingsPanel.WindowWidth, SettingsPanel.WindowHeight);
        panel.Tick();
        var slider = panel.Content.GetVisualDescendants().OfType<DerethSlider>().Single();

        // The track runs from 60 to 150, so its middle is 105: four steps of 5 above 85.
        PressCentre(panel, slider);
        Assert.Equal(new[] { 4 }, steps);

        PressCentre(panel, TabLabel(panel, "World"));
        panel.Tick();
        Assert.Equal(new[] { SettingsTab.World }, tabs);
        Assert.False(slider.IsEffectivelyVisible);

        PressCentre(panel, TabLabel(panel, "General"));
        panel.Tick();
        Assert.True(slider.IsEffectivelyVisible);
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
        foreach (var enabled in new[] { true, false })
        {
            var settings = new ClientSettings(null, new UpdateChoice("Version 0.8.1", enabled, () => checks++), Array.Empty<UiWindowChoice>());
            using var panel = AvaloniaPanel.Create(() => new SettingsPanel(settings), SettingsPanel.WindowWidth, SettingsPanel.WindowHeight);
            panel.Tick();
            PressCentre(panel, panel.Content.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Check for updates"));
        }
        Assert.Equal(1, checks);
    });

    private static void PressCentre(AvaloniaPanel panel, Control control)
    {
        var centre = Avalonia.VisualExtensions.TranslatePoint(control, new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), panel.Content)!.Value;
        panel.PointerDown(centre.X, centre.Y);
        panel.PointerUp(centre.X, centre.Y);
    }

    private static TextBlock TabLabel(AvaloniaPanel panel, string tab) =>
        panel.Content.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == tab);
}
