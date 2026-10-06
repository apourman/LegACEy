using System;
using System.Collections.Generic;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Demo;

/// <summary>
/// The account Vault window. With a <see cref="VaultClient"/> it shows the live Vault from the LegACEy server channel:
/// real items and balance, selection, withdrawal and deposit of the game's selected item, refreshed by server pushes.
/// Without one it is the static sample shell, with sample icons read from the player's DAT.
/// </summary>
public sealed class VaultShellPanel : UserControl, IDisposable
{
    // Increment with each visual iteration; the assembly's source revision identifies the actual build.
    public const string PreviewVersion = "5";
    public static string BuildRevision { get; } = ReadBuildRevision();

    private static string ReadBuildRevision()
    {
        var version = typeof(VaultShellPanel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var separator = version?.IndexOf('+') ?? -1;
        if (separator < 0 || separator == version!.Length - 1) return "local";
        var revision = version.Substring(separator + 1);
        return revision.Substring(0, Math.Min(8, revision.Length));
    }

    // Retail contents-list empty face and selection overlay (32px RenderSurfaces).
    public const uint InventoryCellArtId = 0x06004D20;
    public const uint InventorySelectionArtId = 0x06004D21;
    public const int WindowWidth = 620;
    public const int WindowHeight = 460;
    private const int Columns = 6;
    private const int MinimumCells = 24;
    internal static readonly IBrush Text = Brush("#E6E3D8");
    internal static readonly IBrush Muted = Brush("#AAA79F");
    internal static readonly IBrush Gold = Brush("#D6BB76");
    internal static readonly IBrush Warning = Brush("#E0A070");
    private readonly Dictionary<uint, WriteableBitmap?> _images = new();
    private readonly IGameArtSource _art;
    private readonly VaultClient? _client;
    private readonly ContentControl _summary = new();
    private readonly ContentControl _contents = new();
    private readonly ContentControl _details = new();
    private readonly ContentControl _footerStatus = new();
    private readonly Button? _deposit;
    private uint _selected;
    private bool _disposed;

    private static readonly (string Name, uint Icon, uint Plate)[] Samples =
    {
        ("Chainmail shirt", 0x06000FC7, 0x060011CF),
        ("Leather boots", 0x06000FAD, 0x060011F3),
        ("Gold ring", 0x06000FB5, 0x060011D5),
        ("Pendant", 0x06000FBE, 0x060011D5),
        ("Steel shield", 0x06000FCB, 0x060011CF),
        ("Leather cap", 0x06000FAA, 0x060011F3),
        ("Blue potion", 0x06001012, 0x060011D4),
        ("Yellow potion", 0x06001013, 0x060011D4),
        ("Treasure chest", 0x06001020, 0x060011D4),
        ("Small pouch", 0x06001031, 0x060011D4),
        ("Green bottle", 0x06001030, 0x060011D4),
        ("Silver goblet", 0x0600101F, 0x060011D4)
    };

    /// <param name="client">The live Vault; the panel owns it and disposes it. Null shows the static sample.</param>
    public VaultShellPanel(IGameArtSource art, VaultClient? client = null)
    {
        _art = art ?? throw new ArgumentNullException(nameof(art));
        _client = client;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 10, Margin = new Thickness(20, 8, 20, 18)
        };
        root.Children.Add(new Border
        {
            BorderBrush = Brush("#655B43"), BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 8), Child = _summary
        });

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("330,*"), ColumnSpacing = 22 };
        var inventory = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), RowSpacing = 12 };
        // Static presentation surfaces: search and sorting are not connected yet.
        inventory.Children.Add(new VaultSurface
        {
            Padding = new Thickness(12, 9), Child = Label("Search your vault…", Muted)
        });
        var categories = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        categories.Children.Add(Label("All items", Gold));
        var sort = Label("Name  ↓", Muted);
        Grid.SetColumn(sort, 1);
        categories.Children.Add(sort);
        Grid.SetRow(categories, 1);
        inventory.Children.Add(categories);
        Grid.SetRow(_contents, 2);
        inventory.Children.Add(_contents);
        body.Children.Add(inventory);

        var detailPane = new Border
        {
            BorderBrush = Brush("#655B43"), BorderThickness = new Thickness(1, 0, 0, 0),
            Padding = new Thickness(14, 0, 0, 0), Child = _details
        };
        Grid.SetColumn(detailPane, 1);
        body.Children.Add(detailPane);
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var footer = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto"), RowSpacing = 12 };
        footer.Children.Add(Rule());
        var footerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        if (_client == null) footerRow.Children.Add(ActionFace("Deposit item", false));
        else
        {
            _deposit = ActionButton("Deposit item", false, () => _client.DepositSelection());
            ToolTip.SetTip(_deposit, "Deposit the item selected in the game");
            footerRow.Children.Add(_deposit);
        }
        _footerStatus.HorizontalAlignment = HorizontalAlignment.Right;
        _footerStatus.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_footerStatus, 1);
        footerRow.Children.Add(_footerStatus);
        Grid.SetRow(footerRow, 1);
        footer.Children.Add(footerRow);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;

        if (_client == null) ShowSample();
        else
        {
            _client.Changed += OnClientChanged;
            ShowLive();
            _client.Start();
        }
    }

    private void ShowSample()
    {
        _summary.Content = SummaryRow("12 items  /  1,000 capacity", "245 MMD");
        var slots = new UniformGrid { Columns = Columns, Rows = 4 };
        for (var index = 0; index < MinimumCells; index++)
        {
            var slot = new Border { Height = 48, Margin = new Thickness(0, 0, 6, 6) };
            if (index < Samples.Length)
            {
                var sample = Samples[index];
                slot.Child = InventoryCell(new[] { sample.Plate, sample.Icon }, index == 0);
                ToolTip.SetTip(slot, sample.Name + " — sample item");
            }
            else slot.Child = InventoryCell();
            slots.Children.Add(slot);
        }
        _contents.Content = slots;

        var selected = Samples[0];
        var details = new StackPanel { Spacing = 10 };
        details.Children.Add(Label("SELECTED ITEM", Gold, 11));
        details.Children.Add(Identity(new[] { selected.Plate, selected.Icon }, "Chainmail\nshirt", "Armor · Stored"));
        details.Children.Add(Rule());
        details.Children.Add(Label("Deposited by", Muted, 11));
        details.Children.Add(Label("Arwic Wanderer"));
        details.Children.Add(Label("Withdraw to", Muted, 11));
        details.Children.Add(Label("Current character"));
        details.Children.Add(Label("60-second transfer", Muted, 11));
        details.Children.Add(ActionFace("Withdraw item", true));
        details.Children.Add(Label("Appraise item", Gold));
        _details.Content = details;
        _footerStatus.Content = Label($"Sample vault · Preview v{PreviewVersion} · {BuildRevision}", Gold, 11);
    }

    private void OnClientChanged(object? sender, EventArgs e)
    {
        if (!_disposed) ShowLive();
    }

    private void ShowLive()
    {
        var client = _client!;
        var snapshot = client.Snapshot;
        var items = snapshot?.Items ?? Array.Empty<VaultItemView>();
        if (snapshot is { Available: true })
            _summary.Content = SummaryRow($"{items.Count:N0} item{(items.Count == 1 ? "" : "s")}  /  {snapshot.Capacity:N0} capacity", $"{snapshot.Balance:N0} MMD");
        else
            _summary.Content = SummaryRow(client.Connection == VaultConnection.Connecting ? "Connecting to the server…" : "Vault unavailable", string.Empty);

        if (items.Count > 0 && FindItem(items, _selected) == null) _selected = items[0].Guid;
        if (snapshot is { Available: true })
        {
            var cells = Math.Max(MinimumCells, (items.Count + Columns - 1) / Columns * Columns);
            var slots = new UniformGrid { Columns = Columns, Rows = cells / Columns };
            for (var index = 0; index < cells; index++)
            {
                var slot = new Border { Height = 48, Margin = new Thickness(0, 0, 6, 6) };
                if (index < items.Count)
                {
                    var item = items[index];
                    slot.Child = SelectableCell(item, item.Guid == _selected);
                    ToolTip.SetTip(slot, Describe(item));
                }
                else slot.Child = InventoryCell();
                slots.Children.Add(slot);
            }
            _contents.Content = new ScrollViewer
            {
                Content = slots, Height = 216, Background = Brushes.Transparent,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = cells > MinimumCells ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled
            };
        }
        else
        {
            var message = Label(client.Connection == VaultConnection.Connecting ? "Asking the server for your Vault…" : client.Notice, Muted);
            message.Margin = new Thickness(0, 24, 0, 0);
            _contents.Content = message;
        }

        _details.Content = Details(client, FindItem(items, _selected));
        if (_deposit != null) _deposit.IsEnabled = client.Connection == VaultConnection.Live && !client.TransferPending;
        var roundTrip = client.LastRoundTrip is { } time ? $" · {time.TotalMilliseconds:N0} ms" : string.Empty;
        var state = client.Connection switch
        {
            VaultConnection.Live => $"Live{roundTrip} · {client.PushesReceived} push{(client.PushesReceived == 1 ? "" : "es")}",
            VaultConnection.Connecting => "Connecting",
            VaultConnection.Unavailable => "Unavailable",
            _ => "Server error"
        };
        _footerStatus.Content = Label($"{state} · Preview v{PreviewVersion} · {BuildRevision}", Gold, 11);
    }

    private Control Details(VaultClient client, VaultItemView? item)
    {
        var details = new StackPanel { Spacing = 10 };
        details.Children.Add(Label("SELECTED ITEM", Gold, 11));
        if (item == null)
            details.Children.Add(Label(client.Snapshot is { Available: true } ? "Your Vault is empty. Select an item in your pack and press Deposit item." : "No item selected.", Muted));
        else
        {
            details.Children.Add(Identity(item.IconLayers, item.Name + (item.StackSize > 1 ? $" ×{item.StackSize:N0}" : string.Empty),
                $"{TypeName(item.ItemType)} · {StateName(item.State)}"));
            details.Children.Add(Rule());
            details.Children.Add(Label("Deposited by", Muted, 11));
            details.Children.Add(Label(item.DepositedBy.Length == 0 ? "Unknown character" : $"{item.DepositedBy}, {item.DepositedAt.LocalDateTime:d MMM}"));
            details.Children.Add(Label("Withdraw to", Muted, 11));
            details.Children.Add(Label(client.ServerCharacter.Length == 0 ? "Current character" : client.ServerCharacter));
            var withdraw = ActionButton("Withdraw item", true, () => client.Withdraw(item.Guid));
            withdraw.IsEnabled = item.State == "held" && !client.TransferPending;
            details.Children.Add(withdraw);
        }
        if (client.Notice.Length != 0)
        {
            var notice = Label(client.Notice, client.Connection == VaultConnection.Live ? Gold : Warning, 11);
            details.Children.Add(notice);
        }
        return details;
    }

    private static VaultItemView? FindItem(IReadOnlyList<VaultItemView> items, uint guid)
    {
        foreach (var item in items)
            if (item.Guid == guid) return item;
        return null;
    }

    private static string Describe(VaultItemView item) =>
        $"{item.Name}{(item.StackSize > 1 ? $" ×{item.StackSize:N0}" : string.Empty)} — {StateName(item.State)}, 0x{item.Guid:X8}";

    private static string StateName(string state) => state switch
    {
        "held" => "Stored",
        "listed" => "Listed for sale",
        "withdrawing" => "Withdrawing",
        _ => state
    };

    // ACE.Entity.Enum.ItemType flags, most specific first, as the server's icon plate does.
    private static string TypeName(uint type) =>
        (type & 0x8101) != 0 ? "Weapon" : (type & 0x2) != 0 ? "Armor" : (type & 0x4) != 0 ? "Clothing" : (type & 0x8) != 0 ? "Jewelry" :
        (type & 0x800) != 0 ? "Gem" : (type & 0x80) != 0 ? "Consumable" : (type & 0x200) != 0 ? "Container" : "Item";

    private static Grid SummaryRow(string left, string right)
    {
        var summary = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        summary.Children.Add(Label(left, Text));
        var balance = Label(right, Gold);
        Grid.SetColumn(balance, 1);
        summary.Children.Add(balance);
        return summary;
    }

    private StackPanel Identity(IEnumerable<uint> layers, string name, string subtitle)
    {
        var identity = new StackPanel { Spacing = 12, Orientation = Orientation.Horizontal };
        identity.Children.Add(new Border { Padding = new Thickness(9), Child = InventoryCell(layers) });
        var title = Label(name, Text, 15);
        title.MaxWidth = 150;
        identity.Children.Add(new StackPanel
        {
            Spacing = 4, VerticalAlignment = VerticalAlignment.Center,
            Children = { title, Label(subtitle, Muted, 11) }
        });
        return identity;
    }

    internal static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));

    internal static TextBlock Label(string text, IBrush? color = null, double size = 12) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Foreground = color ?? Text,
        FontSize = size, FontFamily = new FontFamily("Tahoma, avares://LegACEy.Client.Themes/Assets#Liberation Sans")
    };

    private static Border Rule() => new() { Height = 1, Background = Brush("#655B43") };

    // The sample shell's display-only face.
    private static VaultSurface ActionFace(string text, bool primary) => new(VaultMaterial.BlueSteel)
    {
        Padding = new Thickness(14, 9),
        Child = Label(text, Text, primary ? 13 : 12)
    };

    private static Button ActionButton(string text, bool primary, Action clicked)
    {
        var button = new Button
        {
            Content = text, HorizontalAlignment = HorizontalAlignment.Left,
            Template = new FuncControlTemplate<Button>((owner, _) =>
            {
                var label = Label(text, owner.IsEnabled ? Text : Muted, primary ? 13 : 12);
                var face = new VaultSurface(VaultMaterial.BlueSteel) { Padding = new Thickness(14, 9), Child = label, Opacity = owner.IsEnabled ? 1 : 0.55 };
                owner.PropertyChanged += (_, change) =>
                {
                    if (change.Property != IsEnabledProperty) return;
                    label.Foreground = owner.IsEnabled ? Text : Muted;
                    face.Opacity = owner.IsEnabled ? 1 : 0.55;
                };
                return face;
            })
        };
        button.Click += (_, _) => clicked();
        return button;
    }

    private Button SelectableCell(VaultItemView item, bool selected)
    {
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Template = new FuncControlTemplate<Button>((_, _) => new Border { Background = Brushes.Transparent, Child = InventoryCell(item.IconLayers, selected) })
        };
        button.Click += (_, _) =>
        {
            _selected = item.Guid;
            ShowLive();
        };
        return button;
    }

    private Grid InventoryCell(IEnumerable<uint>? layers = null, bool selected = false)
    {
        var cell = new Grid
        {
            Width = 32, Height = 32,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        var background = Bitmap(InventoryCellArtId);
        cell.Children.Add(background != null
            ? (Control)new Image { Source = background, Width = 32, Height = 32, Stretch = Stretch.None }
            : new Border { Background = Brush("#0C0D0E"), BorderBrush = Brush("#484B4B"), BorderThickness = new Thickness(1) });
        if (layers != null) cell.Children.Add(Icon(layers));
        if (selected)
        {
            var overlay = Bitmap(InventorySelectionArtId);
            cell.Children.Add(overlay != null
                ? (Control)new Image { Source = overlay, Width = 32, Height = 32, Stretch = Stretch.None, IsHitTestVisible = false }
                : new Border { BorderBrush = Gold, BorderThickness = new Thickness(1), IsHitTestVisible = false });
        }
        return cell;
    }

    private WriteableBitmap? Bitmap(uint id)
    {
        if (!_images.TryGetValue(id, out var bitmap))
            _images.Add(id, bitmap = GameArtImageExtension.CreateBitmap(_art, id));
        return bitmap;
    }

    private Grid Icon(IEnumerable<uint> ids)
    {
        var layers = new Grid
        {
            Width = 32, Height = 32,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        foreach (var id in ids)
        {
            var bitmap = Bitmap(id);
            if (bitmap != null)
                layers.Children.Add(new Image { Source = bitmap, Width = 32, Height = 32, Stretch = Stretch.None });
        }
        if (layers.Children.Count == 0)
        {
            var fallback = Label("?", Muted);
            fallback.HorizontalAlignment = HorizontalAlignment.Center;
            fallback.VerticalAlignment = VerticalAlignment.Center;
            layers.Children.Add(fallback);
        }
        return layers;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_client != null)
        {
            _client.Changed -= OnClientChanged;
            _client.Dispose();
        }
        foreach (var image in _images.Values) image?.Dispose();
        _images.Clear();
    }
}

