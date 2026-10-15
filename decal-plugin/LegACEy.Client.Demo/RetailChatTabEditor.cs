using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Demo;

/// <summary>
/// What the tab editor does with the player's choices. <see cref="Move"/> gets -1 or +1, and the Left and Right buttons are enabled by the
/// Can flags; <see cref="Remove"/> is null when the tab cannot go.
/// </summary>
public sealed record RetailChatTabEditActions(Action<RetailChatTab> Save, Action Cancel, Action? Remove, Action<int>? Move, bool CanMoveLeft, bool CanMoveRight);

/// <summary>
/// The Dereth dialog that adds or edits a chat tab, as the mockup's New tab card shows it: a name, the kinds of line the tab shows (single
/// channels included) and the talk target it sets. It builds its controls and reports the player's choices; the window applies them.
/// </summary>
public static class RetailChatTabEditor
{
    private const int DialogWidth = 280;
    private const RetailChatKind NoKinds = 0;
    private const RetailChatKind NewTabKinds = RetailChatKind.Local | RetailChatKind.Emote | RetailChatKind.TellIn | RetailChatKind.TellOut
        | RetailChatKind.Fellowship | RetailChatKind.System | RetailChatKind.Combat;

    // Each kind group: its label, the line kinds it covers, and the kind whose colour its check shows. Channels is set from the channel chips.
    private static readonly (string Label, RetailChatKind Lines, RetailChatKind Colour)[] Groups =
    {
        ("Local", RetailChatKind.Local | RetailChatKind.Emote, RetailChatKind.Local),
        ("Tells", RetailChatKind.TellIn | RetailChatKind.TellOut, RetailChatKind.TellIn),
        ("Fellowship", RetailChatKind.Fellowship, RetailChatKind.Fellowship),
        ("Allegiance", RetailChatKind.Allegiance, RetailChatKind.Allegiance),
        ("Channels", RetailChatKind.Channel, RetailChatKind.Channel),
        ("System", RetailChatKind.System, RetailChatKind.System),
        ("Combat", RetailChatKind.Combat, RetailChatKind.Combat),
        ("Plugins", RetailChatKind.Plugin, RetailChatKind.Plugin)
    };

    /// <summary>The talk targets a tab can set, in the order the arrows step through them, with "last tell" last.</summary>
    private static readonly string[] Targets = RetailTalkTargets.All.Select(target => target.Id).Append(RetailChatTab.LastTellId).ToArray();

    /// <summary>
    /// The dialog for a new tab (<paramref name="existing"/> null) or an edited one. Add or Save is disabled until the tab has a name and a kind.
    /// The dialog is kept short enough for the default window, so its buttons stay on screen.
    /// </summary>
    public static Control Create(RetailChatTab? existing, RetailChatTabEditActions actions)
    {
        // The kinds the dialog toggles, without Channel: that bit is on whenever a channel is picked.
        var kinds = (existing?.Kinds ?? NewTabKinds) & ~RetailChatKind.Channel;
        var channels = new HashSet<string>(StringComparer.Ordinal);
        if (existing != null && existing.Kinds.HasFlag(RetailChatKind.Channel))
            foreach (var channel in existing.Channels ?? RetailChatTab.ChannelNames) channels.Add(channel);
        var target = Math.Max(0, Array.IndexOf(Targets, existing?.TargetId ?? RetailTalkTargets.LocalId));

        var title = new TextBlock { Text = existing == null ? "New tab" : "Edit tab", FontFamily = DerethPalette.Title, FontSize = 12, Foreground = DerethPalette.CreamBrush, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBox
        {
            Watermark = "Tab name",
            Text = existing?.Name ?? string.Empty,
            Height = 19,
            MinHeight = 0,
            Padding = new Thickness(4, 0),
            FontFamily = DerethPalette.Body,
            FontSize = 11,
            Foreground = DerethPalette.TextBrush,
            CaretBrush = DerethPalette.GoldBrush,
            Background = DerethPalette.GrooveBrush,
            BorderBrush = DerethPalette.GrooveEdgeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 6, Children = { title, name } };
        Grid.SetColumn(name, 1);
        // Each check takes its label's width and the rows wrap, so the kinds and the channels take as few rows as the dialog allows.
        var kindsHost = new WrapPanel();
        var channelsHost = new WrapPanel();
        var targetHost = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        var save = Button(existing == null ? "Add" : "Save", 40);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };

        RetailChatKind Flags() => kinds | (channels.Count > 0 ? RetailChatKind.Channel : NoKinds);

        // Every channel means "all channel lines", so the tab keeps no list; a partial pick keeps the names in list order.
        string[]? ChannelsOut() => channels.Count == 0 || channels.Count == RetailChatTab.ChannelNames.Count
            ? null
            : RetailChatTab.ChannelNames.Where(channels.Contains).ToArray();

        void Refresh()
        {
            save.IsEnabled = name.Text?.Trim().Length > 0 && Flags() != NoKinds;
            kindsHost.Children.Clear();
            foreach (var group in Groups)
            {
                var on = group.Label == "Channels" ? channels.Count > 0 : (kinds & group.Lines) != NoKinds;
                kindsHost.Children.Add(Check(group.Label, group.Colour, on, () => Toggle(group.Label, group.Lines)));
            }
            channelsHost.Children.Clear();
            foreach (var channel in RetailChatTab.ChannelNames)
                channelsHost.Children.Add(Check(channel, RetailChatKind.Channel, channels.Contains(channel), () => ToggleChannel(channel)));
            targetHost.Children.Clear();
            var prev = Button("‹", 20);
            var next = Button("›", 20);
            prev.Click += (_, _) => { target = (target + Targets.Length - 1) % Targets.Length; Refresh(); };
            next.Click += (_, _) => { target = (target + 1) % Targets.Length; Refresh(); };
            targetHost.Children.Add(new TextBlock { Text = "Talks to", FontFamily = DerethPalette.Title, FontSize = 11, Foreground = DerethPalette.MutedBrush, VerticalAlignment = VerticalAlignment.Center });
            targetHost.Children.Add(prev);
            targetHost.Children.Add(DerethChatParts.Chip(TargetLabel(Targets[target]), ColourOf(Targets[target])));
            targetHost.Children.Add(next);
        }

        void Toggle(string label, RetailChatKind lines)
        {
            if (label == "Channels")
            {
                if (channels.Count > 0) channels.Clear();
                else foreach (var channel in RetailChatTab.ChannelNames) channels.Add(channel);
            }
            else kinds ^= lines;
            Refresh();
        }

        void ToggleChannel(string channel)
        {
            if (!channels.Remove(channel)) channels.Add(channel);
            Refresh();
        }

        void Commit()
        {
            var text = name.Text?.Trim() ?? string.Empty;
            if (text.Length == 0 || Flags() == NoKinds) return;
            actions.Save(new RetailChatTab(text, Flags(), Targets[target], ChannelsOut()));
        }

        name.TextChanged += (_, _) => Refresh();
        name.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Commit();
            else if (e.Key == Key.Escape) actions.Cancel();
            else return;
            e.Handled = true;
        };
        save.Click += (_, _) => Commit();

