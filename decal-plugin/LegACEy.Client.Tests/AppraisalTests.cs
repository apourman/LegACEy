using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using LegACEy.Client.DecalPlugin;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.PanelHost;
using LegACEy.Client.Themes;
using Xunit;

namespace LegACEy.Client.Tests;

/// <summary>
/// The appraisal window's seams: the server's 0x00C9 message in, the window's content out (seam 1), and the opening rules through
/// the inventory port fake (seam 2). Messages are built by <see cref="ServerAppraisal"/>, which mirrors the server's writer.
/// </summary>
public sealed class AppraisalTests
{
    private static readonly IReadOnlyDictionary<uint, SpellInfo> NoSpells = new Dictionary<uint, SpellInfo>();
    private const uint Potion = 0x80000A01;
    private const uint Gem = 0x80000A02;

    [Fact]
    public void A_potion_appraisal_reads_into_a_short_window_of_figures_use_text_and_description()
    {
        var bytes = new ServerAppraisal(Potion)
        {
            Ints = { [19] = 75, [5] = 5, [1] = 0x80 },
            Strings = { [1] = "Mana Potion", [16] = "A small vial of glowing blue liquid.", [14] = "Restores 75 points of Mana." },
            Dids = { [8] = 0x06001F98, [52] = 0x06000001, [50] = 0x06000002 }
        }.Build();

        var content = AppraisalContent.From(AppraisalResponse.Read(bytes), NoSpells);

        Assert.Equal(Potion, content.ObjectId);
        Assert.Equal("Mana Potion", content.Name);
        Assert.False(string.IsNullOrEmpty(content.TypeLine));
        Assert.Equal(new[] { ("Value", "75"), ("Burden", "5") }, content.Figures.Select(figure => (figure.Label, figure.Value)));
        Assert.Equal(0x06001F98u, content.Visual?.Icon);
        // Only the Use section has anything to show, so no Combat, Spells or Requirements heading appears.
        var use = Assert.Single(content.Sections);
        Assert.Equal("Use", use.Heading);
        Assert.Equal("Restores 75 points of Mana.", Assert.Single(use.Rows).Text);
        Assert.Equal("A small vial of glowing blue liquid.", content.Description);
    }

    [Fact]
    public void A_failed_appraisal_shows_the_name_and_figures_the_server_sent()
    {
        var bytes = new ServerAppraisal(Gem) { Success = false, Strings = { [1] = "Peridot" }, Ints = { [19] = 300 } }.Build();

        var response = AppraisalResponse.Read(bytes);
        var content = AppraisalContent.From(response, NoSpells);

        Assert.False(response.Success);
        Assert.Equal("Peridot", content.Name);
        Assert.Equal(new[] { ("Value", "300") }, content.Figures.Select(figure => (figure.Label, figure.Value)));
        Assert.Empty(content.Sections);
    }

    [Fact]
    public void Every_part_the_server_writes_is_read_in_its_order_so_the_stream_stays_aligned()
    {
        var bytes = new ServerAppraisal(0x80000A03)
        {
            Ints = { [19] = 1234 },
            Int64s = { [7] = 99L },
            Bools = { [3] = true },
            Floats = { [5] = 2.5 },
            Strings = { [1] = "Ivory Stick", [14] = "Use text" },
            Dids = { [8] = 0x06001F98 },
            SpellBook = { 0x1111u, 0x2222u },
            Armor = new[] { 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f },
            Creature = new CreatureWrite(10, 20, new uint[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, 0x5, 0x6),
            Weapon = new WeaponWrite(2, 3, 4, 5, 0.5, 1.25, 1.0, 2.0, 1.1, 6),
            Hook = new HookWrite(1, 2, 3),
            ArmorMasks = (0x3, 0x1),
            WeaponMasks = (0x5, 0x4),
            ResistMasks = (0x9, 0x8),
            ArmorLevels = new uint[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }
        }.Build();

        var response = AppraisalResponse.Read(bytes);

        Assert.Equal(1234, response.Ints[19]);
        Assert.Equal(99L, response.Int64s[7]);
        Assert.True(response.Bools[3]);
        Assert.Equal(2.5, response.Floats[5]);
        Assert.Equal("Use text", response.Strings[14]);
        Assert.Equal(0x06001F98u, response.Dids[8]);
        Assert.Equal(new uint[] { 0x1111, 0x2222 }, response.SpellBook);
        Assert.Equal(8f, response.Armor!.Lightning);
        Assert.Equal((0x9u, 10u, 20u), (response.Creature!.Flags, response.Creature.Health, response.Creature.HealthMax));
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, response.Creature.Attributes);
        Assert.Equal(new HighlightMask(0x5, 0x6), response.Creature.AttributeMask);
        Assert.Equal(new WeaponProfile(2, 3, 4, 5, 0.5, 1.25, 1.0, 2.0, 1.1, 6), response.Weapon);
        Assert.Equal(new HookProfile(1, 2, 3), response.Hook);
        Assert.Equal(new HighlightMask(0x3, 0x1), response.ArmorMask);
        Assert.Equal(new HighlightMask(0x5, 0x4), response.WeaponMask);
        Assert.Equal(new HighlightMask(0x9, 0x8), response.ResistMask);
        Assert.Equal(new ArmorLevels(1, 2, 3, 4, 5, 6, 7, 8, 9), response.Levels);
    }

    [Fact]
    public void Right_click_opens_the_window_only_when_the_requested_items_response_arrives() => OnWindowThread(() =>
    {
        var host = new FakeAppraisalHost();
        var port = new FakeInventoryPort();
        var appraisals = new AppraisalFeature(host, _ => { });
        IInventoryPort inventory = new AppraisingInventoryPort(port, appraisals);

        inventory.Assess(Potion);
        Assert.Equal(new[] { $"assess 0x{Potion:X8}" }, port.Commands);
        Assert.Equal(0, host.Opened);

        appraisals.Receive(PotionMessage(Potion, 75));
        Assert.Equal(1, host.Opened);
        Assert.Equal("Mana Potion", appraisals.Shown?.Name);
    });

    [Fact]
    public void An_unrequested_response_only_caches_and_never_opens_the_window() => OnWindowThread(() =>
    {
        var host = new FakeAppraisalHost();
        var appraisals = new AppraisalFeature(host, _ => { });

        appraisals.Receive(PotionMessage(Gem, 300));

        Assert.Equal(0, host.Opened);
        Assert.Null(appraisals.Shown);
        Assert.Equal(300, appraisals.Response(Gem)!.Ints[19]);
    });

    [Fact]
    public void A_response_for_the_item_already_shown_updates_the_window_and_another_item_does_not() => OnWindowThread(() =>
    {
        var host = new FakeAppraisalHost();
        var appraisals = new AppraisalFeature(host, _ => { });
        appraisals.Request(Potion);
        appraisals.Receive(PotionMessage(Potion, 75));

        appraisals.Receive(PotionMessage(Gem, 300));
        Assert.Equal(Potion, appraisals.Shown?.ObjectId);
        Assert.Equal(1, host.Open);

        appraisals.Receive(PotionMessage(Potion, 80));
        Assert.Equal("80", appraisals.Shown?.Figures[0].Value);
        Assert.Equal(1, host.Open);
    });

    [Fact]
    public void The_ivory_stick_shows_its_buffed_damage_speed_and_attack_raised_and_its_type_as_staff()
    {
        // The retail screenshot (https://i.imgur.com/RwUmSMd.png): damage, speed and attack are buffed; the defence bonus is not.
        var bytes = new ServerAppraisal(0x80000A05)
        {
            Strings = { [1] = "Ivory Stick" },
            Ints = { [1] = 0x1, [48] = 44, [353] = 7, [19] = 17285, [5] = 101 },
            Floats = { [29] = 1.21 },
            Weapon = new WeaponWrite(4, 0, 44, 86, 0.52, 1.0, 1.0, 1.0, 1.28, 0),
            WeaponMasks = (0xD, 0xD),
        }.Build();

        var content = AppraisalContent.From(AppraisalResponse.Read(bytes), NoSpells);

        Assert.Equal("Staff · Heavy Weapons", content.TypeLine);
        var combat = Assert.Single(content.Sections, section => section.Heading == "Combat");
        Assert.Equal(new[]
        {
            ("Damage", "41.28 – 86 Bludgeoning", AppraisalTone.Raised),
            ("Speed", "Very Fast (0)", AppraisalTone.Raised),
            ("Attack", "+28%", AppraisalTone.Raised),
            ("Melee defense", "+21%", AppraisalTone.Normal),
        }, combat.Rows.Select(row => (row.Label!, row.Text, row.Tone)));
    }