/// <summary>Vault-specific chrome; independent of the theme gallery's retail control styling.</summary>
public sealed class VaultShellWindow : UserControl
{
    public event EventHandler? CloseRequested;

    public VaultShellWindow(VaultShellPanel panel)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        var layout = new Grid { RowDefinitions = new RowDefinitions("64,*") };
        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,32"), Margin = new Thickness(20, 6, 16, 0) };
        title.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center, Spacing = 3,
            Children = { new TextBlock { Text = "Account Vault", Foreground = VaultShellPanel.Text, FontSize = 23, FontFamily = new FontFamily("Georgia, Liberation Serif") }, VaultShellPanel.Label("Shared across your account", VaultShellPanel.Muted, 11) }
        });
        var version = VaultShellPanel.Label($"Preview v{VaultShellPanel.PreviewVersion}", VaultShellPanel.Gold, 12);
        version.VerticalAlignment = VerticalAlignment.Center;
        version.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(version, 1);
        title.Children.Add(version);
        var close = new Button
        {
            Content = "×", Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center,
            Template = new FuncControlTemplate<Button>((_, _) => new VaultSurface(VaultMaterial.BlueSteel)
            {
                Child = new TextBlock
                {
                    Text = "×", FontSize = 18, Foreground = VaultShellPanel.Muted,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            })
        };
        ToolTip.SetTip(close, "Close vault preview");
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        Grid.SetColumn(close, 2);
        title.Children.Add(close);
        layout.Children.Add(title);
        Grid.SetRow(panel, 1);
        layout.Children.Add(panel);
        Content = new VaultSurface(ornate: true)
        {
            Padding = new Thickness(3), Child = layout
        };
    }
}
