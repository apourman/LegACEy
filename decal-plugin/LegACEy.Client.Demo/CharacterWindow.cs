using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Demo;

/// <summary>What the character window reads from the game and asks of it. The client implements it over Decal and retail's actions.</summary>
public interface ICharacterGame
{
    string Name { get; }
    /// <summary>The character's heritage and gender as retail words them ("Viamontian", "Male"); empty when unknown.</summary>
    string Race { get; }
    string Gender { get; }
    /// <summary>The value the player sees, or null when it cannot be read. Vitals by ACE's id: 1 health, 3 stamina, 5 mana.</summary>
    StatValue? Value(StatKind kind, uint id);
    /// <summary>A vital's current value, by ACE's id.</summary>
    int Current(uint vital);
    /// <summary>Spends experience on an attribute, vital or trained skill (retail's raise actions). False when it could not be sent.</summary>
    bool Raise(StatKind kind, uint id, uint experience);
    /// <summary>Spends skill credits training a skill. The credits must be the skill's training cost.</summary>
    bool Train(uint skill, int credits);
    bool SetTitle(uint title);
}

/// <summary>
/// The Dereth character window, retail's Attributes, Skills and Titles tabs in retail's order: a plaque with the name, level and
/// experience, the list, and retail's plate at the bottom: the selected row's name and value, the experience to raise it beside retail's +10
/// and +1 arrows (or its credit cost beside the arrow that trains it), and the credits and experience to spend. A click on a row selects it; a second click clears it. Each skill group
/// folds from its band, and the folds are remembered; the attributes and vitals have no band. One instance serves the session; <see cref="Refresh"/> redraws what changed.
/// </summary>
public sealed class CharacterWindow
{
    public const string WindowId = "character";
    public const int Width = 310, Height = 640;
    public static readonly WindowResizing Sizing = new(new System.Drawing.Size(280, 360));
    private const double SkillRowHeight = 21, AttributeRowHeight = 27, BandHeight = 20;
    // Retail's raise arrows in the portal dat, lit and dim: the plain one (+1, or train) and the +10 one. Each is 30 × 26 with clear edges,
    // padded to a square because icons only scale square art.
    private const uint Arrow = 0x06004CB6, ArrowDim = 0x06004CB7, TenArrow = 0x0600712B, TenArrowDim = 0x0600712C;
    private const double ArrowSide = 30;

    private static readonly IBrush Transparent = Brushes.Transparent;
    private static readonly IBrush HoverBrush = DerethPalette.Brush(DerethPalette.Gold.WithAlpha(0x12));
    private static readonly IBrush SelectedBrush = DerethPalette.Brush(DerethPalette.Gold.WithAlpha(0x22));
    private static readonly IBrush PlateBrush = DerethPalette.Brush(Color.FromArgb(0x8C, 0x08, 0x07, 0x05));
    private static readonly IBrush LineBrush = DerethPalette.Brush(DerethPalette.Line);
    private static readonly Dictionary<CharacterGroup, Color> GroupColours = new()
    {
        [CharacterGroup.Specialized] = Color.FromRgb(140, 96, 220),
        [CharacterGroup.Trained] = Color.FromRgb(63, 160, 170),
        [CharacterGroup.Untrained] = Color.FromRgb(200, 150, 60),
        [CharacterGroup.Unusable] = Color.FromRgb(130, 130, 140)
    };
    private static readonly Dictionary<CharacterGroup, string> GroupNames = new()
    {
        [CharacterGroup.Specialized] = "Specialized Skills",
        [CharacterGroup.Trained] = "Trained Skills",
        [CharacterGroup.Untrained] = "Untrained Skills",
        [CharacterGroup.Unusable] = "Unusable Skills"
    };
    private static readonly string[] TabNames = { "Attributes", "Skills", "Titles" };

