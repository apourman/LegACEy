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

public sealed class QuestsWindowTests
{
    private const int GameEvent = 0xF7B0;
    private const uint Me = 0x50000002, Tuskers = 3, Shadows = 1, Timed = 4;

    private static readonly Dictionary<uint, ContractInfo> Table = new()
    {
        [Shadows] = new ContractInfo("The Shadows of Bitter Winter", "Farelaith's brothers.", "", "Shade of Farelaith", "", true, false, 0xC8DC0033, 0, 0xC8DC0100),
        [Tuskers] = new ContractInfo("Test Contract #3", "Kill tuskers.", "%d/20 Tuskers", "Sam", "Bob", false, false, 0xA9B40001, 0x7D640024, 0),
        [Timed] = new ContractInfo("Timed Contract", "Hurry.", "", "Greg", "", false, true, 0, 0, 0)
    };

    private static Writer Event(uint type) => new Writer().U32(GameEvent).U32(Me).U32(1).U32(type);

    private static Writer Tracker(Writer w, uint id, uint stage, double doneIn = 0, double repeatsIn = 0) => w.U32(0).U32(id).U32(stage).F64(doneIn).F64(repeatsIn);

    private static byte[] TableOf(params (uint Id, uint Stage)[] contracts)
    {
        var w = Event(QuestState.TableEvent).Header((ushort)contracts.Length);
        foreach (var (id, stage) in contracts) Tracker(w.U32(id), id, stage);
        return w.ToArray();
    }

    [Fact]
    public void The_state_follows_the_table_then_each_change_and_delete()
    {
        var state = new QuestState(() => 100);
        state.Receive(GameEvent, TableOf((Shadows, 1), (Tuskers, 2)));
        Assert.Equal(new[] { Shadows, Tuskers }, state.Contracts.Select(c => c.Id).OrderBy(id => id));

        state.Receive(GameEvent, Tracker(Event(QuestState.ContractEvent), Tuskers, 9, repeatsIn: 60).U32(0).U32(0).ToArray());
        Assert.Equal(new Contract(Tuskers, 9, 0, 60, 100), state.Contracts.Single(c => c.Id == Tuskers));

        state.Receive(GameEvent, Tracker(Event(QuestState.ContractEvent), Shadows, 1).U32(1).U32(0).ToArray());
        Assert.Equal(new[] { Tuskers }, state.Contracts.Select(c => c.Id));

        // A message cut short changes nothing.
        state.Receive(GameEvent, Event(QuestState.TableEvent).U16(3).U16(32).U32(Shadows).ToArray());
        Assert.Single(state.Contracts);
    }

    [Fact]
    public void Statuses_and_lines_read_as_retail_writes_them()
    {
        Contract At(uint id, uint stage, double doneIn = 0, double repeatsIn = 0) => new(id, stage, doneIn, repeatsIn, 1000);
        Assert.Equal("Available", QuestState.Status(At(Shadows, 1), Table[Shadows], 1000));
        Assert.Equal("In Progress", QuestState.Status(At(Shadows, 2), Table[Shadows], 1000));
        Assert.Equal("Done (1d 1h 1m 1s to Repeat)", QuestState.Status(At(Shadows, 3, repeatsIn: 90061 + 10), Table[Shadows], 1010));
        Assert.Equal("Available", QuestState.Status(At(Shadows, 3, repeatsIn: 5), Table[Shadows], 1010));
        Assert.Equal("Available", QuestState.Status(At(Shadows, 3), Table[Shadows], 1000));
        Assert.Equal("Done", QuestState.Status(At(Tuskers, 3), Table[Tuskers], 1000));
        Assert.Equal("7/20 Tuskers", QuestState.Status(At(Tuskers, 11), Table[Tuskers], 1000));
        Assert.Equal("In Progress", QuestState.Status(At(Shadows, 6), Table[Shadows], 1000));

        Assert.Equal("None", QuestState.Timed(At(Shadows, 2), Table[Shadows], 1000));
        Assert.Equal("2mo 1h 0s", QuestState.Timed(At(Timed, 2, doneIn: 2 * 2592000 + 3600), Table[Timed], 1000));
        Assert.Equal("Finished", QuestState.Timed(At(Timed, 2, doneIn: 30), Table[Timed], 1030));

        // Under way, the ending NPC; otherwise the starting one.
        Assert.Equal(("Bob", 0x7D640024u), QuestState.Contact(At(Tuskers, 2), Table[Tuskers]));
        Assert.Equal(("Sam", 0xA9B40001u), QuestState.Contact(At(Tuskers, 1), Table[Tuskers]));

        Assert.Equal("42.1N, 33.3E", QuestState.Location(0xA9B40001));
        Assert.Equal("21.6S, 1.5W", QuestState.Location(0x7D640024));
        Assert.Equal("Indoors", QuestState.Location(0xC8DC0100));
        Assert.Equal(string.Empty, QuestState.Location(0));
    }