        // The moves and Remove are for an existing tab only; the window decides which moves are possible.
        if (actions.Move is { } move)
        {
            var left = Button("Left", 34);
            var right = Button("Right", 36);
            left.IsEnabled = actions.CanMoveLeft;
            right.IsEnabled = actions.CanMoveRight;
            left.Click += (_, _) => move(-1);
            right.Click += (_, _) => move(1);
            buttons.Children.Add(left);
            buttons.Children.Add(right);
        }
        if (actions.Remove is { } remove)
        {
            var removeButton = Button("Remove", 50);
            removeButton.Click += (_, _) => remove();
            buttons.Children.Add(removeButton);
        }
        var cancel = Button("Cancel", 46);
        cancel.Click += (_, _) => actions.Cancel();
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        Refresh();

        var body = new StackPanel { Spacing = 3, Children = { titleRow, kindsHost, channelsHost, targetHost, buttons } };
        // The dialog fills the window's height; a window too short for all of it scrolls, so Add and Save stay reachable.
        return new Border
        {
            Width = DialogWidth,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 2, 0, 0),
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = new DerethFrame(DerethFrameArt.Window)
                {
                    Child = new Border { Padding = new Thickness(8, 5, 8, 6), Child = body }
                }
            }
        };
    }

    private static DerethButton Button(string content, double width) => new()
    {
        Content = content,
        Width = width,
        Height = 20,
        Padding = new Thickness(0),
        FontSize = 11,
        Foreground = DerethPalette.CreamBrush
    };

    private static Control Check(string label, RetailChatKind colour, bool on, Action toggle)
    {
        var box = new Border
        {
            Width = 7,
            Height = 7,
            BorderThickness = new Thickness(1),
            BorderBrush = DerethPalette.GoldBrush,
            Background = on ? DerethPalette.TealBrush : Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center
        };
        var swatch = new Ellipse { Width = 6, Height = 6, Fill = DerethPalette.Brush(RetailChatWindow.ColourOf(colour)), VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock
        {
            Text = label,
            FontFamily = DerethPalette.Title,
            FontSize = 11,
            Foreground = on ? DerethPalette.CreamBrush : DerethPalette.MutedBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        var row = new Border
        {
            Height = 15,
            Margin = new Thickness(0, 0, 7, 0),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { box, swatch, text } }
        };
        row.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
            toggle();
            e.Handled = true;
        };
        return row;
    }

    private static string TargetLabel(string id) =>
        id == RetailChatTab.LastTellId ? "Last tell" : RetailTalkTargets.All.First(target => target.Id == id).Label;

    private static Color ColourOf(string id) =>
        RetailChatWindow.ColourOf(id == RetailChatTab.LastTellId ? RetailChatKind.TellIn : RetailTalkTargets.All.First(target => target.Id == id).Colour);
}
