using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Controls;
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

        var content = AppraisalContent.From(AppraisalResponse.Read(bytes));

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
        var content = AppraisalContent.From(response);

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
        var weapon = AppraisalContent.From(AppraisalResponse.Read(new ServerAppraisal(0x80000A04)
        {
            Strings = { [1] = "Ivory Stick" }, Ints = { [1] = 0x1, [48] = 44, [105] = 6 }
        }.Build()));
        Assert.Equal("Melee weapon · Heavy Weapons", weapon.TypeLine);
        Assert.Equal(("Workmanship", "Nearly flawless (6)"), weapon.Figures.Select(figure => (figure.Label, figure.Value)).Single());

        var potion = AppraisalContent.From(AppraisalResponse.Read(PotionMessage(Potion, 75)));
        Assert.DoesNotContain("·", potion.TypeLine);
    }

    [Fact]
    public void An_item_with_a_set_ratings_and_an_imbue_shows_them_in_the_other_section_in_retail_words()
    {
        var content = AppraisalContent.From(AppraisalResponse.Read(new ServerAppraisal(0x80000A05)
        {
            Strings = { [1] = "Ivory Stick" },
            Ints = { [1] = 0x1, [265] = 13, [370] = 5, [372] = 3, [179] = 0x20 }
        }.Build()));

        var other = Assert.Single(content.Sections, section => section.Heading == "Other");
        Assert.Equal(new[]
        {
            ("Set", "Soldier's"),
            ("Ratings", "Dam 5, Crit 3"),
            ("Properties", "Bludgeon Rending"),
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
        }.Build()));

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
        public void CloseWindow(string id) => _open.Remove(id);
        public int? LoadPluginSettings(string plugin) => null;
        public void SavePluginSettings(string plugin, int value) { }

        private sealed class NoArt : IGameArtSource
        {
            public GameImage? ReadImage(uint id) => null;
        }
    }
}