    // Retail's own file for a new character, byte for byte.
    private const string RetailJournal =
        "<NEWP>\r\n<LABE> \r\n<TITL> \r\n<NOTE> \r\n<DAYS> 0\r\n<HOUR> 0\r\n<MINU> 0\r\n<LOC?> FALSE\r\n<LOCX> 0.000000\r\n<LOCY> 0.000000\r\n" +
        "<TIM?> FALSE\r\n<TIME> 0.000000\r\n\r\n";

    [Fact]
    public void The_journal_reads_and_writes_retail_s_format()
    {
        var journal = QuestJournal.Parse(RetailJournal);
        Assert.Single(journal.Pages);
        Assert.Equal(RetailJournal, journal.Write());

        var page = journal.Pages[0];
        (page.Label, page.Title, page.Notes, page.Hours) = ("Keys", "Mosswart camp", "Two lines\nof notes", 2);
        (page.HasLocation, page.North, page.East, page.TimerRunning, page.TimerEnd) = (true, -21.5, -1.7, true, 1800000000);
        var again = QuestJournal.Parse(journal.Write());
        Assert.Equal("Two lines\nof notes", again.Pages[0].Notes);
        Assert.Equal("21.5S, 1.7W", again.Pages[0].LocationText);
        Assert.Equal("1h 0s", again.Pages[0].TimerText(1800000000 - 3600));
        Assert.Equal("Ready", again.Pages[0].TimerText(1800000001));

        // Seeded from retail's file, a page's timer is left behind: retail's clock is not ours.
        Assert.False(QuestJournal.Parse(journal.Write(), carryTimers: false).Pages[0].TimerRunning);
    }

