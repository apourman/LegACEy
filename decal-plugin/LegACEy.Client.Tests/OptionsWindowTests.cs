using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.PanelHost;
using Avalonia;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class OptionsWindowTests
{
    private const int AutoTarget = 0x0D, ShowHelm = 0x2F, ToggleRun = 0x0A;

    [Fact]
    public void Switches_are_held_until_apply_as_in_retail() => RenderThread.Run(() =>
    {
        var game = new FakeGame();
        OptionsWindow view = null!;
        using var panel = AvaloniaPanel.Create(() => (view = new OptionsWindow(game, art: null)).Window, OptionsWindow.Width, OptionsWindow.Height);
        Assert.False(view.CanApply);

        view.Toggle(AutoTarget);
        Assert.True(view.Shown(AutoTarget));
        Assert.False(game.Options[AutoTarget]);
        Assert.True(view.CanApply);

        // Flipped back, nothing is left to apply.
        view.Toggle(AutoTarget);
        Assert.Empty(view.Held);
        Assert.False(view.CanApply);

        view.Toggle(AutoTarget);
        view.Toggle(ShowHelm);
        view.Apply();
        Assert.Equal(new Dictionary<int, bool> { [AutoTarget] = true, [ShowHelm] = false }, game.Applied.Single());
        Assert.Empty(view.Held);

        view.Toggle(ToggleRun);
        view.Reset();
        Assert.Empty(view.Held);
        Assert.Single(game.Applied);

        // Retail comes to the held value by itself (a key, the server): nothing is left to apply.
        view.Toggle(ToggleRun);
        game.Options[ToggleRun] = true;
        view.Refresh();
        Assert.False(view.CanApply);
    });

    [Fact]
    public void Defaults_hold_only_the_options_that_differ_and_closing_drops_them() => RenderThread.Run(() =>
    {
        var game = new FakeGame();
        OptionsWindow view = null!;
        using var panel = AvaloniaPanel.Create(() => (view = new OptionsWindow(game, art: null)).Window, OptionsWindow.Width, OptionsWindow.Height);
        view.Defaults();
        // AutoTarget, ToggleRun and ShowHelm default on; ShowHelm already is, so only the other two are held.
        Assert.Equal(new Dictionary<int, bool> { [AutoTarget] = true, [ToggleRun] = true }, view.Held);

        view.Hidden();
        Assert.Empty(view.Held);
        Assert.Empty(game.Applied);
    });

    [Fact]
    public void Config_changes_are_held_until_apply_and_defaults_hold_retails() => RenderThread.Run(() =>
    {
        var game = new FakeGame();
        OptionsWindow view = null!;
        using var panel = AvaloniaPanel.Create(() => (view = new OptionsWindow(game, art: null)).Window, OptionsWindow.Width, OptionsWindow.Height);
        view.ShowTab(2);

        view.HoldConfig(FullScreen, 0);
        view.HoldConfig(Stiffness, 0.8);
        view.HoldConfig(FontSize, 3);
        Assert.True(view.CanApplyConfig);
        // The Character tab's Apply has nothing of the Config tab's.
        Assert.False(view.CanApply);
        Assert.Equal(1, game.Preferences[FullScreen]);

        // A slider moved back to within a step of retail's value is no change.
        view.HoldConfig(Stiffness, 0.452);
        view.ApplyConfig();
        var applied = game.Changes.ToDictionary(change => change.Name);
        Assert.Equal(new[] { FullScreen, FontSize }, applied.Keys.OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(new PreferenceChange(PreferenceKind.Choice, FontSize, 3, 1), applied[FontSize]);
        Assert.Equal(0, game.Preferences[FullScreen]);
        Assert.False(view.CanApplyConfig);

        // Defaults holds only what differs from retail's defaults; closing drops it.
        view.DefaultsConfig();
        Assert.Equal(new Dictionary<string, double> { [FullScreen] = 1, [FontSize] = 1 }, view.HeldConfig);
        view.Hidden();
        Assert.Empty(view.HeldConfig);
    });

    [Fact]
    public void An_open_drop_down_lies_over_the_rows_below_it() => RenderThread.Run(() =>
    {
        OptionsWindow view = null!;
        using var panel = AvaloniaPanel.Create(() => (view = new OptionsWindow(new FakeGame(), art: null)).Window, OptionsWindow.Width, OptionsWindow.Height);
        view.ShowTab(2);
        panel.Tick();
        var before = view.MenuBox(FontSize).TranslatePoint(default, view.Window);
        Assert.NotNull(before);

        Assert.True(view.OpenMenu(FontFace));
        panel.Tick();
        Assert.Equal(before, view.MenuBox(FontSize).TranslatePoint(default, view.Window));
        // A second click on the same box closes it.
        Assert.False(view.OpenMenu(FontFace));
    });

    [Fact]
    public void The_character_tab_lists_each_retail_option_once() =>
        Assert.Equal(OptionsWindow.CharacterRows.Count(row => !row.IsHeading),
            OptionsWindow.CharacterRows.Where(row => !row.IsHeading).Select(row => row.Option).Distinct().Count());

    [Fact]
    public void The_options_window_renders_to_pngs_when_asked() => RenderThread.Run(() =>
    {
        // Looked at by a person, not asserted: LEGACEY_OPTIONS_RENDER names the folder, LEGACEY_PORTAL_DAT the portal for retail's art.
        var folder = Environment.GetEnvironmentVariable("LEGACEY_OPTIONS_RENDER");
        if (folder == null) return;
        Directory.CreateDirectory(folder);
        var portalPath = Environment.GetEnvironmentVariable("LEGACEY_PORTAL_DAT");
        using var portal = portalPath == null ? null : new PortalDat(portalPath);
        foreach (var (name, tab) in new[] { ("gameplay", 0), ("character", 1), ("config", 2) })
        {
            OptionsWindow view = null!;
            using var panel = AvaloniaPanel.Create(() => (view = new OptionsWindow(new FakeGame(), portal)).Window, OptionsWindow.Width, OptionsWindow.Height);
            view.ShowTab(tab);
            view.Toggle(ToggleRun);
            panel.Tick();
            if (tab == 2) view.OpenMenu("Sound.SoundFeatures");
            panel.Tick();
            PanelPng.Save(panel, Path.Combine(folder, name + ".png"));
        }
    });

    private const string FullScreen = "Display.FullScreen", Stiffness = "Camera.Stiffness", FontSize = "UI.ChatFontSize", FontFace = "UI.ChatFontFace";

    private sealed class FakeGame : IOptionsGame
    {
        public readonly Dictionary<int, bool> Options = OptionsWindow.CharacterRows.Where(row => !row.IsHeading).ToDictionary(row => row.Option, _ => false);
        public readonly List<Dictionary<int, bool>> Applied = new();
        // Every Config preference at retail's default.
        public readonly Dictionary<string, double> Preferences = OptionsWindow.ConfigRows.Where(row => row.Name != null)
            .SelectMany(row => row.Slider == null ? new[] { (row.Name!, row.Default) } : new[] { (row.Name!, row.Default), (row.Slider, row.SliderDefault) })
            .ToDictionary(pair => pair.Item1, pair => pair.Item2);
        public readonly List<PreferenceChange> Changes = new();

        public FakeGame() => Options[ShowHelm] = true;

        public bool? Option(int option) => Options.TryGetValue(option, out var on) ? on : null;
        public bool DefaultOption(int option) => option is AutoTarget or ToggleRun or ShowHelm;
        public void Apply(IReadOnlyDictionary<int, bool> options)
        {
            Applied.Add(new Dictionary<int, bool>(options));
            foreach (var option in options) Options[option.Key] = option.Value;
        }
        public string? Text(string name) => null;
        public double? Preference(PreferenceKind kind, string name) => Preferences.TryGetValue(name, out var value) ? value : null;
        public (double Low, double High)? Range(string name) => (0, 1);
        public IReadOnlyList<MenuChoice> Choices(string name, bool listed) => new[] { new MenuChoice("Small", 1), new MenuChoice("Large", 3) };
        public string? PreferenceTip(string name) => null;
        public void SetPreferences(IReadOnlyList<PreferenceChange> changes)
        {
            Changes.AddRange(changes);
            foreach (var change in changes) Preferences[change.Name] = change.Value;
        }
        public bool CanPress(GameplayButton button) => button is not (GameplayButton.ConfigureKeyboard or GameplayButton.HelpFiles);
        public void Press(GameplayButton button) { }
    }
}