    [Fact]
    public void A_caster_has_no_weapon_profile_but_shows_its_defence_mana_conversion_and_elemental_damage_in_the_combat_section()
    {
        // A wand: no weapon profile. Melee defence is lowered (highlight without colour); mana conversion and the fire bonus are raised.
        var bytes = new ServerAppraisal(0x80000B01)
        {
            Strings = { [1] = "Wand of Fire Bolts" },
            Ints = { [1] = 0x1000, [19] = 2000, [45] = 16 },
            Floats = { [29] = 1.04, [144] = 0.05, [152] = 1.2 },
            ResistMasks = (0x1000 | 0x2000, 0x1000 | 0x2000),
            WeaponMasks = (0x2, 0x0),
        }.Build();

        var content = AppraisalContent.From(AppraisalResponse.Read(bytes), NoSpells);

        var combat = Assert.Single(content.Sections, section => section.Heading == "Combat");
        Assert.Equal(new (string?, string, AppraisalTone)[]
        {
            ("Melee defense", "+4%", AppraisalTone.Lowered),
            ("Mana conversion", "+5%", AppraisalTone.Raised),
            (null, "Damage bonus for Fire spells:", AppraisalTone.Raised),
            ("vs. Monsters", "+20%", AppraisalTone.Normal),
            ("vs. Players", "+5%", AppraisalTone.Normal),
        }, combat.Rows.Select(row => (row.Label, row.Text, row.Tone)));
    }

    [Fact]
    public void A_bow_shows_its_damage_bonus_modifier_speed_range_and_elemental_bonus_and_no_attack()
    {
        // A bow: a damage bonus of 24 to 30, a 20% damage modifier raised by a buff, speed 40 and a maximum velocity that gives 20 yards.
        var bytes = new ServerAppraisal(0x80000B02)
        {
            Strings = { [1] = "Yumi" },
            Ints = { [1] = 0x100, [204] = 3 },
            Weapon = new WeaponWrite(DamageType: 2, WeaponTime: 40, WeaponSkill: 2, Damage: 30, DamageVariance: 0.2, DamageMod: 1.2,
                WeaponLength: 1.0, MaxVelocity: 10.0, WeaponOffense: 1.0, MaxVelocityEstimated: 1),
            WeaponMasks = (0x20, 0x20),
        }.Build();

        var content = AppraisalContent.From(AppraisalResponse.Read(bytes), NoSpells);

        var combat = Assert.Single(content.Sections, section => section.Heading == "Combat");
        Assert.Equal(new (string?, string, AppraisalTone)[]
        {
            ("Damage Bonus", "24 – 30", AppraisalTone.Normal),
            (null, "Elemental Damage Bonus: 3, Piercing.", AppraisalTone.Normal),
            ("Damage Modifier", "+20%", AppraisalTone.Raised),
            ("Speed", "Average (40)", AppraisalTone.Normal),
            ("Range", "20 yds. (based on STRENGTH 100)", AppraisalTone.Normal),
        }, combat.Rows.Select(row => (row.Label, row.Text, row.Tone)));
    }

    [Fact]
    public void An_armour_piece_names_what_it_covers_on_the_type_line()
    {
        // A chest piece in the chest slot. The world's coverage bits are the chest's and the upper arms' priority.
        var bytes = new ServerAppraisal(0x80000A06) { Strings = { [1] = "Nariyid Breastplate" }, Ints = { [1] = 0x2 } }.Build();

        var content = AppraisalContent.From(AppraisalResponse.Read(bytes), NoSpells, validLocations: 0x200, coverage: 0x0008 | 0x1000);

        Assert.Equal("Armor · Covers Chest, Upper Arms", content.TypeLine);
    }

    [Fact]
    public void A_weapon_names_no_coverage_even_when_the_world_reports_some()
    {
        var bytes = new ServerAppraisal(0x80000A06) { Strings = { [1] = "Ivory Stick" }, Ints = { [1] = 0x1 } }.Build();

        // A two-handed slot (0x0100_0000 is in the weapon and shield slots): no "Covers", whatever the coverage bits say.
        var content = AppraisalContent.From(AppraisalResponse.Read(bytes), NoSpells, validLocations: 0x0100_0000, coverage: 0x0008);

        Assert.Equal("Melee weapon", content.TypeLine);
    }

    [Fact]
    public void A_gem_outside_the_clothing_slots_names_no_coverage_even_when_the_world_reports_some()
    {
        var bytes = new ServerAppraisal(0x80000A07) { Strings = { [1] = "Green Garnet" }, Ints = { [1] = 0x800 } }.Build();

        // No equipable slots: a gem is not clothing, so the coverage bits are not named.
        var content = AppraisalContent.From(AppraisalResponse.Read(bytes), NoSpells, validLocations: 0, coverage: 0x0008 | 0x1000);

        Assert.Equal("Gem", content.TypeLine);
    }

    [Fact]
    public void A_failed_root_check_leaves_retails_examination_panel_untouched() => OnWindowThread(() =>
    {
        var host = new FakeAppraisalHost();
        var game = new FakeAppraisalGame();
        var appraisals = new AppraisalFeature(host, _ => { }, game: game);
        var panel = new NativeUiLifecycleTests.FakeRetailPanelPort { Open = true, Location = new Point(1200, 300) };
        var logged = new List<string>();
        using var examination = new RetailExaminationTakeover(panel, () => Potion, appraisals, logged.Add, () => false);

        examination.Tick(enabled: true);
        examination.Tick(enabled: true);

        // Retail's panel stays open where retail put it: not parked, not closed, nothing asked for and nothing shown by us.
        Assert.True(panel.Open);
        Assert.Equal(new Point(1200, 300), panel.Location);
        Assert.Equal(0, panel.CloseCalls);
        Assert.Empty(game.Appraised);
        Assert.Equal(0, host.Opened);
        // Logged once for the session, not every frame.
        Assert.Single(logged);
    });

    [Fact]
    public void A_breastplate_shows_its_raised_armour_level_and_its_lowered_fire_protection_in_red()
    {
        // Armour is in the same order as the server's ArmorProfile: slashing, piercing, bludgeoning, cold, fire, acid, nether, lightning.
        var bytes = new ServerAppraisal(0x80000A06)
        {
            Strings = { [1] = "Nariyid Breastplate" },
            Ints = { [1] = 0x2, [19] = 8450, [28] = 650 },
            Armor = new[] { 1.2f, 1.0f, 1.2f, 0.78f, 0.38f, 1.0f, 0f, 1.0f },
            ArmorMasks = (0x21, 0x1),
        }.Build();

        var content = AppraisalContent.From(AppraisalResponse.Read(bytes), NoSpells);

        var armor = Assert.Single(content.Sections, section => section.Heading == "Armor");
        Assert.Equal(new[]
        {
            ("Armor level", "650", AppraisalTone.Raised),
            ("Slashing", "Above Average (1.2)", AppraisalTone.Normal),
            ("Piercing", "Average (1.0)", AppraisalTone.Normal),
            ("Bludgeoning", "Above Average (1.2)", AppraisalTone.Normal),
            ("Fire", "Poor (0.4)", AppraisalTone.Lowered),
            ("Cold", "Below Average (0.8)", AppraisalTone.Normal),
            ("Acid", "Average (1.0)", AppraisalTone.Normal),
            ("Lightning", "Average (1.0)", AppraisalTone.Normal),
        }, armor.Rows.Select(row => (row.Label!, row.Text, row.Tone)));
    }

    [Fact]
    public void A_monster_shows_its_type_and_level_its_vitals_as_meters_and_its_attributes_raised_and_lowered()
    {
        // A Drudge Ravener: Strength raised by a buff (highlight and colour), Quickness lowered by a debuff (highlight only).
        var bytes = new ServerAppraisal(0x80000B01)
        {
            Strings = { [1] = "Drudge Ravener" },
            // A heritage on a creature (ACE's Creature.GenerateNewFace sets one from its weenie) does not make it a character.
            Ints = { [25] = 80, [2] = 7, [188] = 1 },
            Creature = new CreatureWrite(1240, 1240, new uint[] { 290, 260, 190, 240, 150, 140, 760, 320, 980, 320 }, 0x5, 0x1),
        }.Build();
        var names = new CharacterNames(new Dictionary<uint, string> { [7] = "Drudge" }, new Dictionary<uint, string>());

        var content = AppraisalContent.From(AppraisalResponse.Read(bytes), NoSpells, names: names);

        Assert.Equal("Drudge · Level 80", content.TypeLine);
        Assert.Equal(string.Empty, content.Subtitle);
        Assert.Null(content.Visual);
        Assert.Empty(content.Figures);
        Assert.Equal(new[] { "Vitals", "Attributes" }, content.Sections.Select(section => section.Heading));
        var vitals = content.Sections[0].Rows.Cast<AppraisalMeterRow>().ToList();
        Assert.Equal(new[]
        {
            ("Health", "1,240 / 1,240", AppraisalMeterFill.Health),
            ("Stamina", "760 / 980", AppraisalMeterFill.Stamina),
            ("Mana", "320 / 320", AppraisalMeterFill.Mana),
        }, vitals.Select(meter => (meter.Name, meter.Text, meter.Fill)));
        Assert.Equal(new[]
        {
            ("Strength", "290", AppraisalTone.Raised),
            ("Endurance", "260", AppraisalTone.Normal),
            ("Coordination", "240", AppraisalTone.Normal),
            ("Quickness", "190", AppraisalTone.Lowered),
            ("Focus", "150", AppraisalTone.Normal),
            ("Self", "140", AppraisalTone.Normal),
        }, content.Sections[1].Rows.Select(row => (row.Label!, row.Text, row.Tone)));
    }