    [Fact]
    public void The_journal_seeds_from_retail_s_file_once_then_keeps_its_own()
    {
        var folder = Path.Combine(Path.GetTempPath(), "legacey-journal-" + Guid.NewGuid().ToString("N"));
        try
        {
            var retail = Path.Combine(folder, "retail.txt");
            var own = Path.Combine(folder, "own", "journal.txt");
            Directory.CreateDirectory(folder);
            File.WriteAllText(retail, RetailJournal.Replace("<TITL> ", "<TITL> From retail"));
            var journal = QuestJournal.Load(own, retail);
            Assert.Equal("From retail", journal.Pages[0].Title);
            journal.Pages[0].Title = "Ours";
            Assert.True(journal.Save(own));
            Assert.Equal("Ours", QuestJournal.Load(own, retail).Pages[0].Title);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Abandon_sends_the_chosen_contract_and_the_list_sorts_by_its_headings() => RenderThread.Run(() =>
    {
        var state = new QuestState(() => 0);
        state.Receive(GameEvent, TableOf((Shadows, 1), (Tuskers, 6), (99, 1)));
        var game = new FakeGame();
        QuestsWindow view = null!;
        using var panel = AvaloniaPanel.Create(() => (view = new QuestsWindow(state, new QuestJournal(), game, () => Table, null)).Window,
            QuestsWindow.Width, QuestsWindow.Height);

        // A contract the table does not know is left out, as retail leaves it.
        Assert.Equal(new[] { "Test Contract #3", "The Shadows of Bitter Winter" }, view.ContractRows().Select(r => r.Info.Name));
        view.SortContracts(1);
        Assert.Equal(new[] { "2/20 Tuskers", "Available" }, view.ContractRows().Select(r => r.Status));
        view.SortContracts(1);
        Assert.Equal(new[] { "Available", "2/20 Tuskers" }, view.ContractRows().Select(r => r.Status));

        view.Abandon();
        Assert.Empty(game.Abandoned);
        view.SelectContract(Tuskers);
        view.Abandon();
        Assert.Equal(new[] { Tuskers }, game.Abandoned);
    });

    [Fact]
    public void The_journal_tab_edits_pages_records_places_and_runs_timers() => RenderThread.Run(() =>
    {
        var journal = new QuestJournal();
        var game = new FakeGame { Coordinates = (33.6, -56.7) };
        QuestsWindow view = null!;
        using var panel = AvaloniaPanel.Create(() => (view = new QuestsWindow(new QuestState(), journal, game, () => Table, null)).Window,
            QuestsWindow.Width, QuestsWindow.Height);
        view.ShowTab(1);
        view.TitleBox.Text = "Shreth hunt";
        view.NotesBox.Text = "North of Holtburg";
        Assert.Equal(("Shreth hunt", "North of Holtburg"), (journal.Pages[0].Title, journal.Pages[0].Notes));
        Assert.True(journal.Dirty);

        view.Record();
        Assert.Equal("33.6N, 56.7W", journal.Pages[0].LocationText);
        view.HoursBox.Text = "2";
        view.StartTimer();
        Assert.Equal("2h 0s", journal.Pages[0].TimerText(game.Time));
        view.StartTimer();
        Assert.False(journal.Pages[0].TimerRunning);

        view.NewPage();
        Assert.Equal((2, 1), (journal.Pages.Count, view.CurrentPage));
        view.LabelBox.Text = "Keys";
        view.ShowPage(0);
        Assert.Equal("Shreth hunt", view.TitleBox.Text);

        // The page list finds pages by their words, opens one on a double click, and deletes the chosen one.
        view.ShowTab(2);
        view.SearchBox.Text = "keys";
        Assert.Equal(new[] { 1 }, view.ListedPages());
        view.SearchBox.Text = string.Empty;
        view.SelectListed(1, open: true);
        Assert.Equal((1, 1), (view.Tab, view.CurrentPage));
        view.ShowTab(2);
        view.SelectListed(0);
        view.DeletePage();
        Assert.Equal(new[] { "Keys" }, journal.Pages.Select(p => p.Label));
    });

    [Fact]
    public void The_quests_window_renders_to_pngs_when_asked() => RenderThread.Run(() =>
    {
        // Looked at by a person, not asserted: LEGACEY_QUESTS_RENDER names the folder, LEGACEY_PORTAL_DAT the portal for the parchment art.
        var folder = Environment.GetEnvironmentVariable("LEGACEY_QUESTS_RENDER");
        if (folder == null) return;
        Directory.CreateDirectory(folder);
        var portalPath = Environment.GetEnvironmentVariable("LEGACEY_PORTAL_DAT");
        using var portal = portalPath == null ? null : new PortalDat(portalPath);
        var state = new QuestState(() => 0);
        state.Receive(GameEvent, TableOf((Shadows, 1), (Tuskers, 6), (Timed, 2)));
        var journal = QuestJournal.Parse(RetailJournal);
        (journal.Pages[0].Title, journal.Pages[0].Label, journal.Pages[0].Notes) = ("Shreth hunt", "Hunt", "North of Holtburg, past the bridge.");
        journal.Pages[0].HasLocation = true;
        journal.Add();
        foreach (var (name, tab) in new[] { ("contracts", 0), ("journal", 1), ("page-list", 2) })
        {
            QuestsWindow view = null!;
            using var panel = AvaloniaPanel.Create(() => (view = new QuestsWindow(state, journal, new FakeGame(), () => Table, portal)).Window,
                QuestsWindow.Width, QuestsWindow.Height);
            view.SelectContract(Tuskers);
            view.ShowTab(tab);
            panel.Tick();
            panel.Tick();
            PanelPng.Save(panel, Path.Combine(folder, name + ".png"));
        }
    });

    private sealed class FakeGame : IQuestsGame
    {
        public readonly List<uint> Abandoned = new();
        public double Time { get; set; } = 1_800_000_000;
        public (double North, double East)? Coordinates { get; set; }
        public bool Abandon(uint contract) { Abandoned.Add(contract); return true; }
    }
}
