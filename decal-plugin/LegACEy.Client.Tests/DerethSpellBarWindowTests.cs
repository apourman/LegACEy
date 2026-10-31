using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.PanelHost;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class DerethSpellBarWindowTests
{
    private static readonly Dictionary<uint, SpellInfo> Spells = Enumerable.Range(1, 20)
        .ToDictionary(i => (uint)i, i => new SpellInfo($"Spell {i}", string.Empty) { School = 1, Formula = new[] { 1u } });

    // Retail's places in window DIPs: the tabs along the top, the first cell, the cell pitch, Cast, and the arrows.
    private const double TabY = 15, CellY = 45, CastX = 567, EndowmentX = 5 + 4 + 16;
    private static double TabX(int tab) => 5 + 47 * tab + 23;
    private static double CellX(int position) => 68 + 32 * position + 16;

    /// <summary>The bar over the port at its design size, at 1x; built inside the panel, once Avalonia is up.</summary>
    private static (AvaloniaPanel Panel, DerethSpellBarWindow View) Open(ISpellBarPort port, IReadOnlyDictionary<uint, SpellInfo>? spells = null,
        IGameArtSource? art = null)
    {
        DerethSpellBarWindow view = null!;
        var panel = AvaloniaPanel.Create(() => view = new DerethSpellBarWindow(port, () => spells ?? Spells, art, null, () => { }),
            DerethSpellBarWindow.DesignWidth, DerethSpellBarWindow.DesignHeight);
        return (panel, view);
    }

    private static void Click(AvaloniaPanel panel, double x, double y)
    {
        panel.Tick();
        panel.PointerDown(x, y);
        panel.PointerUp(x, y);
        panel.Tick();
    }

    [Fact]
    public void A_tab_opens_a_click_selects_and_shows_a_spell_and_cast_casts_it() => RenderThread.Run(() =>
    {
        var port = new FakeSpellBarPort(3, 1, 2);
        var (panel, view) = Open(port);
        using var _ = panel;

        Click(panel, TabX(5), TabY);
        Assert.Equal(5, port.Tab);

        Assert.Equal(string.Empty, view.SelectedName);
        Click(panel, CellX(1), CellY);
        Assert.Equal(1u, port.Selected);
        Assert.Equal(new[] { 1u }, port.Examined);
        Assert.Equal("Spell 1", view.SelectedName);

        // An empty cell does nothing; Cast casts what is selected.
        Click(panel, CellX(6), CellY);
        Assert.Equal(1u, port.Selected);
        Click(panel, CastX, CellY);
        Assert.Equal(new[] { 1u }, port.Cast);
    });

    [Fact]
    public void A_spell_dropped_on_the_cells_goes_before_the_spell_under_it_or_at_the_end() => RenderThread.Run(() =>
    {
        var port = new FakeSpellBarPort(3, 1, 2);
        var (panel, view) = Open(port);
        using var _ = panel;

        Assert.True(view.DropAt(7, new Avalonia.Point(CellX(1), CellY)));
        Assert.Equal(new uint[] { 3, 7, 1, 2 }, port.Spells);
        Assert.True(view.DropAt(8, new Avalonia.Point(CellX(9), CellY)));
        Assert.Equal(new uint[] { 3, 7, 1, 2, 8 }, port.Spells);
        Assert.False(view.DropAt(9, new Avalonia.Point(CastX, CellY)));
    });

    [Fact]
    public void A_spell_dragged_over_a_tab_opens_it_and_a_drop_there_goes_at_its_end() => RenderThread.Run(() =>
    {
        var port = new FakeSpellBarPort(3, 1, 2);
        var (panel, view) = Open(port);
        using var _ = panel;

        view.HoverAt(new Avalonia.Point(TabX(4), TabY));
        Assert.Equal(4, port.Tab);
        Assert.True(view.DropAt(7, new Avalonia.Point(TabX(4), TabY)));
        Assert.Equal(7u, port.Spells[^1]);
        view.HoverAt(null);
    });

    [Fact]
    public void A_drag_takes_the_spell_off_and_a_bar_closing_mid_drag_puts_it_back() => RenderThread.Run(() =>
    {
        var port = new FakeSpellBarPort(3, 1, 2);
        var drag = new FakeItemDragHost();
        DerethSpellBarWindow view = null!;
        using var panel = AvaloniaPanel.Create(() => view = new DerethSpellBarWindow(port, () => Spells, null, drag, () => { }),
            DerethSpellBarWindow.DesignWidth, DerethSpellBarWindow.DesignHeight);
        panel.Tick();
        panel.PointerDown(CellX(1), CellY);
        panel.PointerMove(CellX(1), CellY + 30);
        panel.Tick();
        Assert.Equal(new uint[] { 3, 2 }, port.Spells);

        view.Dispose();
        Assert.Equal(new uint[] { 3, 1, 2 }, port.Spells);
        Assert.Single(drag.IconsShown);
    });

    [Fact]
    public void A_tab_longer_than_the_cells_scrolls_with_its_arrows() => RenderThread.Run(() =>
    {
        var port = new FakeSpellBarPort(Enumerable.Range(1, 16).Select(i => (uint)i).ToArray());
        var (panel, view) = Open(port);
        using var _ = panel;

        Click(panel, 5 + 502 + 11, CellY);
        Click(panel, 5 + 502 + 11, CellY);
        Assert.Equal(2, view.First);
        Click(panel, CellX(0), CellY);
        Assert.Equal(3u, port.Selected);
        // It stops with the last spell in the last whole cell.
        for (var i = 0; i < 5; i++) Click(panel, 5 + 502 + 11, CellY);
        Assert.Equal(16 - view.VisibleCells, view.First);
    });

    [Fact]
    public void Wider_the_bar_shows_more_cells_and_keeps_cast_at_its_right_edge() => RenderThread.Run(() =>
    {
        var port = new FakeSpellBarPort(3, 1, 2);
        DerethSpellBarWindow view = null!;
        using var panel = AvaloniaPanel.Create(() => view = new DerethSpellBarWindow(port, () => Spells, null, null, () => { }), 930, 90);
        panel.Tick();

        Assert.Equal(23, view.VisibleCells);
        Click(panel, 930 - 5 - 37, CellY);
        Assert.Equal(new[] { 0u }, port.Cast);
    });

    [Fact]
    public void Its_height_scales_the_bar_and_clicks_still_land_on_their_cells() => RenderThread.Run(() =>
    {
        var port = new FakeSpellBarPort(3, 1, 2);
        DerethSpellBarWindow view = null!;
        // Two thirds of retail's height and (nearly) of its width: the same 13 cells, smaller.
        using var panel = AvaloniaPanel.Create(() => view = new DerethSpellBarWindow(port, () => Spells, null, null, () => { }), 407, 60);
        panel.Tick();

        Assert.Equal(13, view.VisibleCells);
        Click(panel, CellX(2) * 2 / 3, CellY * 2 / 3);
        Assert.Equal(2u, port.Selected);
        Assert.True(view.DropAt(7, new Avalonia.Point(CellX(0) * 2 / 3, CellY * 2 / 3)));
        Assert.Equal(new uint[] { 7, 3, 1, 2 }, port.Spells);
    });

    [Fact]
    public void The_held_item_shows_at_the_left_a_click_selects_it_and_a_double_click_uses_it() => RenderThread.Run(() =>
    {
        var port = new FakeSpellBarPort(3, 1, 2)
        {
            Endowment = new SpellBarEndowment(new ItemVisual(0x06001234, 0, 0, 0), "White Sapphire Orb (Might of the 5 Mules)", false)
        };
        port.Select(1);
        var (panel, view) = Open(port);
        using var _ = panel;
        Assert.Equal("Spell 1", view.SelectedName);

        Click(panel, EndowmentX, CellY);
        Assert.Equal("White Sapphire Orb (Might of the 5 Mules)", view.SelectedName);
        Assert.Equal(0u, port.Selected);
        Assert.Empty(port.Cast);
        Click(panel, EndowmentX, CellY);
        Assert.Single(port.Cast);
    });

    [Fact]
    public void The_spell_bar_renders_to_a_png_when_asked() => RenderThread.Run(() =>
    {
        // Looked at by a person, not asserted: LEGACEY_SPELLBAR_RENDER names the folder, LEGACEY_PORTAL_DAT the portal for retail's art.
        var folder = Environment.GetEnvironmentVariable("LEGACEY_SPELLBAR_RENDER");
        if (folder == null) return;
        Directory.CreateDirectory(folder);
        var portalPath = Environment.GetEnvironmentVariable("LEGACEY_PORTAL_DAT");
        using var portal = portalPath == null ? null : new PortalDat(portalPath);
        var spells = portal?.ReadSpells() ?? Spells;
        // The screenshot's tab: Strength, Invulnerability, Armor and Focus Self I.
        var names = new[] { "Strength Self I", "Invulnerability Self I", "Armor Self I", "Focus Self I" };
        var ids = names.Select(n => spells.Where(s => s.Value.Name == n).Select(s => s.Key).DefaultIfEmpty(0u).Min()).Where(id => id != 0).ToArray();
        var port = new FakeSpellBarPort(ids);
        port.Select(ids.Skip(1).FirstOrDefault());
        // LEGACEY_SPELLBAR_ENDOWMENT names a 32 by 32 BGRA icon for the item at the left, selected, under retail's name for it.
        var endowmentIcon = Environment.GetEnvironmentVariable("LEGACEY_SPELLBAR_ENDOWMENT");
        IGameArtSource? art = portal;
        if (endowmentIcon != null)
        {
            art = new WithIcon(portal, 0x0600FFFF, new GameImage(32, 32, File.ReadAllBytes(endowmentIcon)));
            port.Endowment = new SpellBarEndowment(new ItemVisual(0x0600FFFF, 0, 0, 0), "White Sapphire Orb (Might of the 5 Mules)", false);
            port.SelectEndowment();
        }
        foreach (var (width, height) in new[] { (DerethSpellBarWindow.DesignWidth, 90), (900, 90), (457, 67), (813, 120) })
        {
            using var panel = AvaloniaPanel.Create(() => new DerethSpellBarWindow(port, () => spells, art, null, () => { }), width, height);
            panel.Tick();
            panel.Tick();
            PanelPng.Save(panel, Path.Combine(folder, $"spellbar-{width}x{height}.png"));
        }
    });

    private sealed class WithIcon(IGameArtSource? inner, uint id, GameImage image) : IGameArtSource
    {
        public GameImage? ReadImage(uint wanted) => wanted == id ? image : inner?.ReadImage(wanted);
    }

    private sealed class FakeSpellBarPort : ISpellBarPort
    {
        private readonly List<uint> _spells;
        public FakeSpellBarPort(params uint[] spells) => _spells = spells.ToList();
        public int Tab { get; set; }
        public IReadOnlyList<uint> Spells => _spells.ToArray();
        public uint Selected { get; private set; }
        public readonly List<uint> Cast = new(), Examined = new();
        public void Select(uint spell) => Selected = spell;
        void ISpellBarPort.Cast() => Cast.Add(Selected);
        public void Remove(uint spell) => _spells.Remove(spell);
        public void Insert(uint spell, int index)
        {
            _spells.Remove(spell);
            _spells.Insert(index < 0 ? _spells.Count : index, spell);
            Selected = spell;
        }
        public void Examine(uint spell) => Examined.Add(spell);
        public SpellBarEndowment? Endowment { get; set; }
        public void SelectEndowment()
        {
            if (Endowment != null) Endowment = Endowment with { Selected = true };
            Selected = 0;
        }
        public bool UiLocked => false;
        public event EventHandler? Changed { add { } remove { } }
    }
}
