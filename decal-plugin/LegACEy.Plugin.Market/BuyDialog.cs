using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LegACEy.Client.Themes;

namespace LegACEy.Plugin.Market;

/// <summary>
/// The buy dialog over the dimmed Market: the price, the balance after, the seller, and where the item goes (the pack by default, greyed out when
/// the server says it won't fit, or the Vault). It draws the browse state's <see cref="BuyAttempt"/> and sends the clicks back to it.
/// </summary>
public sealed class BuyDialog : Border
{
    private static readonly IBrush Scrim = DerethPalette.Brush(Color.FromArgb(0xAD, 5, 4, 3));

    private readonly MarketBrowse _browse;
    private readonly Func<ListingCard, Control>? _iconFor;
    private readonly StackPanel _body = new() { Spacing = 14 };

    /// <param name="iconFor">draws a listing's icon; null draws none</param>
    public BuyDialog(MarketBrowse browse, Func<ListingCard, Control>? iconFor = null)
    {
        _browse = browse ?? throw new ArgumentNullException(nameof(browse));
        _iconFor = iconFor;
        Background = Scrim;
        IsVisible = false;
        Child = new Border
        {
            Width = 440, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(24, 22),
            CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), BorderBrush = DerethPalette.GoldDarkBrush,
            Background = MarketInk.Vertical(Color.Parse("#2A2419"), Color.Parse("#17140F")),
            BoxShadow = BoxShadows.Parse("0 20 60 0 #B3000000"), Child = _body,
        };
    }

    /// <summary>Draws the attempt, or hides the dialog when there is none. The account gives the balance after and whose pack it is.</summary>
    public void Show(BuyAttempt? attempt, MarketAccount? account)
    {
        IsVisible = attempt != null;
        _body.Children.Clear();
        if (attempt == null) return;

        var listing = attempt.Listing;
        var title = MarketParts.Text($"Buy {listing.Name}?", DerethPalette.CreamBrush, 22, title: true);
        title.TextWrapping = TextWrapping.Wrap;
        _body.Children.Add(title);

        // the item, then its price, the balance after and the seller
        var facts = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), ColumnSpacing = 18, RowSpacing = 3 };
        var price = MarketParts.Price(listing.Price.ToString("N0"), ink: DerethPalette.CreamBrush);
        if (listing.Quantity > 1) price.Children.Add(MarketParts.Text($"for {listing.Quantity:N0}", MarketInk.MutedBrush, 12.5));
        Fact(facts, 0, "Price", price);
        Fact(facts, 1, "Balance after", MarketParts.Text(account == null ? "—" : $"{account.Balance - listing.Price:N0} MMD", MarketInk.TextBrush, 12.5));
        Fact(facts, 2, "Seller", MarketParts.Text(listing.Seller, MarketInk.TextBrush, 12.5));
        var item = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 14 };
        if (_iconFor != null) item.Children.Add(MarketParts.IconTile(_iconFor(listing), 48));
        Grid.SetColumn(facts, 1);
        item.Children.Add(facts);
        _body.Children.Add(item);

        var deliver = new StackPanel { Spacing = 8 };
        deliver.Children.Add(MarketParts.Caps("Deliver to", MarketInk.MutedBrush, 11));
        var pack = attempt.Fit is { Fits: false } fit ? fit.Reason
            : attempt.Fit is { } room ? $"Right away. {room.FreeSlots:N0} of {room.Slots:N0} slots free." : "Right away.";
        var character = account?.Character is { Length: > 0 } name ? $"{name}'s pack" : "Your pack";
        deliver.Children.Add(Option("pack", character, pack, attempt.ToPack, attempt.PackAllowed && !attempt.Busy, () => _browse.ChooseDelivery(true)));
        deliver.Children.Add(Option("vault", "My Vault", "Withdraw it at the Vault chest whenever you like.", !attempt.ToPack, !attempt.Busy, () => _browse.ChooseDelivery(false)));
        var hint = MarketParts.Text("If the pack is full, it's greyed out and the Vault is chosen.", MarketInk.MutedBrush, 12);
        hint.TextWrapping = TextWrapping.Wrap;
        deliver.Children.Add(hint);
        _body.Children.Add(deliver);

        if (attempt.Notice.Length > 0)
        {
            var notice = MarketParts.Text(attempt.Notice, MarketInk.BadBrush, 12.5);
            notice.TextWrapping = TextWrapping.Wrap;
            notice.Tag = "buy-notice";
            _body.Children.Add(notice);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Button("cancel-buy", attempt.Code.Length > 0 && !attempt.CanConfirm ? "Close" : "Cancel", MarketParts.Kind.Ghost, !attempt.Busy, _browse.CloseBuy);
        buttons.Children.Add(cancel);
        if (attempt.NewPrice is long newPrice)
            buttons.Children.Add(Button("new-price", $"Review the new price, {newPrice:N0}", MarketParts.Kind.Red, !attempt.Busy, _browse.AcceptNewPrice));
        else if (attempt.Code.Length == 0 || attempt.CanConfirm)
            buttons.Children.Add(Button("confirm-buy", attempt.Busy ? "Buying…" : $"Buy for {listing.Price:N0}", MarketParts.Kind.Go, attempt.CanConfirm, _browse.ConfirmBuy));
        _body.Children.Add(buttons);
    }

    private static void Fact(Grid grid, int row, string label, Control value)
    {
        var name = MarketParts.Text(label, MarketInk.MutedBrush, 12.5);
        Grid.SetRow(name, row);
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);
        grid.Children.Add(name);
        grid.Children.Add(value);
    }

    /// <summary>A delivery choice: a radio ring, the place and a line about it; the chosen one is edged green.</summary>
    private static Button Option(string tag, string title, string detail, bool chosen, bool enabled, Action choose)
    {
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(MarketParts.Text(title, DerethPalette.CreamBrush, 13));
        var small = MarketParts.Text(detail, MarketInk.MutedBrush, 12);
        small.TextWrapping = TextWrapping.Wrap;
        text.Children.Add(small);
        var ring = new Border
        {
            Width = 14, Height = 14, CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1.5), VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0), BorderBrush = chosen ? MarketInk.LitBrush : DerethPalette.GoldBrush,
            Child = chosen ? new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = MarketInk.LitBrush } : null,
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        row.Children.Add(ring);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var option = new MarketButton
        {
            Tag = tag, Content = row, IsEnabled = enabled, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(8),
            BorderBrush = chosen ? MarketInk.LitBrush : MarketInk.LineBrush,
            Background = chosen ? DerethPalette.Brush(MarketInk.Lit.WithAlpha(0x0F)) : Brushes.Transparent,
        };
        option.Click += (_, _) => choose();
        return option;
    }

    private static Button Button(string tag, string text, MarketParts.Kind kind, bool enabled, Action click)
    {
        var button = MarketParts.Button(text, kind, 34);
        button.Tag = tag;
        button.IsEnabled = enabled;
        button.Click += (_, _) => click();
        return button;
    }
}
