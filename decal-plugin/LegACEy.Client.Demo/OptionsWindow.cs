using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Demo;

/// <summary>The buttons on retail's Gameplay Options tab, top to bottom.</summary>
public enum GameplayButton { ExitToCharacterSelection, ExitGame, ConfigureKeyboard, MouseTurning, HelpFiles, UrgentAssistance, ReportAbuse }

/// <summary>What the options window reads from the game and asks of it. The client implements it over retail's own calls.</summary>
public interface IOptionsGame
{
    /// <summary>A character option (retail's PlayerOption number) as retail holds it, or null when it cannot be read.</summary>
    bool? Option(int option);
    /// <summary>The option's default, as retail's Defaults button sets it.</summary>
    bool DefaultOption(int option);
    /// <summary>Sets the options as retail's Apply does, which also tells the server.</summary>
    void Apply(IReadOnlyDictionary<int, bool> options);
    /// <summary>Retail's interface text by its string name (an option's tooltip), or null.</summary>
    string? Text(string name);
    /// <summary>A Config preference as retail holds it (a switch as 1 or 0), or null when it cannot be read.</summary>
    double? Preference(PreferenceKind kind, string name);
    /// <summary>A slider preference's range, or null when it cannot be read.</summary>
    (double Low, double High)? Range(string name);
    /// <summary>A menu preference's choices in retail's order; a listed menu's are text (Resolution), the others retail's string ids.</summary>
    IReadOnlyList<MenuChoice> Choices(string name, bool listed);
    /// <summary>Retail's tooltip for a Config preference, or null.</summary>
    string? PreferenceTip(string name);
    /// <summary>Sets the preferences as retail's Config Apply does, telling retail's windows each one changed.</summary>
    void SetPreferences(IReadOnlyList<PreferenceChange> changes);
    /// <summary>Whether the button does anything here; one that does not is dimmed.</summary>
    bool CanPress(GameplayButton button);
    /// <summary>Does what retail's button does.</summary>
    void Press(GameplayButton button);
}

/// <summary>One row of retail's Character tab: a section heading, or an option with its PlayerOption number and retail's string name.</summary>
public sealed record CharacterOptionRow(string Label, int Option = -1, string? Name = null)
{
    public bool IsHeading => Option < 0;
}

/// <summary>
/// The Dereth options window, retail's options panel in its layout and art: the Gameplay Options buttons, the Character tab's switches and
/// the Config tab's settings. As in retail, a change on a tab is held until that tab's Apply, which sends every held change; Reset drops them
/// and Defaults holds retail's defaults. Closing the window drops what was not applied. One instance serves the session; <see cref="Refresh"/>
/// redraws what changed.
/// </summary>
public sealed partial class OptionsWindow
{
    public const string WindowId = "options";
    public const int Width = 310, Height = 600;
    public static readonly WindowResizing Sizing = new(new System.Drawing.Size(290, 360));
    private static readonly string[] TabNames = { "Gameplay Options", "Character", "Config" };
    private const double RowHeight = 20, HeadingHeight = 22;

    // Retail's art (client_portal.dat): the blue Gameplay buttons and the Apply/Reset/Defaults buttons, each a left, middle and right
    // piece, with a pressed and a disabled set; the check lights (off, off hovered, on, on hovered, disabled); the gold section rule.
    private static readonly uint[] BlueButton = { 0x060074A9, 0x060074AC, 0x060074AF }, BluePressed = { 0x060074A8, 0x060074AB, 0x060074AE },
        BlueDisabled = { 0x060074A7, 0x060074AA, 0x060074AD };
    private static readonly uint[] RedButton = { 0x06004C96, 0x06004C99, 0x06004C9C }, RedPressed = { 0x06004C95, 0x06004C98, 0x06004C9B },
        RedDisabled = { 0x06004C94, 0x06004C97, 0x06004C9A };
    private const uint LightOff = 0x06004D15, LightOffHover = 0x06004D16, LightOn = 0x06004D17, LightOnHover = 0x06004D18, LightDisabled = 0x06004D19;
    private const uint SectionRule = 0x060012C5;
    private static readonly IBrush HeadingBrush = DerethPalette.Brush(Color.Parse("#E6D47E"));
    private static readonly IBrush LabelBrush = DerethPalette.Brush(Color.Parse("#E4E0D4"));

