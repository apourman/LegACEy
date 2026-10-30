using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;
using LegACEy.GameArt;

namespace LegACEy.Client.Demo;

/// <summary>
/// LegACEy's spell bar: retail's spell bar laid out one for one (LayoutDesc 0x21000010 as the combat panel 0x21000073 places it, 610 by 90
/// with a 5 DIP frame) on retail's own tab, cell and button art, in the Dereth gilded frame the attack bar wears. It is open in magic only.
/// The tabs I to VIII open a tab; a click on a spell selects it (retail's selection frame, its name under the cells) and shows it in the
/// appraisal window, a double click casts it, and Cast casts the selected spell, each through <see cref="ISpellBarPort"/>. Like retail's, the
/// bar resizes in width only: the cells take the room, Cast keeps to the right. A drag takes a spell off the bar, as retail's does, and a release over the
/// cells puts it back where it is dropped; a spell from the spellbook dropped on the cells goes there too (<see cref="DropAt"/>). While a
/// spell is dragged, retail's accept ring marks the cell it would land in, and a tab under the pointer opens, so a drop moves the spell to
/// that tab: on its cells where dropped, on the tab itself at its end. A press on
/// the frame away from the controls drags the window, unless retail's UI is locked.
/// </summary>
public sealed class DerethSpellBarWindow : UserControl, IDisposable
{
    /// <summary>The window's id, under which its place and size are kept for the character.</summary>
    public const string WindowId = "dereth-spellbar";
    public const int DesignWidth = 610, DesignHeight = 90, MinimumWidth = 400, MaximumWidth = 1920;

    // Retail's places inside its 5 DIP frame: the tabs along the top, the cell list (23 DIPs into its row, between the scroll arrows), the
    // selected spell's name under it, and Cast at the right. Measured from the right: the right arrow's and Cast's left edges, and the room
    // the list leaves after it (at 600 DIPs inside, the list is 439 wide).
    private const double Frame = 5, TabWidth = 47, TabHeight = 20, Cell = 32, DragThreshold = 4;
    private const double CellsX = Frame + 63, CellsY = Frame + 24, CellsRightGap = 98;
    private const double ArrowY = Frame + 22, ArrowWidth = 23, ArrowHeight = 36, LeftArrowX = Frame + 40, RightArrowFromRight = 98;
    private const double CastY = Frame + 24, CastWidth = 75, NameX = Frame + 40, NameY = Frame + 60, NameHeight = 18;
    // Retail's art: the panel's tile, the open and the other tabs, an empty cell, Cast's left, middle and right (and pressed), the arrows.
    private const uint BackgroundArt = 0x06004CC2, OpenTabArt = 0x06005EB8, TabArt = 0x06005EB9, EmptyCellArt = 0x06001A97, SelectedArt = 0x06004D21,
        AcceptArt = 0x060011F9;
    private static readonly uint[] CastArt = { 0x06004C96, 0x06004C99, 0x06004C9C }, CastPressedArt = { 0x06004C95, 0x06004C98, 0x06004C9B };
    private const uint LeftArrowArt = 0x06004CDE, RightArrowArt = 0x06004CDC;
    private static readonly string[] Numerals = { "I", "II", "III", "IV", "V", "VI", "VII", "VIII" };
    // Retail numbers the first nine spells of a tab, for the keys that cast them.
    private const int NumberedSpells = 9;

    private readonly ISpellBarPort _port;
    private readonly Func<IReadOnlyDictionary<uint, SpellInfo>> _spells;
    private readonly IGameArtSource? _art;
    private readonly IItemDragHost? _drag;
    private readonly Action _startMove;
    private readonly Canvas _root;
    private readonly Border[] _tabs = new Border[Numerals.Length];
    private readonly Canvas _cells = new() { Height = Cell, ClipToBounds = true, Background = Brushes.Transparent };
    private readonly DerethGildedPlate _plate = new(6, ornate: false);
    private readonly Border _background = new();
    private readonly Control _castButton;
    private readonly TextBlock _name;
    private readonly Border _leftArrow, _rightArrow;
    private readonly DerethCornerMarks _corners = new() { IsVisible = false, IsHitTestVisible = false };
    private readonly Image[] _cast = new Image[3];
    private readonly Dictionary<uint, Bitmap?> _bitmaps = new();
    private readonly Dictionary<(uint Spell, int Number), WriteableBitmap?> _icons = new();
    private string _drawn = string.Empty;
    // The first spell shown, while a tab holds more than the cells show.
    private int _first;
    // A pressed spell and where, until a release or a drag; the spell a drag carries and the tab and index it left, and the cell it would
    // land before (-1 the end).
    private (uint Spell, Point At)? _pressed;
    private IDisposable? _dragIcon;
    private uint _dragSpell;
    private (int Tab, int Index) _dragFrom;
    private int? _dropAt;

