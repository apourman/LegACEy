using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using LegACEy.Client.Demo;
using LegACEy.Client.Themes;
using LegACEy.Plugin.Vault;
using Xunit;

namespace LegACEy.Client.Tests;

/// <summary>The Vault window's pages and search, over the real channel wire to a FakeVaultServer of 317 items.</summary>
public sealed class VaultPagingTests
{
    [Fact]
    public void Arrows_page_the_vault_a_hundred_at_a_time_and_are_disabled_at_either_end() => RenderThread.Run(() =>
    {
        using var vault = new PagedVault(317);
        var loads = 0;
        vault.Client.PageLoaded += (_, _) => loads++;
        Assert.Equal("1 – 100 of 317", vault.PagerText);
        Assert.False(vault.Previous.IsEnabled);
        Assert.True(vault.Next.IsEnabled);
        // The window holds one page of the 317.
        Assert.Equal(100, vault.Client.Snapshot!.Items.Count);
        Assert.Equal(317, vault.Client.Snapshot.Total);

        vault.Click(vault.Next);
        Assert.Equal(("", 100, 100), vault.Server.ListRequests[^1]);
        Assert.Equal("101 – 200 of 317", vault.PagerText);
        Assert.Equal(1, loads);

        vault.Click(vault.Next);
        vault.Click(vault.Next);
        Assert.Equal(("", 300, 100), vault.Server.ListRequests[^1]);
        Assert.Equal("301 – 317 of 317", vault.PagerText);
        Assert.Equal(17, vault.Client.Snapshot.Items.Count);
        Assert.False(vault.Next.IsEnabled);
        Assert.True(vault.Previous.IsEnabled);

        vault.Click(vault.Previous);
        Assert.Equal(("", 200, 100), vault.Server.ListRequests[^1]);
        Assert.Equal("201 – 300 of 317", vault.PagerText);
        Assert.Null(vault.Host.LastError);
    });

    [Fact]
    public void Typing_searches_the_whole_vault_and_pages_the_matches_from_the_first() => RenderThread.Run(() =>
    {
        using var vault = new PagedVault(317);
        vault.Click(vault.Next);

        // Half of the 317 items are rings, so the matches take two pages.
        vault.SearchBox.Text = "RING";
        vault.Settle();
        Assert.Equal(("RING", 0, 100), vault.Server.ListRequests[^1]);
        Assert.Equal("1 – 100 of 158", vault.PagerText);
        Assert.Contains("317 / 1,000", vault.Texts());

        vault.Click(vault.Next);
        Assert.Equal(("RING", 100, 100), vault.Server.ListRequests[^1]);
        Assert.Equal("101 – 158 of 158", vault.PagerText);

        // Clearing the search returns to the whole Vault, from its first page.
        vault.SearchBox.Text = string.Empty;
        vault.Settle();
        Assert.Equal(("", 0, 100), vault.Server.ListRequests[^1]);
        Assert.Equal("1 – 100 of 317", vault.PagerText);
        Assert.Null(vault.Host.LastError);
    });

    [Fact]
    public void A_burst_of_typing_asks_the_server_once_for_the_text_it_settled_on() => RenderThread.Run(() =>
    {
        using var vault = new PagedVault(317);
        var requests = vault.Server.ListRequests.Count;

        vault.SearchBox.Text = "r";
        vault.SearchBox.Text = "ri";
        vault.SearchBox.Text = "RING";
        vault.Settle();

        Assert.Equal(requests + 1, vault.Server.ListRequests.Count);
        Assert.Equal(("RING", 0, 100), vault.Server.ListRequests[^1]);
        Assert.Equal("1 – 100 of 158", vault.PagerText);
        Assert.Null(vault.Host.LastError);
    });

    [Fact]
    public void A_push_refetches_the_page_on_screen() => RenderThread.Run(() =>
    {
        using var vault = new PagedVault(317);
        vault.Click(vault.Next);
        var removed = vault.Server.Items[150].Guid; // on the second page

        vault.Server.Edit(items => items.RemoveAt(150));
        vault.Server.Push(VaultProtocol.Changed, VaultProtocol.WriteChanged(true, "Withdrawn", "Your item is back in your pack.", removed));
        vault.Settle();

        Assert.Equal(("", 100, 100), vault.Server.ListRequests[^1]);
        Assert.Equal("101 – 200 of 316", vault.PagerText);
        Assert.DoesNotContain(vault.Client.Snapshot!.Items, item => item.Guid == removed);
        Assert.Null(vault.Host.LastError);
    });

    [Fact]
    public void When_the_page_no_longer_exists_the_window_shows_the_last_page() => RenderThread.Run(() =>
    {
        using var vault = new PagedVault(317);
        for (var click = 0; click < 3; click++) vault.Click(vault.Next); // the fourth page, with 17 items
        Assert.Equal("301 – 317 of 317", vault.PagerText);

        // Items leave while the window isn't looking, so the page it holds is past the end.
        vault.Server.Edit(items => items.RemoveRange(167, 150));
        vault.Server.Push(VaultProtocol.Changed, VaultProtocol.WriteChanged(true, "Withdrawn", "Items were withdrawn.", 0));
        vault.Settle();

        Assert.Equal(("", 300, 100), vault.Server.ListRequests[^2]);
        Assert.Equal(("", 100, 100), vault.Server.ListRequests[^1]);
        Assert.Equal("101 – 167 of 167", vault.PagerText);
        Assert.Equal(67, vault.Client.Snapshot!.Items.Count);
        Assert.Null(vault.Host.LastError);
    });