    /// <summary>
    /// Retail's Character tab top to bottom, as gmCharacterSettingsUI::InitOptions adds it: each heading, then its options by PlayerOption
    /// number with retail's English label and the string name its tooltip is read by (ID_PlayerOption_&lt;name&gt;_Help).
    /// </summary>
    public static readonly IReadOnlyList<CharacterOptionRow> CharacterRows = new CharacterOptionRow[]
    {
        new("User Interface Behavior"),
        new("Keep Combat Targets in View", 0x07, "ViewCombatTarget"),
        new("Salvage Multiple Materials at Once", 0x22, "SalvageMultiple"),
        new("Use Main Pack as Default for Picking Up Items", 0x29, "MainPackPreferred"),
        new("User Interface Display"),
        new("Vivid Targeting Indicator", 0x0E, "VividTargetingIndicator"),
        new("Display 3D Tooltips", 0x08, "ShowTooltips"),
        new("Show Coordinates By the Radar", 0x14, "CoordinatesOnRadar"),
        new("Side By Side Vitals", 0x13, "SideBySideVitals"),
        new("Display Spell Durations", 0x15, "SpellDuration"),
        new("Disable Most Weather Effects", 0x04, "DisableMostWeatherEffects"),
        new("Disable Distance Fog", 0x30, "DisableDistanceFog"),
        new("Always Daylight Outdoors", 0x05, "PersistentAtDay"),
        new("Disable House Restriction Effects", 0x16, "DisableHouseRestrictionEffects"),
        new("Use Crafting Chance of Success Dialog", 0x1A, "UseCraftSuccessDialog"),
        new("Confirm Use of Rare Gems", 0x2D, "ConfirmVolatileRareUse"),
        new("Display Timestamps", 0x21, "DisplayTimeStamps"),
        new("Filter Language", 0x2C, "FilterLanguage"),
        new("Show Your Helm or Head Gear", 0x2F, "ShowHelm"),
        new("Show Your Cloak", 0x32, "ShowCloak"),
        new("Grouping"),
        new("Ignore Allegiance Requests", 0x01, "IgnoreAllegianceRequests"),
        new("Ignore Fellowship Requests", 0x02, "IgnoreFellowshipRequests"),
        new("Show Allegiance Logons", 0x18, "DisplayAllegianceLogonNotifications"),
        new("Share Fellowship Experience and Luminance", 0x0F, "FellowshipShareXP"),
        new("Share Fellowship Loot", 0x11, "FellowshipShareLoot"),
        new("Automatically Accept Fellowship Requests", 0x12, "FellowshipAutoAcceptRequests"),
        new("Other Players"),
        new("Accept Corpse Looting Permissions", 0x10, "AcceptLootPermits"),
        new("Attempt to Deceive Other Players", 0x09, "UseDeception"),
        new("Let Other Players Give You Items", 0x06, "AllowGive"),
        new("Ignore All Trade Requests", 0x03, "IgnoreTradeRequests"),
        new("Drag Item to Player Opens Trade", 0x17, "DragItemOnPlayerOpensSecureTrade"),
        new("Allow Others to See Your Date of Birth", 0x1C, "DisplayDateOfBirth"),
        new("Allow Others to See Your Age", 0x1D, "DisplayAge"),
        new("Allow Others to See Your Chess Rank", 0x1E, "DisplayChessRank"),
        new("Allow Others to See Your Fishing Skill", 0x1F, "DisplayFishingSkill"),
        new("Allow Others to See Your Number of Deaths", 0x20, "DisplayNumberDeaths"),
        new("Allow Others to See Your Number of Titles", 0x28, "DisplayNumberCharacterTitles"),
        new("Character Behavior"),
        new("Run as Default Movement", 0x0A, "ToggleRun"),
        new("Advanced Combat Interface", 0x0C, "AdvancedCombatUI"),
        new("Auto Target", 0x0D, "AutoTarget"),
        new("Automatically Repeat Attacks", 0x00, "AutoRepeatAttack"),
        new("Use Charge Attack", 0x19, "UseChargeAttack"),
        new("Lead Missile Targets", 0x2A, "LeadMissileTargets"),
        new("Use Fast Missiles", 0x2B, "UseFastMissiles"),
        new("Chat"),
        new("Stay in Chat Mode After Sending a Message", 0x0B, "StayInChatMode"),
        new("Listen to Allegiance Chat", 0x1B, "HearAllegianceChat"),
        new("Listen to General Chat", 0x23, "HearGeneralChat"),
        new("Listen to Trade Chat", 0x24, "HearTradeChat"),
        new("Listen to LFG Chat", 0x25, "HearLFGChat"),
        new("Listen to Roleplay Chat", 0x26, "HearRoleplayChat"),
        new("Listen to Society Chat", 0x2E, "HearSocietyChat"),
        new("Listen to PK death messages.", 0x34, "HearPKDeaths")
    };