    /// <param name="port">The game's spell bar and its controls.</param>
    /// <param name="spells">The portal's spell table, for each spell's icon and name.</param>
    /// <param name="art">The portal's art; without it the bar draws its frame only.</param>
    /// <param name="drag">Shows a dragged spell under the pointer; without it spells do not drag.</param>
    /// <param name="startMove">Starts the window's move from the pointer's place, on a press on the frame.</param>
    public DerethSpellBarWindow(ISpellBarPort port, Func<IReadOnlyDictionary<uint, SpellInfo>> spells, IGameArtSource? art, IItemDragHost? drag,
        Action startMove)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _spells = spells ?? throw new ArgumentNullException(nameof(spells));
        _art = art;
        _drag = drag;
        _startMove = startMove ?? throw new ArgumentNullException(nameof(startMove));

        _root = new Canvas { Background = Brushes.Transparent };
        _background.Background = Tile(BackgroundArt);
        Put(_plate, 0, 0, DesignWidth, DesignHeight);
        Put(_background, Frame, Frame, DesignWidth - 2 * Frame, DesignHeight - 2 * Frame);
        for (var i = 0; i < _tabs.Length; i++)
        {
            var tab = i;
            _tabs[i] = Pressable(new Border
            {
                Child = DerethPalette.CentreInk(new TextBlock
                {
                    Text = Numerals[i], FontFamily = DerethPalette.Title, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                })
            }, () => _port.Tab = tab);
            Put(_tabs[i], Frame + i * TabWidth, Frame, TabWidth, TabHeight);
        }
        Put(_cells, CellsX, CellsY, 0, Cell);
        _name = new TextBlock
        {
            FontFamily = DerethPalette.Title, FontSize = 13, Foreground = DerethPalette.TextBrush, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false
        };
        Put(_name, NameX, NameY, 0, NameHeight);
        _leftArrow = Pressable(new Border { Background = Stretched(LeftArrowArt) }, () => _first--);
        _rightArrow = Pressable(new Border { Background = Stretched(RightArrowArt) }, () => _first++);
        Put(_leftArrow, LeftArrowX, ArrowY, ArrowWidth, ArrowHeight);
        Put(_rightArrow, 0, ArrowY, ArrowWidth, ArrowHeight);
        _castButton = CastButton();
        Put(_castButton, 0, CastY, CastWidth, Cell);
        Put(_corners, 0, 0, DesignWidth, DesignHeight);
        // Hovering shows where the window resizes, unless retail's UI is locked.
        _root.PointerEntered += (_, _) => _corners.IsVisible = !_port.UiLocked;
        _root.PointerExited += (_, _) => _corners.IsVisible = false;
        SizeChanged += (_, e) => Place(e.NewSize.Width);
        Place(DesignWidth);

