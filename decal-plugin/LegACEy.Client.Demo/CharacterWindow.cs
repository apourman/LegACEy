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
/// experience, the list, and a plate at the bottom that raises or trains the selected row. Every group (attributes, vitals and each skill group) folds, and the folds are
/// remembered. One instance serves the session; <see cref="Refresh"/> redraws what changed.
/// </summary>
public sealed class CharacterWindow
{
    public const string WindowId = "character";
    public const int Width = 310, Height = 640;
    public static readonly WindowResizing Sizing = new(new System.Drawing.Size(280, 360));
    private const double SkillRowHeight = 21, AttributeRowHeight = 27, BandHeight = 20;

    private static readonly IBrush Transparent = Brushes.Transparent;
    private static readonly IBrush HoverBrush = DerethPalette.Brush(DerethPalette.Gold.WithAlpha(0x12));
    private static readonly IBrush SelectedBrush = DerethPalette.Brush(DerethPalette.Gold.WithAlpha(0x22));
    private static readonly IBrush PlateBrush = DerethPalette.Brush(Color.FromArgb(0x8C, 0x08, 0x07, 0x05));
    private static readonly IBrush LineBrush = DerethPalette.Brush(DerethPalette.Line);
    private static readonly Dictionary<CharacterGroup, Color> GroupColours = new()
    {
        [CharacterGroup.Attributes] = DerethPalette.Gold,
        [CharacterGroup.Vitals] = Color.FromRgb(176, 48, 42),
        [CharacterGroup.Specialized] = Color.FromRgb(140, 96, 220),
        [CharacterGroup.Trained] = Color.FromRgb(63, 160, 170),
        [CharacterGroup.Untrained] = Color.FromRgb(200, 150, 60),
        [CharacterGroup.Unusable] = Color.FromRgb(130, 130, 140)
    };
    private static readonly Dictionary<CharacterGroup, string> GroupNames = new()
    {
        [CharacterGroup.Attributes] = "Attributes",
        [CharacterGroup.Vitals] = "Vitals",
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
    // The untrained skill whose Train was pressed once: a second press spends the credits.
    private uint _confirmTrain;

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

        _tabs = new DerethSegmented(TabNames) { Margin = new Thickness(1, 6, 1, 4) };
        _tabs.Chosen += index =>
        {
            _tab = index;
            _selected = null;
            _confirmTrain = 0;
            _scroll!.Offset = default;
            Show();
        };

        _plaque = new ContentControl { Margin = new Thickness(1, 0, 1, 4) };
        _list = new StackPanel();
        _scroll = new ScrollViewer
        {
            Content = _list,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
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

        _page = new ContentControl();
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        body.Children.Add(_tabs);
        Grid.SetRow(_page, 1);
        body.Children.Add(_page);
        Window = new DerethWindow("Character", null, body);
        Window.CloseRequested += (_, _) => Close?.Invoke();
        Show();
    }

    public DerethWindow Window { get; }

    /// <summary>Runs when the player closes the window with its close box.</summary>
    public Action? Close { get; set; }

    /// <summary>The open tab: 0 Attributes, 1 Skills, 2 Titles.</summary>
    public int Tab => _tab;

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
        _confirmTrain = 0;
        // The highlight is drawn with the rows.
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

    /// <summary>The first press arms the train; the second spends the skill's credits.</summary>
    public void Train()
    {
        if (SelectedRow is not { TrainCost: > 0 } row || row.TrainCost > _sheet.SkillCredits) return;
        if (_confirmTrain != row.Id)
        {
            _confirmTrain = row.Id;
            Refresh();
            return;
        }
        _confirmTrain = 0;
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
        if (_drawnRows == null || !_drawnRows.SequenceEqual(_rows)) DrawList(tables);
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
        figures.Children.Add(Pair("XP for next level", Number(needed)));
        figures.Children.Add(Bar(fraction));
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

    private void DrawList(CharacterTables tables)
    {
        _drawnRows = _rows;
        _list.Children.Clear();
        // Every group folds from its band: the attributes and vitals as the skill groups do.
        var height = _tab == 0 ? AttributeRowHeight : SkillRowHeight;
        foreach (var group in _rows.GroupBy(row => row.Group))
        {
            _list.Children.Add(Band(group.Key, group.Count()));
            if (_folded.Contains(group.Key)) continue;
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
            Select(row.Kind, row.Id);
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

    private void DrawFoot()
    {
        var row = SelectedRow;
        var key = row == null ? $"none|{_tab}|{_sheet.SkillCredits}|{_sheet.UnassignedExperience}"
            : $"{row}|{_sheet.SkillCredits}|{_sheet.UnassignedExperience}|{_confirmTrain}";
        if (key == _drawnFoot) return;
        _drawnFoot = key;

        var stack = new StackPanel { Spacing = 3 };
        if (row == null)
        {
            stack.Children.Add(Centred(Label(_tab == 1 ? "Select a Skill to Improve" : "Select an Attribute to Improve", 14, DerethPalette.CreamBrush, title: true)));
            stack.Children.Add(Pair("Skill Credits Available", _sheet.SkillCredits.ToString(CultureInfo.InvariantCulture)));
            stack.Children.Add(Pair("Unassigned Experience", Number(_sheet.UnassignedExperience)));
            _foot.Content = Plate(stack);
            return;
        }

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6, MinHeight = 24 };
        head.Children.Add(Label($"{row.Name}: {ValueText(row)}", 14, DerethPalette.CreamBrush, title: true));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(buttons, 1);
        head.Children.Add(buttons);
        stack.Children.Add(head);

        if (row.TrainCost > 0)
        {
            var affordable = row.TrainCost <= _sheet.SkillCredits;
            var armed = _confirmTrain == row.Id;
            var train = Button(armed ? $"Confirm · {row.TrainCost} credits" : "Train", affordable, Train);
            ToolTip.SetTip(train, armed ? "Press again to spend the credits" : $"Spend {row.TrainCost} skill credits to train {row.Name}");
            buttons.Children.Add(train);
            stack.Children.Add(Pair("Credits To Train", row.TrainCost.ToString(CultureInfo.InvariantCulture), short_: !affordable));
            stack.Children.Add(Pair("Skill Credits Available", _sheet.SkillCredits.ToString(CultureInfo.InvariantCulture)));
        }
        else if (row.NextCost is { } next)
        {
            var unassigned = _sheet.UnassignedExperience;
            buttons.Children.Add(Button("+10", row.TenCost is { } ten && ten > 0 && ten <= unassigned, () => Raise(ten: true)));
            buttons.Children.Add(Button("Raise", next > 0 && next <= unassigned, () => Raise(ten: false)));
            stack.Children.Add(Pair("Experience To Raise", Number(next), short_: next > unassigned));
            stack.Children.Add(Pair("Unassigned Experience", Number(unassigned)));
        }
        else
        {
            stack.Children.Add(Pair("Experience To Raise", row.AtMax ? "Max" : "–"));
            stack.Children.Add(Pair("Unassigned Experience", Number(_sheet.UnassignedExperience)));
        }
        _foot.Content = Plate(stack);
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

    /// <summary>Selects a title, as a click on it does.</summary>
    public void SelectTitle(uint title)
    {
        _titleSelected = title;
        Refresh();
    }

    /// <summary>Sets the selected title as the display title, as the button does.</summary>
    public void SetSelectedTitle()
    {
        if (_titleSelected == 0 || _titleSelected == _sheet.DisplayTitle) return;
        if (_game.SetTitle(_titleSelected)) _sheet.SetDisplayTitle(_titleSelected);
        Refresh();
    }

    private Control? Icon(uint id)
    {
        if (id == 0 || _art == null) return null;
        // Each art is decoded once; a control has one parent, so each row gets its own icon over the shared bitmap, at the art's own size.
        if (!_icons.TryGetValue(id, out var bitmap)) _icons[id] = bitmap = GameArtImageExtension.CreateBitmap(_art.ReadImage(id));
        return bitmap == null ? null
            : new DerethItemIcon(bitmap) { Width = bitmap.PixelSize.Width, Height = bitmap.PixelSize.Height, VerticalAlignment = VerticalAlignment.Center };
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

    private static Control Bar(double fraction)
    {
        var fill = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = { new GradientStop(DerethPalette.GoldDark, 0), new GradientStop(DerethPalette.Gold, 1) }
            }
        };
        var groove = new Border
        {
            Height = 4, CornerRadius = new CornerRadius(2), Background = DerethPalette.GrooveBrush, BorderBrush = LineBrush,
            BorderThickness = new Thickness(1), Margin = new Thickness(0, 2, 0, 0), Child = fill
        };
        groove.SizeChanged += (_, e) => fill.Width = Math.Max(0, (e.NewSize.Width - 2) * fraction);
        return groove;
    }

    private static DerethButton Button(string text, bool enabled, Action click)
    {
        var button = new DerethButton { Content = Label(text, 11.5, DerethPalette.CreamBrush), Padding = new Thickness(9, 2), MinHeight = 22, IsEnabled = enabled };
        button.Click += (_, _) => click();
        return button;
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
