using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Demo;

/// <summary>How retail holds a Config setting: on or off, a number, or a menu choice's value.</summary>
public enum PreferenceKind { Switch, Number, Choice }

/// <summary>The kind of row on retail's Config tab.</summary>
public enum ConfigRowKind { Heading, Switch, Slider, SwitchSlider, Menu }

/// <summary>
/// One row of retail's Config tab, as gmConfigUI::InitOptions adds it: the retail preference it shows (UserPreferences name) and the
/// default retail's Defaults button sets. A slider names its end labels by the word in ID_Graphics_Value_&lt;word&gt;. A switch with a slider
/// also has the slider's preference. A listed menu (Resolution) has its choices as text; the others as retail's string ids.
/// </summary>
public sealed record ConfigRow(ConfigRowKind Kind, string Label, string? Name = null, double Default = 0, string? Low = null, string? High = null,
    string? Slider = null, double SliderDefault = 0, bool Listed = false);

/// <summary>A menu choice: retail's words for it and the value it stores.</summary>
public sealed record MenuChoice(string Label, uint Value);

/// <summary>A held Config change for Apply: the new value and the value retail had, which a menu's change notice carries.</summary>
public sealed record PreferenceChange(PreferenceKind Kind, string Name, double Value, double Old);

public sealed partial class OptionsWindow
{
    /// <summary>Retail's Config tab top to bottom, with each preference and the default gmConfigUI::InitOptions gives it.</summary>
    public static readonly IReadOnlyList<ConfigRow> ConfigRows = new ConfigRow[]
    {
        new(ConfigRowKind.Heading, "Sound Options"),
        new(ConfigRowKind.Menu, "Sound Features", "Sound.SoundFeatures", 0),
        new(ConfigRowKind.SwitchSlider, "Sound Effects", "Sound.SoundDisabled", 1, Slider: "Sound.SoundVolume", SliderDefault: 1),
        new(ConfigRowKind.SwitchSlider, "Ambient Sounds", "Sound.AmbientSoundDisabled", 1, Slider: "Sound.AmbientSoundVolume", SliderDefault: 1),
        new(ConfigRowKind.SwitchSlider, "Interface Sounds", "Sound.InterfaceSoundDisabled", 1, Slider: "Sound.InterfaceSoundVolume", SliderDefault: 1),
        new(ConfigRowKind.Switch, "Play Sounds Only When Active", "Sound.PlaySoundOnlyWhenActive", 1),
        new(ConfigRowKind.Heading, "Camera Options"),
        new(ConfigRowKind.Slider, "Camera Stiffness", "Camera.Stiffness", 0.45, "Soft", "Hard"),
        new(ConfigRowKind.Slider, "Camera Adjustment Speed", "Camera.AdjustmentSpeed", 40, "Slow", "Fast"),
        new(ConfigRowKind.Slider, "Field of View", "Render.FieldOfView", 90, "Narrow", "Wide"),
        new(ConfigRowKind.Switch, "Align camera to slope", "Camera.AlignToSlope", 1),
        new(ConfigRowKind.Heading, "Graphics Options"),
        new(ConfigRowKind.Menu, "Resolution", "Display.Resolution", 0x03200258, Listed: true),
        new(ConfigRowKind.Switch, "Full Screen", "Display.FullScreen", 1),
        new(ConfigRowKind.Switch, "Sync with Refresh Rate", "Display.SyncToRefresh", 0),
        new(ConfigRowKind.Slider, "Screen Brightness", "Render.ScreenBrightness", 0, "Dark", "Bright"),
        new(ConfigRowKind.Switch, "Adaptive Degrade", "Render.AutomaticDegrades", 0),
        new(ConfigRowKind.Slider, "Adaptive Degrade Bias", "Render.GraphicsPerformance", 0, "Speed", "Detail"),
        new(ConfigRowKind.Slider, "Degrade Distance", "Render.DegradeDistance", 50, "Close", "Far"),
        new(ConfigRowKind.Heading, "Rendering Quality Options"),
        new(ConfigRowKind.Menu, "Landscape Texture Detail", "Render.LandscapeTextureDetail", 2),
        new(ConfigRowKind.Menu, "Environment Texture", "Render.EnvironmentTextureDetail", 1),
        new(ConfigRowKind.Menu, "Texture Filtering", "Render.TextureFiltering", 1),
        new(ConfigRowKind.Menu, "Landscape Draw Distance", "Render.LandscapeDrawDistance", 8),
        new(ConfigRowKind.Switch, "Environment Detail Textures", "Render.BuildingDetailTextures", 1),
        new(ConfigRowKind.Switch, "Multiple Pass Alpha", "Render.MultiPassAlpha", 0),
        new(ConfigRowKind.Heading, "Input Options"),
        new(ConfigRowKind.Slider, "Mouselook Sensitivity", "Input.MouseLookSensitivity", 0.55),
        new(ConfigRowKind.Switch, "Invert mouselook axes", "Input.InvertMouseLookYAxis", 0),
        new(ConfigRowKind.Switch, "Turn your character with camera turning.", "Input.UseMouseTurning", 0),
        new(ConfigRowKind.Heading, "UI Options"),
        new(ConfigRowKind.Menu, "Chat Font Face", "UI.ChatFontFace", 2),
        new(ConfigRowKind.Menu, "Chat Font Size", "UI.ChatFontSize", 1)
    };