    /// <summary>Retail's Gameplay Options buttons: label and top edge, each 240 × 32 at x 26 (LayoutDesc 0x2100002A).</summary>
    private static readonly (GameplayButton Button, string Label, double Y)[] GameplayButtons =
    {
        (GameplayButton.ExitToCharacterSelection, "Exit to Character Selection", 20),
        (GameplayButton.ExitGame, "Exit Game", 60),
        (GameplayButton.ConfigureKeyboard, "Configure Keyboard", 110),
        (GameplayButton.MouseTurning, "Use Mouse Turning Settings", 150),
        (GameplayButton.HelpFiles, "In-Game Help Files", 190),
        (GameplayButton.UrgentAssistance, "Urgent Assistance", 240),
        (GameplayButton.ReportAbuse, "Report Abuse", 280)
    };

    private readonly IOptionsGame _game;
    private readonly IGameArtSource? _art;
    private readonly Dictionary<uint, Bitmap?> _bitmaps = new();
    private readonly DerethSegmented _tabs;
    private readonly ContentControl _page;
    private readonly Control[] _pages;
    private readonly Dictionary<int, Light> _lights = new();
    private readonly Dictionary<GameplayButton, ArtButton> _gameplay = new();
    private readonly ArtButton _apply, _reset;
    // Clicks not yet applied: each option whose shown value differs from retail's.
    private readonly Dictionary<int, bool> _held = new();
    private int _tab;

