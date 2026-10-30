using System;
using System.Collections.Generic;
using System.Globalization;
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

/// <summary>What the magic window reads from the game and asks of it. The client implements it over retail; tests fake it.</summary>
public interface IMagicGame
{
    /// <summary>How many of each spell component (by weenie class) the player's packs hold.</summary>
    IReadOnlyDictionary<uint, int> CarriedComponents();
    /// <summary>Sends retail's SpellbookFilterEvent (game action 0x0286). False when it could not be sent.</summary>
    bool SendFilters(uint filters);
    /// <summary>Sends retail's RemoveSpell (game action 0x01A8). False when it could not be sent.</summary>
    bool DeleteSpell(uint spell);
    /// <summary>Sends retail's SetDesiredComponentLevel (game action 0x0224). False when it could not be sent.</summary>
    bool SendDesired(uint wcid, int amount);
    /// <summary>Adds the spell to the spell bar's current tab, as a double click in retail's spellbook does. False when it could not.</summary>
    bool AddToSpellBar(uint spell);
    /// <summary>A spell dragged out of the window was released: true when it was over retail's spell bar, which then took it.</summary>
    bool DropOnSpellBar(uint spell);
    /// <summary>A spell drag started (true) or ended (false): while it lasts, the spell bar lights the cell a drop would land in.</summary>
    void HoverSpellBar(bool dragging);
    /// <summary>Shows the spell in the appraisal window, as retail's examination panel shows a spell.</summary>
    void Examine(uint spell);
}

/// <summary>
/// The Dereth magic window: retail's Spellbook and Components tabs laid out as retail lays them (LayoutDesc 0x21000032 and 0x21000033 in
/// the installed client_local_English.dat), on retail's own row, filter and button art. A click on a spell shows it in the appraisal window;
/// a double click or a drag onto the spell bar adds it there. One instance serves a session; <see cref="Refresh"/> redraws what changed.
/// </summary>
public sealed class MagicWindow
{
    public const string WindowId = "magic";
    public const int Width = 310, Height = 600;
    public static readonly WindowResizing Sizing = new(new System.Drawing.Size(Width, 320), new System.Drawing.Size(Width, 10_000));
    private const double RowHeight = 32, DragThreshold = 4;
    private static readonly string[] TabNames = { "Spellbook", "Components" };
    // Retail's art: the list rows (and their selected edge), the filter block, the toggles' off, off-hover, on and on-hover lights, Delete.
    private const uint RowArt = 0x06001396, RowSelectedArt = 0x06001397, ComponentRowArt = 0x06005E21, ComponentRowSelectedArt = 0x06005E22;
    private const uint FilterBlockArt = 0x06002722, DeleteArt = 0x06004CDA, DeleteHoverArt = 0x06004CDB;
    private const uint ToggleOff = 0x06004D15, ToggleOffHover = 0x06004D16, ToggleOn = 0x06004D17, ToggleOnHover = 0x06004D18;
    /// <summary>Retail's question before a spell leaves the spellbook (gmSpellbookUI::DeleteSpell).</summary>
    public const string DeleteQuestion =
        "Are you sure you want to remove {0} from your spellbook? You will no longer be able to cast this spell unless you learn it again!";

    // The school filters, as retail places them: Creature, Item, Life, War, Void at y 22.
    private static readonly (string Label, uint Filter, double X, double Y)[] Schools =
    {
        ("Creature", MagicState.CreatureFilter, 17, 22), ("Item", MagicState.ItemFilter, 87, 22), ("Life", MagicState.LifeFilter, 137, 22),
        ("War", MagicState.WarFilter, 187, 22), ("Void", MagicState.VoidFilter, 237, 22)
    };
    private static readonly string[] Numerals = { "I", "II", "III", "IV", "V", "VI", "VII", "VIII" };

    private static readonly IBrush WellBrush = DerethPalette.Brush(Color.FromArgb(0xB0, 0x06, 0x05, 0x04));

