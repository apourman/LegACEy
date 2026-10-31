using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.PanelHost;
using Xunit;
using Writer = LegACEy.Client.Tests.CharacterWindowTests.Writer;

namespace LegACEy.Client.Tests;

public sealed class MagicWindowTests
{
    private const int GameEvent = 0xF7B0, UpdateInt = 0x02CD;
    private const uint Me = 0x50000002;
    private const uint FlameBolt = 27, HealSelf = 6, ArmorOther = 23, Unlevelled = 99;
    // Lead scarab (level I), iron scarab (level II), and a formula that starts with no scarab.
    private const uint Lead = 1, Iron = 2;
    private const uint AllFilters = 0x3FFF;

    private static readonly Dictionary<uint, SpellInfo> Spells = new()
    {
        [FlameBolt] = new("Flame Bolt I", "Shoots a bolt of flame.") { School = 1, Icon = 0x06001000, Formula = new[] { Lead, 7u, 15u } },
        [HealSelf] = new("Heal Self I", "Restores health.") { School = 2, Icon = 0x06001001, Formula = new[] { Lead, 8u } },
        [ArmorOther] = new("armor Other II", "Raises armor.") { School = 4, Icon = 0x06001002, Formula = new[] { Iron, 9u } },
        [Unlevelled] = new("Odd Spell", "No scarab.") { School = 1, Formula = new[] { 9u } },
    };

    private static readonly SpellComponentTable Components = new(new Dictionary<uint, SpellComponentInfo>
    {
        [Lead] = new(Lead, "Lead Scarab", 0x060013E7, 1, 691),
        [7] = new(7, "Hyssop", 0x06002000, 2, 701),
        [8] = new(8, "Ginseng", 0x06002001, 2, 702),
    });

    private static Writer Event(uint type) => new Writer().U32(GameEvent).U32(Me).U32(1).U32(type);

    /// <summary>A PlayerDescription as ACE writes it, with only what the magic state reads (and one of each thing it reads past).</summary>
    private static byte[] Description(uint filters, (uint Wcid, int Amount)[] desired, params uint[] spells)
    {
        var w = Event(MagicState.PlayerDescriptionEvent)
            .U32(0x01 | 0x10) // an int table and a string table
            .U32(1) // the weenie type
            .Header(2).U32(25).U32(12).U32(296).U32(1) // level 12, and Infused Life Magic
            .Header(1).U32(1).U16(3).Bytes(3).Bytes(3) // the name "abc", padded to four with its length
            .U32(0x01 | 0x02 | 0x100 | 0x200) // attributes, skills, spells and enchantments
            .U32(1) // has health
            .U32(0x1 | 0x40) // strength, then health with its current value
            .Bytes(12).Bytes(16)
            .Header(1).Bytes(32); // one skill
        w.Header((ushort)spells.Length);
        foreach (var spell in spells) w.U32(spell).U32(0x40000000); // ACE's 2f
        // Additive enchantments: one with a set id; then vitae.
        w.U32(0x2 | 0x4).U32(1).U16(5).U16(1).U16(3).U16(1).Bytes(4 + 8 + 8 + 4 + 4 + 4 + 8 + 4 + 4 + 4).U32(77)
            .U16(666).U16(0).U16(1).U16(0).Bytes(4 + 8 + 8 + 4 + 4 + 4 + 8 + 4 + 4 + 4);
        w.U32(0x1 | 0x8 | 0x20 | 0x40 | 0x400).U32(0) // options: shortcuts, desired comps, filters, options 2, eight spell lists
            .U32(1).U32(0).U32(0x80000001).U16(0).U16(0); // one shortcut
        w.U32(2).U32(FlameBolt).U32(HealSelf);
        for (var i = 1; i < 8; i++) w.U32(0);
        w.Header((ushort)desired.Length);
        foreach (var (wcid, amount) in desired) w.U32(wcid).U32(amount);
        return w.U32(filters).U32(0).ToArray();
    }

    [Fact]
    public void The_state_reads_the_spellbook_filters_components_and_augmentations_then_follows_each_change()
    {
        var state = new MagicState();
        state.Receive(GameEvent, Description(0x13, new[] { (691u, 50) }, FlameBolt, HealSelf));
        Assert.Equal(new[] { HealSelf, FlameBolt }, state.Spells.OrderBy(s => s));
        Assert.Equal(0x13u, state.Filters);
        Assert.Equal(50, state.Desired[691]);
        Assert.Equal(new[] { 2u }, state.Infused);

        state.Receive(GameEvent, Event(MagicState.UpdateSpellEvent).U16((ushort)ArmorOther).U16(0).ToArray());
        state.Receive(GameEvent, Event(MagicState.RemoveSpellEvent).U16((ushort)HealSelf).U16(0).ToArray());
        Assert.Equal(new[] { ArmorOther, FlameBolt }, state.Spells.OrderBy(s => s));

        // The augmentation lost: Life casts with the full formula again.
        state.Receive(UpdateInt, new Writer().U32(UpdateInt).U8(1).U32(296).U32(0).ToArray());
        Assert.Empty(state.Infused);

        // A message cut short changes nothing.
        state.Receive(GameEvent, Description(0, Array.Empty<(uint, int)>(), FlameBolt).Take(60).ToArray());
        Assert.Equal(2, state.Spells.Count);
    }

