using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.PanelHost;
using LegACEy.Client.Themes;
using LegACEy.Client.Themes;
using LegACEy.Plugin.Vault;
using Xunit;

namespace LegACEy.Client.Tests;

/// <summary>Selecting Vault items with the mouse over a live Vault of 317 items: clicks with Ctrl and Shift, the selected slots, the header line and the dimming.</summary>
public sealed class VaultSelectionTests
{
    private const KeyModifiers Ctrl = KeyModifiers.Control;
    private const KeyModifiers Shift = KeyModifiers.Shift;

    [Fact]
    public void A_click_selects_one_item_and_Ctrl_click_toggles_items_in_and_out() => RenderThread.Run(() =>
    {
        using var vault = new SelectionVault();
        vault.Click(3);
        Assert.Equal(new[] { 3 }, vault.Selected);

        vault.Click(5, Ctrl);
        Assert.Equal(new[] { 3, 5 }, vault.Selected);

        vault.Click(3, Ctrl); // toggles off, and the anchor moves to the item clicked
        Assert.Equal(new[] { 5 }, vault.Selected);
        Assert.Equal(3, vault.Anchor);

        vault.Click(9);
        Assert.Equal(new[] { 9 }, vault.Selected);

        vault.Click(9); // the only one selected: a plain click clears it
        Assert.Empty(vault.Selected);
        Assert.Null(vault.Host.LastError);
    });

    [Fact]
    public void Shift_click_with_no_anchor_is_a_plain_click_and_then_selects_the_range_both_ways_across_rows() => RenderThread.Run(() =>
    {
        using var vault = new SelectionVault();
        vault.Click(4, Shift); // nothing to range from yet
        Assert.Equal(new[] { 4 }, vault.Selected);

        // Downward, from row one to row three of the six-column window; the anchor stays where it was.
        vault.Click(13, Shift);
        Assert.Equal(Enumerable.Range(4, 10), vault.Selected);
        Assert.Equal(4, vault.Anchor);

        // Upward: the anchor is the lower item this time.
        vault.Click(13);
        vault.Click(4, Shift);
        Assert.Equal(Enumerable.Range(4, 10), vault.Selected);
        Assert.Equal(13, vault.Anchor);
        Assert.Null(vault.Host.LastError);
    });

    [Fact]
    public void Ctrl_Shift_click_adds_the_range_from_the_anchor_to_the_selection() => RenderThread.Run(() =>
    {
        using var vault = new SelectionVault();
        vault.Click(0);
        vault.Click(8, Ctrl);
        vault.Click(10, Ctrl | Shift);
        Assert.Equal(new[] { 0, 8, 9, 10 }, vault.Selected);
        Assert.Equal(8, vault.Anchor);
        Assert.Null(vault.Host.LastError);
    });

    [Fact]
    public void A_selected_range_survives_a_resize_and_still_ranges_from_its_anchor() => RenderThread.Run(() =>
    {
        using var vault = new SelectionVault();
        vault.Click(1);
        vault.Click(8, Shift);

        vault.Host.Resize(294, VaultShellPanel.WindowHeight); // five columns now: the same items, in the same order
        vault.Host.Tick();
        Assert.Equal(Enumerable.Range(1, 8), vault.Selected);
        Assert.Equal(Enumerable.Range(1, 8), vault.SelectedSlots());

        vault.Click(11, Shift);
        Assert.Equal(Enumerable.Range(1, 11), vault.Selected);
        Assert.Equal(Enumerable.Range(1, 11), vault.SelectedSlots());
        Assert.Null(vault.Host.LastError);
    });

