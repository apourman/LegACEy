using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.PanelHost;
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
        foreach (var (name, tab) in new[] { ("gameplay", 0), ("character", 1) })
        {
            OptionsWindow view = null!;
            using var panel = AvaloniaPanel.Create(() => (view = new OptionsWindow(new FakeGame(), portal)).Window, OptionsWindow.Width, OptionsWindow.Height);
            view.ShowTab(tab);
            view.Toggle(ToggleRun);
            panel.Tick();
            panel.Tick();
            PanelPng.Save(panel, Path.Combine(folder, name + ".png"));
        }
    });

    private sealed class FakeGame : IOptionsGame
    {
        public readonly Dictionary<int, bool> Options = OptionsWindow.CharacterRows.Where(row => !row.IsHeading).ToDictionary(row => row.Option, _ => false);
        public readonly List<Dictionary<int, bool>> Applied = new();

        public FakeGame() => Options[ShowHelm] = true;

        public bool? Option(int option) => Options.TryGetValue(option, out var on) ? on : null;
        public bool DefaultOption(int option) => option is AutoTarget or ToggleRun or ShowHelm;
        public void Apply(IReadOnlyDictionary<int, bool> options)
        {
            Applied.Add(new Dictionary<int, bool>(options));
            foreach (var option in options) Options[option.Key] = option.Value;
        }
        public string? Text(string name) => null;
        public bool CanPress(GameplayButton button) => button is not (GameplayButton.ConfigureKeyboard or GameplayButton.HelpFiles);
        public void Press(GameplayButton button) { }
    }
}