    private readonly MagicState _state;
    private readonly IMagicGame _game;
    private readonly Func<IReadOnlyDictionary<uint, SpellInfo>> _spells;
    private readonly Func<SpellComponentTable> _components;
    private readonly IGameArtSource? _art;
    private readonly IItemDragHost? _drag;
    private readonly DerethSegmented _tabs;
    private readonly ContentControl _page;
    private readonly Control[] _pages;
    private readonly StackPanel _spellList = new();
    private readonly StackPanel _componentList = new();
    private readonly Canvas _filters;
    private readonly Control _question;
    private readonly TextBlock _questionText;
    private readonly ArtButton _delete;
    private readonly Dictionary<uint, (Image Light, bool On)> _toggles = new();
    private readonly Dictionary<uint, Bitmap?> _bitmaps = new();
    private readonly Dictionary<uint, Bitmap?> _spellIcons = new();
    private readonly Dictionary<Panel, string> _drawn = new();
    private int _tab;
    private uint _selected, _selectedComponent;
    private bool _asking;
    // A pressed spell row: the spell and where the press was, until a release or a drag.
    private (uint Spell, Point At)? _pressed;
    private IDisposable? _dragIcon;
    private uint _dragSpell;

    public MagicWindow(MagicState state, IMagicGame game, Func<IReadOnlyDictionary<uint, SpellInfo>> spells, Func<SpellComponentTable> components,
        IGameArtSource? art, IItemDragHost? drag = null)
    {
        _state = state;
        _game = game;
        _spells = spells;
        _components = components;
        _art = art;
        _drag = drag;
        _tabs = new DerethSegmented(TabNames, cellPadding: new Thickness(4, 4)) { VerticalAlignment = VerticalAlignment.Center };
        _tabs.Chosen += ShowTab;

        // Spellbook: the list, then retail's filter block with the Schools and Levels lights and Delete.
        _filters = new Canvas { Width = 300, Height = 113, Background = Art(FilterBlockArt, Stretch.Fill) ?? WellBrush };
        Place(_filters, Label("Schools:", 13), 12, 6);
        foreach (var (label, filter, x, y) in Schools) Place(_filters, Toggle(label, filter), x, y);
        Place(_filters, Label("Levels:", 13), 12, 39);
        for (var level = 1u; level <= 8; level++)
        {
            var column = (int)((level - 1) % 3);
            var row = (int)((level - 1) / 3);
            Place(_filters, Toggle(Numerals[level - 1], MagicState.LevelFilter(level)), 17 + column * 65, 55 + row * 17);
        }
        _delete = new ArtButton("Delete", DeleteArt, DeleteHoverArt, art, AskDelete);
        Place(_filters, _delete, 187, 61);
        _questionText = new TextBlock
        {
            Foreground = DerethPalette.TextBrush, FontFamily = DerethPalette.Title, FontSize = 12, TextWrapping = TextWrapping.Wrap, Width = 276
        };
        var yes = new ArtButton("Yes", DeleteArt, DeleteHoverArt, art, () => Answer(true)) { Width = 80, Height = 26 };
        var no = new ArtButton("No", DeleteArt, DeleteHoverArt, art, () => Answer(false)) { Width = 80, Height = 26 };
        var answers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center, Children = { yes, no } };
        _question = new Border
        {
            Width = 300, Height = 113, Background = Art(FilterBlockArt, Stretch.Fill) ?? WellBrush, Padding = new Thickness(12, 8),
            Child = new StackPanel { Spacing = 8, Children = { _questionText, answers } }, IsVisible = false
        };
        var block = new Grid { HorizontalAlignment = HorizontalAlignment.Center, Children = { _filters, _question } };
        _pages = new Control[2];
        _pages[0] = Stack((Well(_spellList, () => Select(0)), true), (block, false));
        _pages[1] = Stack((Well(_componentList, () => SelectComponent(0)), true));