    [Fact]
    public void Two_or_more_selected_show_the_selection_header_and_Clear_restores_the_item_line() => RenderThread.Run(() =>
    {
        using var vault = new SelectionVault();
        // At the minimum width the header's three parts must share one line.
        vault.Host.Resize(294, 306);
        vault.Host.Tick();

        vault.Click(2);
        Assert.Contains("Items:", vault.VisibleTexts());
        Assert.DoesNotContain(vault.VisibleTexts(), text => text.EndsWith("selected"));

        vault.Click(4, Ctrl);
        vault.Host.Tick(); // the frame lays the header's new buttons out, as it does before a real press
        Assert.Contains("2 selected", vault.VisibleTexts());
        Assert.DoesNotContain("Items:", vault.VisibleTexts());
        var withdraw = vault.ButtonLabelled("Withdraw 2");
        var clear = vault.ButtonLabelled("Clear");
        Assert.Equal(vault.OriginIn(withdraw).Y, vault.OriginIn(clear).Y);
        Assert.True(vault.OriginIn(clear).X + clear.Bounds.Width <= vault.Host.Content.Bounds.Width, "The Clear button runs past the window.");
        // Withdraw N is live: enabled, and not dimmed by the shared button.
        Assert.True(withdraw.IsEnabled);
        Assert.Equal(1, withdraw.Opacity);

        vault.Press(clear);
        Assert.Empty(vault.Selected);
        Assert.Contains("Items:", vault.VisibleTexts());
        Assert.Contains("317 / 1,000", vault.VisibleTexts());
        Assert.Contains("245 MMD", vault.VisibleTexts());
        Assert.DoesNotContain(vault.VisibleTexts(), text => text.EndsWith("selected"));
        Assert.Null(vault.Host.LastError);
    });

    [Fact]
    public void Unselected_cells_dim_only_while_two_or_more_items_are_selected() => RenderThread.Run(() =>
    {
        using var vault = new SelectionVault();
        vault.Click(2);
        Assert.All(vault.Cells.Take(10), cell => Assert.Equal(1, cell.Opacity));

        vault.Click(4, Ctrl);
        for (var index = 0; index < 10; index++)
            Assert.Equal(index is 2 or 4 ? 1 : 0.4, vault.Cells[index].Opacity);

        vault.Click(4, Ctrl); // back to one
        Assert.All(vault.Cells.Take(10), cell => Assert.Equal(1, cell.Opacity));
        Assert.Null(vault.Host.LastError);
    });

    [Fact]
    public void The_selection_clears_on_a_page_change_a_search_change_and_a_push_refresh() => RenderThread.Run(() =>
    {
        using var vault = new SelectionVault();
        vault.Click(2);
        vault.Click(4, Ctrl);
        vault.Press(vault.Next);
        vault.Settle();
        Assert.Equal(100, vault.Server.ListRequests[^1].Offset);
        Assert.Empty(vault.Selected);

        vault.Click(2);
        vault.Click(4, Ctrl);
        vault.SearchBox.Text = "Ring";
        vault.Settle();
        Assert.Equal("Ring", vault.Server.ListRequests[^1].Search);
        Assert.Empty(vault.Selected);

        vault.Click(2);
        vault.Click(4, Ctrl);
        vault.Server.Push(VaultProtocol.Changed, VaultProtocol.WriteChanged(true, "Withdrawn", "Your item is back in your pack.", vault.Server.Items[0].Guid));
        vault.Settle();
        Assert.Empty(vault.Selected);
        Assert.Null(vault.Host.LastError);
    });

    [Fact]
    public void A_dragged_selection_shows_its_cells_carried_and_bright_only_while_it_is_dragged() => RenderThread.Run(() =>
    {
        using var vault = new SelectionVault();
        vault.Click(2);
        vault.Click(4, Ctrl);
        Assert.DoesNotContain(vault.Cells.OfType<DerethSlot>(), cell => cell.Carried);

        var start = vault.Center(vault.Cells[2]);
        vault.Host.PointerDown(start.X, start.Y);
        vault.Host.PointerMove(start.X + 30, start.Y);
        for (var index = 0; index < 10; index++)
            Assert.Equal(index is 2 or 4, ((DerethSlot)vault.Cells[index]).Carried);
        Assert.Equal(1, vault.Cells[2].Opacity);

        vault.Host.PointerMove(VaultShellPanel.WindowWidth + 80, start.Y);
        vault.Host.PointerUp(VaultShellPanel.WindowWidth + 80, start.Y);
        vault.Step(TimeSpan.FromMilliseconds(30));
        Assert.DoesNotContain(vault.Cells.OfType<DerethSlot>(), cell => cell.Carried);
        Assert.Null(vault.Host.LastError);
    });