    private static readonly IBrush MenuBrush = DerethPalette.Brush(Color.Parse("#1E3A1E")), MenuEdgeBrush = DerethPalette.Brush(Color.Parse("#6E9A4E")),
        MenuArrowBrush = DerethPalette.Brush(Color.Parse("#4FB040"));
    private const double LabelColumn = 136, ControlLeft = 140, ControlWidth = 112;

    // Each preference with its kind and default, the slider of a switch-and-slider row included.
    private readonly Dictionary<string, (PreferenceKind Kind, double Default)> _preferences = new();
    private readonly Dictionary<string, double> _heldConfig = new();
    private readonly List<Action> _configRefresh = new();
    private readonly Dictionary<string, IReadOnlyList<MenuChoice>> _choices = new();
    private ArtButton? _configApply, _configReset;
    // The open drop-down's list, drawn over the rows below its box as retail's is, and the preference it is for.
    private readonly Canvas _menuLayer = new();
    private readonly StackPanel _menuItems = new();
    private Border? _menuList;
    private string? _openMenu;
    private readonly Dictionary<string, Control> _menuBoxes = new();

    /// <summary>Opens a drop-down as a click on its box does, for tests; true when its list shows.</summary>
    internal bool OpenMenu(string name)
    {
        OpenMenu(name, ConfigRows.First(row => row.Name == name).Listed, _menuBoxes[name]);
        return _openMenu == name;
    }

    /// <summary>A drop-down's box, for tests.</summary>
    internal Control MenuBox(string name) => _menuBoxes[name];

    /// <summary>The Config changes held for Apply, for tests.</summary>
    public IReadOnlyDictionary<string, double> HeldConfig => _heldConfig;

    public bool CanApplyConfig => _configApply?.IsEnabled == true;

    /// <summary>A Config preference as the window shows it: a held change, else retail's value; null when retail's cannot be read.</summary>
    public double? ShownConfig(string name) => _heldConfig.TryGetValue(name, out var held) ? held : _game.Preference(_preferences[name].Kind, name);

    /// <summary>Holds a Config value until Apply, as a change on retail's page is; one back at retail's value is let go.</summary>
    public void HoldConfig(string name, double value)
    {
        if (_game.Preference(_preferences[name].Kind, name) is not { } current) return;
        if (Same(name, value, current)) _heldConfig.Remove(name);
        else _heldConfig[name] = value;
        Refresh();
    }

    public void ApplyConfig()
    {
        if (_heldConfig.Count == 0) return;
        var changes = _heldConfig.Select(held => new PreferenceChange(_preferences[held.Key].Kind, held.Key, held.Value,
            _game.Preference(_preferences[held.Key].Kind, held.Key) ?? held.Value)).ToList();
        _game.SetPreferences(changes);
        _heldConfig.Clear();
        Refresh();
    }

    public void ResetConfig()
    {
        _heldConfig.Clear();
        Refresh();
    }

    /// <summary>Holds retail's default for every Config setting that differs from it, as retail's Defaults does; Apply sends them.</summary>
    public void DefaultsConfig()
    {
        _heldConfig.Clear();
        foreach (var preference in _preferences)
            if (_game.Preference(preference.Value.Kind, preference.Key) is { } current && !Same(preference.Key, preference.Value.Default, current))
                _heldConfig[preference.Key] = preference.Value.Default;
        Refresh();
    }

    // A slider's value is close enough when it is within half of one of its hundred steps.
    private bool Same(string name, double a, double b) =>
        _preferences[name].Kind == PreferenceKind.Number && _game.Range(name) is { } range
            ? Math.Abs(a - b) <= Math.Abs(range.High - range.Low) / 200
            : a == b;