        _page = new ContentControl { Margin = new Thickness(0, 5, 0, 0) };
        Window = new DerethWindow(string.Empty, null, _page, _tabs, stretchActions: true);
        Window.CloseRequested += (_, _) => Close?.Invoke();
        ShowTab(0);
    }

    public DerethWindow Window { get; }

    /// <summary>Runs when the player closes the window with its close box.</summary>
    public Action? Close { get; set; }

    public int Tab => _tab;

    /// <summary>The spell the spellbook has selected, or 0.</summary>
    public uint Selected => _selected;

    /// <summary>Whether the spellbook is asking to confirm a delete.</summary>
    public bool Asking => _asking;

    public void ShowTab(int tab)
    {
        _tab = tab;
        _tabs.Selected = tab;
        _page.Content = _pages[tab];
        Refresh();
    }

    /// <summary>Redraws what changed: call it when the spellbook, the filters or the carried components change.</summary>
    public void Refresh()
    {
        if (_tab == 0) DrawSpellbook();
        else DrawComponents();
    }

    // Spellbook.

    /// <summary>
    /// The spells the spellbook lists: those the player knows that the spell table names, whose school and level are both switched on in
    /// <paramref name="filters"/> (gmSpellbookUI::IsFilteredOut; a spell of no school or no level is never listed), sorted by name.
    /// </summary>
    public static IReadOnlyList<(uint Id, SpellInfo Spell)> Listed(IEnumerable<uint> known, IReadOnlyDictionary<uint, SpellInfo> table, uint filters) =>
        known.Where(table.ContainsKey).Select(id => (Id: id, Spell: table[id]))
            .Where(s => (filters & SchoolFilter(s.Spell.School)) != 0 && SpellFormula.Level(s.Spell) is var level && level is >= 1 and <= 9
                && (filters & MagicState.LevelFilter(level)) != 0)
            .OrderBy(s => s.Spell.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The filter bit for ACE's MagicSchool: 1 War, 2 Life, 3 Item, 4 Creature, 5 Void; 0 for none.</summary>
    public static uint SchoolFilter(uint school) => school switch
    {
        1 => MagicState.WarFilter, 2 => MagicState.LifeFilter, 3 => MagicState.ItemFilter, 4 => MagicState.CreatureFilter, 5 => MagicState.VoidFilter,
        _ => 0
    };

    public IReadOnlyList<(uint Id, SpellInfo Spell)> ListedSpells() => Listed(_state.Spells, _spells(), _state.Filters);

    /// <summary>A click on a spell: selects it and shows it in the appraisal window. Zero clears the selection.</summary>
    public void Select(uint spell)
    {
        _selected = spell;
        if (spell != 0) _game.Examine(spell);
        Refresh();
    }

    /// <summary>A light's click: switches its school or level on or off, and sends the filters on, as retail's UpdateFilter does.</summary>
    public void ToggleFilter(uint filter)
    {
        var filters = _state.Filters ^ filter;
        _state.SetFilters(filters);
        _game.SendFilters(filters);
    }

    /// <summary>Delete: asks retail's question about the selected spell. Nothing happens with none selected.</summary>
    public void AskDelete()
    {
        if (_selected == 0 || !_spells().TryGetValue(_selected, out var spell)) return;
        _asking = true;
        _questionText.Text = string.Format(CultureInfo.InvariantCulture, DeleteQuestion, spell.Name);
        Refresh();
    }

    /// <summary>The answer to the delete question: yes removes the spell from the spellbook.</summary>
    public void Answer(bool yes)
    {
        _asking = false;
        if (yes && _selected != 0 && _game.DeleteSpell(_selected)) _selected = 0;
        Refresh();
    }

    private void DrawSpellbook()
    {
        var listed = ListedSpells();
        if (listed.All(s => s.Id != _selected))
        {
            _selected = 0;
            _asking = false;
        }
        _filters.IsVisible = !_asking;
        _question.IsVisible = _asking;
        _delete.IsEnabled = _selected != 0;
        foreach (var toggle in _toggles.ToList())
        {
            var now = (_state.Filters & toggle.Key) != 0;
            if (now == toggle.Value.On) continue;
            _toggles[toggle.Key] = (toggle.Value.Light, now);
            toggle.Value.Light.Source = Bitmap(now ? ToggleOn : ToggleOff);
        }
        Redraw(_spellList, $"{_selected}|{string.Join(",", listed.Select(s => s.Id))}", () =>
        {
            foreach (var (id, spell) in listed) _spellList.Children.Add(SpellRow(id, spell, id == _selected));
        });
    }

    private Control SpellRow(uint id, SpellInfo spell, bool selected)
    {
        var row = Row(selected ? RowSelectedArt : RowArt, SpellBitmap(id, spell), spell.Name, 230);
        row.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            if (e.ClickCount > 1)
            {
                _pressed = null;
                _game.AddToSpellBar(id);
                return;
            }
            _pressed = (id, e.GetPosition(Window));
            e.Pointer.Capture(row);
        };
        row.PointerMoved += (_, e) =>
        {
            if (_pressed is not { } press || _dragIcon != null || _drag == null) return;
            var point = e.GetPosition(Window);
            if (Math.Abs(point.X - press.At.X) < DragThreshold && Math.Abs(point.Y - press.At.Y) < DragThreshold) return;
            _dragSpell = press.Spell;
            _dragIcon = _drag.ShowDragIcon(_art == null ? null : SpellIcon.Draw(_art, spell), 1, retailDropIndicator: false);
            _game.HoverSpellBar(true);
        };
        row.PointerReleased += (_, e) =>
        {
            if (_pressed is not { } press) return;
            e.Handled = true;
            _pressed = null;
            if (_dragIcon != null)
            {
                EndDrag(drop: true);
                return;
            }
            if (new Rect(row.Bounds.Size).Contains(e.GetPosition(row))) Select(press.Spell);
        };
        row.PointerCaptureLost += (_, _) =>
        {
            _pressed = null;
            EndDrag(drop: false);
        };
        return row;
    }

    private void EndDrag(bool drop)
    {
        if (_dragIcon == null) return;
        _dragIcon.Dispose();
        _dragIcon = null;
        if (drop) _game.DropOnSpellBar(_dragSpell);
        else _game.HoverSpellBar(false);
        _dragSpell = 0;
    }

    // Components.

    /// <summary>
    /// The components the Components tab lists: every one the player carries or asked to keep, by name, with how many are carried and
    /// how many are wanted.
    /// </summary>
    public static IReadOnlyList<(SpellComponentInfo Component, int Carried, int Desired)> ComponentRows(SpellComponentTable table,
        IReadOnlyDictionary<uint, int> carried, IReadOnlyDictionary<uint, int> desired) =>
        carried.Keys.Concat(desired.Keys).Distinct().Select(table.ByWcid).OfType<SpellComponentInfo>()
            .Select(c => (Component: c, Carried: carried.TryGetValue(c.Wcid, out var n) ? n : 0, Desired: desired.TryGetValue(c.Wcid, out var d) ? d : 0))
            .OrderBy(r => r.Component.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<(SpellComponentInfo Component, int Carried, int Desired)> ListedComponents() =>
        ComponentRows(_components(), _game.CarriedComponents(), _state.Desired);

    public void SelectComponent(uint wcid)
    {
        _selectedComponent = wcid;
        Refresh();
    }

    /// <summary>How many of the component to keep: set here and sent on, as retail's entry does when it is changed.</summary>
    public void SetDesired(uint wcid, int amount)
    {
        amount = Math.Max(0, amount);
        if ((_state.Desired.TryGetValue(wcid, out var now) ? now : 0) == amount) return;
        _state.SetDesired(wcid, amount);
        _game.SendDesired(wcid, amount);
    }

    private void DrawComponents()
    {
        var rows = ListedComponents();
        if (rows.All(r => r.Component.Wcid != _selectedComponent)) _selectedComponent = 0;
        Redraw(_componentList, $"{_selectedComponent}|{string.Join(",", rows.Select(r => $"{r.Component.Wcid}:{r.Carried}:{r.Desired}"))}", () =>
        {
            foreach (var (component, carried, desired) in rows) _componentList.Children.Add(ComponentRow(component, carried, desired));
        });
    }

    private Control ComponentRow(SpellComponentInfo component, int carried, int desired)
    {
        var wcid = component.Wcid;
        var row = Row(wcid == _selectedComponent ? ComponentRowSelectedArt : ComponentRowArt, Bitmap(component.Icon), component.Name, 180);
        var count = Label(carried.ToString(CultureInfo.InvariantCulture), 12);
        count.TextAlignment = TextAlignment.Right;
        count.Width = 60;
        var entry = new TextBox
        {
            Text = desired > 0 ? desired.ToString(CultureInfo.InvariantCulture) : string.Empty, MaxLength = 5, Width = 60, Height = 15, MinHeight = 0,
            Padding = new Thickness(3, 0), FontFamily = DerethPalette.Title, FontSize = 11, Foreground = DerethPalette.CreamBrush,
            CaretBrush = DerethPalette.GoldBrush, Background = DerethPalette.Brush(Color.FromArgb(0xE0, 0x0A, 0x09, 0x07)),
            BorderBrush = DerethPalette.Brush(DerethPalette.Line), BorderThickness = new Thickness(1), TextAlignment = TextAlignment.Right,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(entry, "How many to keep");
        void Commit() => SetDesired(wcid, int.TryParse((entry.Text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0);
        entry.LostFocus += (_, _) => Commit();
        entry.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        var figures = new StackPanel { Width = 60, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 1, 8, 1), Children = { count, entry } };
        ((Grid)row.Child!).Children.Add(figures);
        row.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
            SelectComponent(wcid);
        };
        return row;
    }

    // The parts.

    private void Redraw(Panel list, string key, Action fill)
    {
        if (_drawn.TryGetValue(list, out var drawn) && drawn == key) return;
        _drawn[list] = key;
        list.Children.Clear();
        fill();
    }

    /// <summary>A list row on retail's art: the icon at the left, the name from x 42 as retail places it.</summary>
    private Border Row(uint art, IImage? icon, string name, double nameWidth)
    {
        var text = Label(name, 13);
        text.Width = nameWidth;
        text.Margin = new Thickness(42, 0, 0, 0);
        text.HorizontalAlignment = HorizontalAlignment.Left;
        var grid = new Grid
        {
            Height = RowHeight, Children =
            {
                new Image { Source = icon, Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Left }, text
            }
        };
        return new Border { Height = RowHeight, Background = Art(art, Stretch.Fill) ?? Brushes.Transparent, Child = grid, Cursor = new Cursor(StandardCursorType.Hand) };
    }

    /// <summary>A filter light and its label, as retail's toggle template draws it: the 13 × 13 light, the label beside it.</summary>
    private Control Toggle(string label, uint filter)
    {
        var on = (_state.Filters & filter) != 0;
        var light = new Image { Source = Bitmap(on ? ToggleOn : ToggleOff), Width = 13, Height = 13, Margin = new Thickness(0, 1, 0, 0) };
        _toggles[filter] = (light, on);
        var text = Label(label, 12);
        text.Margin = new Thickness(4, 0, 0, 0);
        var toggle = new StackPanel { Orientation = Orientation.Horizontal, Height = 14, Background = Brushes.Transparent, Children = { light, text } };
        toggle.Cursor = new Cursor(StandardCursorType.Hand);
        toggle.PointerEntered += (_, _) => light.Source = Bitmap(_toggles[filter].On ? ToggleOnHover : ToggleOffHover);
        toggle.PointerExited += (_, _) => light.Source = Bitmap(_toggles[filter].On ? ToggleOn : ToggleOff);
        toggle.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(toggle).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            ToggleFilter(filter);
            light.Source = Bitmap(_toggles[filter].On ? ToggleOnHover : ToggleOffHover);
        };
        return toggle;
    }

    /// <summary>A spell's icon as retail composes it (<see cref="SpellIcon"/>), drawn once per spell.</summary>
    private IImage? SpellBitmap(uint id, SpellInfo spell)
    {
        if (!_spellIcons.TryGetValue(id, out var bitmap))
            _spellIcons[id] = bitmap = _art == null ? null : GameArtImageExtension.CreateBitmap(SpellIcon.Draw(_art, spell));
        return bitmap;
    }

    private Bitmap? Bitmap(uint id)
    {
        if (!_bitmaps.TryGetValue(id, out var bitmap)) _bitmaps[id] = bitmap = GameArtImageExtension.CreateBitmap(_art, id);
        return bitmap;
    }

    private IBrush? Art(uint id, Stretch stretch) => Bitmap(id) is { } bitmap ? new ImageBrush(bitmap) { Stretch = stretch } : null;

    private static void Place(Canvas canvas, Control child, double x, double y)
    {
        Canvas.SetLeft(child, x);
        Canvas.SetTop(child, y);
        canvas.Children.Add(child);
    }

    /// <summary>A dark list well. A click on its empty space clears the selection, as in retail.</summary>
    private static Control Well(StackPanel list, Action clear)
    {
        var scroll = new ScrollViewer
        {
            Content = list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            Background = Brushes.Transparent
        };
        scroll.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(scroll).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            clear();
        };
        return new Border { Background = WellBrush, Margin = new Thickness(4, 0, 4, 2), Child = scroll };
    }

    private static Grid Stack(params (Control Child, bool Fill)[] rows)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions(string.Join(",", rows.Select(row => row.Fill ? "*" : "Auto"))) };
        for (var i = 0; i < rows.Length; i++)
        {
            Grid.SetRow(rows[i].Child, i);
            grid.Children.Add(rows[i].Child);
        }
        return grid;
    }

    /// <summary>Retail letters this panel in its serif face throughout.</summary>
    private static TextBlock Label(string text, double size) => DerethPalette.CentreInk(new TextBlock
    {
        Text = text, FontSize = size, Foreground = DerethPalette.TextBrush, FontFamily = DerethPalette.Title, VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis
    });

    /// <summary>Retail's red Delete button: its art, lighter under the pointer, dimmed while it can do nothing, the label centred on it.</summary>
    private sealed class ArtButton : Border
    {
        public ArtButton(string text, uint art, uint hoverArt, IGameArtSource? source, Action click)
        {
            Width = 100;
            Height = 32;
            var normal = GameArtImageExtension.CreateBitmap(source, art) is { } a ? new ImageBrush(a) { Stretch = Stretch.Fill } : (IBrush)Brushes.DarkRed;
            var hover = GameArtImageExtension.CreateBitmap(source, hoverArt) is { } h ? new ImageBrush(h) { Stretch = Stretch.Fill } : normal;
            Background = normal;
            Child = DerethPalette.CentreInk(new TextBlock
            {
                Text = text, FontFamily = DerethPalette.Title, FontSize = 13, Foreground = DerethPalette.TextBrush, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            Cursor = new Cursor(StandardCursorType.Hand);
            PointerEntered += (_, _) => { if (IsEnabled) Background = hover; };
            PointerExited += (_, _) => Background = normal;
            PointerPressed += (_, e) =>
            {
                if (!IsEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                click();
            };
            PropertyChanged += (_, e) => { if (e.Property == IsEnabledProperty) Opacity = IsEnabled ? 1 : 0.5; };
        }
    }
}