    [Fact]
    public void Dragging_an_item_clears_the_selection_and_withdraws_only_that_item() => RenderThread.Run(() =>
    {
        using var vault = new SelectionVault();
        vault.Click(2);
        vault.Click(4, Ctrl);

        // An unselected item: the drag drops the selection and moves that one item, as a stray drag always has.
        var start = vault.Center(vault.Cells[6]);
        vault.Host.PointerDown(start.X, start.Y);
        vault.Host.PointerMove(start.X + 30, start.Y);
        vault.Host.PointerMove(VaultShellPanel.WindowWidth + 80, start.Y);
        vault.Host.PointerUp(VaultShellPanel.WindowWidth + 80, start.Y);
        vault.Step(TimeSpan.FromMilliseconds(30));

        Assert.Empty(vault.Selected);
        Assert.Equal(VaultProtocol.Withdraw, vault.Server.Received.Last());
        Assert.Null(vault.Host.LastError);
    });

    [Fact]
    public void A_press_dragged_past_the_threshold_is_not_a_click_even_with_no_drag_host() => RenderThread.Run(() =>
    {
        // The sample Vault has neither a client nor a drag host, so nothing takes the drag. Released over another cell, it must not select the item pressed.
        VaultShellPanel? panel = null;
        using var host = AvaloniaPanel.Create(() => new VaultShellWindow(panel = new VaultShellPanel(new NoArt())),
            VaultShellPanel.WindowWidth, VaultShellPanel.WindowHeight);
        using var disposePanel = panel!;
        host.Tick();
        var cells = host.Content.GetVisualDescendants().OfType<DerethSlot>().ToArray();
        var start = CentreIn(host, cells[2]);
        var end = CentreIn(host, cells[5]);

        host.PointerDown(start.X, start.Y);
        host.PointerMove(end.X, end.Y);
        host.PointerUp(end.X, end.Y);
        host.Tick();

        Assert.DoesNotContain(cells, cell => cell.Selected);
        Assert.Null(host.LastError);
    });

    private static Point CentreIn(AvaloniaPanel host, Visual cell) =>
        cell.TranslatePoint(new Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2), host.Content)!.Value;

    /// <summary>A live Vault of 317 items in the default window, with the helpers the selection tests need.</summary>
    private sealed class SelectionVault : VaultFixture
    {
        public SelectionVault() : base(Vault(317)) { }

        public IReadOnlyCollection<int> Selected => Client.Selection.Indices;

        public int Anchor => Client.Selection.Anchor;

        public Button Next => Window.GetVisualDescendants().OfType<DerethPagerButton>().ElementAt(1);

        public TextBox SearchBox => Window.GetVisualDescendants().OfType<TextBox>().Single();

        /// <summary>Clicks the cell at a place of the page on screen, with Ctrl or Shift held.</summary>
        public void Click(int index, KeyModifiers modifiers = KeyModifiers.None) => Press(Cells[index], modifiers);

        public Point OriginIn(Control control) => control.TranslatePoint(default, Host.Content)!.Value;

        /// <summary>The text the player can see: a collapsed or hidden line is left out.</summary>
        public List<string> VisibleTexts() =>
            Host.Content.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible).Select(text => text.Text ?? string.Empty).ToList();

        /// <summary>The places of the page whose slots draw as selected.</summary>
        public int[] SelectedSlots() =>
            Enumerable.Range(0, Cells.Length).Where(index => ((DerethSlot)Cells[index]).Selected).ToArray();
    }

    private sealed class NoArt : IGameArtSource
    {
        public GameImage? ReadImage(uint id) => null;
    }
}