    public OptionsWindow(IOptionsGame game, IGameArtSource? art)
    {
        _game = game;
        _art = art;
        _tabs = new DerethSegmented(TabNames, cellPadding: new Thickness(4, 4), fitLabels: true) { VerticalAlignment = VerticalAlignment.Center };
        _tabs.Chosen += ShowTab;

        var gameplay = new Canvas { Width = 298, Height = 337, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        foreach (var (button, label, y) in GameplayButtons)
        {
            var press = new ArtButton(this, label, BlueButton, BluePressed, BlueDisabled, () => _game.Press(button)) { Width = 240, Height = 32 };
            _gameplay[button] = press;
            Canvas.SetLeft(press, 26);
            Canvas.SetTop(press, y);
            gameplay.Children.Add(press);
        }

        var rows = new StackPanel { Margin = new Thickness(0, 0, 4, 0) };
        var first = true;
        foreach (var row in CharacterRows)
        {
            if (row.IsHeading)
            {
                if (!first) rows.Children.Add(Rule());
                first = false;
                rows.Children.Add(new Border { Height = HeadingHeight, Padding = new Thickness(4, 0), Child = Label(row.Label, 12.5, HeadingBrush) });
                continue;
            }
            var option = row.Option;
            var light = new Light(this, row.Label, row.Name == null ? null : _game.Text($"ID_PlayerOption_{row.Name}_Help"), () => Shown(option),
                () => Toggle(option));
            _lights[row.Option] = light;
            rows.Children.Add(light);
        }
        var character = PageWithButtons(rows, Apply, Reset, Defaults, out _apply, out _reset);

        _menuList = MenuList(_menuItems);
        _pages = new Control[] { gameplay, character, BuildConfig() };
        _page = new ContentControl { Margin = new Thickness(0, 5, 0, 0) };
        // The tabs sit in the header beside the close box, as the social window's do.
        Window = new DerethWindow(string.Empty, null, _page, _tabs, stretchActions: true);
        Window.CloseRequested += (_, _) => Close?.Invoke();
        ShowTab(0);
    }

    public DerethWindow Window { get; }

    /// <summary>Runs when the player closes the window with its close box.</summary>
    public Action? Close { get; set; }

    public int Tab => _tab;

    /// <summary>The changes held for Apply, for tests.</summary>
    public IReadOnlyDictionary<int, bool> Held => _held;

    public bool CanApply => _apply.IsEnabled;

    /// <summary>The option as the window shows it: a held click, else retail's value; null when retail's cannot be read.</summary>
    public bool? Shown(int option) => _held.TryGetValue(option, out var held) ? held : _game.Option(option);

    public void ShowTab(int tab)
    {
        _tab = tab;
        _tabs.Selected = tab;
        CloseMenu();
        _page.Content = _pages[tab];
        Refresh();
    }

    /// <summary>Reads retail's options again and redraws. Call it on a change and about once a second while open.</summary>
    public void Refresh()
    {
        // A held click retail has since come to (a key, the server, another window) is no longer a change.
        foreach (var option in _held.Where(held => _game.Option(held.Key) == held.Value).Select(held => held.Key).ToList()) _held.Remove(option);
        foreach (var pair in _gameplay) pair.Value.IsEnabled = _game.CanPress(pair.Key);
        foreach (var light in _lights.Values) light.Refresh();
        _apply.IsEnabled = _reset.IsEnabled = _held.Count > 0;
        RefreshConfig();
    }

    /// <summary>Flips an option as a click on its switch does: held until Apply, or let go when it is back to retail's value.</summary>
    public void Toggle(int option)
    {
        if (_game.Option(option) is not { } current) return;
        var shown = !(Shown(option) ?? current);
        if (shown == current) _held.Remove(option);
        else _held[option] = shown;
        Refresh();
    }

    public void Apply()
    {
        if (_held.Count == 0) return;
        _game.Apply(new Dictionary<int, bool>(_held));
        _held.Clear();
        Refresh();
    }

    public void Reset()
    {
        _held.Clear();
        Refresh();
    }

    /// <summary>Holds retail's default for every option, as retail's Defaults does; Apply sends them.</summary>
    public void Defaults()
    {
        _held.Clear();
        foreach (var option in _lights.Keys)
            if (_game.Option(option) is { } current && _game.DefaultOption(option) != current) _held[option] = !current;
        Refresh();
    }

    /// <summary>The window was hidden: as retail's page does, what was not applied is dropped.</summary>
    public void Hidden()
    {
        _held.Clear();
        _heldConfig.Clear();
        CloseMenu();
    }

    /// <summary>A settings page as retail lays it out: its rows in a scrolling list, and Apply, Reset and Defaults below at x 16, 106 and 196.</summary>
    private Control PageWithButtons(Control rows, Action apply, Action reset, Action defaults, out ArtButton applyButton, out ArtButton resetButton)
    {
        var list = new ScrollViewer
        {
            Content = rows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Visible
        };
        applyButton = new ArtButton(this, "Apply", RedButton, RedPressed, RedDisabled, apply) { Width = 80, Height = 32 };
        resetButton = new ArtButton(this, "Reset", RedButton, RedPressed, RedDisabled, reset) { Width = 80, Height = 32 };
        var defaultsButton = new ArtButton(this, "Defaults", RedButton, RedPressed, RedDisabled, defaults) { Width = 80, Height = 32 };
        var buttons = new Canvas { Width = 298, Height = 32, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4) };
        foreach (var (button, x) in new[] { (applyButton, 16.0), (resetButton, 106.0), (defaultsButton, 196.0) })
        {
            Canvas.SetLeft(button, x);
            buttons.Children.Add(button);
        }
        var page = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        page.Children.Add(list);
        Grid.SetRow(buttons, 1);
        page.Children.Add(buttons);
        return page;
    }

    private Control Rule() => new Border
    {
        Height = 8, Margin = new Thickness(0, 2, 0, 0),
        Background = Art(SectionRule) is { } rule ? new ImageBrush(rule) { Stretch = Stretch.Fill } : DerethPalette.GoldDarkBrush
    };

    private Bitmap? Art(uint id)
    {
        if (!_bitmaps.TryGetValue(id, out var bitmap)) _bitmaps[id] = bitmap = GameArtImageExtension.CreateBitmap(_art, id);
        return bitmap;
    }