    [Fact]
    public void A_player_shows_its_level_heritage_gender_and_title_its_allegiance_and_fellowship_and_its_attributes()
    {
        // Big Dog: an Aluvian male, a title, a patron and a monarch, rank 4, and a fellowship; Strength raised.
        var bytes = new ServerAppraisal(0x50000C01)
        {
            Strings = { [1] = "Big Dog", [21] = "Ronin Kael", [35] = "Ilsa of Yaraq", [10] = "Lightning" },
            Ints = { [25] = 126, [188] = 1, [113] = 1, [261] = 5, [30] = 4, [35] = 12, [125] = 3700, [2] = 1 },
            Creature = new CreatureWrite(412, 455, new uint[] { 200, 100, 100, 100, 60, 100, 380, 510, 380, 510 }, 0x1, 0x1),
        }.Build();
        var names = new CharacterNames(new Dictionary<uint, string>(), new Dictionary<uint, string> { [5] = "Vanquisher of the Olthoi" });

        var content = AppraisalContent.From(AppraisalResponse.Read(bytes), NoSpells, names: names);

        Assert.Equal("Level 126 · Aluvian Male", content.TypeLine);
        Assert.Equal("Vanquisher of the Olthoi", content.Subtitle);
        Assert.Equal(new[] { "Allegiance", "Vitals", "Attributes", "Character" }, content.Sections.Select(section => section.Heading));
        Assert.Equal(new[]
        {
            ("Monarch", "Ronin Kael"),
            ("Patron", "Ilsa of Yaraq"),
            ("Rank", "4"),
            ("Followers", "12"),
            ("Fellowship", "Lightning"),
        }, content.Sections[0].Rows.Select(row => (row.Label!, row.Text)));
        Assert.Equal(new[] { ("Time in Dereth", "1h 1m 40s"), ("* = Unenchantable", string.Empty) },
            content.Sections[3].Rows.Select(row => (row.Label!, row.Text)));
        Assert.Equal(new[] { ("412 / 455", AppraisalTone.Normal), ("380 / 380", AppraisalTone.Normal), ("510 / 510", AppraisalTone.Normal) },
            content.Sections[1].Rows.Cast<AppraisalMeterRow>().Select(meter => (meter.Text, meter.Tone)));
        Assert.Equal(AppraisalTone.Raised, content.Sections[2].Rows[0].Tone);
        Assert.Equal(new[] { "Strength", "Endurance", "Coordination", "Quickness", "Focus", "Self" },
            content.Sections[2].Rows.Select(row => row.Label!));
    }

    [Fact]
    public void A_character_with_no_named_monarch_shows_its_followers_and_its_society_with_the_rank_word()
    {
        // The template name marks a character (the server sends it for every player); the Celestial Hand bit with rank 150 is an Adept.
        var bytes = new ServerAppraisal(0x50000C03)
        {
            Strings = { [1] = "Lord Two", [5] = "Adventurer" },
            Ints = { [25] = 20, [188] = 2, [113] = 2, [30] = 1, [35] = 1, [281] = 1, [287] = 150 },
            Creature = new CreatureWrite(100, 100, null, 0, 0),
        }.Build();

        var content = AppraisalContent.From(AppraisalResponse.Read(bytes), NoSpells);

        Assert.Equal(new[] { ("Society", "Celestial Hand ~ Adept"), ("Alleg. Monarch", "1 Follower"), ("Rank", "1") },
            content.Sections[0].Rows.Select(row => (row.Label!, row.Text)));
    }

    [Fact]
    public void A_creature_whose_appraisal_failed_shows_its_health_as_a_percentage_and_no_stamina_or_mana_or_attributes()
    {
        var bytes = new ServerAppraisal(0x80000B02)
        {
            Success = false,
            Strings = { [1] = "Drudge Ravener" },
            Creature = new CreatureWrite(412, 455, null, 0, 0),
        }.Build();

        var content = AppraisalContent.From(AppraisalResponse.Read(bytes), NoSpells);

        Assert.Equal(new[] { "Vitals", "Attributes" }, content.Sections.Select(section => section.Heading));
        var vitals = content.Sections[0].Rows;
        Assert.Equal(new[] { ("Health", "91 %"), ("Stamina", "???"), ("Mana", "???") }, vitals.Select(row => (row.Label!, row.Text)));
        Assert.IsNotType<AppraisalMeterRow>(vitals[0]);
        Assert.Equal(new[] { "Strength", "Endurance", "Coordination", "Quickness", "Focus", "Self" },
            content.Sections[1].Rows.Select(row => row.Label!));
        Assert.All(content.Sections[1].Rows, row => Assert.Equal("???", row.Text));
    }

    [Fact]
    public void The_game_event_0xF7B0_carries_an_identify_response_to_the_reader()
    {
        var payload = PotionMessage(Potion, 75);
        var raw = Words(0xF7B0, 0x50000001, 1, AppraisalResponse.IdentifyEvent).Concat(payload).ToArray();

        Assert.Equal(payload, GameChannelTransport.GameEventPayload(0xF7B0, raw, AppraisalResponse.IdentifyEvent));
        Assert.Null(GameChannelTransport.GameEventPayload(0xF7B0, raw, ChannelWire.GameEventType));
    }

    [Fact]
    public void A_payload_cut_short_or_with_a_negative_count_is_malformed_and_never_reads_past_its_end()
    {
        var potion = PotionMessage(Potion, 75);
        Assert.Throws<EndOfStreamException>(() => AppraisalResponse.Read(potion.Take(potion.Length - 2).ToArray()));
        // Flags name the spell book, which is a count of minus one.
        Assert.Throws<InvalidDataException>(() => AppraisalResponse.Read(Words(Potion, 0x10, 1, 0xFFFFFFFF)));
    }

    [Fact]
    public void A_weapon_names_its_skill_in_the_type_line_and_its_workmanship_in_retail_words_and_a_potion_names_no_skill()
    {
        // The skill comes in the weapon profile: the server never sends PropertyInt WeaponSkill as an assessment property.
        var weapon = AppraisalContent.From(AppraisalResponse.Read(new ServerAppraisal(0x80000A04)
        {
            Strings = { [1] = "Ivory Stick" }, Ints = { [1] = 0x1, [105] = 6 },
            Weapon = new WeaponWrite(4, 0, 44, 6, 0, 1.0, 1.0, 1.0, 1.0, 0),
        }.Build()), NoSpells);
        Assert.Equal("Melee weapon · Heavy Weapons", weapon.TypeLine);
        Assert.Equal(("Workmanship", "Nearly flawless (6)"), weapon.Figures.Select(figure => (figure.Label, figure.Value)).Single());

        var potion = AppraisalContent.From(AppraisalResponse.Read(PotionMessage(Potion, 75)), NoSpells);
        Assert.DoesNotContain("·", potion.TypeLine);
    }

    [Fact]
    public void An_item_with_a_material_set_imbue_ratings_and_a_slayer_shows_them_in_the_other_section_in_retail_words()
    {
        var content = AppraisalContent.From(AppraisalResponse.Read(new ServerAppraisal(0x80000A05)
        {
            Strings = { [1] = "Ivory Stick" },
            Ints = { [1] = 0x1, [131] = 64, [265] = 13, [370] = 5, [372] = 3, [179] = 0x20, [166] = 1 }
        }.Build()), NoSpells);

        var other = Assert.Single(content.Sections, section => section.Heading == "Other");
        Assert.Equal(new[]
        {
            ("Material", "Steel"),
            ("Set", "Soldier's"),
            ("Imbued Effect", "Bludgeon Rending"),
            ("Ratings", "Dam 5, Crit 3"),
            ("Properties", "Olthoi slayer"),
            (null, "This item cannot be further imbued."),
        }, other.Rows.Select(row => (row.Label, row.Text)));
    }

    [Fact]
    public void A_portal_shows_its_destination_and_restrictions_in_the_other_section_and_no_use_section()
    {
        var content = AppraisalContent.From(AppraisalResponse.Read(new ServerAppraisal(0x80000A06)
        {
            Strings = { [1] = "Portal", [16] = "A shimmering archway.", [38] = "Holtburg" },
            Ints = { [111] = 0x2 | 0x20 }
        }.Build()), NoSpells);

        var section = Assert.Single(content.Sections);
        Assert.Equal("Other", section.Heading);
        Assert.Equal(new[]
        {
            ("Destination", "Holtburg"),
            (null, "Player Killers may not use this portal."),
            (null, "This portal cannot be recalled nor linked to."),
        }, section.Rows.Select(row => (row.Label, row.Text)));
    }