    [Fact]
    public void The_spellbook_lists_by_name_only_spells_whose_school_and_level_are_on()
    {
        var known = new[] { FlameBolt, HealSelf, ArmorOther, Unlevelled, 12345u };
        Assert.Equal(new[] { ArmorOther, FlameBolt, HealSelf }, MagicWindow.Listed(known, Spells, AllFilters).Select(s => s.Id));
        // War and level I only: Heal Self is Life, Armor Other is level II, and a spell of no level is never listed.
        Assert.Equal(new[] { FlameBolt }, MagicWindow.Listed(known, Spells, MagicState.WarFilter | MagicState.LevelFilter(1)).Select(s => s.Id));
        Assert.Empty(MagicWindow.Listed(known, Spells, 0));
    }

    [Fact]
    public void Formulas_follow_the_scarab_for_their_level_and_a_focus_takes_scarabs_and_prismatic_tapers()
    {
        Assert.Equal(1u, SpellFormula.Level(Spells[FlameBolt]));
        Assert.Equal(0u, SpellFormula.Level(Spells[Unlevelled]));
        var diamond = new SpellInfo("Six", "") { Formula = new[] { 110u, 7u, 8u, 9u, 10u } };
        Assert.Equal(6u, SpellFormula.Level(diamond));
        Assert.Equal(new[] { 110u, 188u, 188u, 188u }, SpellFormula.ScarabOnly(diamond));
        // ACE's ComputeHash, reading each Windows-1252 byte as signed.
        Assert.Equal(SpellFormula.Hash(new byte[] { 0x61, 0xE9 }), SpellFormula.Hash("aé"));
        Assert.Equal((uint)((0x61 << 4) + unchecked((sbyte)0xE9)), SpellFormula.Hash("aé"));
    }

    [Fact]
    public void The_components_tab_lists_what_is_carried_or_wanted_and_sends_a_new_amount() => RenderThread.Run(() =>
    {
        var state = new MagicState();
        var game = new FakeGame { Carried = { [702] = 12, [999] = 4 } };
        state.SetDesired(691, 25);
        MagicWindow view = null!;
        using var panel = AvaloniaPanel.Create(() => (view = new MagicWindow(state, game, () => Spells, () => Components, null)).Window, MagicWindow.Width, 400);
        // Ginseng is carried, Lead Scarab wanted; weenie class 999 is no component. Scarabs list before herbs, as retail groups them.
        Assert.Equal(new[] { ("Lead Scarab", 0, 25), ("Ginseng", 12, 0) },
            view.ListedComponents().Select(r => (r.Component.Name, r.Carried, r.Desired)));

        // A click lights the row and selects the carried item in the game; a click on empty space only clears the row.
        view.SelectComponent(702);
        view.SelectComponent(0);
        Assert.Equal(new[] { 702u }, game.SelectedComponents);

        view.SetDesired(702, 30);
        view.SetDesired(691, 0);
        Assert.Equal(new[] { (702u, 30), (691u, 0) }, game.Desired);
        Assert.Equal(new[] { 702u }, state.Desired.Keys);
    });

    [Fact]
    public void Filters_send_as_they_change_and_delete_asks_first() => RenderThread.Run(() =>
    {
        var state = new MagicState();
        state.SetFilters(AllFilters);
        foreach (var spell in new[] { FlameBolt, HealSelf }) state.Receive(GameEvent, Event(MagicState.UpdateSpellEvent).U16((ushort)spell).U16(0).ToArray());
        var game = new FakeGame();
        MagicWindow view = null!;
        using var panel = AvaloniaPanel.Create(() => (view = new MagicWindow(state, game, () => Spells, () => Components, null)).Window, MagicWindow.Width, 400);

        view.ToggleFilter(MagicState.LifeFilter);
        Assert.Equal(new[] { AllFilters & ~MagicState.LifeFilter }, game.Filters);
        Assert.Equal(new[] { FlameBolt }, view.ListedSpells().Select(s => s.Id));

        // Nothing selected: Delete does nothing.
        view.AskDelete();
        Assert.False(view.Asking);

        view.Select(FlameBolt);
        Assert.Equal(new[] { FlameBolt }, game.Examined);
        view.AskDelete();
        Assert.True(view.Asking);
        view.Answer(false);
        Assert.Empty(game.Deleted);
        view.AskDelete();
        view.Answer(true);
        Assert.Equal(new[] { FlameBolt }, game.Deleted);
        Assert.Equal(0u, view.Selected);
    });