    private static TextBlock Label(string text, double size, IBrush brush) => DerethPalette.CentreInk(new TextBlock
    {
        Text = text, FontSize = size, Foreground = brush, FontFamily = DerethPalette.Title, VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis
    });

    /// <summary>One of retail's check lights and its words: a click flips the setting; one retail cannot read is dimmed.</summary>
    private sealed class Light : Border
    {
        private readonly OptionsWindow _window;
        private readonly Func<bool?> _shown;
        private readonly Image _light = new() { Width = 13, Height = 13, VerticalAlignment = VerticalAlignment.Center };
        private bool _hover;

        public Light(OptionsWindow window, string label, string? tip, Func<bool?> shown, Action toggle)
        {
            _window = window;
            _shown = shown;
            Height = RowHeight;
            Padding = new Thickness(6, 0, 0, 0);
            Background = Brushes.Transparent;
            Cursor = new Cursor(StandardCursorType.Hand);
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            line.Children.Add(_light);
            line.Children.Add(Label(label, 11.5, LabelBrush));
            Child = line;
            if (tip != null) ToolTip.SetTip(this, tip);
            PointerEntered += (_, _) => { _hover = true; Refresh(); };
            PointerExited += (_, _) => { _hover = false; Refresh(); };
            PointerPressed += (_, e) =>
            {
                if (!IsEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                toggle();
            };
        }

        public void Refresh()
        {
            var shown = _shown();
            IsEnabled = shown != null;
            var art = shown switch
            {
                null => LightDisabled,
                true => _hover ? LightOnHover : LightOn,
                false => _hover ? LightOffHover : LightOff
            };
            _light.Source = _window.Art(art);
        }
    }

    /// <summary>A retail button drawn from its three pieces of art (left, stretched middle, right), pressed while held and dark when disabled.</summary>
    private sealed class ArtButton : Border
    {
        private readonly Border _left = new() { Width = 32 }, _middle = new(), _right = new() { Width = 32 };
        private readonly IBrush?[] _normal, _pressed, _disabled;
        private bool _down;

        public ArtButton(OptionsWindow window, string text, uint[] normal, uint[] pressed, uint[] disabled, Action click)
        {
            // Every piece fills its cell: in a scaled window, unstretched ends land a row off the stretched middle.
            IBrush?[] Brushes(uint[] ids) => ids.Select(id => window.Art(id) is { } bitmap
                ? (IBrush)new ImageBrush(bitmap) { Stretch = Stretch.Fill }
                : null).ToArray();
            _normal = Brushes(normal);
            _pressed = Brushes(pressed);
            _disabled = Brushes(disabled);
            var pieces = new Grid { ColumnDefinitions = new ColumnDefinitions("32,*,32") };
            pieces.Children.Add(_left);
            Grid.SetColumn(_middle, 1);
            pieces.Children.Add(_middle);
            Grid.SetColumn(_right, 2);
            pieces.Children.Add(_right);
            var label = Label(text, 13, DerethPalette.CreamBrush);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            var face = new Grid();
            face.Children.Add(pieces);
            face.Children.Add(label);
            Child = face;
            Background = Avalonia.Media.Brushes.Transparent;
            Cursor = new Cursor(StandardCursorType.Hand);
            PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                _down = true;
                Draw();
            };
            PointerReleased += (_, e) =>
            {
                if (!_down) return;
                _down = false;
                Draw();
                if (IsEnabled && new Rect(Bounds.Size).Contains(e.GetPosition(this))) click();
            };
            PointerCaptureLost += (_, _) => { _down = false; Draw(); };
            PropertyChanged += (_, e) => { if (e.Property == IsEnabledProperty) Draw(); };
            Draw();
        }

        private void Draw()
        {
            var set = !IsEnabled ? _disabled : _down ? _pressed : _normal;
            _left.Background = set[0] ?? DerethPalette.GoldDarkBrush;
            _middle.Background = set[1] ?? DerethPalette.GoldDarkBrush;
            _right.Background = set[2] ?? DerethPalette.GoldDarkBrush;
            Opacity = IsEnabled || set[0] != null ? 1 : 0.5;
        }
    }
}
