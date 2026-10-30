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
    private const double TabY = 15, CellY = 45, CastX = 567;
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

        Click(panel, CellX(1), CellY);
        Assert.Equal(1u, port.Selected);
        Assert.Equal(new[] { 1u }, port.Examined);

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
        Assert.Equal(16 - DerethSpellBarWindow.VisibleCells, view.First);
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
        var (panel, _) = Open(port, spells, portal);
        using var __ = panel;
        panel.Tick();
        panel.Tick();
        PanelPng.Save(panel, Path.Combine(folder, "spellbar.png"));
    });

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
        public bool UiLocked => false;
        public event EventHandler? Changed { add { } remove { } }
    }
}