    [Fact]
    public void The_magic_window_renders_to_pngs_when_asked() => RenderThread.Run(() =>
    {
        // Looked at by a person, not asserted: LEGACEY_MAGIC_RENDER names the folder, LEGACEY_PORTAL_DAT the portal for retail's art.
        var folder = Environment.GetEnvironmentVariable("LEGACEY_MAGIC_RENDER");
        if (folder == null) return;
        Directory.CreateDirectory(folder);
        var portalPath = Environment.GetEnvironmentVariable("LEGACEY_PORTAL_DAT");
        using var portal = portalPath == null ? null : new PortalDat(portalPath);
        var spells = portal?.ReadSpells() ?? Spells;
        var components = portal?.ReadSpellComponents() ?? Components;
        var state = new MagicState();
        state.SetFilters(AllFilters);
        // The recording's spells: Armor Other I to Shock Wave I.
        var names = new[] { "Armor Other I", "Armor Self I", "Drain Health Other I", "Flame Bolt I", "Focus Self I", "Force Bolt I", "Frost Bolt I",
            "Harm Other I", "Heal Other I", "Heal Self I", "Imperil Other I", "Invulnerability Other I", "Invulnerability Self I", "Shock Wave I" };
        foreach (var id in spells.Where(s => names.Contains(s.Value.Name)).GroupBy(s => s.Value.Name).Select(g => g.Min(s => s.Key)))
            state.Receive(GameEvent, Event(MagicState.UpdateSpellEvent).U16((ushort)id).U16(0).ToArray());
        var game = new FakeGame();
        // The tester's packs (backlog 172's screenshot); the fake table has only some of them.
        var carried = new Dictionary<string, int>
        {
            ["Copper Scarab"] = 97, ["Diamond Scarab"] = 10, ["Ginseng"] = 75, ["Gold Scarab"] = 101, ["Gold Scarab Pea"] = 1, ["Hazel Talisman"] = 89,
            ["Iron Scarab"] = 189, ["Lead Scarab"] = 80, ["Mana Scarab"] = 88, ["Myrrh"] = 89, ["Platinum Scarab"] = 83, ["Powdered Lapis Lazuli"] = 70,
            ["Prismatic Taper"] = 2497, ["Pyreal Scarab"] = 98, ["Silver Scarab"] = 100, ["White Taper"] = 56
        };
        foreach (var component in components.Components.Values.Where(c => c.Wcid != 0 && carried.ContainsKey(c.Name)).GroupBy(c => c.Name).Select(g => g.First()))
            game.Carried[component.Wcid] = carried[component.Name];
        foreach (var (name, tab) in new[] { ("spellbook", 0), ("components", 1) })
        {
            MagicWindow view = null!;
            using var panel = AvaloniaPanel.Create(() => (view = new MagicWindow(state, game, () => spells, () => components, portal)).Window,
                MagicWindow.Width, 760);
            view.ShowTab(tab);
            if (tab == 0) view.Select(view.ListedSpells()[2].Id);
            else view.SelectComponent(view.ListedComponents()[4].Component.Wcid);
            panel.Tick();
            panel.Tick();
            PanelPng.Save(panel, Path.Combine(folder, name + ".png"));
        }
    });

    private sealed class FakeGame : IMagicGame
    {
        public readonly Dictionary<uint, int> Carried = new();
        public readonly List<uint> Filters = new(), Deleted = new(), Examined = new(), AddedToBar = new(), SelectedComponents = new();
        public readonly List<(uint, int)> Desired = new();
        public IReadOnlyDictionary<uint, int> CarriedComponents() => Carried;
        public bool SendFilters(uint filters) { Filters.Add(filters); return true; }
        public bool DeleteSpell(uint spell) { Deleted.Add(spell); return true; }
        public bool SendDesired(uint wcid, int amount) { Desired.Add((wcid, amount)); return true; }
        public bool AddToSpellBar(uint spell) { AddedToBar.Add(spell); return true; }
        public bool DropOnSpellBar(uint spell) => AddToSpellBar(spell);
        public void HoverSpellBar(bool dragging) { }
        public void Examine(uint spell) => Examined.Add(spell);
        public void SelectComponent(uint wcid) => SelectedComponents.Add(wcid);
    }
}