    private void RefreshConfig()
    {
        // A held change retail has since come to (Use Mouse Turning Settings, another window) is no longer a change.
        foreach (var name in _heldConfig.Where(held => _game.Preference(_preferences[held.Key].Kind, held.Key) is { } current && Same(held.Key, held.Value, current))
                     .Select(held => held.Key).ToList())
            _heldConfig.Remove(name);
        foreach (var refresh in _configRefresh) refresh();
        if (_configApply != null) _configApply.IsEnabled = _configReset!.IsEnabled = _heldConfig.Count > 0;
    }

    private Control BuildConfig()
    {
        var rows = new StackPanel { Margin = new Thickness(0, 0, 4, 0) };
        var first = true;
        foreach (var row in ConfigRows)
        {
            if (row.Kind == ConfigRowKind.Heading)
            {
                if (!first) rows.Children.Add(Rule());
                first = false;
                rows.Children.Add(new Border { Height = HeadingHeight, Padding = new Thickness(4, 0), Child = Label(row.Label, 12.5, HeadingBrush) });
                continue;
            }
            var name = row.Name!;
            _preferences[name] = (row.Kind switch
            {
                ConfigRowKind.Slider => PreferenceKind.Number,
                ConfigRowKind.Menu => PreferenceKind.Choice,
                _ => PreferenceKind.Switch
            }, row.Default);
            if (row.Slider != null) _preferences[row.Slider] = (PreferenceKind.Number, row.SliderDefault);
            rows.Children.Add(row.Kind switch
            {
                ConfigRowKind.Switch => SwitchRow(row.Label, name),
                ConfigRowKind.SwitchSlider => SwitchSliderRow(row.Label, name, row.Slider!),
                ConfigRowKind.Slider => SliderRow(row),
                _ => MenuRow(row)
            });
        }
        _menuList = new Border
        {
            IsVisible = false, Width = ControlWidth, Background = MenuBrush, BorderBrush = MenuEdgeBrush, BorderThickness = new Thickness(1),
            Child = new ScrollViewer { Content = _menuItems, MaxHeight = 200, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }
        };
        _menuLayer.Children.Add(_menuList);
        var content = new Panel();
        content.Children.Add(rows);
        content.Children.Add(_menuLayer);
        // A press anywhere but on a drop-down or its list closes the list.
        content.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!(e.Source as Visual)?.GetSelfAndVisualAncestors().Any(visual => visual == _menuList || visual is Border { Tag: MenuTag }) ?? true)
                CloseMenu();
        }, RoutingStrategies.Tunnel);
        return PageWithButtons(content, ApplyConfig, ResetConfig, DefaultsConfig, out _configApply, out _configReset);
    }

    private const string MenuTag = "menu";

    private void CloseMenu()
    {
        _openMenu = null;
        if (_menuList != null) _menuList.IsVisible = false;
    }

    /// <summary>Lists a drop-down's choices over the rows just below its box, or above it when the list would run past the page.</summary>
    private void OpenMenu(string name, bool listed, Control box)
    {
        if (_openMenu == name || _menuList == null)
        {
            CloseMenu();
            return;
        }
        _menuItems.Children.Clear();
        foreach (var choice in Choices(name, listed))
        {
            var item = new Border
            {
                Background = MenuBrush, Padding = new Thickness(4, 1), Cursor = new Cursor(StandardCursorType.Hand),
                Child = Label(choice.Label, 11.5, DerethPalette.CreamBrush)
            };
            item.PointerEntered += (_, _) => item.Background = MenuEdgeBrush;
            item.PointerExited += (_, _) => item.Background = MenuBrush;
            item.PointerPressed += (_, pressed) =>
            {
                pressed.Handled = true;
                CloseMenu();
                HoldConfig(name, choice.Value);
            };
            _menuItems.Children.Add(item);
        }
        if (_menuItems.Children.Count == 0 || box.TranslatePoint(new Point(0, box.Bounds.Height), _menuLayer) is not { } below)
        {
            CloseMenu();
            return;
        }
        _menuList.Measure(Size.Infinity);
        var height = _menuList.DesiredSize.Height;
        var top = below.Y + height > _menuLayer.Bounds.Height ? below.Y - box.Bounds.Height - height : below.Y;
        Canvas.SetLeft(_menuList, below.X);
        Canvas.SetTop(_menuList, Math.Max(0, top));
        _openMenu = name;
        _menuList.IsVisible = true;
    }

    private Light ConfigLight(string label, string name)
    {
        var light = new Light(this, label, _game.PreferenceTip(name), () => ShownConfig(name) is { } value ? value != 0 : null,
            () => { if (ShownConfig(name) is { } value) HoldConfig(name, value != 0 ? 0 : 1); });
        _configRefresh.Add(light.Refresh);
        return light;
    }

    private Control SwitchRow(string label, string name) => ConfigLight(label, name);

    private Control SwitchSliderRow(string label, string name, string slider)
    {
        var line = new Canvas { Height = RowHeight };
        var light = ConfigLight(label, name);
        light.Width = ControlLeft - 4;
        line.Children.Add(light);
        line.Children.Add(Place(Slider(slider), ControlLeft, 2));
        return line;
    }

    private Control SliderRow(ConfigRow row)
    {
        var line = new Canvas { Height = row.Low == null ? RowHeight : 36 };
        var label = Label(row.Label, 11, LabelBrush);
        label.TextAlignment = TextAlignment.Right;
        label.Width = LabelColumn;
        line.Children.Add(Place(label, 0, 2));
        line.Children.Add(Place(Slider(row.Name!), ControlLeft, 2));
        if (row.Low != null)
        {
            line.Children.Add(Place(EndLabel(row.Low, TextAlignment.Left), ControlLeft, 18));
            line.Children.Add(Place(EndLabel(row.High!, TextAlignment.Right), ControlLeft + ControlWidth / 2, 18));
        }
        if (_game.PreferenceTip(row.Name!) is { } tip) ToolTip.SetTip(line, tip);
        return line;
    }

    private TextBlock EndLabel(string word, TextAlignment alignment)
    {
        var text = Label(_game.Text($"ID_Graphics_Value_{word}") ?? word, 10, LabelBrush);
        text.Width = ControlWidth / 2;
        text.TextAlignment = alignment;
        return text;
    }

    /// <summary>A slider over the preference's own range in a hundred steps; a move is held until Apply.</summary>
    private DerethSlider Slider(string name)
    {
        var slider = new DerethSlider(0, 100, 1) { Width = ControlWidth };
        slider.ValueChanged += step =>
        {
            if (_game.Range(name) is { } range) HoldConfig(name, range.Low + (range.High - range.Low) * step / 100);
        };
        _configRefresh.Add(() =>
        {
            var range = _game.Range(name);
            var value = ShownConfig(name);
            slider.IsEnabled = range != null && value != null;
            if (range is { } r && value is { } v && r.High != r.Low) slider.Value = (int)Math.Round((v - r.Low) / (r.High - r.Low) * 100);
        });
        return slider;
    }

    /// <summary>Retail's drop-down: the chosen value in a green box; a click lists the choices over the rows below, and a choice is held until Apply.</summary>
    private Control MenuRow(ConfigRow row)
    {
        var name = row.Name!;
        var label = Label(row.Label, 11, LabelBrush);
        label.TextAlignment = TextAlignment.Right;
        label.Width = LabelColumn;
        var chosen = Label(string.Empty, 11.5, DerethPalette.CreamBrush);
        chosen.HorizontalAlignment = HorizontalAlignment.Center;
        var arrow = new TextBlock { Text = "▼", FontSize = 10, Foreground = MenuArrowBrush, VerticalAlignment = VerticalAlignment.Center };
        var face = new Grid { ColumnDefinitions = new ColumnDefinitions("*,14") };
        face.Children.Add(chosen);
        Grid.SetColumn(arrow, 1);
        face.Children.Add(arrow);
        var box = new Border
        {
            Width = ControlWidth, Height = 18, Background = MenuBrush, BorderBrush = MenuEdgeBrush, BorderThickness = new Thickness(1),
            Child = face, Cursor = new Cursor(StandardCursorType.Hand), Tag = MenuTag
        };
        _menuBoxes[name] = box;
        var line = new Canvas { Height = RowHeight };
        line.Children.Add(Place(label, 0, 2));
        line.Children.Add(Place(box, ControlLeft, 1));
        if (_game.PreferenceTip(name) is { } tip) ToolTip.SetTip(line, tip);

        box.PointerPressed += (_, e) =>
        {
            if (!box.IsEnabled || !e.GetCurrentPoint(box).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            OpenMenu(name, row.Listed, box);
        };
        _configRefresh.Add(() =>
        {
            var value = ShownConfig(name);
            box.IsEnabled = value != null;
            chosen.Text = value is { } v ? Choices(name, row.Listed).FirstOrDefault(choice => choice.Value == (uint)v)?.Label ?? ((uint)v).ToString() : string.Empty;
        });
        return line;
    }

    // Retail's menu lists rarely change (a new display mode), so each is read once per window.
    private IReadOnlyList<MenuChoice> Choices(string name, bool listed)
    {
        if (_choices.TryGetValue(name, out var choices)) return choices;
        choices = _game.Choices(name, listed);
        if (choices.Count > 0) _choices[name] = choices;
        return choices;
    }

    private static Control Place(Control control, double x, double y)
    {
        Canvas.SetLeft(control, x);
        Canvas.SetTop(control, y);
        return control;
    }
}
