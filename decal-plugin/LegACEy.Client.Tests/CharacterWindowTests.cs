using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using LegACEy.Client.PanelHost;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class CharacterWindowTests
{
    private const int GameEvent = 0xF7B0;

    /// <summary>
    /// A PlayerDescription as ACE's GameEventPlayerDescription writes it, with Decal's message type in front: level 5, 4 credits, 11,056 total
    /// and 11,006 unassigned experience, a name, a bool, a position, every attribute and vital, Run trained and Alchemy untrained.
    /// </summary>
    private static byte[] Description()
    {
        var w = new Writer();
        w.U32(GameEvent).U32(0x50000001).U32(1).U32(CharacterSheet.PlayerDescriptionEvent);
        w.U32(0x01 | 0x02 | 0x10 | 0x20 | 0x80).U32(0x2B);
        w.Header(3).U32(25).U32(5).U32(24).U32(4).U32(134).U32(2);
        w.Header(2).U32(1).I64(11056).U32(2).I64(11006);
        w.Header(1).U32(104).U32(1);
        w.Header(1).U32(1).String("Testerman");
        w.Header(1).U32(14).Bytes(32);
        w.U32(0x01 | 0x02).U32(1);
        w.U32(0x1FF);
        // Strength, Endurance, Quickness, Coordination, Focus, Self: ranks, start, experience.
        foreach (var (ranks, start) in new[] { (0u, 40u), (0u, 30u), (0u, 100u), (0u, 100u), (0u, 50u), (0u, 10u) }) w.U32(ranks).U32(start).U32(0);
        // Health, stamina, mana: ranks, start, experience, current.
        for (var i = 0; i < 3; i++) w.U32(2).U32(0).U32(200).U32(15);
        w.Header(2);
        w.U32(24).U16(3).U16(1).U32(2).U32(400).U32(10).U32(0).F64(0);
        w.U32(38).U16(0).U16(1).U32(1).U32(0).U32(0).U32(0).F64(0);
        return w.ToArray();
    }

    private static CharacterTables Tables() => new(
        new Dictionary<uint, SkillDefinition>
        {
            [24] = new(24, "Run", "Helps you run faster.", 0x06000173, 0, 4, 1, 3, 0, 1, true),
            [38] = new(38, "Alchemy", "Brew potions.", 0x060019E4, 4, 12, 2, 4, 5, 3, true),
            [20] = new(20, "Deception", "Lie.", 0x0600016A, 4, 6, 1, 5, 0, 1, false),
        },
        new CharacterTables.Xp
        {
            Attribute = new uint[] { 0, 110, 277, 501, 784, 1125, 1527, 1988, 2511, 3097, 3746, 4459 },
            Vital = new uint[] { 0, 73, 183, 330 },
            TrainedSkill = new uint[] { 0, 101, 255, 465, 735 },
            SpecializedSkill = new uint[] { 0, 51, 128 },
            Level = new ulong[] { 0, 0, 1000, 2500, 5000, 9000, 17031, 25000 }
        },
        new Dictionary<uint, uint>(), new Dictionary<uint, uint>());

    [Fact]
    public void The_sheet_reads_the_login_description_and_the_updates_after_it()
    {
        var sheet = new CharacterSheet();
        sheet.Receive(GameEvent, Description());

        Assert.True(sheet.Known);
        Assert.Equal(5, sheet.Level);
        Assert.Equal(4, sheet.SkillCredits);
        Assert.Equal(2, sheet.PlayerKillerStatus);
        Assert.Equal(11056, sheet.TotalExperience);
        Assert.Equal(11006, sheet.UnassignedExperience);
        Assert.Equal(new Advancement(0, 30, 0), sheet.Attributes[2]);
        Assert.Equal(new Advancement(0, 100, 0), sheet.Attributes[3]);
        Assert.Equal(new Advancement(2, 0, 200), sheet.Vitals[5]);
        Assert.Equal(new SkillAdvancement(new Advancement(3, 10, 400), SkillTraining.Trained), sheet.Skills[24]);
        Assert.Equal(SkillTraining.Untrained, sheet.Skills[38].Training);

        // Raising Endurance: the attribute update, then the unassigned experience. Decal's bytes may come without the message type.
        sheet.Receive(0x02E3, new Writer().U8(1).U32(2).U32(1).U32(30).U32(110).ToArray());
        sheet.Receive(0x02CF, new Writer().U32(0x02CF).U8(1).U32(2).I64(10896).ToArray());
        Assert.Equal(new Advancement(1, 30, 110), sheet.Attributes[2]);
        Assert.Equal(10896, sheet.UnassignedExperience);

        // Training Alchemy: the skill update, then the credits.
        sheet.Receive(0x02DD, new Writer().U8(1).U32(38).U16(0).U16(1).U32(2).U32(0).U32(5).U32(0).F64(0).ToArray());
        sheet.Receive(0x02CD, new Writer().U8(1).U32(24).U32(-2).ToArray());
        Assert.Equal(SkillTraining.Trained, sheet.Skills[38].Training);
        Assert.Equal(-2, sheet.SkillCredits);

        // The titles at login, then a new title set as the display title.
        sheet.Receive(GameEvent, new Writer().U32(GameEvent).U32(0x50000001).U32(2).U32(CharacterSheet.CharacterTitleEvent).U32(1).U32(7).U32(2).U32(7).U32(9).ToArray());
        Assert.Equal(7u, sheet.DisplayTitle);
        sheet.Receive(GameEvent, new Writer().U32(GameEvent).U32(0x50000001).U32(3).U32(CharacterSheet.UpdateTitleEvent).U32(12).U32(1).ToArray());
        Assert.Equal(new uint[] { 7, 9, 12 }, sheet.Titles);
        Assert.Equal(12u, sheet.DisplayTitle);
    }

    [Fact]
    public void A_message_cut_short_changes_nothing()
    {
        var sheet = new CharacterSheet();
        var description = Description();
        sheet.Receive(GameEvent, description.AsSpan(0, description.Length - 10));
        Assert.False(sheet.Known);
    }

    [Fact]
    public void Costs_are_the_next_rank_and_ten_ranks_less_what_is_spent_and_stop_at_the_last_rank()
    {
        var list = new uint[] { 0, 110, 277, 501, 784 };
        Assert.Equal(((uint?)110, (uint?)784), CharacterRows.Costs(list, new Advancement(0, 10, 0)));
        Assert.Equal(((uint?)167, (uint?)674), CharacterRows.Costs(list, new Advancement(1, 10, 110)));
        Assert.Equal(((uint?)null, (uint?)null), CharacterRows.Costs(list, new Advancement(4, 10, 784)));
    }

    [Fact]
    public void Skills_group_as_retail_does_and_only_untrained_skills_offer_training()
    {
        var sheet = new CharacterSheet();
        sheet.Receive(GameEvent, Description());
        var rows = CharacterRows.Skills(sheet, Tables(), (_, _) => new StatValue(5, 5));

        Assert.Equal(new[] { "Run", "Deception", "Alchemy" }, rows.Select(row => row.Name));
        Assert.Equal(new[] { CharacterGroup.Trained, CharacterGroup.Untrained, CharacterGroup.Unusable }, rows.Select(row => row.Group));
        Assert.Equal(735u - 400u, rows[0].NextCost);
        Assert.Equal(0, rows[0].TrainCost);
        Assert.Equal(4, rows[2].TrainCost);
        Assert.Null(rows[2].NextCost);
        Assert.Equal("(Coordination + Focus) / 3", rows[2].Formula);
        Assert.Equal((17031 - 11056, (11056 - 9000) / (double)(17031 - 9000)), CharacterRows.NextLevel(sheet, Tables()));
    }

    [Fact]
    public void Raise_spends_the_rank_cost_and_train_spends_the_credits_on_the_second_press() => RenderThread.Run(() =>
    {
        var sheet = new CharacterSheet();
        sheet.Receive(GameEvent, Description());
        var game = new FakeGame();
        CharacterWindow view = null!;
        using var panel = AvaloniaPanel.Create(() => (view = new CharacterWindow(sheet, game, null, Tables, () => new Dictionary<uint, string>(),
            Array.Empty<CharacterGroup>(), _ => { })).Window, CharacterWindow.Width, CharacterWindow.Height);
        panel.Tick();

        view.Select(StatKind.Attribute, 2);
        view.Raise(ten: false);
        Assert.Equal((StatKind.Attribute, 2u, 110u), game.Raised.Single());
        // Applied at once, as retail does: the value shows the new rank, and a press straight after costs the next ranks.
        Assert.Equal(31, view.Rows.Single(row => row.Name == "Endurance").Value.Buffed);
        Assert.Equal(11006 - 110, sheet.UnassignedExperience);
        view.Raise(ten: true);
        Assert.Equal((StatKind.Attribute, 2u, 4459u - 110u), game.Raised.Last());

        // The server's update confirms the first raise; Decal's value has caught up, so nothing is added to it twice.
        game.Endurance = 31;
        sheet.Receive(0x02E3, new Writer().U8(1).U32(2).U32(1).U32(30).U32(110).ToArray());
        view.SheetChanged();
        Assert.Equal(31, view.Rows.Single(row => row.Name == "Endurance").Value.Buffed);

        view.ShowTab(1);
        view.Select(StatKind.Skill, 38);
        view.Train();
        Assert.Empty(game.Trained);
        view.Train();
        Assert.Equal((38u, 4), game.Trained.Single());
    });

    [Fact]
    public void A_folded_group_keeps_its_band_and_the_fold_is_saved() => RenderThread.Run(() =>
    {
        var sheet = new CharacterSheet();
        sheet.Receive(GameEvent, Description());
        IReadOnlyCollection<CharacterGroup>? saved = null;
        CharacterWindow view = null!;
        using var panel = AvaloniaPanel.Create(() => (view = new CharacterWindow(sheet, new FakeGame(), null, Tables, () => new Dictionary<uint, string>(),
            Array.Empty<CharacterGroup>(), folds => saved = folds.ToArray())).Window, CharacterWindow.Width, CharacterWindow.Height);
        view.ShowTab(1);
        panel.Tick();

        view.ToggleFold(CharacterGroup.Unusable);
        Assert.Equal(new[] { CharacterGroup.Unusable }, saved);
        Assert.Contains(CharacterGroup.Unusable, view.Folded);
        view.ToggleFold(CharacterGroup.Unusable);
        Assert.Empty(saved!);
    });

    [Fact]
    public void The_character_window_renders_to_pngs_when_asked() => RenderThread.Run(() =>
    {
        // Looked at by a person, not asserted: LEGACEY_CHARACTER_RENDER names the folder; LEGACEY_DAT_DIR gives it the real tables and icons.
        var folder = Environment.GetEnvironmentVariable("LEGACEY_CHARACTER_RENDER");
        if (folder == null) return;
        Directory.CreateDirectory(folder);
        var dats = Environment.GetEnvironmentVariable("LEGACEY_DAT_DIR");
        using var portal = dats == null ? null : new PortalDat(Path.Combine(dats, "client_portal.dat"));
        var tables = portal?.ReadCharacterTables() ?? Tables();
        var titles = portal?.ReadCharacterNames().Titles ?? new Dictionary<uint, string>();
        var sheet = new CharacterSheet();
        sheet.Receive(GameEvent, Description());
        var bowHunter = titles.FirstOrDefault(t => t.Value == "Bow Hunter").Key;
        sheet.Receive(GameEvent, new Writer().U32(GameEvent).U32(1).U32(2).U32(CharacterSheet.CharacterTitleEvent).U32(1).U32(bowHunter).U32(1).U32(bowHunter).ToArray());
        foreach (var (name, tab, select, folds) in new[]
                 {
                     ("attributes", 0, (StatKind.Attribute, 2u), new CharacterGroup[0]),
                     ("skills", 1, (StatKind.Skill, 38u), new CharacterGroup[0]),
                     ("skills-folded", 1, (StatKind.Skill, 24u), new[] { CharacterGroup.Untrained, CharacterGroup.Unusable }),
                     ("titles", 2, (StatKind.Skill, 0u), new CharacterGroup[0])
                 })
        {
            CharacterWindow view = null!;
            using var panel = AvaloniaPanel.Create(() => (view = new CharacterWindow(sheet, new FakeGame(), portal, () => tables, () => titles, folds, _ => { })).Window,
                CharacterWindow.Width, CharacterWindow.Height);
            view.ShowTab(tab);
            if (select.Item2 != 0) view.Select(select.Item1, select.Item2);
            panel.Tick();
            panel.Tick();
            PanelPng.Save(panel, Path.Combine(folder, name + ".png"));
        }
    });

    private sealed class FakeGame : ICharacterGame
    {
        public readonly List<(StatKind, uint, uint)> Raised = new();
        public readonly List<(uint, int)> Trained = new();
        public string Name => "Testerman";
        public string Race => "Viamontian";
        public string Gender => "Male";
        public int Endurance = 30;

        public StatValue? Value(StatKind kind, uint id) => kind switch
        {
            StatKind.Attribute => new StatValue(new[] { 0, 40, Endurance, 100, 100, 50, 10 }[id], new[] { 0, 40, Endurance, 100, 100, 50, 10 }[id]),
            StatKind.Vital => new StatValue(new[] { 0, 15, 0, 30, 0, 10 }[id], new[] { 0, 15, 0, 30, 0, 10 }[id]),
            _ => id switch { 24 => new StatValue(110, 110), 46 => new StatValue(82, 92), _ => new StatValue(5, 5) }
        };

        public int Current(uint vital) => new[] { 0, 15, 0, 30, 0, 10 }[vital];
        public bool Raise(StatKind kind, uint id, uint experience) { Raised.Add((kind, id, experience)); return true; }
        public bool Train(uint skill, int credits) { Trained.Add((skill, credits)); return true; }
        public bool SetTitle(uint title) => true;
    }

    private sealed class Writer
    {
        private readonly MemoryStream _stream = new();
        private readonly BinaryWriter _writer;
        public Writer() => _writer = new BinaryWriter(_stream);
        public Writer U8(byte value) { _writer.Write(value); return this; }
        public Writer U16(ushort value) { _writer.Write(value); return this; }
        public Writer U32(uint value) { _writer.Write(value); return this; }
        public Writer U32(int value) { _writer.Write(value); return this; }
        public Writer I64(long value) { _writer.Write(value); return this; }
        public Writer F64(double value) { _writer.Write(value); return this; }
        public Writer Bytes(int count) { _writer.Write(new byte[count]); return this; }
        public Writer Header(ushort count) => U16(count).U16(32);

        public Writer String(string value)
        {
            U16((ushort)value.Length);
            _writer.Write(Encoding.ASCII.GetBytes(value));
            return Bytes((4 - (2 + value.Length) % 4) % 4);
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}