        _cells.PointerPressed += OnCellPressed;
        _cells.PointerMoved += OnCellMoved;
        _cells.PointerReleased += OnCellReleased;
        _cells.PointerCaptureLost += (_, _) =>
        {
            _pressed = null;
            EndDrag(null);
        };
        _root.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(_root).Properties.IsLeftButtonPressed && !_port.UiLocked) _startMove();
        };
        Content = _root;
        _port.Changed += OnPortChanged;
        Sync();
    }

    /// <summary>
    /// The window's definition: no title bar, so the frame drags it, at retail's size; it resizes in width only, as retail's does, unless
    /// retail's UI is locked. Per-window scaling draws it larger or smaller.
    /// </summary>
    public static WindowDefinition Definition(Func<bool> uiLocked) => new(WindowId, "Spell bar", DesignWidth, DesignHeight, titleBarHeight: 1,
        theme: new DerethClientTheme(), resizing: new WindowResizing(new System.Drawing.Size(MinimumWidth, DesignHeight),
            new System.Drawing.Size(MaximumWidth, DesignHeight), uiLocked), dragsFromContent: true);

    /// <summary>The first spell the cells show.</summary>
    public int First => _first;

    /// <summary>How many whole cells show at the window's width: 13 at retail's 610 DIPs, with the start of a fourteenth.</summary>
    public int VisibleCells => Math.Max(1, (int)(_cells.Width / Cell));

    /// <summary>The selected spell's name, as retail writes it under the cells; empty with none selected.</summary>
    public string SelectedName => _name.Text ?? string.Empty;

    /// <summary>Closes the bar; a drag still under way is cancelled, and its spell goes back where it was.</summary>
    public void Dispose()
    {
        EndDrag(null);
        _port.Changed -= OnPortChanged;
    }

    /// <summary>
    /// A spell dragged from another window was released at <paramref name="at"/>, in window DIPs: over the cells it goes before the spell
    /// under the pointer, or at the tab's end over the empty cells, as retail's drop does. False when the pointer is not over the cells.
    /// </summary>
    public bool DropAt(uint spell, Point at)
    {
        if (DropIndexAt(at) is not { } index) return false;
        _port.Insert(spell, index);
        Sync();
        return true;
    }

    /// <summary>
    /// A spell dragged from another window is over <paramref name="at"/>, in window DIPs: opens a tab under it and rings the cell the spell
    /// would land in; null, or away from the cells and tabs, clears the ring.
    /// </summary>
    public void HoverAt(Point? at)
    {
        var dropAt = at is { } point ? DropIndexAt(point) : null;
        if (dropAt == _dropAt) return;
        _dropAt = dropAt;
        Sync();
    }

    private void OnPortChanged(object? sender, EventArgs e) => Sync();

    private void OnCellPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_cells).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        var spells = _port.Spells;
        var index = SpellIndexAt(e.GetPosition(_cells).X, spells.Count);
        if (index < 0) return;
        var spell = spells[index];
        if (e.ClickCount > 1)
        {
            // Retail's double click casts: the first click has selected the spell already.
            _pressed = null;
            _port.Select(spell);
            _port.Cast();
            Sync();
            return;
        }
        _pressed = (spell, e.GetPosition(this));
        e.Pointer.Capture(_cells);
    }

    private void OnCellMoved(object? sender, PointerEventArgs e)
    {
        var point = e.GetPosition(this);
        if (_dragIcon != null)
        {
            HoverAt(point);
            return;
        }
        if (_pressed is not { } press || _drag == null) return;
        if (Math.Abs(point.X - press.At.X) < DragThreshold && Math.Abs(point.Y - press.At.Y) < DragThreshold) return;
        // Retail takes a spell off its bar as the drag starts; a release over the cells puts it back.
        var number = Index(press.Spell);
        _dragSpell = press.Spell;
        _dragFrom = (_port.Tab, number);
        _port.Remove(press.Spell);
        _dragIcon = _drag.ShowDragIcon(IconImage(press.Spell, number), 1, retailDropIndicator: false);
        HoverAt(point);
    }

    private void OnCellReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressed is not { } press) return;
        e.Handled = true;
        _pressed = null;
        if (_dragIcon != null)
        {
            EndDrag(e.GetPosition(this));
            return;
        }
        if (!new Rect(_cells.Bounds.Size).Contains(e.GetPosition(_cells))) return;
        _port.Select(press.Spell);
        _port.Examine(press.Spell);
        Sync();
    }

    /// <summary>
    /// Ends a drag of a spell off the bar: released at <paramref name="at"/> over the cells or a tab, it goes there; released anywhere else it
    /// stays off the bar, as retail's does. A drag cancelled without a release (null) puts it back where it was.
    /// </summary>
    private void EndDrag(Point? at)
    {
        if (_dragIcon == null) return;
        _dragIcon.Dispose();
        _dragIcon = null;
        _dropAt = null;
        var spell = _dragSpell;
        _dragSpell = 0;
        if (at is { } point)
        {
            DropAt(spell, point);
            return;
        }
        _port.Tab = _dragFrom.Tab;
        _port.Insert(spell, _dragFrom.Index);
        Sync();
    }

    /// <summary>Where a spell released at a window point goes: the index it goes before, -1 for the end, or null away from the cells.</summary>
    private int? DropIndexAt(Point at)
    {
        // Over a tab, the tab opens and the spell would go at its end.
        if (at.Y >= Frame && at.Y < Frame + TabHeight && at.X >= Frame && at.X < Frame + _tabs.Length * TabWidth)
        {
            var tab = (int)((at.X - Frame) / TabWidth);
            if (_port.Tab != tab) _port.Tab = tab;
            return -1;
        }
        if (!new Rect(CellsX, CellsY, _cells.Width, Cell).Contains(at)) return null;
        return SpellIndexAt(at.X - CellsX, _port.Spells.Count);
    }

    /// <summary>The index of the spell at x in the cells, or -1 over an empty cell.</summary>
    private int SpellIndexAt(double x, int count)
    {
        var index = _first + (int)Math.Floor(x / Cell);
        return index >= 0 && index < count ? index : -1;
    }

    private int Index(uint spell)
    {
        var spells = _port.Spells;
        for (var i = 0; i < spells.Count; i++) if (spells[i] == spell) return i;
        return -1;
    }

    /// <summary>Shows what the port holds: the open tab, and the tab's spells from the first shown.</summary>
    private void Sync()
    {
        var open = _port.Tab;
        for (var i = 0; i < _tabs.Length; i++)
        {
            _tabs[i].Background = Stretched(i == open ? OpenTabArt : TabArt);
            ((TextBlock)_tabs[i].Child!).Foreground = i == open ? DerethPalette.TextBrush : DerethPalette.MutedBrush;
        }
        var spells = _port.Spells;
        var selected = _port.Selected;
        _first = Math.Max(0, Math.Min(_first, spells.Count - VisibleCells));
        _leftArrow.IsVisible = _rightArrow.IsVisible = spells.Count > VisibleCells;
        _leftArrow.Opacity = _first > 0 ? 1 : 0.5;
        _rightArrow.Opacity = _first < spells.Count - VisibleCells ? 1 : 0.5;
        _name.Text = selected != 0 && _spells().TryGetValue(selected, out var named) ? named.Name : string.Empty;
        var drawn = $"{open}|{_first}|{selected}|{_dropAt}|{_cells.Width}|{string.Join(",", spells)}";
        if (drawn == _drawn) return;
        _drawn = drawn;
        _cells.Children.Clear();
        // One more cell than fit whole, so the list's last DIPs show the start of the next as retail's do.
        for (var position = 0; position <= VisibleCells; position++)
        {
            var index = _first + position;
            var x = position * Cell;
            Add(_cells, new Image { Source = Bitmap(EmptyCellArt), Width = Cell, Height = Cell }, x);
            if (index < spells.Count)
            {
                var spell = spells[index];
                var icon = Icon(spell, index);
                Control face = icon == null ? new Border { Background = DerethPalette.PlaqueBrush } : new DerethItemIcon(icon);
                ToolTip.SetTip(face, _spells().TryGetValue(spell, out var info) ? info.Name : null);
                Add(_cells, face, x);
                if (spell == selected) Add(_cells, Overlay(SelectedArt), x);
            }
            // Retail's accept ring on the cell a dragged spell would land in; at the end, the first empty cell.
            if (_dropAt is { } at && (at == index || at < 0 && index == spells.Count)) Add(_cells, Overlay(AcceptArt), x);
        }
    }

    /// <summary>Lays the bar out for its width: the frame, the background and the cells stretch, the right arrow and Cast keep to the right.</summary>
    private void Place(double width)
    {
        width = Math.Max(MinimumWidth, width);
        var inside = width - 2 * Frame;
        _plate.Width = _corners.Width = width;
        _background.Width = inside;
        _cells.Width = inside - (CellsX - Frame) - CellsRightGap;
        _name.Width = inside - (NameX - Frame) - CastWidth;
        Canvas.SetLeft(_rightArrow, Frame + inside - RightArrowFromRight);
        Canvas.SetLeft(_castButton, Frame + inside - CastWidth);
        Sync();
    }

    /// <summary>A spell's icon as retail composes it, with retail's key tab over the first nine, drawn once each.</summary>
    private WriteableBitmap? Icon(uint spell, int index)
    {
        var number = index >= 0 && index < NumberedSpells ? index : -1;
        if (!_icons.TryGetValue((spell, number), out var bitmap)) _icons[(spell, number)] = bitmap = GameArtImageExtension.CreateBitmap(IconImage(spell, number));
        return bitmap;
    }

    /// <summary>The icon's image, with the key tab for <paramref name="number"/> 0 to 8; null without art or the spell.</summary>
    private GameImage? IconImage(uint spell, int number)
    {
        if (_art == null || !_spells().TryGetValue(spell, out var info) || SpellIcon.Draw(_art, info) is not { } image) return null;
        if (number < 0 || number >= NumberedSpells || _art.ReadImage(ShortcutBar.TabOf(number)) is not { } tab || tab.Pixels.Length != image.Pixels.Length)
            return image;
        return new GameImage(image.Width, image.Height, ItemIconOutline.Over(tab.Pixels, image.Pixels));
    }

    /// <summary>Retail's Cast button, from its three pieces, darker while pressed; a press casts the selected spell.</summary>
    private Control CastButton()
    {
        var pieces = new Grid { ColumnDefinitions = new ColumnDefinitions("32,*,32") };
        for (var i = 0; i < 3; i++)
        {
            _cast[i] = new Image { Source = Bitmap(CastArt[i]), Stretch = Stretch.Fill };
            Grid.SetColumn(_cast[i], i);
            pieces.Children.Add(_cast[i]);
        }
        var label = DerethPalette.CentreInk(new TextBlock
        {
            Text = "Cast", FontFamily = DerethPalette.Title, FontSize = 13, Foreground = DerethPalette.TextBrush,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumnSpan(label, 3);
        pieces.Children.Add(label);
        var button = new Border { Child = pieces, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
        void Pressed(bool down)
        {
            for (var i = 0; i < 3; i++) _cast[i].Source = Bitmap(down ? CastPressedArt[i] : CastArt[i]);
        }
        button.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            Pressed(true);
            _port.Cast();
        };
        button.PointerReleased += (_, _) => Pressed(false);
        button.PointerCaptureLost += (_, _) => Pressed(false);
        return button;
    }

    /// <summary>A control that runs <paramref name="press"/> on a left press, then shows the port again.</summary>
    private T Pressable<T>(T control, Action press) where T : Control
    {
        control.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            press();
            Sync();
        };
        return control;
    }

    private Image Overlay(uint art) => new() { Source = Bitmap(art), Width = Cell, Height = Cell, IsHitTestVisible = false };

    private void Put(Control control, double x, double y, double width, double height)
    {
        control.Width = width;
        control.Height = height;
        Canvas.SetLeft(control, x);
        Canvas.SetTop(control, y);
        _root.Children.Add(control);
    }

    private static void Add(Canvas canvas, Control control, double x)
    {
        Canvas.SetLeft(control, x);
        canvas.Children.Add(control);
    }

    private Bitmap? Bitmap(uint id)
    {
        if (!_bitmaps.TryGetValue(id, out var bitmap)) _bitmaps[id] = bitmap = GameArtImageExtension.CreateBitmap(_art, id);
        return bitmap;
    }

    private IBrush? Stretched(uint id) => Bitmap(id) is { } bitmap ? new ImageBrush(bitmap) { Stretch = Stretch.Fill } : DerethPalette.PlaqueBrush;

    private IBrush? Tile(uint id) => Bitmap(id) is { } bitmap
        ? new ImageBrush(bitmap)
        {
            TileMode = TileMode.Tile, Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
            DestinationRect = new RelativeRect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height, RelativeUnit.Absolute)
        }
        : DerethPalette.PlaqueBrush;
}