    [Fact]
    public void An_assess_replaced_by_another_does_not_open_the_window_for_the_first_item() => OnWindowThread(() =>
    {
        var host = new FakeAppraisalHost();
        var appraisals = new AppraisalFeature(host, _ => { });
        appraisals.Request(Potion);
        appraisals.Request(Gem);

        appraisals.Receive(PotionMessage(Potion, 75));
        Assert.Equal(0, host.Opened);

        appraisals.Receive(PotionMessage(Gem, 300));
        Assert.Equal(Gem, appraisals.Shown?.ObjectId);
    });

    [Fact]
    public void An_assess_that_never_answers_does_not_open_the_window_for_a_silent_id_that_comes_later() => OnWindowThread(() =>
    {
        var now = 0L;
        var host = new FakeAppraisalHost();
        var appraisals = new AppraisalFeature(host, _ => { }, () => now);
        appraisals.Request(Potion);

        now = 60_000;
        appraisals.Receive(PotionMessage(Potion, 75));

        Assert.Equal(0, host.Opened);
        Assert.Null(appraisals.Shown);
    });

    [Fact]
    public void Another_appraisal_of_the_same_height_takes_over_the_open_window() => OnWindowThread(() =>
    {
        var host = new FakeAppraisalHost();
        var appraisals = new AppraisalFeature(host, _ => { });
        appraisals.Request(Potion);
        appraisals.Receive(PotionMessage(Potion, 75));
        var first = host.LastWindow;

        appraisals.Request(Gem);
        appraisals.Receive(PotionMessage(Gem, 300));

        Assert.Equal(1, host.Opened);
        Assert.Same(first, host.LastWindow);
        Assert.Equal("300", appraisals.Shown?.Figures[0].Value);
    });

    [Fact]
    public void A_response_that_cannot_be_read_or_shown_is_logged_and_never_thrown() => OnWindowThread(() =>
    {
        var logged = new List<string>();
        var host = new FakeAppraisalHost();
        var appraisals = new AppraisalFeature(host, logged.Add);
        appraisals.Request(Potion);

        appraisals.Receive(new byte[] { 1, 0, 0 });
        Assert.Single(logged);

        host.FailOpen = true;
        appraisals.Receive(PotionMessage(Potion, 75));
        Assert.Equal(2, logged.Count);
        Assert.Equal(0, host.Opened);
    });

    [Fact]
    public void The_ivory_stick_lists_five_spells_with_their_descriptions_then_its_requirements_and_mana()
    {
        // The retail screenshot's item: the spells in the order the server sends them (the same as retail's list), the wield and activation
        // requirements, spellcraft, and mana at 1,492 of 1,494 with one point burned every 15 seconds.
        var bytes = new ServerAppraisal(0x80000A05)
        {
            Ints =
            {
                [1] = 0x1, [48] = 44, [105] = 6, [19] = 17285, [5] = 101, [106] = 370, [107] = 1492, [108] = 1494,
                [109] = 188, [115] = 390, [176] = 44, [158] = 2, [159] = 44, [160] = 420,
            },
            Floats = { [5] = -1.0 / 15 },
            Strings = { [1] = "Ivory Stick", [16] = "Nearly flawless Ivory Stick of Blood Drinker, set with 4 Green Garnets" },
            Dids = { [8] = 0x06001F98 },
            SpellBook = { 2087u, 2096u, 2116u, 2572u, 4405u },
        }.Build();
        var spells = new Dictionary<uint, SpellInfo>
        {
            [2087] = new("Might of the Lugians", "Increases the caster's Strength by 40 points."),
            [2096] = new("Aura of Infected Caress", "Increases a weapon's damage value by 22 points."),
            [2116] = new("Aura of Atlan's Alacrity", "Improves a weapon's speed by 70 points."),
            [2572] = new("Major Coordination", "Increases the target's Coordination by 15 points. Additional spells can be layered over this."),
            [4405] = new("Aura of Incantation of Heart Seeker Self", "Increases a weapon's Attack Skill modifier by 20.0 percentage points."),
        };

        var content = AppraisalContent.From(AppraisalResponse.Read(bytes), spells);

        var sections = content.Sections.ToDictionary(section => section.Heading, section => section.Rows);
        Assert.Equal(new[] { "Spells", "Requirements" }, content.Sections.Select(section => section.Heading));
        Assert.Equal(new AppraisalRow[]
        {
            new AppraisalSpellRow("Might of the Lugians", "Increases the caster's Strength by 40 points."),
            new AppraisalSpellRow("Aura of Infected Caress", "Increases a weapon's damage value by 22 points."),
            new AppraisalSpellRow("Aura of Atlan's Alacrity", "Improves a weapon's speed by 70 points."),
            new AppraisalSpellRow("Major Coordination", "Increases the target's Coordination by 15 points. Additional spells can be layered over this."),
            new AppraisalSpellRow("Aura of Incantation of Heart Seeker Self", "Increases a weapon's Attack Skill modifier by 20.0 percentage points."),
        }, sections["Spells"]);
        Assert.Equal(new AppraisalRow[]
        {
            new("Wield", "Base Heavy Weapons 420"),
            new("Activation", "Arcane Lore 188, Heavy Weapons 390"),
            new("Spellcraft", "370"),
            new AppraisalMeterRow("Mana", 1492, 1494, "1 point per 15 seconds"),
        }, sections["Requirements"]);
    }

    [Fact]
    public void A_spell_the_table_does_not_know_is_named_by_its_id_and_an_enchantment_is_listed_by_the_id_it_carries()
    {
        var bytes = new ServerAppraisal(0x80000A06) { Strings = { [1] = "Ivory Stick" }, SpellBook = { 0x80000000u | 2087u, 9999u } }.Build();
        var spells = new Dictionary<uint, SpellInfo> { [2087] = new("Might of the Lugians", "Increases the caster's Strength by 40 points.") };

        var rows = Assert.Single(AppraisalContent.From(AppraisalResponse.Read(bytes), spells).Sections).Rows;

        Assert.Equal(new AppraisalRow[]
        {
            new AppraisalSpellRow("Might of the Lugians", "Increases the caster's Strength by 40 points."),
            new AppraisalSpellRow("Spell 9999", string.Empty),
        }, rows);
    }

    [Fact]
    public void The_mana_cost_is_one_point_per_the_rounded_seconds_for_a_negative_rate_and_never_per_zero()
    {
        Assert.Equal("1 point per 15 seconds", ManaNote(-1.0 / 15));
        Assert.Equal("1 point per 1 second", ManaNote(-100.0));
        Assert.Null(ManaNote(0.5));
        Assert.Null(ManaNote(0.0));
    }

    /// <summary>The cost line under the mana meter for an item whose mana rate is the given value.</summary>
    private static string? ManaNote(double rate)
    {
        var bytes = new ServerAppraisal(0x80000A07)
        {
            Strings = { [1] = "Wand" }, Ints = { [107] = 10, [108] = 20 }, Floats = { [5] = rate },
        }.Build();
        var meter = Assert.IsType<AppraisalMeterRow>(Assert.Single(AppraisalContent.From(AppraisalResponse.Read(bytes), NoSpells).Sections,
            section => section.Heading == "Requirements").Rows.Single());
        return meter.Note;
    }

    [Fact]
    public void Wield_lines_name_trained_and_specialized_skills_and_factions_and_leave_out_inactive_and_untrained_skills_and_creatures()
    {
        var bytes = new ServerAppraisal(0x80000A08)
        {
            Strings = { [1] = "Ivory Stick" },
            Ints =
            {
                // Trained Heavy Weapons (SkillAdvancementClass 2), then untrained (1), a faction standing, and a creature (no line).
                [158] = 8, [159] = 44, [160] = 2,
                [270] = 8, [271] = 44, [272] = 1,
                [273] = 9, [274] = 0x11F, [275] = 500,
                [276] = 11, [277] = 5, [278] = 3,
            },
        }.Build();

        var requirements = AppraisalContent.From(AppraisalResponse.Read(bytes), NoSpells).Sections
            .Single(section => section.Heading == "Requirements").Rows;

        Assert.Equal(new AppraisalRow[]
        {
            new("Wield", "Trained Heavy Weapons"),
            new("Wield", "Standing with the Celestial Hand 500"),
        }, requirements);
    }

