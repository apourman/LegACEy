using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using LegACEy.Client.Demo;
using LegACEy.Client.Themes;
using LegACEy.Plugin.Vault;
using Xunit;

namespace LegACEy.Client.Tests;

/// <summary>Selecting Vault items with the mouse: clicks with Ctrl and Shift, the header line and the dimming, over the sample Vault.</summary>
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

        vault.Click(11, Shift);
        Assert.Equal(Enumerable.Range(1, 11), vault.Selected);
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

        vault.ClickButton(clear);
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
        vault.ClickButton(vault.Next);
        vault.Settle();
        Assert.Equal(100, vault.Server.ListRequests[^1].Offset);
        Assert.Empty(vault.Selected);

        vault.Click(2);
        vault.Click(4, Ctrl);
        vault.SearchBox.Text = "Blue";
        vault.Settle();
        Assert.Equal("Blue", vault.Server.ListRequests[^1].Search);
        Assert.Empty(vault.Selected);

        vault.Click(2);
        vault.Click(4, Ctrl);
        vault.Server.Push(VaultProtocol.Changed, VaultProtocol.WriteChanged(true, "Withdrawn", "Your item is back in your pack.", vault.Server.Items[0].Guid));
        vault.Settle();
        Assert.Empty(vault.Selected);
        Assert.Null(vault.Host.LastError);
    });

    [Fact]
    public void Dragging_an_item_clears_the_selection_and_withdraws_only_that_item() => RenderThread.Run(() =>
    {
        using var vault = new SelectionVault();
        vault.Click(2);
        vault.Click(4, Ctrl);

        // Still a single-item drag for now: the batch from a selection is ticket 05.
        var start = vault.Center(vault.Cells[4]);
        vault.Host.PointerDown(start.X, start.Y);
        vault.Host.PointerMove(start.X + 30, start.Y);
        vault.Host.PointerMove(VaultShellPanel.WindowWidth + 80, start.Y);
        vault.Host.PointerUp(VaultShellPanel.WindowWidth + 80, start.Y);
        vault.Step(TimeSpan.FromMilliseconds(30));

        Assert.Empty(vault.Selected);
        Assert.Equal(VaultProtocol.Withdraw, vault.Server.Received.Last());
        Assert.Null(vault.Host.LastError);
    });

    /// <summary>A Vault of 317 items, a third of them blue potions, in its default window, with the mouse helpers the selection tests need.</summary>
    private sealed class SelectionVault : VaultFixture
    {
        public SelectionVault() : base(Vault(317)) { }

        public IReadOnlyCollection<int> Selected => Client.Selection.Indices;

        public int Anchor => Client.Selection.Anchor;

        public Button Next => Window.GetVisualDescendants().OfType<DerethPagerButton>().ElementAt(1);

        public TextBox SearchBox => Window.GetVisualDescendants().OfType<TextBox>().Single();

        /// <summary>Clicks the cell at a place of the page on screen, with Ctrl or Shift held.</summary>
        public void Click(int index, KeyModifiers modifiers = KeyModifiers.None) => ClickAt(Cells[index], modifiers);

        public void ClickButton(Control button) => ClickAt(button, KeyModifiers.None);

        public Button ButtonLabelled(string text) =>
            Window.GetVisualDescendants().OfType<DerethButton>().Single(button => button.Content is TextBlock label && label.Text == text);

        public Point OriginIn(Control control) => control.TranslatePoint(default, Host.Content)!.Value;

        /// <summary>The text the player can see: a collapsed or hidden line is left out.</summary>
        public List<string> VisibleTexts() =>
            Host.Content.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible).Select(text => text.Text ?? string.Empty).ToList();

        private void ClickAt(Control control, KeyModifiers modifiers)
        {
            Host.Tick(); // a press lands on what the last frame drew
            var point = Center(control);
            Host.PointerDown(point.X, point.Y, modifiers);
            Host.PointerUp(point.X, point.Y);
        }

        private static IEnumerable<VaultItemView> Vault(int count)
        {
            var deposited = new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.Zero);
            for (var number = 1; number <= count; number++)
            {
                var name = number % 3 == 0 ? "Blue Potion" : number % 2 == 0 ? "Gold Ring" : "Steel Sword";
                yield return new VaultItemView(0x80100000u + (uint)number, name, 0x2, 1, 120, "held", "Arwic Wanderer", deposited, 0x060011CF, 0, 0x06000FC7, 0, 0, 0);
            }
        }
    }
}
