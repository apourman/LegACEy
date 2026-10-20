using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LegACEy.Client.Themes;
using static LegACEy.Client.Themes.DerethItemCells;

namespace LegACEy.Plugin.Market;

/// <summary>
/// The buy dialog over the dimmed Market: the price, the balance after, the seller, and where the item goes (the pack by default, greyed out when
/// the server says it won't fit, or the Vault). It draws the browse state's <see cref="BuyAttempt"/> and sends the clicks back to it.
/// </summary>
public sealed class BuyDialog : Border
{
    private static readonly IBrush Scrim = DerethPalette.Brush(Color.FromArgb(0xA0, 0, 0, 0));

    private readonly MarketBrowse _browse;
    private readonly StackPanel _body = new() { Spacing = 8 };

    public BuyDialog(MarketBrowse browse)
    {
        _browse = browse ?? throw new ArgumentNullException(nameof(browse));
        Background = Scrim;
        IsVisible = false;
        Child = new Border
        {
            Width = 380, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(16),
            CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), BorderBrush = DerethPalette.GoldDarkBrush,
            Background = DerethPalette.Brush(DerethPalette.Navy), Child = _body,
        };
    }

    /// <summary>Draws the attempt, or hides the dialog when there is none. The account gives the balance after and whose pack it is.</summary>
    public void Show(BuyAttempt? attempt, MarketAccount? account)
    {
        IsVisible = attempt != null;
        _body.Children.Clear();
        if (attempt == null) return;

        var listing = attempt.Listing;
        _body.Children.Add(Label($"Buy {listing.Name}?", DerethPalette.CreamBrush, 15));

        var facts = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), ColumnSpacing = 12 };
        Fact(facts, 0, "Price", listing.Quantity > 1 ? $"{listing.Price:N0} MMD for {listing.Quantity:N0}" : $"{listing.Price:N0} MMD", DerethPalette.GoldBrush);
        Fact(facts, 1, "Balance after", account == null ? "—" : $"{account.Balance - listing.Price:N0} MMD", DerethPalette.TextBrush);
        Fact(facts, 2, "Seller", listing.Seller, DerethPalette.TextBrush);
        _body.Children.Add(facts);

        _body.Children.Add(Label("Deliver to", DerethPalette.MutedBrush, 11));
        var pack = attempt.Fit is { Fits: false } fit ? fit.Reason
            : attempt.Fit is { } room ? $"Right away. {room.FreeSlots:N0} of {room.Slots:N0} slots free." : "Right away.";
        var character = account?.Character is { Length: > 0 } name ? $"{name}'s pack" : "Your pack";
        _body.Children.Add(Option("pack", character, pack, attempt.ToPack, attempt.PackAllowed && !attempt.Busy, () => _browse.ChooseDelivery(true)));
        _body.Children.Add(Option("vault", "My Vault", "Withdraw it at the Vault chest whenever you like.", !attempt.ToPack, !attempt.Busy, () => _browse.ChooseDelivery(false)));
        _body.Children.Add(Label("If the pack is full, it's greyed out and the Vault is chosen.", DerethPalette.MutedBrush, 11));

        if (attempt.Notice.Length > 0)
        {
            var notice = Label(attempt.Notice, DerethPalette.InvalidBrush, 12);
            notice.TextWrapping = TextWrapping.Wrap;
            notice.Tag = "buy-notice";
            _body.Children.Add(notice);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Button("cancel-buy", attempt.Code.Length > 0 && !attempt.CanConfirm ? "Close" : "Cancel", !attempt.Busy, _browse.CloseBuy);
        buttons.Children.Add(cancel);
        if (attempt.NewPrice is long price)
            buttons.Children.Add(Button("new-price", $"Review the new price, {price:N0}", !attempt.Busy, _browse.AcceptNewPrice));
        else if (attempt.Code.Length == 0 || attempt.CanConfirm)
            buttons.Children.Add(Button("confirm-buy", attempt.Busy ? "Buying…" : $"Buy for {listing.Price:N0}", attempt.CanConfirm, _browse.ConfirmBuy));
        _body.Children.Add(buttons);
    }

    private static void Fact(Grid grid, int row, string label, string value, IBrush brush)
    {
        var name = Label(label, DerethPalette.MutedBrush, 12);
        var text = Label(value, brush, 12);
        Grid.SetRow(name, row);
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 1);
        grid.Children.Add(name);
        grid.Children.Add(text);
    }

    private static Button Option(string tag, string title, string detail, bool chosen, bool enabled, Action choose)
    {
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(Label(title, DerethPalette.TextBrush, 13));
        var small = Label(detail, DerethPalette.MutedBrush, 11);
        small.TextWrapping = TextWrapping.Wrap;
        text.Children.Add(small);
        var option = new Button
        {
            Tag = tag, Content = text, IsEnabled = enabled, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 8), BorderThickness = new Thickness(1),
            BorderBrush = chosen ? DerethPalette.TealBrush : DerethPalette.GrooveEdgeBrush, Opacity = enabled ? 1 : 0.5,
        };
        option.Click += (_, _) => choose();
        return option;
    }

    private static Button Button(string tag, string text, bool enabled, Action click)
    {
        var button = new DerethButton { Tag = tag, Height = 28, Padding = new Thickness(12, 0), Content = Label(text, DerethPalette.TextBrush, 12), IsEnabled = enabled };
        button.Click += (_, _) => click();
        return button;
    }
}