    [Theory]
    [InlineData(true, "", true, InscriptionMode.Editable)]
    [InlineData(true, "Aerbax", true, InscriptionMode.Editable)]
    [InlineData(true, "Ilsa of Yaraq", true, InscriptionMode.ReadOnly)]
    [InlineData(true, "", false, InscriptionMode.ReadOnly)]
    [InlineData(true, "Aerbax", false, InscriptionMode.ReadOnly)]
    [InlineData(false, "Aerbax", true, InscriptionMode.ReadOnly)]
    public void The_inscription_is_editable_only_for_a_carried_inscribable_item_that_is_unscribed_or_scribed_by_the_character(
        bool inscribable, string scribe, bool carried, InscriptionMode expected)
    {
        var content = AppraisalContent.From(AppraisalResponse.Read(InscribedPotion(Potion, scribe.Length > 0 ? "Bonked" : "", scribe, inscribable)),
            NoSpells, "Aerbax", carried);

        Assert.Equal(expected, content.Inscription!.Mode);
    }

    [Fact]
    public void An_item_that_is_neither_inscribable_nor_inscribed_has_no_inscription_field()
    {
        var content = AppraisalContent.From(AppraisalResponse.Read(PotionMessage(Potion, 75)), NoSpells, "Aerbax", carried: true);

        Assert.Null(content.Inscription);
    }

    [Fact]
    public void Another_characters_inscription_reads_with_its_text_and_scribe()
    {
        var content = AppraisalContent.From(AppraisalResponse.Read(InscribedPotion(Potion, "Bonked many a drudge", "Ilsa of Yaraq")), NoSpells, "Aerbax", carried: true);

        Assert.Equal(new AppraisalInscription(InscriptionMode.ReadOnly, "Bonked many a drudge", "Ilsa of Yaraq"), content.Inscription);
    }

    [Fact]
    public void The_set_inscription_frame_is_the_item_then_the_text_after_the_game_action_header()
    {
        var frame = GameChannelTransport.GameActionFrame(7, InscriptionAction.Opcode, InscriptionAction.Payload(Potion, "Hi"));

        Assert.Equal(new byte[] { 0xB1, 0xF7, 0, 0, 7, 0, 0, 0, 0xBF, 0, 0, 0, 0x01, 0x0A, 0, 0x80, 0x02, 0, 0x48, 0x69 }, frame);
    }

    [Fact]
    public void Erasing_sends_an_empty_text_padded_to_four_bytes_and_three_characters_pad_by_three()
    {
        Assert.Equal(new byte[] { 0x01, 0x0A, 0, 0x80, 0, 0, 0, 0 }, InscriptionAction.Payload(Potion, ""));
        Assert.Equal(new byte[] { 0x01, 0x0A, 0, 0x80, 3, 0, 0x61, 0x62, 0x63, 0, 0, 0 }, InscriptionAction.Payload(Potion, "abc"));
    }