    private readonly CharacterSheet _sheet;
    private readonly ICharacterGame _game;
    private readonly IGameArtSource? _art;
    private readonly Func<CharacterTables> _tables;
    private readonly Func<IReadOnlyDictionary<uint, string>> _titleNames;
    private readonly Action<IReadOnlyCollection<CharacterGroup>> _foldsChanged;
    private readonly HashSet<CharacterGroup> _folded;
    private readonly Dictionary<uint, Avalonia.Media.Imaging.WriteableBitmap?> _icons = new();

    private readonly DerethSegmented _tabs;
    private readonly ContentControl _page;
    private readonly Grid _statsPage;
    private readonly ContentControl _plaque;
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _list;
    private readonly ContentControl _foot;
    private readonly Grid _titlesPage;
    private readonly ContentControl _currentTitle;
    private readonly StackPanel _titleList;
    private readonly DerethButton _setTitle;

    private int _tab;
    private (StatKind Kind, uint Id)? _selected;
    private uint _titleSelected;
    private IReadOnlyList<CharacterRow> _rows = Array.Empty<CharacterRow>();
    private IReadOnlyList<CharacterRow>? _drawnRows;
    private string? _drawnPlaque, _drawnFoot, _drawnTitles;

    public CharacterWindow(CharacterSheet sheet, ICharacterGame game, IGameArtSource? art, Func<CharacterTables> tables,
        Func<IReadOnlyDictionary<uint, string>> titleNames, IEnumerable<CharacterGroup> folded, Action<IReadOnlyCollection<CharacterGroup>> foldsChanged)
    {
        _sheet = sheet;
        _game = game;
        _art = art;
        _tables = tables;
        _titleNames = titleNames;
        _folded = new HashSet<CharacterGroup>(folded);
        _foldsChanged = foldsChanged;

        // The tabs sit in the header beside the close box, as retail's do: the window has no title to fill it.
        _tabs = new DerethSegmented(TabNames) { VerticalAlignment = VerticalAlignment.Center };
        _tabs.Chosen += index =>
        {
            _tab = index;
            _selected = null;
            _scroll!.Offset = default;
            Show();
        };

        _plaque = new ContentControl { Margin = new Thickness(1, 0, 1, 4) };
        _list = new StackPanel();
        _scroll = new ScrollViewer
        {
            Content = _list,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Background = Transparent
        };
        // A click on the list's empty space clears the selection, as in retail; rows and bands handle their own clicks.
        _scroll.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(_scroll).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            ClearSelection();
        };
        _foot = new ContentControl { Margin = new Thickness(1, 4, 1, 0) };
        _statsPage = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        _statsPage.Children.Add(_plaque);
        Grid.SetRow(_scroll, 1);
        _statsPage.Children.Add(_scroll);
        Grid.SetRow(_foot, 2);
        _statsPage.Children.Add(_foot);