    [Fact]
    public void A_refused_refetch_keeps_the_page_on_screen_and_the_window_live() => RenderThread.Run(() =>
    {
        using var vault = new PagedVault(317);
        vault.Click(vault.Next); // the second page is on screen
        var shown = vault.Client.Snapshot!.Items[0].Guid;
        var loads = 0;
        vault.Client.PageLoaded += (_, _) => loads++;

        vault.Server.ListStatus = ChannelStatus.RateLimited;
        vault.Server.Push(VaultProtocol.Changed, VaultProtocol.WriteChanged(true, "Withdrawn", "Your item is back in your pack.", 0));
        vault.Settle();

        Assert.Equal(VaultConnection.Live, vault.Client.Connection);
        Assert.Equal(shown, vault.Client.Snapshot!.Items[0].Guid);
        Assert.Equal("Too many requests; try again shortly.", vault.Client.Notice);
        Assert.Equal(0, loads); // the page on screen wasn't replaced
        Assert.Equal("101 – 200 of 317", vault.PagerText);

        // The arrows act on the page on screen, so the next click goes back from it.
        vault.Server.ListStatus = null;
        vault.Click(vault.Previous);
        Assert.Equal(("", 0, 100), vault.Server.ListRequests[^1]);
        Assert.Equal("1 – 100 of 317", vault.PagerText);
        Assert.Null(vault.Host.LastError);
    });

    [Fact]
    public void Moving_an_item_within_a_page_places_it_in_the_whole_vault_order() => RenderThread.Run(() =>
    {
        using var vault = new PagedVault(317);
        vault.Click(vault.Next); // the page holds the items at 100 to 199
        var moving = vault.Client.Snapshot!.Items[0];
        var loads = 0;
        vault.Client.PageLoaded += (_, _) => loads++;

        vault.DragCell(0, 3);
        Assert.Equal(0, loads); // the swap on screen isn't a page loaded: the server's reply is
        vault.Settle();
        Assert.Equal(1, loads);
        Assert.Equal(moving.Guid, vault.Server.Items[103].Guid);
        Assert.Equal(moving.Guid, vault.Client.Snapshot.Items[3].Guid);

        // A search filters the page, so its cells have no place in the whole order: the drag says so, and the move is refused.
        vault.SearchBox.Text = "ring";
        vault.Settle();
        var moves = vault.Server.Received.Count(action => action == VaultProtocol.Move);
        var start = vault.Center(vault.Cells[0]);
        var target = vault.Center(vault.Cells[2]);
        vault.Host.PointerDown(start.X, start.Y);
        vault.Host.PointerMove(start.X + 20, start.Y);
        vault.Host.PointerMove(target.X, target.Y);
        vault.Host.Tick();
        Assert.Contains(VaultClient.SearchBlocksMove, vault.Texts());
        Assert.DoesNotContain(vault.Texts(), text => text.StartsWith("Release to move", StringComparison.Ordinal));
        vault.Host.PointerUp(target.X, target.Y);
        vault.Settle();
        Assert.Equal(moves, vault.Server.Received.Count(action => action == VaultProtocol.Move));
        Assert.Contains(VaultClient.SearchBlocksMove, vault.Texts());
        Assert.Null(vault.Host.LastError);
    });

    [Fact]
    public void A_move_made_while_the_next_page_loads_lands_on_the_page_on_screen() => RenderThread.Run(() =>
    {
        using var vault = new PagedVault(317);
        vault.Click(vault.Next); // the second page is on screen
        var moving = vault.Client.Snapshot!.Items[0];

        vault.Press(vault.Next); // the third page is asked for, and its reply hasn't come
        vault.DragCell(0, 3);
        vault.Settle();

        // The cell is the second page's, so the item goes to 100 + 3 in the Vault's order, not to the third page's.
        Assert.Equal(moving.Guid, vault.Server.Items[103].Guid);
        Assert.Null(vault.Host.LastError);
    });

    /// <summary>A Vault window over <paramref name="count"/> items, half of them rings.</summary>
    private sealed class PagedVault : VaultFixture
    {
        public PagedVault(int count) : base(Vault(count)) { }

        /// <summary>The arrows, previous first.</summary>
        public Button Previous => Window.GetVisualDescendants().OfType<DerethPagerButton>().ElementAt(0);
        public Button Next => Window.GetVisualDescendants().OfType<DerethPagerButton>().ElementAt(1);
        public TextBox SearchBox => Window.GetVisualDescendants().OfType<TextBox>().Single();
        public string PagerText => Texts().Single(text => text.Contains('–'));

        public void Click(Button button)
        {
            Press(button);
            Settle();
        }

        /// <summary>Clicks the button without letting its request's reply arrive.</summary>
        public void Press(Button button)
        {
            var point = Center(button);
            Host.PointerDown(point.X, point.Y);
            Host.PointerUp(point.X, point.Y);
        }

        /// <summary>Drags the cell at <paramref name="from"/> onto the cell at <paramref name="to"/> of the page on screen.</summary>
        public void DragCell(int from, int to)
        {
            var start = Center(Cells[from]);
            var target = Center(Cells[to]);
            Host.PointerDown(start.X, start.Y);
            Host.PointerMove(start.X + 20, start.Y);
            Host.PointerMove(target.X, target.Y);
            Host.PointerUp(target.X, target.Y);
        }
    }

    /// <summary>The Vault's items in order: even numbers are rings.</summary>
    private static IEnumerable<VaultItemView> Vault(int count)
    {
        var deposited = new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.Zero);
        for (var number = 1; number <= count; number++)
        {
            var name = number % 2 == 0 ? "Gold Ring" : "Steel Sword";
            yield return new VaultItemView(0x80100000u + (uint)number, name, 0x2, 1, 120, "held", "Arwic Wanderer", deposited, 0x060011CF, 0, 0x06000FC7, 0, 0, 0);
        }
    }
}
