using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;

namespace LegACEy.Plugin.Inventory;

/// <summary>
/// The inventory in one layout, drawn with the Dereth parts from the port's snapshot. It re-renders when the port changes.
/// Its own toggles (layout and Slots) are local and report through <see cref="SettingsChanged"/>; the owner decides what a layout
/// change does. Selection, use and moves are not handled yet: every slot carries its item, container and slot index for them.
/// </summary>
public sealed class InventoryWindow : UserControl, IDisposable
{
    internal const uint BackpackIcon = 0x0600127E;
    internal const string Title = "Inventory";
    // The pyreal stack's icon in the portal.
    private const uint PyrealIcon = 0x06001080;
    private const double EmptyOpacity = 0.55;
    private const string StackedGlyph = "M2,1 H12 V6 H2 Z M2,8 H12 V13 H2 Z";
    private const string SideBySideGlyph = "M1,2 H6 V12 H1 Z M8,2 H13 V12 H8 Z";

    private static readonly IBrush GoldBrush = DerethPalette.GoldBrush;
    private static readonly IBrush TextBrush = DerethPalette.TextBrush;
    private static readonly IBrush MutedBrush = DerethPalette.MutedBrush;
    private static readonly IBrush InvalidBrush = DerethPalette.InvalidBrush;
    private static readonly IBrush TealTextBrush = DerethPalette.Brush(DerethPalette.TealText);
    private static readonly IBrush GoldFillBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#E2C27E"), 0), new GradientStop(Color.Parse("#9C7A3C"), 1) },
    };

    private readonly IInventoryPort _port;
    private readonly IGameArtSource _art;
    private readonly InventoryLayout _layout;
    private readonly Dictionary<ItemVisual, WriteableBitmap?> _images = new();
    private readonly DerethWindow _frame;
    private readonly InventoryPaperdoll _paperdoll = new();
    private readonly DerethSlotGrid _grid = new();
    private readonly TextBlock _contentsName = Label(string.Empty, TextBrush, 13);
    private readonly TextBlock _contentsCount = Label(string.Empty, MutedBrush, 13);
    // The pack list: a column in the vertical layout, a strip of tabs in the horizontal one.
    private readonly Panel _packList;
    private readonly ContentControl _burdenMeter = new();
    private readonly TextBlock _burdenText = Label(string.Empty, TextBrush, 12);
    private readonly TextBlock _pyrealCount = Label(string.Empty, GoldBrush, 12);
    private bool _showSlots;
    private bool _disposed;

    /// <param name="port">The character's inventory. The window reads its snapshot and re-renders on its change event.</param>
    /// <param name="art">The game art the icons are drawn from.</param>
    /// <param name="settings">The layout this window draws, and whether the armour slots show.</param>
    public InventoryWindow(IInventoryPort port, IGameArtSource art, InventorySettings settings)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _art = art ?? throw new ArgumentNullException(nameof(art));
        _layout = settings.Layout;
        _showSlots = settings.ShowSlots;
        _packList = _layout == InventoryLayout.Vertical
            ? new StackPanel { Spacing = 4 }
            : new WrapPanel { Orientation = Orientation.Horizontal };

        var body = _layout == InventoryLayout.Vertical ? VerticalBody() : HorizontalBody();
        _frame = new DerethWindow(Title, Icon(new ItemVisual(BackpackIcon, 0, 0, 0)), body, LayoutToggles());
        _frame.CloseRequested += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        Content = _frame;

        _paperdoll.SlotsToggle.Click += (_, _) => ToggleSlots();
        _paperdoll.SetSlotsOn(_showSlots);
        _port.Changed += OnChanged;
        Render(_port.Snapshot);
    }

    /// <summary>The window's current settings: its layout, and the Slots toggle.</summary>
    public InventorySettings Settings => new(_layout, _showSlots);

    /// <summary>Raised when the player changes the layout or the Slots toggle. The settings are the new ones.</summary>
    public event Action<InventorySettings>? SettingsChanged;

    /// <summary>Raised when the header's close box is pressed.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The doll area under the paperdoll, which the 3D character fills. Its content is the owner's to set.</summary>
    public Border DollArea => _paperdoll.DollArea;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _port.Changed -= OnChanged;
        foreach (var image in _images.Values) image?.Dispose();
        _images.Clear();
    }

    private void OnChanged()
    {
        if (!_disposed) Render(_port.Snapshot);
    }

    /// <summary>
    /// Sets the Slots toggle from outside, as the owner does when the other layout changed it while this window was hidden.
    /// It redraws without raising <see cref="SettingsChanged"/>.
    /// </summary>
    public void SetShowSlots(bool show)
    {
        if (_disposed || show == _showSlots) return;
        _showSlots = show;
        _paperdoll.SetSlotsOn(show);
        Render(_port.Snapshot);
    }

    private void ToggleSlots()
    {
        SetShowSlots(!_showSlots);
        SettingsChanged?.Invoke(Settings);
    }

    private void RequestLayout(InventoryLayout layout)
    {
        if (layout != _layout) SettingsChanged?.Invoke(new InventorySettings(layout, _showSlots));
    }

    private void Render(InventorySnapshot snapshot)
    {
        if (_disposed) return;
        var open = OpenPack(snapshot);
        var worn = new Dictionary<PaperdollSlot, WieldedItem>();
        foreach (var item in snapshot.Wielded)
            foreach (var slot in item.Slots)
                worn[slot] = item;
        _paperdoll.Show(slot => WornSlot(snapshot, worn, slot), _showSlots);
        RenderPacks(snapshot, open);
        RenderContents(snapshot, open);
        RenderBurden(snapshot);
        _pyrealCount.Text = snapshot.Pyreals.ToString("N0");
    }

    /// <summary>The pack the grid shows: the port's open container, or the main pack when the port has none of the listed packs open.</summary>
    private static InventoryPack OpenPack(InventorySnapshot snapshot) =>
        snapshot.OpenContainer == snapshot.MainPack.Id ? snapshot.MainPack
            : snapshot.SidePacks.FirstOrDefault(pack => pack.Id != 0 && pack.Id == snapshot.OpenContainer) ?? snapshot.MainPack;

    private InventorySlot WornSlot(InventorySnapshot snapshot, Dictionary<PaperdollSlot, WieldedItem> worn, PaperdollSlot slot)
    {
        if (!worn.TryGetValue(slot, out var item))
            return new InventorySlot(SlotPlace.Paperdoll, 0, 0, -1, slot, InventoryGlyphs.For(slot));
        return new InventorySlot(SlotPlace.Paperdoll, item.Id, 0, -1, slot, Layers(item.Visual, item.StackCount))
        {
            Selected = item.Id == snapshot.Selected,
        };
    }

    private void RenderPacks(InventorySnapshot snapshot, InventoryPack open)
    {
        _packList.Children.Clear();
        if (_layout == InventoryLayout.Vertical)
        {
            _packList.Children.Add(PackColumnSlot(snapshot, snapshot.MainPack, open));
            _packList.Children.Add(new DerethRule { Margin = new Thickness(0, 2), Opacity = 0.7 });
            foreach (var pack in snapshot.SidePacks) _packList.Children.Add(PackColumnSlot(snapshot, pack, open));
            return;
        }
        _packList.Children.Add(PackTab(snapshot, snapshot.MainPack, open));
        _packList.Children.Add(new Border { Width = 1, Background = DerethPalette.GrooveEdgeBrush, Margin = new Thickness(3, 4) });
        foreach (var pack in snapshot.SidePacks) _packList.Children.Add(PackTab(snapshot, pack, open));
    }

    /// <summary>A pack in the vertical column: its icon, with a fill bar along the foot. An empty side-pack slot is a placeholder.</summary>
    private InventorySlot PackColumnSlot(InventorySnapshot snapshot, InventoryPack pack, InventoryPack open)
    {
        if (pack.Id == 0)
            return new InventorySlot(SlotPlace.Pack, 0, 0, -1, null, null) { Opacity = EmptyOpacity, HorizontalAlignment = HorizontalAlignment.Center };
        var layers = new Grid();
        layers.Children.Add(Icon(new ItemVisual(pack.Icon, 0, 0, 0)));
        layers.Children.Add(FillBar(ItemsIn(snapshot, pack.Id), pack.Capacity));
        return new InventorySlot(SlotPlace.Pack, 0, pack.Id, -1, null, layers)
        {
            Selected = pack.Id == open.Id, HorizontalAlignment = HorizontalAlignment.Center,
        };
    }

    /// <summary>A pack in the horizontal strip: its icon, with "n/cap" under it. An empty side-pack slot is a placeholder.</summary>
    private Control PackTab(InventorySnapshot snapshot, InventoryPack pack, InventoryPack open)
    {
        var stack = new StackPanel { Spacing = 2, Width = DerethSlotGrid.Pitch };
        if (pack.Id == 0)
        {
            stack.Children.Add(new InventorySlot(SlotPlace.Pack, 0, 0, -1, null, null) { Opacity = EmptyOpacity });
            return stack;
        }
        var selected = pack.Id == open.Id;
        stack.Children.Add(new InventorySlot(SlotPlace.Pack, 0, pack.Id, -1, null, Icon(new ItemVisual(pack.Icon, 0, 0, 0))) { Selected = selected });
        var count = Label($"{ItemsIn(snapshot, pack.Id)}/{pack.Capacity}", selected ? TealTextBrush : MutedBrush, 10);
        count.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(count);
        return stack;
    }

    private void RenderContents(InventorySnapshot snapshot, InventoryPack open)
    {
        var items = new Dictionary<int, InventoryItem>();
        foreach (var item in snapshot.Items)
            if (item.Container == open.Id) items[item.Slot] = item;
        _contentsName.Text = "Contents of " + open.Name;
        _contentsCount.Text = $"{ItemsIn(snapshot, open.Id)} / {open.Capacity}";
        // One cell per slot of the container, empty ones included, in slot-index order. The grid keeps its scroll position.
        _grid.Cells.Clear();
        for (var index = 0; index < open.Capacity; index++)
        {
            items.TryGetValue(index, out var item);
            _grid.Cells.Add(Cell(snapshot, open.Id, index, item));
        }
    }

    private InventorySlot Cell(InventorySnapshot snapshot, uint container, int index, InventoryItem? item) =>
        item == null
            ? new InventorySlot(SlotPlace.Cell, 0, container, index, null, null) { Opacity = EmptyOpacity }
            : new InventorySlot(SlotPlace.Cell, item.Id, container, index, null, Layers(item.Visual, item.StackCount))
            {
                Selected = item.Id == snapshot.Selected,
            };

    private void RenderBurden(InventorySnapshot snapshot)
    {
        var over = snapshot.BurdenLimit > 0 && snapshot.Burden > snapshot.BurdenLimit;
        var percent = snapshot.BurdenLimit > 0 ? snapshot.Burden * 100 / snapshot.BurdenLimit : 0;
        var vertical = _layout == InventoryLayout.Vertical;
        _burdenMeter.Content = BurdenMeter(snapshot, over, vertical);
        // Over the limit the reading turns red as well as the meter, so it reads at a glance.
        _burdenText.Text = vertical ? $"{percent}%" : $"{snapshot.Burden:N0} / {snapshot.BurdenLimit:N0}  ·  {percent}%";
        _burdenText.Foreground = over ? InvalidBrush : TextBrush;
    }

    private static int ItemsIn(InventorySnapshot snapshot, uint container) => snapshot.Items.Count(item => item.Container == container);

    private static Control FillBar(int count, int capacity)
    {
        var share = capacity <= 0 ? 0 : Math.Min(1.0, (double)count / capacity);
        var track = new Border
        {
            Width = 30, Height = 3, Background = DerethPalette.GrooveBrush, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 2),
        };
        track.Child = new Border { Width = 30 * share, HorizontalAlignment = HorizontalAlignment.Left, Background = GoldBrush };
        return track;
    }

    /// <summary>A groove filled in gold to the burden. Over the limit the fill turns red.</summary>
    private static Control BurdenMeter(InventorySnapshot snapshot, bool over, bool vertical)
    {
        var share = snapshot.BurdenLimit <= 0 ? 0 : Math.Min(1.0, (double)snapshot.Burden / snapshot.BurdenLimit);
        var track = new Border
        {
            Background = DerethPalette.GrooveBrush, BorderBrush = DerethPalette.GrooveEdgeBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2), Padding = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
        };
        if (vertical)
        {
            track.Width = 12;
            track.Height = 62;
        }
        else
        {
            track.Height = 8;
        }
        var fill = new Border { Background = over ? InvalidBrush : GoldFillBrush, CornerRadius = new CornerRadius(1) };
        var holder = new Grid();
        if (vertical)
        {
            holder.RowDefinitions = new RowDefinitions($"{1 - share:0.###}*,{share:0.###}*");
            Grid.SetRow(fill, 1);
        }
        else
        {
            holder.ColumnDefinitions = new ColumnDefinitions($"{share:0.###}*,{1 - share:0.###}*");
        }
        holder.Children.Add(fill);
        track.Child = holder;
        return track;
    }

    /// <summary>An item's icon and, for a stack, its count, in a slot-sized layer stack.</summary>
    private Grid Layers(ItemVisual visual, int stack)
    {
        var layers = Icon(visual);
        if (stack > 1) layers.Children.Add(StackCount(stack));
        return layers;
    }

    /// <summary>An icon at native size; a question mark when the art is missing.</summary>
    private Grid Icon(ItemVisual visual)
    {
        var layers = new Grid { Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var bitmap = Bitmap(visual);
        if (bitmap != null)
            layers.Children.Add(new Image { Source = bitmap, Width = 32, Height = 32, Stretch = Stretch.None });
        else
            layers.Children.Add(Centred(Label("?", MutedBrush, 12)));
        return layers;
    }

    /// <summary>The drawn icon, cached per look. The grid redraws on every change, so the bitmaps are kept.</summary>
    private WriteableBitmap? Bitmap(ItemVisual visual)
    {
        if (!_images.TryGetValue(visual, out var bitmap))
            _images.Add(visual, bitmap = GameArtImageExtension.CreateBitmap(ItemIcon.Draw(_art, visual.Underlay, visual.Icon, visual.Overlay, 0, visual.UiEffects)));
        return bitmap;
    }

    private static Control StackCount(int count)
    {
        var text = count >= 10000 ? $"{count / 1000}k" : count.ToString();
        var panel = new Panel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 3, 1) };
        var shadow = Label(text, Brushes.Black, 11);
        shadow.FontWeight = FontWeight.SemiBold;
        shadow.Margin = new Thickness(1, 1, 0, 0);
        var front = Label(text, TextBrush, 11);
        front.FontWeight = FontWeight.SemiBold;
        front.Margin = new Thickness(0, 0, 1, 1);
        panel.Children.Add(shadow);
        panel.Children.Add(front);
        return panel;
    }

    private Control VerticalBody()
    {
        var left = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*") };
        left.Children.Add(At(_paperdoll, 0));
        left.Children.Add(At(new DerethRule { Margin = new Thickness(0, 8, 0, 0), Opacity = 0.7 }, 1));
        left.Children.Add(At(ContentsLine(), 2));
        left.Children.Add(At(_grid, 3));

        // The pack column: burden at its head, then the main pack, a rule and the side packs, which scroll.
        var packs = new ScrollViewer
        {
            Content = _packList, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 4, 0, 0),
        };
        var column = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Width = 44 };
        column.Children.Add(At(Centred(_burdenMeter), 0));
        column.Children.Add(At(Centred(_burdenText), 1));
        column.Children.Add(At(packs, 2));

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("*,Auto"), ColumnSpacing = 8, Margin = new Thickness(2, 8, 2, 0) };
        body.Children.Add(At(left, 0, 0));
        body.Children.Add(At(column, 0, 1));
        var footer = PyrealsFooter();
        body.Children.Add(At(footer, 1, 0));
        Grid.SetColumnSpan(footer, 2);
        return body;
    }

    private Control HorizontalBody()
    {
        var left = new StackPanel { Children = { _paperdoll } };
        var rule = new Border { Width = 1, Background = DerethPalette.Brush(DerethPalette.Gold.WithAlpha(0x40)), Margin = new Thickness(0, 4) };

        var right = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        _packList.Margin = new Thickness(0, 0, 0, 6);
        right.Children.Add(At(_packList, 0));
        right.Children.Add(At(ContentsLine(), 1));
        right.Children.Add(At(_grid, 2));

        // The footer: pyreals, then the burden meter with its reading.
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), ColumnSpacing = 8, Margin = new Thickness(2, 6, 2, 0) };
        footer.Children.Add(PyrealsLine());
        footer.Children.Add(At(Label("Burden", MutedBrush, 12), 0, 1));
        // The meter stretches across its star column, so it has a width to fill.
        _burdenMeter.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(At(_burdenMeter, 0, 2));
        footer.Children.Add(At(_burdenText, 0, 3));

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), RowDefinitions = new RowDefinitions("*,Auto"), ColumnSpacing = 10, Margin = new Thickness(2, 8, 2, 0) };
        body.Children.Add(At(left, 0, 0));
        body.Children.Add(At(rule, 0, 1));
        body.Children.Add(At(right, 0, 2));
        body.Children.Add(At(footer, 1, 0));
        Grid.SetColumnSpan(footer, 3);
        return body;
    }

    private Control LayoutToggles()
    {
        var toggles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        toggles.Children.Add(LayoutToggle(InventoryLayout.Vertical, StackedGlyph));
        toggles.Children.Add(LayoutToggle(InventoryLayout.Horizontal, SideBySideGlyph));
        return toggles;
    }

    /// <summary>A header button that switches to its layout. The window's own layout is lit teal.</summary>
    private Control LayoutToggle(InventoryLayout layout, string glyph)
    {
        var active = layout == _layout;
        var icon = new Path
        {
            Data = Geometry.Parse(glyph), Width = 14, Height = 14, StrokeThickness = 1.2,
            Stroke = active ? DerethPalette.TealBrush : MutedBrush,
            Fill = active ? DerethPalette.Brush(DerethPalette.Teal.WithAlpha(0x40)) : null,
        };
        var button = new DerethButton { Width = 24, Height = 24, Tag = layout, Content = icon };
        button.Click += (_, _) => RequestLayout(layout);
        return button;
    }

    private Control ContentsLine()
    {
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(2, 6, 2, 6) };
        line.Children.Add(_contentsName);
        line.Children.Add(At(_contentsCount, 0, 1));
        return line;
    }

    /// <summary>The pyreal count with its icon, as the footer's first item.</summary>
    private Control PyrealsLine()
    {
        var money = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        money.Children.Add(new Image { Source = Bitmap(new ItemVisual(PyrealIcon, 0, 0, 0)), Width = 20, Height = 20 });
        money.Children.Add(_pyrealCount);
        return money;
    }

    /// <summary>The vertical layout's footer: the pyreals alone.</summary>
    private Grid PyrealsFooter()
    {
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(2, 6, 2, 0) };
        line.Children.Add(PyrealsLine());
        return line;
    }

    private static T At<T>(T control, int row, int column = 0) where T : Control
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        return control;
    }

    private static T Centred<T>(T control) where T : Control
    {
        control.HorizontalAlignment = HorizontalAlignment.Center;
        control.VerticalAlignment = VerticalAlignment.Center;
        return control;
    }

    private static TextBlock Label(string text, IBrush brush, double size) => new()
    {
        Text = text, Foreground = brush, FontSize = size, FontFamily = DerethPalette.Body, VerticalAlignment = VerticalAlignment.Center,
    };
}