        _currentTitle = new ContentControl { Margin = new Thickness(1, 0, 1, 6) };
        _titleList = new StackPanel();
        var titleScroll = new ScrollViewer
        {
            Content = _titleList,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        var titleWell = new Border
        {
            Background = PlateBrush, BorderBrush = LineBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
            Padding = new Thickness(3), Margin = new Thickness(1, 0), Child = titleScroll
        };
        _setTitle = new DerethButton { Content = Label("Set as Display Title", 12, DerethPalette.CreamBrush), Padding = new Thickness(14, 4),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        _setTitle.Click += (_, _) => SetSelectedTitle();
        _titlesPage = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        _titlesPage.Children.Add(_currentTitle);
        Grid.SetRow(titleWell, 1);
        _titlesPage.Children.Add(titleWell);
        Grid.SetRow(_setTitle, 2);
        _titlesPage.Children.Add(_setTitle);

        _page = new ContentControl { Margin = new Thickness(0, 5, 0, 0) };
        Window = new DerethWindow(string.Empty, null, _page, _tabs, stretchActions: true);
        Window.CloseRequested += (_, _) => Close?.Invoke();
        Show();
    }

    public DerethWindow Window { get; }

    /// <summary>Runs when the player closes the window with its close box.</summary>
    public Action? Close { get; set; }

    /// <summary>The rows the open Attributes or Skills tab lists, folded groups included.</summary>
    public IReadOnlyList<CharacterRow> Rows => _rows;

    public IReadOnlyCollection<CharacterGroup> Folded => _folded;

    public void ShowTab(int tab)
    {
        _tab = tab;
        _tabs.Selected = tab;
        _selected = null;
        Show();
    }

    /// <summary>Selects a row, as a click on it does.</summary>
    public void Select(StatKind kind, uint id)
    {
        _selected = (kind, id);
        // The highlight is drawn with the rows.
        _drawnRows = null;
        Refresh();
    }

    /// <summary>Clears the selection, as a click on the list's empty space does.</summary>
    public void ClearSelection()
    {
        _selected = null;
        _drawnRows = null;
        Refresh();
    }

    /// <summary>Folds or unfolds a group, as a click on its band does.</summary>
    public void ToggleFold(CharacterGroup group)
    {
        if (!_folded.Remove(group)) _folded.Add(group);
        _foldsChanged(_folded);
        _drawnRows = null;
        Refresh();
    }

    /// <summary>Spends experience on the selected row: the next rank, or ten ranks (fewer near the top).</summary>
    public void Raise(bool ten)
    {
        if (SelectedRow is not { } row) return;
        var cost = ten ? row.TenCost : row.NextCost;
        if (cost is not { } experience || experience == 0 || experience > _sheet.UnassignedExperience) return;
        // Applied at once, as retail does: the next click costs the next rank, and the server's update confirms it.
        if (_game.Raise(row.Kind, row.Id, experience) && CharacterRows.CostList(_sheet, _tables(), row.Kind, row.Id) is { } costs)
            _sheet.Spend(row.Kind, row.Id, experience, costs);
        Refresh();
    }

    /// <summary>Spends the selected skill's credits training it, as retail's triangle does.</summary>
    public void Train()
    {
        if (SelectedRow is not { TrainCost: > 0 } row || row.TrainCost > _sheet.SkillCredits) return;
        if (_game.Train(row.Id, row.TrainCost)) _sheet.Train(row.Id, row.TrainCost);
        Refresh();
    }

    private CharacterRow? SelectedRow => _selected is { } key ? _rows.FirstOrDefault(row => row.Kind == key.Kind && row.Id == key.Id) : null;

    private void Show()
    {
        _page.Content = _tab == 2 ? _titlesPage : _statsPage;
        _drawnRows = null;
        _drawnPlaque = _drawnFoot = _drawnTitles = null;
        Refresh();
    }

    /// <summary>The character changed (the server's update, or a raise just sent): the window redraws.</summary>
    public void SheetChanged() => Refresh();

    /// <summary>Reads the character again and redraws only what changed. Call it when the sheet changes and about once a second while open.</summary>
    public void Refresh()
    {
        var tables = _tables();
        if (_tab == 2)
        {
            DrawTitles();
            return;
        }
        _rows = _tab == 0 ? CharacterRows.Attributes(_sheet, tables, _game.Value, _game.Current) : CharacterRows.Skills(_sheet, tables, _game.Value);
        DrawPlaque(tables);
        if (_drawnRows == null || !_drawnRows.SequenceEqual(_rows)) DrawList();
        DrawFoot();
    }

    private void DrawPlaque(CharacterTables tables)
    {
        var title = _titleNames().TryGetValue(_sheet.DisplayTitle, out var name) ? name : string.Empty;
        var (needed, fraction) = CharacterRows.NextLevel(_sheet, tables);
        var line = string.Join(" ", new[] { _game.Gender, _game.Race, title }.Where(part => part.Length > 0));
        var key = $"{_game.Name}|{line}|{_sheet.PlayerKillerStatus}|{_sheet.TotalExperience}|{needed}|{_sheet.Level}";
        if (key == _drawnPlaque) return;
        _drawnPlaque = key;

        var who = new StackPanel { Spacing = 1, Margin = new Thickness(8, 5, 8, 6) };
        who.Children.Add(Centred(Label(_game.Name, 15, DerethPalette.CreamBrush, title: true)));
        if (line.Length > 0) who.Children.Add(Centred(Label(line, 11, DerethPalette.MutedBrush)));
        var pk = CharacterRows.PlayerKillerText(_sheet.PlayerKillerStatus);
        if (pk.Length > 0) who.Children.Add(Centred(Label(pk, 11, DerethPalette.MutedBrush)));
        var figures = new StackPanel { Spacing = 2, Margin = new Thickness(0, 5, 0, 0) };
        figures.Children.Add(Pair("Total Experience", Number(_sheet.TotalExperience)));
        figures.Children.Add(Progress(Pair("XP for next level", Number(needed)), fraction));
        who.Children.Add(figures);

        var level = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        level.Children.Add(Centred(Label("LEVEL", 10.5, DerethPalette.MutedBrush)));
        level.Children.Add(Centred(Label(_sheet.Level > 0 ? _sheet.Level.ToString(CultureInfo.InvariantCulture) : "–", 32, DerethPalette.GoldBrush, title: true)));
        var levelBox = new Border { BorderBrush = LineBrush, BorderThickness = new Thickness(1, 0, 0, 0), Child = level };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,64") };
        grid.Children.Add(who);
        Grid.SetColumn(levelBox, 1);
        grid.Children.Add(levelBox);
        _plaque.Content = Plate(grid, padding: 0);
    }

    private void DrawList()
    {
        _drawnRows = _rows;
        _list.Children.Clear();
        // The skill groups fold from their bands; the attributes and vitals are listed with no band, as in retail.
        var height = _tab == 0 ? AttributeRowHeight : SkillRowHeight;
        foreach (var group in _rows.GroupBy(row => row.Group))
        {
            if (group.Key is not (CharacterGroup.Attributes or CharacterGroup.Vitals))
            {
                _list.Children.Add(Band(group.Key, group.Count()));
                if (_folded.Contains(group.Key)) continue;
            }
            foreach (var row in group) _list.Children.Add(Row(row, height));
        }
    }

    private Control Band(CharacterGroup group, int count)
    {
        var colour = GroupColours[group];
        var folded = _folded.Contains(group);
        var chevron = new DerethPathGlyph("M1,2.5 L4,5.5 L7,2.5", 8)
        {
            Stroke = DerethPalette.GoldBrush, StrokeThickness = 1.4, Width = 10, Height = 10,
            RenderTransform = new RotateTransform(folded ? -90 : 0), VerticalAlignment = VerticalAlignment.Center
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 6 };
        row.Children.Add(chevron);
        var name = Label(GroupNames[group], 12.5, DerethPalette.CreamBrush, title: true);
        Grid.SetColumn(name, 1);
        row.Children.Add(name);
        var number = Label(count.ToString(CultureInfo.InvariantCulture), 11, DerethPalette.MutedBrush);
        Grid.SetColumn(number, 2);
        row.Children.Add(number);
        var band = new Border
        {
            Height = BandHeight, Padding = new Thickness(5, 0, 6, 0), Margin = new Thickness(0, 1, 0, 1), Child = row,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = { new GradientStop(colour.WithAlpha(0x8C), 0), new GradientStop(colour.WithAlpha(0x1F), 0.7), new GradientStop(Color.FromArgb(0xF2, 0x11, 0x0F, 0x0A), 1) }
            },
            BorderBrush = DerethPalette.Brush(colour.WithAlpha(0x99)), BorderThickness = new Thickness(0, 0, 0, 1)
        };
        ToolTip.SetTip(band, folded ? "Show this group" : "Hide this group");
        band.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(band).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            ToggleFold(group);
        };
        return band;
    }

    private Control Row(CharacterRow row, double height)
    {
        var selected = _selected is { } key && key.Kind == row.Kind && key.Id == row.Id;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 6 };
        var icon = Icon(row.IconId);
        if (icon != null) grid.Children.Add(icon);
        var name = Label(row.Name, 13, row.Group == CharacterGroup.Unusable ? DerethPalette.MutedBrush : DerethPalette.TextBrush, title: true);
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        var value = Label(ValueText(row), 12.5, ValueBrush(row));
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);
        var border = new Border
        {
            Height = height, Padding = new Thickness(3, 0, 6, 0), CornerRadius = new CornerRadius(2), Child = grid,
            Background = selected ? SelectedBrush : Transparent,
            BorderBrush = selected ? DerethPalette.GoldDarkBrush : Transparent, BorderThickness = new Thickness(0, 1)
        };
        var tip = Tip(row);
        if (tip != null) ToolTip.SetTip(border, tip);
        border.PointerEntered += (_, _) => { if (!IsSelected(row)) border.Background = HoverBrush; };
        border.PointerExited += (_, _) => { if (!IsSelected(row)) border.Background = Transparent; };
        border.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            if (!IsSelected(row)) Select(row.Kind, row.Id);
            else ClearSelection();
        };
        return border;
    }

    private bool IsSelected(CharacterRow row) => _selected is { } key && key.Kind == row.Kind && key.Id == row.Id;

    private static string? Tip(CharacterRow row)
    {
        var parts = new List<string>();
        if (row.Value.Buffed != row.Value.Base) parts.Add($"Base {row.Value.Base}, {(row.Value.Buffed > row.Value.Base ? "+" : "")}{row.Value.Buffed - row.Value.Base} from spells and items");
        if (row.Formula.Length > 0) parts.Add($"Trained from {row.Formula}");
        if (row.Description.Length > 0) parts.Add(row.Description);
        return parts.Count == 0 ? null : string.Join("\n", parts);
    }

    private static string ValueText(CharacterRow row) => row.Kind == StatKind.Vital
        ? $"{row.Current}/{row.Value.Buffed}"
        : row.Value.Buffed.ToString(CultureInfo.InvariantCulture);

    private static IBrush ValueBrush(CharacterRow row) =>
        row.Value.Buffed > row.Value.Base ? DerethPalette.RaisedBrush
        : row.Value.Buffed < row.Value.Base ? DerethPalette.InvalidBrush
        : row.Group is CharacterGroup.Untrained or CharacterGroup.Unusable ? DerethPalette.MutedBrush
        : DerethPalette.TextBrush;

    /// <summary>
    /// The plate at the bottom, as retail's. A row experience raises: its name and value, the experience to raise it and the unassigned
    /// experience, beside the +10 and +1 triangles. An untrained or unusable skill: its name and the credits to train it, beside one triangle
    /// that trains it.
    /// </summary>
    private void DrawFoot()
    {
        var row = SelectedRow;
        var key = $"{_tab}|{row}|{_sheet.SkillCredits}|{_sheet.UnassignedExperience}";
        if (key == _drawnFoot) return;
        _drawnFoot = key;
        var unassigned = _sheet.UnassignedExperience;
        var credits = _sheet.SkillCredits.ToString(CultureInfo.InvariantCulture);

        var stack = new StackPanel { Spacing = 3 };
        // Retail's arrows sit beside the last rows, each overlapping the clear edge of the one above.
        var side = new StackPanel { Spacing = -13, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, -7) };
        if (row == null)
        {
            stack.Children.Add(Centred(Label(_tab == 0 ? "Select an Attribute to Improve" : "Select a Skill to Improve", 13.5, DerethPalette.CreamBrush, title: true)));
            stack.Children.Add(Pair("Skill Credits Available", credits));
            stack.Children.Add(Pair("Unassigned Experience", Number(unassigned)));
        }
        else if (row.Group is CharacterGroup.Untrained or CharacterGroup.Unusable)
        {
            stack.Children.Add(Centred(Label(row.Name, 13.5, DerethPalette.CreamBrush, title: true)));
            if (row.TrainCost > 0)
            {
                stack.Children.Add(Pair("Skill Credits To Train", row.TrainCost.ToString(CultureInfo.InvariantCulture), short_: row.TrainCost > _sheet.SkillCredits));
                side.Children.Add(RaiseArrow(ten: false, row.TrainCost <= _sheet.SkillCredits,
                    $"Train {row.Name} for {row.TrainCost} skill credits", Train));
            }
            stack.Children.Add(Pair("Skill Credits Available", credits));
        }
        else
        {
            stack.Children.Add(Heading(row));
            var next = row.NextCost;
            stack.Children.Add(Pair("Experience To Raise", next is { } cost ? Number(cost) : row.AtMax ? "Max" : "–", short_: next > unassigned));
            stack.Children.Add(Pair("Unassigned Experience", Number(unassigned)));
            side.Children.Add(RaiseArrow(ten: true, row.TenCost is { } ten && ten > 0 && ten <= unassigned, "Raise 10 ranks", () => Raise(ten: true)));
            side.Children.Add(RaiseArrow(ten: false, next is { } one && one > 0 && one <= unassigned, "Raise 1 rank", () => Raise(ten: false)));
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        grid.Children.Add(stack);
        Grid.SetColumn(side, 1);
        grid.Children.Add(side);
        _foot.Content = Plate(grid);
    }

    // "Arcane Lore: 379 (+55)": the buff in the colour the list gives it.
    private static Control Heading(CharacterRow row)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        line.Children.Add(Label($"{row.Name}: {row.Value.Buffed}", 13.5, DerethPalette.CreamBrush, title: true));
        var buff = row.Value.Buffed - row.Value.Base;
        if (buff != 0) line.Children.Add(Label($"({(buff > 0 ? "+" : "")}{buff})", 12.5, ValueBrush(row)));
        return line;
    }

    // One of retail's raise arrows, dim when the click cannot be afforded.
    private Control RaiseArrow(bool ten, bool enabled, string tip, Action click)
    {
        var hit = new Border
        {
            Width = ArrowSide, Height = ArrowSide, Background = Transparent,
            Child = Icon(ten ? enabled ? TenArrow : TenArrowDim : enabled ? Arrow : ArrowDim, square: true),
            Cursor = new Cursor(enabled ? StandardCursorType.Hand : StandardCursorType.Arrow)
        };
        ToolTip.SetTip(hit, tip);
        hit.PointerPressed += (_, e) =>
        {
            if (!enabled || !e.GetCurrentPoint(hit).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            click();
        };
        return hit;
    }

    private void DrawTitles()
    {
        var names = _titleNames();
        string NameOf(uint id) => names.TryGetValue(id, out var name) ? name : $"Title {id}";
        var titles = _sheet.Titles.Select(id => (Id: id, Name: NameOf(id))).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (_titleSelected == 0 || !_sheet.Titles.Contains(_titleSelected)) _titleSelected = _sheet.DisplayTitle;
        var key = $"{_sheet.DisplayTitle}|{_titleSelected}|{string.Join(",", titles.Select(t => t.Id))}";
        _setTitle.IsEnabled = _titleSelected != 0 && _titleSelected != _sheet.DisplayTitle;
        if (key == _drawnTitles) return;
        _drawnTitles = key;

        var current = new StackPanel { Spacing = 2, Margin = new Thickness(6) };
        current.Children.Add(Centred(Label("Current Display Title", 11, DerethPalette.MutedBrush)));
        current.Children.Add(Centred(Label(_sheet.DisplayTitle != 0 ? NameOf(_sheet.DisplayTitle) : "None", 15, DerethPalette.CreamBrush, title: true)));
        current.Children.Add(Centred(Label(titles.Count == 1 ? "1 title" : $"{titles.Count} titles", 11, DerethPalette.MutedBrush)));
        _currentTitle.Content = Plate(current, padding: 0);

        _titleList.Children.Clear();
        if (titles.Count == 0) _titleList.Children.Add(Centred(Label("No titles yet", 12, DerethPalette.MutedBrush)));
        foreach (var (id, name) in titles)
        {
            var selected = id == _titleSelected;
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
            grid.Children.Add(Label(name, 13, DerethPalette.TextBrush, title: true));
            if (id == _sheet.DisplayTitle)
            {
                var mark = Label("current", 10, DerethPalette.GoldBrush);
                Grid.SetColumn(mark, 1);
                grid.Children.Add(mark);
            }
            var border = new Border
            {
                Height = SkillRowHeight, Padding = new Thickness(6, 0), CornerRadius = new CornerRadius(2), Child = grid,
                Background = selected ? SelectedBrush : Transparent,
                BorderBrush = selected ? DerethPalette.GoldDarkBrush : Transparent, BorderThickness = new Thickness(0, 1)
            };
            border.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                _titleSelected = id;
                Refresh();
            };
            _titleList.Children.Add(border);
        }
    }

    /// <summary>Sets the selected title as the display title, as the button does.</summary>
    public void SetSelectedTitle()
    {
        if (_titleSelected == 0 || _titleSelected == _sheet.DisplayTitle) return;
        if (_game.SetTitle(_titleSelected)) _sheet.SetDisplayTitle(_titleSelected);
        Refresh();
    }

    private Control? Icon(uint id, bool square = false)
    {
        if (id == 0 || _art == null) return null;
        // Each art is decoded once; a control has one parent, so each row gets its own icon over the shared bitmap, at the art's own size.
        if (!_icons.TryGetValue(id, out var bitmap))
        {
            var image = _art.ReadImage(id);
            _icons[id] = bitmap = GameArtImageExtension.CreateBitmap(square && image != null ? Square(image) : image);
        }
        return bitmap == null ? null
            : new DerethItemIcon(bitmap) { Width = bitmap.PixelSize.Width, Height = bitmap.PixelSize.Height, VerticalAlignment = VerticalAlignment.Center };
    }

    // The art centred on a clear square as wide as its longer side.
    private static GameImage Square(GameImage image)
    {
        var side = Math.Max(image.Width, image.Height);
        var pixels = new byte[side * side * 4];
        int left = (side - image.Width) / 2, top = (side - image.Height) / 2;
        for (var y = 0; y < image.Height; y++)
            Buffer.BlockCopy(image.Pixels, y * image.Width * 4, pixels, ((top + y) * side + left) * 4, image.Width * 4);
        return new GameImage(side, side, pixels);
    }

    private static Border Plate(Control child, double padding = 7) => new()
    {
        Background = PlateBrush, BorderBrush = LineBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
        Padding = new Thickness(padding + 1, padding - 1, padding + 1, padding), Child = child
    };

    private static Control Pair(string label, string value, bool short_ = false)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        grid.Children.Add(Label(label, 11, DerethPalette.MutedBrush));
        var text = Label(value, 11, short_ ? DerethPalette.InvalidBrush : DerethPalette.TextBrush);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    /// <summary>A row with the progress filled in behind it, as retail fills its XP for next level row.</summary>
    private static Control Progress(Control row, double fraction)
    {
        var fill = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = { new GradientStop(DerethPalette.GoldDark.WithAlpha(0x60), 0), new GradientStop(DerethPalette.Gold.WithAlpha(0x60), 1) }
            }
        };
        row.Margin = new Thickness(3, 1);
        var well = new Grid();
        well.Children.Add(fill);
        well.Children.Add(row);
        var groove = new Border { CornerRadius = new CornerRadius(2), Background = DerethPalette.GrooveBrush, BorderBrush = LineBrush, BorderThickness = new Thickness(1), Child = well };
        well.SizeChanged += (_, e) => fill.Width = e.NewSize.Width * fraction;
        return groove;
    }

    private static TextBlock Centred(TextBlock text)
    {
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.TextAlignment = TextAlignment.Center;
        return text;
    }

    private static TextBlock Label(string text, double size, IBrush brush, bool title = false) => new()
    {
        Text = text, FontSize = size, Foreground = brush, FontFamily = title ? DerethPalette.Title : DerethPalette.Body,
        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
    };

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