    [Fact]
    public void Someone_elses_inscription_shows_as_text_with_its_scribe_and_no_input() => OnWindowThread(() =>
    {
        var host = new FakeAppraisalHost();
        var appraisals = new AppraisalFeature(host, _ => { }, game: new FakeAppraisalGame());
        appraisals.Request(Potion);

        appraisals.Receive(InscribedPotion(Potion, "Bonked many a drudge", "Ilsa of Yaraq"));

        var window = host.LastWindow!;
        var texts = window.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text).ToList();
        Assert.Empty(window.GetLogicalDescendants().OfType<TextBox>());
        Assert.Contains("“Bonked many a drudge”", texts);
        Assert.Contains("Inscribed by Ilsa of Yaraq", texts);
    });

    [Fact]
    public void Enter_saves_once_leaving_it_unchanged_sends_nothing_and_escape_puts_back_the_saved_text() => OnWindowThread(() =>
    {
        var host = new FakeAppraisalHost();
        var game = new FakeAppraisalGame();
        var appraisals = new AppraisalFeature(host, _ => { }, game: game);
        appraisals.Request(Potion);
        appraisals.Receive(InscribedPotion(Potion, "", "", inscribable: true));
        var field = InscriptionField(host);
        Assert.Equal(300, field.MaxLength);

        field.Text = "Remember.";
        Press(field, Key.Enter);
        Leave(field);
        Assert.Equal(new[] { (Potion, "Remember.") }, game.Inscriptions);
        Assert.Equal(new[] { Potion }, game.Appraised);

        // The saved text is the appraisal's, and no refresh has come back yet, so Escape puts back the empty text it was shown with.
        field.Text = "Changed my mind";
        Press(field, Key.Escape);
        Assert.Equal("", field.Text);
        Assert.Single(game.Inscriptions);
    });

    [Fact]
    public void Leaving_the_field_with_changed_text_saves_it_and_empty_text_erases_the_inscription() => OnWindowThread(() =>
    {
        var host = new FakeAppraisalHost();
        var game = new FakeAppraisalGame();
        var appraisals = new AppraisalFeature(host, _ => { }, game: game);
        appraisals.Request(Potion);
        appraisals.Receive(InscribedPotion(Potion, "Old", "Aerbax", inscribable: true));
        var field = InscriptionField(host);

        field.Text = "New";
        Leave(field);
        field.Text = "";
        Press(field, Key.Enter);

        Assert.Equal(new[] { (Potion, "New"), (Potion, "") }, game.Inscriptions);
    });

    [Fact]
    public void Text_is_sent_as_utf8_with_its_character_count_as_the_length()
    {
        // The server reads ReadChars(length) with a UTF-8 reader, so "é" (two bytes, one character) has length 1.
        Assert.Equal(new byte[] { 0x01, 0x0A, 0, 0x80, 0x01, 0x00, 0xC3, 0xA9 }, InscriptionAction.Payload(Potion, "é"));
    }

    [Fact]
    public void After_a_save_the_fresh_appraisal_shows_the_new_text_and_a_refused_one_shows_the_old_text() => OnWindowThread(() =>
    {
        var host = new FakeAppraisalHost();
        var game = new FakeAppraisalGame();
        var appraisals = new AppraisalFeature(host, _ => { }, game: game);
        appraisals.Request(Potion);
        appraisals.Receive(InscribedPotion(Potion, "", "", inscribable: true));
        InscriptionField(host).Text = "Remember.";
        Press(InscriptionField(host), Key.Enter);
        Assert.Equal(new[] { Potion }, game.Appraised);

        // The silent ID's answer carries what was saved and the scribe.
        appraisals.Receive(InscribedPotion(Potion, "Remember.", "Aerbax", inscribable: true));
        Assert.Equal(new AppraisalInscription(InscriptionMode.Editable, "Remember.", "Aerbax"), appraisals.Shown!.Inscription);
        Assert.Equal("Remember.", InscriptionField(host).Text);

        // A refused save: the answer still has the old text, and the field shows it.
        InscriptionField(host).Text = "Refused";
        Press(InscriptionField(host), Key.Enter);
        appraisals.Receive(InscribedPotion(Potion, "Remember.", "Aerbax", inscribable: true));
        Assert.Equal("Remember.", appraisals.Shown!.Inscription!.Text);
        Assert.Equal("Remember.", InscriptionField(host).Text);
    });

    [Fact]
    public void A_refresh_does_not_replace_the_inscription_field_while_it_holds_text_not_yet_saved() => OnWindowThread(() =>
    {
        var host = new FakeAppraisalHost();
        var game = new FakeAppraisalGame();
        var appraisals = new AppraisalFeature(host, _ => { }, game: game);
        appraisals.Request(Potion);
        appraisals.Receive(InscribedPotion(Potion, "Old", "Aerbax", inscribable: true));
        var field = InscriptionField(host);
        field.Text = "Typed, not sent";

        // A refresh of the same item (a silent ID's answer, say) brings other saved text: the field keeps what was typed.
        appraisals.Receive(InscribedPotion(Potion, "Saved elsewhere", "Aerbax", inscribable: true));

        Assert.Same(field, InscriptionField(host));
        Assert.Equal("Typed, not sent", field.Text);
        Assert.Empty(game.Inscriptions);
        // Leaving the field then saves what was typed, once.
        Leave(field);
        Assert.Equal(new[] { (Potion, "Typed, not sent") }, game.Inscriptions);
    });

    [Fact]
    public void A_refresh_of_the_same_item_makes_escape_restore_its_new_saved_text() => OnWindowThread(() =>
    {
        var host = new FakeAppraisalHost();
        var game = new FakeAppraisalGame();
        var appraisals = new AppraisalFeature(host, _ => { }, game: game);
        appraisals.Request(Potion);
        appraisals.Receive(InscribedPotion(Potion, "Old", "Aerbax", inscribable: true));
        var field = InscriptionField(host);
        field.Text = "Typed";
        appraisals.Receive(InscribedPotion(Potion, "Saved elsewhere", "Aerbax", inscribable: true));

        Press(field, Key.Escape);

        Assert.Equal("Saved elsewhere", field.Text);
        Assert.Empty(game.Inscriptions);
    });

    [Fact]
    public void A_refresh_of_another_item_replaces_the_unsaved_field_and_sends_nothing_for_the_first_item() => OnWindowThread(() =>
    {
        const uint OtherPotion = 0x80000B21;
        var host = new FakeAppraisalHost();
        var game = new FakeAppraisalGame();
        var appraisals = new AppraisalFeature(host, _ => { }, game: game);
        appraisals.Request(Potion);
        appraisals.Receive(InscribedPotion(Potion, "Old", "Aerbax", inscribable: true));
        var first = InscriptionField(host);
        first.Text = "Typed for the first item";

        appraisals.Request(OtherPotion);
        appraisals.Receive(InscribedPotion(OtherPotion, "Saved for the second", "Aerbax", inscribable: true));

        Assert.Equal(OtherPotion, appraisals.Shown?.ObjectId);
        Assert.Equal("Saved for the second", InscriptionField(host).Text);
        // The replaced field no longer saves: leaving it sends nothing, least of all the first item's text.
        Leave(first);
        Assert.Empty(game.Inscriptions);
    });

    [Fact]
    public void A_window_the_client_released_is_built_again_for_the_next_appraisal() => OnWindowThread(() =>
    {
        var host = new FakeAppraisalHost();
        var appraisals = new AppraisalFeature(host, _ => { });
        appraisals.Request(Potion);
        appraisals.Receive(PotionMessage(Potion, 75));
        var first = host.LastWindow;

        // The client lets the window go on its own: it is neither open nor hidden, so the next appraisal opens a new one.
        host.Release(AppraisalFeature.WindowId);
        appraisals.Request(Potion);
        appraisals.Receive(PotionMessage(Potion, 80));

        Assert.Equal(2, host.Opened);
        Assert.NotSame(first, host.LastWindow);
        Assert.Equal(Potion, appraisals.Shown?.ObjectId);
    });

    [Fact]
    public void Clearing_at_logoff_does_not_throw_when_the_client_refuses_to_close_the_window() => OnWindowThread(() =>
    {
        var host = new FakeAppraisalHost { FailClose = true };
        var logged = new List<string>();
        var appraisals = new AppraisalFeature(host, logged.Add);
        appraisals.Request(Potion);
        appraisals.Receive(PotionMessage(Potion, 75));

        appraisals.Clear();

        Assert.Null(appraisals.Shown);
        Assert.Contains(logged, line => line.Contains("Could not close the appraisal window", StringComparison.Ordinal));
    });

    [Fact]
    public void Strings_are_read_as_windows_1252_so_byte_0x92_is_a_right_single_quote()
    {
        // The server writes Windows-1252. The writer sends U+0092, which Latin-1 writes as the byte 0x92.
        var bytes = new ServerAppraisal(Potion) { Strings = { [1] = "Asheron\u0092s" } }.Build();

        Assert.Equal("Asheron’s", AppraisalResponse.Read(bytes).Strings[1]);
    }

    // The examination takeover, through the retail panel port fake and the appraisal feature's host fake. The panel parks when it shows,
    // the window opens for the selection's response, and a silent ID without a show opens nothing.

    [Fact]
    public void The_examination_panel_parks_when_it_shows_and_our_window_opens_for_the_selections_response() => OnWindowThread(() =>
    {
        var host = new FakeAppraisalHost();
        var game = new FakeAppraisalGame();
        var appraisals = new AppraisalFeature(host, _ => { }, game: game);
        var panel = new NativeUiLifecycleTests.FakeRetailPanelPort { Open = true, Location = new Point(1200, 300) };
        using var examination = new RetailExaminationTakeover(panel, () => Potion, appraisals, _ => { }, () => true);

        examination.Tick(enabled: true);

        // Waiting for the response: retail's panel is parked out of sight and not closed, so retail's text never shows.
        Assert.Equal(0, panel.CloseCalls);
        Assert.True(panel.Open);
        Assert.Equal(RetailPanelTakeover.Parked, panel.Location);
        Assert.Equal(0, host.Opened);
        // Nothing cached for the object, so it is asked for silently; retail's own identify may also answer.
        Assert.Equal(new[] { Potion }, game.Appraised);
        appraisals.Receive(PotionMessage(Potion, 75));
        Assert.Equal(1, host.Opened);
        Assert.Equal(Potion, appraisals.Shown?.ObjectId);
        // Our window shows: retail's panel is closed on the next frame, and given back where it was on the one after.
        examination.Tick(enabled: true);
        Assert.Equal(1, panel.CloseCalls);
        Assert.False(panel.Open);
        examination.Tick(enabled: true);
        Assert.Equal(new Point(1200, 300), panel.Location);
    });

    [Fact]
    public void An_examination_with_no_response_within_the_timeout_gives_retails_panel_back() => OnWindowThread(() =>
    {
        long now = 0;
        var host = new FakeAppraisalHost();
        var appraisals = new AppraisalFeature(host, _ => { }, clock: () => now, game: new FakeAppraisalGame());
        var panel = new NativeUiLifecycleTests.FakeRetailPanelPort { Open = true, Location = new Point(1200, 300) };
        using var examination = new RetailExaminationTakeover(panel, () => Potion, appraisals, _ => { }, () => true);
        examination.Tick(enabled: true);
        Assert.Equal(RetailPanelTakeover.Parked, panel.Location);

        now = 10_001;
        examination.Tick(enabled: true);

        // The response did not come in time: retail's panel is back on screen, open, and our window stays shut.
        Assert.Equal(new Point(1200, 300), panel.Location);
        Assert.True(panel.Open);
        Assert.Equal(0, panel.CloseCalls);
        // A response that comes late only caches; it does not open the window over retail's panel.
        appraisals.Receive(PotionMessage(Potion, 75));
        examination.Tick(enabled: true);
        Assert.Equal(0, host.Opened);
        Assert.Equal(new Point(1200, 300), panel.Location);
    });

    [Fact]
    public void An_examination_whose_window_cannot_show_the_response_gives_retails_panel_back() => OnWindowThread(() =>
    {
        var host = new FakeAppraisalHost { FailOpen = true };
        var appraisals = new AppraisalFeature(host, _ => { }, game: new FakeAppraisalGame());
        var panel = new NativeUiLifecycleTests.FakeRetailPanelPort { Open = true, Location = new Point(1200, 300) };
        using var examination = new RetailExaminationTakeover(panel, () => Potion, appraisals, _ => { }, () => true);
        examination.Tick(enabled: true);

        appraisals.Receive(PotionMessage(Potion, 75));
        examination.Tick(enabled: true);

        Assert.Equal(0, host.Opened);
        Assert.Null(appraisals.Shown);
        Assert.Equal(0, panel.CloseCalls);
        Assert.True(panel.Open);
        Assert.Equal(new Point(1200, 300), panel.Location);
    });

    [Fact]
    public void An_examination_whose_response_came_first_shows_the_cached_response_when_the_panel_shows() => OnWindowThread(() =>
    {
        var host = new FakeAppraisalHost();
        var appraisals = new AppraisalFeature(host, _ => { });
        appraisals.Receive(PotionMessage(Potion, 75));
        Assert.Equal(0, host.Opened);
        var panel = new NativeUiLifecycleTests.FakeRetailPanelPort { Open = true, Location = new Point(1200, 300) };
        using var examination = new RetailExaminationTakeover(panel, () => Potion, appraisals, _ => { }, () => true);

        examination.Tick(enabled: true);

        Assert.Equal(1, host.Opened);
        Assert.Equal(Potion, appraisals.Shown?.ObjectId);
    });

    [Fact]
    public void An_empty_selection_when_the_panel_shows_leaves_retails_panel_to_retail() => OnWindowThread(() =>
    {
        var host = new FakeAppraisalHost();
        var appraisals = new AppraisalFeature(host, _ => { });
        var panel = new NativeUiLifecycleTests.FakeRetailPanelPort { Open = true, Location = new Point(1200, 300) };
        using var examination = new RetailExaminationTakeover(panel, () => 0, appraisals, _ => { }, () => true);

        examination.Tick(enabled: true);
        appraisals.Receive(PotionMessage(Potion, 75));

        Assert.True(panel.Open);
        Assert.Equal(new Point(1200, 300), panel.Location);
        Assert.Equal(0, panel.CloseCalls);
        Assert.Equal(0, host.Opened);
    });

    [Fact]
    public void A_panel_is_left_to_retail_while_our_window_cannot_show_yet_and_is_not_parked_out_of_sight() => OnWindowThread(() =>
    {
        var host = new FakeAppraisalHost();
        var game = new FakeAppraisalGame();
        var appraisals = new AppraisalFeature(host, _ => { }, game: game);
        uint selected = Potion;
        var panel = new NativeUiLifecycleTests.FakeRetailPanelPort { Open = true, Location = new Point(1200, 300) };
        using var examination = new RetailExaminationTakeover(panel, () => selected, appraisals, _ => { }, () => true);

        examination.Tick(enabled: true, canShow: false);

        // Portal space: nothing is parked, nothing is asked for, and retail's panel stays where it is.
        Assert.Equal(new Point(1200, 300), panel.Location);
        Assert.True(panel.Open);
        Assert.Empty(game.Appraised);
        Assert.Equal(0, host.Opened);

        selected = 0;
        examination.Tick(enabled: true);
        Assert.Equal(new Point(1200, 300), panel.Location);
        Assert.True(panel.Open);
        Assert.Equal(0, panel.CloseCalls);
        Assert.Equal(0, host.Opened);
    });

    [Fact]
    public void A_yielded_panel_is_taken_over_again_when_the_player_selects_another_object() => OnWindowThread(() =>
    {
        const uint OtherPotion = 0x80000B55;
        long now = 0;
        var host = new FakeAppraisalHost();
        var game = new FakeAppraisalGame();
        var appraisals = new AppraisalFeature(host, _ => { }, clock: () => now, game: game);
        uint selected = Potion;
        var panel = new NativeUiLifecycleTests.FakeRetailPanelPort { Open = true, Location = new Point(1200, 300) };
        using var examination = new RetailExaminationTakeover(panel, () => selected, appraisals, _ => { }, () => true);
        examination.Tick(enabled: true);
        now = 10_001;
        examination.Tick(enabled: true);
        Assert.Equal(new Point(1200, 300), panel.Location);

        // Retail's panel is left as retail shows it while the selection stays.
        examination.Tick(enabled: true);
        Assert.Equal(new Point(1200, 300), panel.Location);
        Assert.Equal(0, panel.CloseCalls);

        // Another object selected: it is examined afresh, so retail's panel is parked and our window waits for it.
        selected = OtherPotion;
        examination.Tick(enabled: true);
        Assert.Equal(RetailPanelTakeover.Parked, panel.Location);
        Assert.Equal(new[] { Potion, OtherPotion }, game.Appraised);
        appraisals.Receive(PotionMessage(OtherPotion, 75));
        examination.Tick(enabled: true);
        Assert.Equal(1, host.Opened);
        Assert.Equal(OtherPotion, appraisals.Shown?.ObjectId);
        Assert.Equal(1, panel.CloseCalls);
    });

    [Fact]
    public void An_exception_while_the_examination_waits_gives_retails_panel_back_and_is_logged() => OnWindowThread(() =>
    {
        var clockFails = false;
        var logged = new List<string>();
        var host = new FakeAppraisalHost();
        var appraisals = new AppraisalFeature(host, logged.Add,
            clock: () => clockFails ? throw new InvalidOperationException("The clock failed.") : 0, game: new FakeAppraisalGame());
        var panel = new NativeUiLifecycleTests.FakeRetailPanelPort { Open = true, Location = new Point(1200, 300) };
        using var examination = new RetailExaminationTakeover(panel, () => Potion, appraisals, logged.Add, () => true);
        examination.Tick(enabled: true);
        Assert.Equal(RetailPanelTakeover.Parked, panel.Location);

        // The clock fails while the response is awaited: nothing escapes the takeover, and retail's panel comes back.
        clockFails = true;
        examination.Tick(enabled: true);

        Assert.Equal(new Point(1200, 300), panel.Location);
        Assert.True(panel.Open);
        Assert.Equal(0, panel.CloseCalls);
        Assert.Contains(logged, line => line.Contains("given back", StringComparison.Ordinal));
    });

    [Fact]
    public void A_silent_id_without_the_panel_showing_opens_nothing_and_leaves_the_panel_alone() => OnWindowThread(() =>
    {
        var host = new FakeAppraisalHost();
        var appraisals = new AppraisalFeature(host, _ => { });
        var panel = new NativeUiLifecycleTests.FakeRetailPanelPort { Open = false, Location = new Point(1200, 300) };
        using var examination = new RetailExaminationTakeover(panel, () => Potion, appraisals, _ => { }, () => true);
        examination.Tick(enabled: true);

        appraisals.Receive(PotionMessage(Potion, 75));

        Assert.Equal(0, host.Opened);
        Assert.Null(appraisals.Shown);
        Assert.Equal(0, panel.CloseCalls);
        Assert.Equal(new Point(1200, 300), panel.Location);
    });

    [Fact]
    public void With_the_takeover_off_retails_panel_shows_untouched_and_no_window_opens() => OnWindowThread(() =>
    {
        var host = new FakeAppraisalHost();
        var appraisals = new AppraisalFeature(host, _ => { });
        var panel = new NativeUiLifecycleTests.FakeRetailPanelPort { Open = true, Location = new Point(1200, 300) };
        using var examination = new RetailExaminationTakeover(panel, () => Potion, appraisals, _ => { }, () => true);

        examination.Tick(enabled: false);
        appraisals.Receive(PotionMessage(Potion, 75));

        Assert.True(panel.Open);
        Assert.Equal(new Point(1200, 300), panel.Location);
        Assert.Equal(0, panel.CloseCalls);
        Assert.Equal(0, host.Opened);
    });

    [Fact]
    public void A_failed_park_gives_retail_its_panel_back_open_and_our_window_stays_shut() => OnWindowThread(() =>
    {
        var host = new FakeAppraisalHost();
        var appraisals = new AppraisalFeature(host, _ => { });
        var panel = new NativeUiLifecycleTests.FakeRetailPanelPort { Open = true, Location = new Point(1200, 300), FailNextMove = true };
        using var examination = new RetailExaminationTakeover(panel, () => Potion, appraisals, _ => { }, () => true);

        examination.Tick(enabled: true);
        appraisals.Receive(PotionMessage(Potion, 75));

        Assert.True(panel.Open);
        Assert.Equal(new Point(1200, 300), panel.Location);
        Assert.Equal(0, panel.CloseCalls);
        Assert.Equal(0, host.Opened);
    });

    /// <summary>
    /// Runs on the render thread with Avalonia started, since the window is measured for its height before it opens. The panel host starts
    /// the headless runtime the first time it is created, as it does in the game.
    /// </summary>
    private static void OnWindowThread(Action action) => RenderThread.Run(() =>
    {
        AvaloniaPanel.Create(() => new Grid(), 1, 1).Dispose();
        action();
    });

    /// <summary>A potion's appraisal from the server's writer: a name, a value and a burden.</summary>
    private static byte[] PotionMessage(uint objectId, int value) => new ServerAppraisal(objectId)
    {
        Ints = { [19] = value, [5] = 5, [1] = 0x80 },
        Strings = { [1] = "Mana Potion", [16] = "A small vial of glowing blue liquid.", [14] = "Restores 75 points of Mana." },
        Dids = { [8] = 0x06001F98 }
    }.Build();

    /// <summary>A potion with its inscription and scribe as the server sends them: the inscription (7), its scribe's name (8) and inscribable (22).</summary>
    private static byte[] InscribedPotion(uint objectId, string inscription, string scribe, bool inscribable = true) => new ServerAppraisal(objectId)
    {
        Ints = { [19] = 75, [5] = 5, [1] = 0x80 },
        Bools = { [22] = inscribable },
        Strings = { [1] = "Mana Potion", [7] = inscription, [8] = scribe },
        Dids = { [8] = 0x06001F98 }
    }.Build();

    /// <summary>The one text box in the window's foot: the inscription field, present only when the character can write in it.</summary>
    private static TextBox InscriptionField(FakeAppraisalHost host) => host.LastWindow!.GetLogicalDescendants().OfType<TextBox>().Single();

    /// <summary>A key press in the field, as the panel delivers it to the focused text box.</summary>
    private static void Press(TextBox field, Key key) => field.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = field });

    /// <summary>The field loses focus, as a click elsewhere takes it.</summary>
    private static void Leave(TextBox field) => field.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));

    /// <summary>Little-endian words, as the game event header and the test's raw message are laid out.</summary>
    private static byte[] Words(params uint[] words)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (var word in words) writer.Write(word);
        writer.Flush();
        return stream.ToArray();
    }

    public sealed record CreatureWrite(uint Health, uint HealthMax, uint[]? Attributes, ushort Highlights, ushort Colors);
    public sealed record WeaponWrite(uint DamageType, uint WeaponTime, uint WeaponSkill, uint Damage, double DamageVariance, double DamageMod,
        double WeaponLength, double MaxVelocity, double WeaponOffense, uint MaxVelocityEstimated);
    public sealed record HookWrite(uint Flags, uint ValidLocations, uint AmmoType);

    /// <summary>
    /// The server's identify response as the server writes it: GameEventIdentifyObjectResponse writes the object's id, then AppraiseInfo's
    /// flags, success and the parts those flags name, in the order AppraiseInfoExtensions.Write uses.
    /// </summary>
    private sealed class ServerAppraisal
    {
        private const uint IntTable = 0x1, BoolTable = 0x2, FloatTable = 0x4, StringTable = 0x8, SpellBookFlag = 0x10, WeaponFlag = 0x20,
            HookFlag = 0x40, ArmorFlag = 0x80, CreatureFlag = 0x100, ArmorEnchantment = 0x200, ResistEnchantment = 0x400,
            WeaponEnchantment = 0x800, DidTable = 0x1000, Int64Table = 0x2000, ArmorLevelsFlag = 0x4000;

        private readonly uint _objectId;

        public ServerAppraisal(uint objectId) => _objectId = objectId;

        public bool Success { get; init; } = true;
        public SortedDictionary<uint, int> Ints { get; } = new();
        public SortedDictionary<uint, long> Int64s { get; } = new();
        public SortedDictionary<uint, bool> Bools { get; } = new();
        public SortedDictionary<uint, double> Floats { get; } = new();
        public SortedDictionary<uint, string> Strings { get; } = new();
        public SortedDictionary<uint, uint> Dids { get; } = new();
        public List<uint> SpellBook { get; } = new();
        public float[]? Armor { get; init; }
        public CreatureWrite? Creature { get; init; }
        public WeaponWrite? Weapon { get; init; }
        public HookWrite? Hook { get; init; }
        public (ushort Highlight, ushort Color)? ArmorMasks { get; init; }
        public (ushort Highlight, ushort Color)? WeaponMasks { get; init; }
        public (ushort Highlight, ushort Color)? ResistMasks { get; init; }
        public uint[]? ArmorLevels { get; init; }

        public byte[] Build()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(_objectId);
            writer.Write(Flags());
            writer.Write(Success ? 1u : 0u);
            if (Ints.Count > 0) WriteTable(writer, Ints, (w, value) => w.Write(value));
            if (Int64s.Count > 0) WriteTable(writer, Int64s, (w, value) => w.Write(value));
            if (Bools.Count > 0) WriteTable(writer, Bools, (w, value) => w.Write(value ? 1u : 0u));
            if (Floats.Count > 0) WriteTable(writer, Floats, (w, value) => w.Write(value));
            if (Strings.Count > 0) WriteTable(writer, Strings, WriteString16L);
            if (Dids.Count > 0) WriteTable(writer, Dids, (w, value) => w.Write(value));
            if (SpellBook.Count > 0)
            {
                writer.Write(SpellBook.Count);
                foreach (var spell in SpellBook) writer.Write(spell);
            }
            if (Armor is { } armor) foreach (var protection in armor) writer.Write(protection);
            if (Creature is { } creature)
            {
                writer.Write(CreatureFlags(creature));
                writer.Write(creature.Health);
                writer.Write(creature.HealthMax);
                if (creature.Attributes != null) foreach (var attribute in creature.Attributes) writer.Write(attribute);
                if (creature.Highlights != 0) { writer.Write(creature.Highlights); writer.Write(creature.Colors); }
            }
            if (Weapon is { } weapon)
            {
                writer.Write(weapon.DamageType); writer.Write(weapon.WeaponTime); writer.Write(weapon.WeaponSkill); writer.Write(weapon.Damage);
                writer.Write(weapon.DamageVariance); writer.Write(weapon.DamageMod); writer.Write(weapon.WeaponLength); writer.Write(weapon.MaxVelocity);
                writer.Write(weapon.WeaponOffense); writer.Write(weapon.MaxVelocityEstimated);
            }
            if (Hook is { } hook) { writer.Write(hook.Flags); writer.Write(hook.ValidLocations); writer.Write(hook.AmmoType); }
            if (ArmorMasks is { } armorMasks) { writer.Write(armorMasks.Highlight); writer.Write(armorMasks.Color); }
            if (WeaponMasks is { } weaponMasks) { writer.Write(weaponMasks.Highlight); writer.Write(weaponMasks.Color); }
            if (ResistMasks is { } resistMasks) { writer.Write(resistMasks.Highlight); writer.Write(resistMasks.Color); }
            if (ArmorLevels is { } levels) foreach (var level in levels) writer.Write(level);
            writer.Flush();
            return stream.ToArray();
        }

        private uint Flags()
        {
            uint flags = 0;
            if (Ints.Count > 0) flags |= IntTable;
            if (Int64s.Count > 0) flags |= Int64Table;
            if (Bools.Count > 0) flags |= BoolTable;
            if (Floats.Count > 0) flags |= FloatTable;
            if (Strings.Count > 0) flags |= StringTable;
            if (Dids.Count > 0) flags |= DidTable;
            if (SpellBook.Count > 0) flags |= SpellBookFlag;
            if (Armor != null) flags |= ArmorFlag;
            if (Creature != null) flags |= CreatureFlag;
            if (Weapon != null) flags |= WeaponFlag;
            if (Hook != null) flags |= HookFlag;
            if (ArmorMasks != null) flags |= ArmorEnchantment;
            if (WeaponMasks != null) flags |= WeaponEnchantment;
            if (ResistMasks != null) flags |= ResistEnchantment;
            if (ArmorLevels != null) flags |= ArmorLevelsFlag;
            return flags;
        }

        /// <summary>The creature profile's flags: 0x8 when the attributes are sent, 0x1 when there are buff highlights.</summary>
        private static uint CreatureFlags(CreatureWrite creature) =>
            (creature.Attributes != null ? 0x8u : 0u) | (creature.Highlights != 0 ? 0x1u : 0u);

        private static void WriteTable<T>(BinaryWriter writer, SortedDictionary<uint, T> table, Action<BinaryWriter, T> value)
        {
            writer.Write((ushort)table.Count);
            writer.Write((ushort)8);
            foreach (var (key, item) in table)
            {
                writer.Write(key);
                value(writer, item);
            }
        }

        /// <summary>A 16-bit length, the text, then padding so the whole string is a multiple of four bytes, as the client expects.</summary>
        private static void WriteString16L(BinaryWriter writer, string text)
        {
            var bytes = Encoding.Latin1.GetBytes(text);
            writer.Write((ushort)text.Length);
            writer.Write(bytes);
            const uint multiple = 4;
            var length = (uint)(sizeof(ushort) + bytes.Length);
            writer.Write(new byte[multiple * ((length + multiple - 1) / multiple) - length]);
        }
    }

    /// <summary>The character Aerbax, who carries every item and sends every inscription, recording what the window asks of the game.</summary>
    private sealed class FakeAppraisalGame : IAppraisalGame
    {
        public List<(uint Id, string Text)> Inscriptions { get; } = new();
        public List<uint> Appraised { get; } = new();

        public string CharacterName => "Aerbax";
        public bool IsCarried(uint itemId) => true;

        public bool SetInscription(uint itemId, string text)
        {
            Inscriptions.Add((itemId, text));
            return true;
        }

        public void RequestAppraisal(uint itemId) => Appraised.Add(itemId);
        public uint ValidLocationsOf(uint itemId) => 0;
        public uint CoverageOf(uint itemId) => 0;
    }

    /// <summary>A plugin host with one window, recording what the feature opens. Its art has no images, so items show no icon.</summary>
    private sealed class FakeAppraisalHost : ILegACEyPluginHost
    {
        private readonly HashSet<string> _open = new(StringComparer.Ordinal);

        public int Opened { get; private set; }
        public int Open => _open.Count;
        /// <summary>The content the last opened window was built with.</summary>
        public DerethWindow? LastWindow { get; private set; }
        /// <summary>When set, opening fails the way a broken window does.</summary>
        public bool FailOpen { get; set; }
        /// <summary>When set, closing fails the way a window the client will not close does.</summary>
        public bool FailClose { get; set; }

        public IServerChannel ServerChannel => UnavailableServerChannel.Instance;
        public string PortalPath => string.Empty;
        public IGameArtSource Art { get; } = new NoArt();
        public IItemDragHost ItemDrag => throw new NotSupportedException();
        public IInventoryPort Inventory => throw new NotSupportedException();
        public bool IsWindowOpen(string id) => _open.Contains(id);
        public IRetailPanel TakeOverRetailInventory(Action<bool> retailOpenChanged) => throw new NotSupportedException();

        public bool OpenWindow(WindowDefinition definition, Point location, Func<Action, Control> createContent, Action<Exception> failed, bool ownChrome)
        {
            if (FailOpen) throw new InvalidOperationException("The window broke while opening.");
            LastWindow = (DerethWindow)createContent(() => HideWindow(definition.Id));
            _open.Add(definition.Id);
            Opened++;
            return true;
        }

        public void HideWindow(string id) => _open.Remove(id);

        public void CloseWindow(string id)
        {
            if (FailClose) throw new InvalidOperationException("The window would not close.");
            _open.Remove(id);
        }

        /// <summary>The client lets a window go on its own, without the close action: its surface is gone.</summary>
        public void Release(string id) => _open.Remove(id);
        public int? LoadPluginSettings(string plugin) => null;
        public void SavePluginSettings(string plugin, int value) { }

        private sealed class NoArt : IGameArtSource
        {
            public GameImage? ReadImage(uint id) => null;
        }
    }
}
