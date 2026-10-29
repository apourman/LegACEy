using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using LegACEy.Client.GameArt;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Demo;

/// <summary>The social options retail's panel shows, which are retail's own character options.</summary>
public enum SocialOption
{
    IgnoreAllegianceRequests, IgnoreFellowshipRequests, AutoAcceptFellowshipRequests, ShareFellowshipExperience, ShareFellowshipLoot, AppearOffline
}

/// <summary>What the social window reads from the game and asks of it. The client implements it over Decal and retail's own calls.</summary>
public interface ISocialGame
{
    /// <summary>Retail's selected object, 0 for none.</summary>
    uint Selection { get; }
    /// <summary>Selects an object in the game, as a click on it in the world does.</summary>
    void Select(uint id);
    /// <summary>The option as retail holds it, or null when it cannot be read.</summary>
    bool? Option(SocialOption option);
    /// <summary>Sets the option as retail's own switch does, which also tells the server.</summary>
    void SetOption(SocialOption option, bool on);
    /// <summary>Sends one of retail's game actions. False when it could not be sent.</summary>
    bool Send(SocialAction action);
    /// <summary>Starts a tell to a player in the chat window.</summary>
    void Tell(string name, uint id);
}

/// <summary>
/// One of retail's social game actions and its payload, as ACE reads them (its GameActionType and the GameAction handler for each).
/// </summary>
public readonly record struct SocialAction(uint Opcode, byte[] Payload)
{
    // ACE's ChatMessageType.AllChannels: a character squelch covers every channel, as retail's panel squelches.
    private const uint AllChannels = 1;

    public static SocialAction Swear(uint patron) => new(0x001D, Pack(patron));
    /// <summary>Breaks from a patron or releases a vassal.</summary>
    public static SocialAction Break(uint member) => new(0x001E, Pack(member));
    /// <summary>Asks for the allegiance again, as retail's panel does when it opens.</summary>
    public static SocialAction AllegianceUpdate() => new(0x001F, Pack(1));
    public static SocialAction RemoveFriend(uint friend) => new(0x0017, Pack(friend));
    public static SocialAction AddFriend(string name) => new(0x0018, Pack(name));
    public static SocialAction SquelchCharacter(bool squelch, uint id, string name) => new(0x0058, Pack(squelch ? 1u : 0u, id, name, AllChannels));
    public static SocialAction SquelchAccount(bool squelch, string name) => new(0x0059, Pack(squelch ? 1u : 0u, name));
    public static SocialAction CreateFellowship(string name, bool shareExperience) => new(0x00A2, Pack(name, shareExperience ? 1u : 0u));
    public static SocialAction QuitFellowship(bool disband) => new(0x00A3, Pack(disband ? 1u : 0u));
    public static SocialAction Dismiss(uint fellow) => new(0x00A4, Pack(fellow));
    public static SocialAction Recruit(uint player) => new(0x00A5, Pack(player));
    /// <summary>Whether the fellowship panel shows: the server sends the fellows' vitals only while it does.</summary>
    public static SocialAction FellowshipPanel(bool open) => new(0x00A6, Pack(open ? 1u : 0u));
    public static SocialAction Leader(uint fellow) => new(0x0290, Pack(fellow));
    public static SocialAction Openness(bool open) => new(0x0291, Pack(open ? 1u : 0u));

    /// <summary>Numbers as 32-bit fields; text as a 16-bit length and its bytes, padded to a multiple of four with the length.</summary>
    private static byte[] Pack(params object[] fields)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (var field in fields)
        {
            if (field is string text)
            {
                var bytes = Encoding.ASCII.GetBytes(text);
                writer.Write((ushort)bytes.Length);
                writer.Write(bytes);
                writer.Write(new byte[(4 - (2 + bytes.Length) % 4) % 4]);
            }
            else writer.Write(Convert.ToUInt32(field, CultureInfo.InvariantCulture));
        }
        return stream.ToArray();
    }
}

/// <summary>
/// The Dereth social window, retail's Allegiance, Fellowship, Friends and Squelch tabs laid out as retail lays them: the same lines, lists,
/// switches and buttons in the same places. Swear, Break and Kick ask for a second press, as retail asks to confirm them. One instance serves
/// the session; <see cref="Refresh"/> redraws what changed.
/// </summary>
public sealed class SocialWindow
{
    public const string WindowId = "social";
    public const int Width = 310, Height = 600;
    public static readonly WindowResizing Sizing = new(new System.Drawing.Size(290, 460));
    private const double RowHeight = 19, ButtonWidth = 86, ButtonHeight = 27;
    private const long ConfirmMs = 4000;
    private static long NowMs => System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency;
    private static readonly string[] TabNames = { "Allegiance", "Fellowship", "Friends", "Squelch" };
    // ACE's Fellowship.GetMemberSharePercent: what each member of an even-share fellowship gets, by size.
    private static readonly int[] EvenShares = { 100, 100, 75, 60, 55, 50, 45, 40, 35, 30 };
    private const string NoFellowshipText = "You do not belong to a fellowship.\nTo create a fellowship, enter a name in the box below, then click "
        + "Create Fellowship. Once you've created a fellowship, you can start recruiting members.";

    private static readonly IBrush WellBrush = DerethPalette.Brush(Color.FromArgb(0xB0, 0x06, 0x05, 0x04));
    private static readonly IBrush LineBrush = DerethPalette.Brush(DerethPalette.Line);
    private static readonly IBrush HoverBrush = DerethPalette.Brush(DerethPalette.Gold.WithAlpha(0x12));
    private static readonly IBrush SelectedBrush = DerethPalette.Brush(DerethPalette.Gold.WithAlpha(0x26));
    private static readonly IBrush OnlineBrush = DerethPalette.Brush(Color.Parse("#3FE04A"));
    private static readonly IBrush EntryBrush = DerethPalette.Brush(Color.FromArgb(0xE0, 0x0A, 0x09, 0x07));
    private static readonly (IBrush Fill, IBrush Track)[] VitalBrushes =
    {
        (DerethPalette.Brush(Color.Parse("#B01414")), DerethPalette.Brush(Color.Parse("#3A0808"))),
        (DerethPalette.Brush(Color.Parse("#C88A10")), DerethPalette.Brush(Color.Parse("#3A2806"))),
        (DerethPalette.Brush(Color.Parse("#1E5AD0")), DerethPalette.Brush(Color.Parse("#081A3A")))
    };

    private readonly SocialState _state;
    private readonly ISocialGame _game;
    private readonly Func<CharacterTables> _tables;
    private readonly DerethSegmented _tabs;
    private readonly ContentControl _page;
    private readonly Control[] _pages;

    // Allegiance.
    private readonly TextBlock _followers, _rank;
    private readonly StackPanel _chain = new() { Spacing = 2, Margin = new Thickness(6, 6) };
    private readonly StackPanel _vassalList = new();
    private readonly Orb _ignoreAllegiance;
    private readonly DerethButton _swear, _break, _kick;
    // Fellowship, out of one and in one.
    private readonly Grid _noFellowship, _inFellowship;
    private readonly ContentControl _fellowshipPage = new();
    private readonly TextBox _fellowshipName;
    private readonly Orb[] _fellowshipOrbs;
    private readonly DerethButton _create;
    private readonly TextBlock _fellowshipTitle;
    private readonly StackPanel _fellowList = new();
    private readonly DerethButton _leader, _quit, _open, _recruit, _dismiss, _disband;
    // Friends and squelches.
    private readonly StackPanel _friendList = new();
    private readonly Orb _appearOffline;
    private readonly DerethButton _tell, _removeFriend, _add;
    private readonly TextBox _friendName;
    private readonly StackPanel _squelchList = new();
    private readonly DerethButton _removeSquelch, _squelchCharacter, _squelchAccount;
    private readonly TextBox _squelchName;

    private int _tab;
    private uint _vassal, _fellow, _friend;
    // The game's selection when the fellowship list last looked: a new one that is a fellow selects their row, as in retail.
    private uint _seenSelection;
    private Squelch? _squelch;
    private (DerethButton Button, long At)? _arming;
    private readonly Dictionary<Control, string> _drawn = new();

    public SocialWindow(SocialState state, ISocialGame game, Func<CharacterTables> tables)
    {
        _state = state;
        _game = game;
        _tables = tables;
        _tabs = new DerethSegmented(TabNames, cellPadding: new Thickness(4, 4)) { VerticalAlignment = VerticalAlignment.Center };
        _tabs.Chosen += ShowTab;

        // Allegiance: the counts, the patron and monarch, the vassals, retail's switch and three buttons.
        _followers = Label("Followers: 0", 13, DerethPalette.TextBrush);
        _rank = Label("Rank: [0]", 13, DerethPalette.TextBrush);
        var counts = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Margin = new Thickness(2, 0, 2, 4) };
        counts.Children.Add(_followers);
        Grid.SetColumn(_rank, 1);
        counts.Children.Add(_rank);
        _ignoreAllegiance = new Orb(game, SocialOption.IgnoreAllegianceRequests, "Ignore Allegiance Requests") { Margin = new Thickness(14, 8, 0, 4) };
        _swear = Button("Swear", () => Confirm(_swear!, Swear));
        _break = Button("Break", () => Confirm(_break!, Break));
        _kick = Button("Kick", () => Confirm(_kick!, Kick));
        _pages = new Control[4];
        _pages[0] = Stack(
            (counts, false), (new DerethRule(), false), (new Border { Height = 112, Child = _chain }, false),
            (Columns("VASSALS", "XP Produced"), false), (Well(_vassalList, () => { _vassal = 0; Refresh(); }), true),
            (_ignoreAllegiance, false), (Buttons(_swear, _break, _kick), false));

        // Fellowship, out of one: retail's words, the name box, its four switches and Create Fellowship.
        _fellowshipName = Entry(30, () => Refresh());
        _fellowshipOrbs = new[]
        {
            new Orb(game, SocialOption.IgnoreFellowshipRequests, "Ignore Fellowship Requests"),
            new Orb(game, SocialOption.AutoAcceptFellowshipRequests, "Automatically Accept Fellowship Requests"),
            new Orb(game, SocialOption.ShareFellowshipExperience, "Share Fellowship Experience and Luminance"),
            new Orb(game, SocialOption.ShareFellowshipLoot, "Share Fellowship Loot")
        };
        _create = Button("Create Fellowship", CreateFellowship, width: 230);
        var orbs = new StackPanel { Spacing = 2, Margin = new Thickness(26, 6, 0, 10) };
        foreach (var orb in _fellowshipOrbs) orbs.Children.Add(orb);
        var words = Label(NoFellowshipText, 13, DerethPalette.TextBrush);
        words.TextWrapping = TextWrapping.Wrap;
        words.VerticalAlignment = VerticalAlignment.Top;
        words.Margin = new Thickness(8, 10, 8, 0);
        // Retail's words fill the top third; the rule, the name box, the switches and the button follow from there.
        words.Height = 180;
        _noFellowship = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,*") };
        Place(_noFellowship, words, 0);
        Place(_noFellowship, new DerethRule(), 1);
        Place(_noFellowship, Field("Fellowship Name:", _fellowshipName, new Thickness(30, 8, 8, 0)), 2);
        Place(_noFellowship, orbs, 3);
        Place(_noFellowship, Centred(_create), 4);

        // Fellowship, in one: its name, the fellows with their vitals, and six buttons in retail's two rows.
        _fellowshipTitle = Centred(Label(string.Empty, 13, DerethPalette.TextBrush, title: true));
        _leader = Button("Leader", () => Act(SocialAction.Leader(_fellow)));
        _quit = Button("Quit", () => Act(SocialAction.QuitFellowship(disband: false)));
        _open = Button("Open", () => Act(SocialAction.Openness(!_state.Open)));
        _recruit = Button("Recruit", () => Act(SocialAction.Recruit(_game.Selection)));
        _dismiss = Button("Dismiss", () => Act(SocialAction.Dismiss(_fellow)));
        _disband = Button("Disband", () => Act(SocialAction.QuitFellowship(disband: true)));
        var fellowButtons = new StackPanel { Spacing = -3, Margin = new Thickness(0, 6, 0, 0) };
        fellowButtons.Children.Add(Buttons(_leader, _quit, _open));
        fellowButtons.Children.Add(Buttons(_recruit, _dismiss, _disband));
        _inFellowship = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        Place(_inFellowship, _fellowshipTitle, 0);
        Place(_inFellowship, Columns("FELLOW", "LVL/XP%"), 1);
        Place(_inFellowship, Well(_fellowList, () => { _fellow = 0; Refresh(); }), 2);
        Place(_inFellowship, fellowButtons, 3);
        _pages[1] = _fellowshipPage;

        // Friends: the list, Appear Offline, Send Tell and Remove, then the name box and Add.
        _appearOffline = new Orb(game, SocialOption.AppearOffline, "Appear Offline") { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        _tell = Button("Send Tell", TellFriend);
        _removeFriend = Button("Remove", () => Act(SocialAction.RemoveFriend(_friend)));
        _friendName = Entry(32, () => Refresh());
        _add = Button("Add", AddFriend);
        _friendName.KeyDown += (_, e) => { if (e.Key == Key.Enter) AddFriend(); };
        _pages[2] = Stack(
            (Columns("Friend", "Status"), false), (Well(_friendList, () => { _friend = 0; Refresh(); }), true), (new DerethRule(), false),
            (_appearOffline, false), (Buttons(_tell, _removeFriend), false), (new DerethRule { Margin = new Thickness(0, 6, 0, 0) }, false),
            (Field("Friend Name:", _friendName, new Thickness(8, 6, 8, 4)), false), (Centred(_add), false));

        // Squelch: the list and Remove, then the name box and the two squelches.
        _removeSquelch = Button("Remove", RemoveSquelch);
        _squelchName = Entry(32, () => Refresh());
        _squelchCharacter = Button("Squelch Character", () => SquelchTyped(account: false), width: 128);
        _squelchAccount = Button("Squelch Account", () => SquelchTyped(account: true), width: 128);
        _pages[3] = Stack(
            (Columns("Squelch", "Type"), false), (Well(_squelchList, () => { _squelch = null; Refresh(); }), true),
            (new Border { Margin = new Thickness(0, 6, 0, 0), Child = Centred(_removeSquelch) }, false), (new DerethRule { Margin = new Thickness(0, 6, 0, 0) }, false),
            (Field("Character", _squelchName, new Thickness(8, 6, 8, 4)), false), (Buttons(_squelchCharacter, _squelchAccount), false));

        _page = new ContentControl { Margin = new Thickness(0, 5, 0, 0) };
        // The tabs sit in the header beside the close box, as the character window's do.
        Window = new DerethWindow(string.Empty, null, _page, _tabs, stretchActions: true);
        Window.CloseRequested += (_, _) => Close?.Invoke();
        ShowTab(0);
    }

    public DerethWindow Window { get; }

    /// <summary>Runs when the player closes the window with its close box.</summary>
    public Action? Close { get; set; }

    public int Tab => _tab;

    /// <summary>
    /// Shows a tab, as a click on it does. The allegiance and fellowship tabs ask the server for the latest, as retail's do; leaving the
    /// fellowship tab stops the fellows' vitals.
    /// </summary>
    public void ShowTab(int tab)
    {
        if (_tab == 1 && tab != 1) _game.Send(SocialAction.FellowshipPanel(open: false));
        _tab = tab;
        _tabs.Selected = tab;
        _arming = null;
        _page.Content = _pages[tab];
        if (tab == 0) _game.Send(SocialAction.AllegianceUpdate());
        if (tab == 1) _game.Send(SocialAction.FellowshipPanel(open: true));
        Refresh();
    }

    public void SelectVassal(uint id) { _vassal = id; Refresh(); }
    /// <summary>Selects a fellow's row and, as retail's list does, the fellow in the game; 0 clears the row only.</summary>
    public void SelectFellow(uint id)
    {
        _fellow = id;
        if (id != 0) _game.Select(id);
        Refresh();
    }
    public void SelectFriend(uint id) { _friend = id; Refresh(); }
    public void SelectSquelch(Squelch squelch) { _squelch = squelch; Refresh(); }

    /// <summary>Reads the state and retail's options again and redraws what changed. Call it on a change and about once a second while open.</summary>
    public void Refresh()
    {
        // A selection the server has since dropped is cleared.
        if (_state.Vassals.All(v => v.Id != _vassal)) _vassal = 0;
        if (_state.Fellows.All(f => f.Id != _fellow)) _fellow = 0;
        if (_state.Friends.All(f => f.Id != _friend)) _friend = 0;
        if (_squelch is { } chosen && !_state.Squelches.Contains(chosen)) _squelch = null;
        if (_arming is { } armed && NowMs - armed.At > ConfirmMs) Disarm();
        switch (_tab)
        {
            case 0: DrawAllegiance(); break;
            case 1: DrawFellowship(); break;
            case 2: DrawFriends(); break;
            default: DrawSquelches(); break;
        }
    }

    private void DrawAllegiance()
    {
        _followers.Text = $"Followers: {_state.Followers}";
        _rank.Text = $"Rank: [{_state.Rank}]";
        Redraw(_chain, string.Join("|", _state.Monarch, _state.Patron), () =>
        {
            if (_state.Monarch is { } monarch && monarch.Id != _state.Player) _chain.Children.Add(Member("Monarch", monarch));
            if (_state.Patron is { } patron && patron.Id != _state.Monarch?.Id) _chain.Children.Add(Member("Patron", patron));
            if (_state.Monarch?.Id == _state.Player) _chain.Children.Add(Label("You are the monarch of your allegiance.", 12, DerethPalette.MutedBrush));
        });
        Redraw(_vassalList, $"{_vassal}|{string.Join(",", _state.Vassals)}", () =>
        {
            foreach (var vassal in _state.Vassals)
                _vassalList.Children.Add(Row(vassal.Name, vassal.Produced.ToString("N0", CultureInfo.InvariantCulture), vassal.Id == _vassal,
                    () => SelectVassal(vassal.Id == _vassal ? 0 : vassal.Id), vassal.Online ? DerethPalette.TextBrush : DerethPalette.MutedBrush));
        });
        _ignoreAllegiance.Refresh();
        var selection = _game.Selection;
        _swear.IsEnabled = selection != 0 && selection != _state.Player;
        _break.IsEnabled = _state.Patron != null;
        _kick.IsEnabled = _vassal != 0;
    }

    private Control Member(string role, AllegianceMember member)
    {
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("62,*,Auto"), ColumnSpacing = 4 };
        line.Children.Add(Label(role + ":", 12.5, DerethPalette.MutedBrush));
        var name = Label(member.Name, 13, member.Online ? DerethPalette.TextBrush : DerethPalette.MutedBrush, title: true);
        Grid.SetColumn(name, 1);
        line.Children.Add(name);
        var level = Label(member.Level > 0 ? $"Level {member.Level}, Rank {member.Rank}" : $"Rank {member.Rank}", 11.5, DerethPalette.MutedBrush);
        Grid.SetColumn(level, 2);
        line.Children.Add(level);
        return line;
    }

    private void DrawFellowship()
    {
        var inOne = _state.Fellowship != null;
        var page = inOne ? _inFellowship : _noFellowship;
        if (_fellowshipPage.Content != page) _fellowshipPage.Content = page;
        if (!inOne)
        {
            foreach (var orb in _fellowshipOrbs) orb.Refresh();
            _create.IsEnabled = !string.IsNullOrWhiteSpace(_fellowshipName.Text);
            return;
        }
        _fellowshipTitle.Text = _state.Fellowship;
        var selection = _game.Selection;
        if (selection != _seenSelection && _state.Fellows.Any(f => f.Id == selection)) _fellow = selection;
        _seenSelection = selection;
        var shares = Shares();
        Redraw(_fellowList, $"{_fellow}|{_state.Leader}|{string.Join(",", _state.Fellows)}|{string.Join(",", shares)}", () =>
        {
            for (var i = 0; i < _state.Fellows.Count; i++) _fellowList.Children.Add(FellowRow(_state.Fellows[i], shares[i]));
        });
        var leading = _state.Leader == _state.Player;
        var someoneElse = _fellow != 0 && _fellow != _state.Player;
        _leader.IsEnabled = _dismiss.IsEnabled = leading && someoneElse;
        _open.IsEnabled = _disband.IsEnabled = leading;
        _recruit.IsEnabled = (leading || _state.Open) && selection != 0 && selection != _state.Player && _state.Fellows.All(f => f.Id != selection);
        Text(_open, _state.Open ? "Close" : "Open");
    }

    /// <summary>
    /// Each fellow's share of the experience, in percent, as ACE splits it: an even share gives each the same part by the fellowship's size;
    /// otherwise each gets the part of the experience their next level costs. Null without sharing.
    /// </summary>
    public IReadOnlyList<int?> Shares()
    {
        var fellows = _state.Fellows;
        if (!_state.ShareExperience) return fellows.Select(_ => (int?)null).ToArray();
        if (_state.EvenShare || fellows.Count <= 1) return fellows.Select(_ => (int?)EvenShares[Math.Min(fellows.Count, EvenShares.Length - 1)]).ToArray();
        var levels = _tables().Experience.Level;
        double Cost(Fellow fellow) => fellow.Level + 1 < levels.Length ? levels[fellow.Level + 1] - levels[fellow.Level] : 1;
        var total = fellows.Sum(Cost);
        return fellows.Select(fellow => (int?)(int)Math.Round(100 * Cost(fellow) / total)).ToArray();
    }

    private Control FellowRow(Fellow fellow, int? share)
    {
        var selected = fellow.Id == _fellow;
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        top.Children.Add(Label(fellow.Name, 12.5, fellow.Id == _state.Leader ? DerethPalette.GoldBrush : DerethPalette.TextBrush, title: true));
        var level = Label(share is { } part ? $"{fellow.Level}/{part}%" : fellow.Level.ToString(CultureInfo.InvariantCulture), 12, DerethPalette.TextBrush);
        Grid.SetColumn(level, 1);
        top.Children.Add(level);
        var bars = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 3, Margin = new Thickness(0, 1, 0, 0) };
        var values = new[] { (fellow.Health, fellow.MaxHealth), (fellow.Stamina, fellow.MaxStamina), (fellow.Mana, fellow.MaxMana) };
        for (var i = 0; i < 3; i++)
        {
            var bar = VitalBar(values[i].Item1, values[i].Item2, VitalBrushes[i]);
            Grid.SetColumn(bar, i);
            bars.Children.Add(bar);
        }
        var stack = new StackPanel();
        stack.Children.Add(top);
        stack.Children.Add(bars);
        return Selectable(stack, selected, () => SelectFellow(selected ? 0 : fellow.Id), new Thickness(3, 1, 3, 3));
    }

    private static Control VitalBar(uint current, uint maximum, (IBrush Fill, IBrush Track) brushes)
    {
        var fraction = maximum == 0 ? 0 : Math.Min(1, (double)current / maximum);
        var fill = new Border { Background = brushes.Fill, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2) };
        var text = Centred(Label($"{current}/{maximum}", 10.5, DerethPalette.CreamBrush));
        var well = new Grid { Height = 13 };
        well.Children.Add(fill);
        well.Children.Add(text);
        well.SizeChanged += (_, e) => fill.Width = e.NewSize.Width * fraction;
        return new Border { Background = brushes.Track, BorderBrush = Brushes.Black, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Child = well };
    }

    private void DrawFriends()
    {
        Redraw(_friendList, $"{_friend}|{string.Join(",", _state.Friends)}", () =>
        {
            foreach (var friend in _state.Friends.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                _friendList.Children.Add(Row(friend.Name, friend.Online ? "Online" : "Offline", friend.Id == _friend,
                    () => SelectFriend(friend.Id == _friend ? 0 : friend.Id), DerethPalette.TextBrush, friend.Online ? OnlineBrush : DerethPalette.MutedBrush));
        });
        _appearOffline.Refresh();
        _tell.IsEnabled = _removeFriend.IsEnabled = _friend != 0;
        _add.IsEnabled = !string.IsNullOrWhiteSpace(_friendName.Text);
    }

    private void DrawSquelches()
    {
        Redraw(_squelchList, $"{_squelch}|{string.Join(",", _state.Squelches)}", () =>
        {
            foreach (var squelch in _state.Squelches.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
                _squelchList.Children.Add(Row(squelch.Name, squelch.Account ? "Account" : "Character", squelch == _squelch,
                    () => SelectSquelch(squelch), DerethPalette.TextBrush, DerethPalette.GoldBrush));
        });
        _removeSquelch.IsEnabled = _squelch != null;
        _squelchCharacter.IsEnabled = _squelchAccount.IsEnabled = !string.IsNullOrWhiteSpace(_squelchName.Text);
    }

    // The buttons.

    public void Swear()
    {
        var selection = _game.Selection;
        if (selection != 0 && selection != _state.Player) Act(SocialAction.Swear(selection));
    }

    public void Break()
    {
        if (_state.Patron is { } patron) Act(SocialAction.Break(patron.Id));
    }

    /// <summary>Releases the selected vassal.</summary>
    public void Kick()
    {
        if (_vassal != 0) Act(SocialAction.Break(_vassal));
    }

    public void CreateFellowship() =>
        Submit(_fellowshipName, name => SocialAction.CreateFellowship(name, _game.Option(SocialOption.ShareFellowshipExperience) ?? true));

    public void AddFriend() => Submit(_friendName, SocialAction.AddFriend);

    public void TellFriend()
    {
        if (_state.Friends.FirstOrDefault(f => f.Id == _friend) is { Id: not 0 } friend) _game.Tell(friend.Name, friend.Id);
    }

    public void RemoveSquelch()
    {
        if (_squelch is not { } squelch) return;
        Act(squelch.Account ? SocialAction.SquelchAccount(false, squelch.Name) : SocialAction.SquelchCharacter(false, squelch.Id, squelch.Name));
    }

    public void SquelchTyped(bool account) =>
        Submit(_squelchName, name => account ? SocialAction.SquelchAccount(true, name) : SocialAction.SquelchCharacter(true, 0, name));

    /// <summary>Sends the action for the name typed in a box, if there is one, and empties the box once it is sent.</summary>
    private void Submit(TextBox box, Func<string, SocialAction> action)
    {
        var name = (box.Text ?? string.Empty).Trim();
        if (name.Length > 0 && Act(action(name))) box.Text = string.Empty;
    }

    /// <summary>The text boxes, for tests.</summary>
    public TextBox FellowshipNameBox => _fellowshipName;
    public TextBox FriendNameBox => _friendName;
    public TextBox SquelchNameBox => _squelchName;

    private bool Act(SocialAction action)
    {
        var sent = _game.Send(action);
        Refresh();
        return sent;
    }

    /// <summary>The first press arms the button and asks for a second, which acts; the request lapses after a few seconds.</summary>
    private void Confirm(DerethButton button, Action act)
    {
        if (_arming?.Button == button)
        {
            Disarm();
            act();
            return;
        }
        Disarm();
        _arming = (button, NowMs);
        button.Tag = Text(button);
        Text(button, "Confirm");
    }

    private void Disarm()
    {
        if (_arming is { } armed && armed.Button.Tag is string text) Text(armed.Button, text);
        _arming = null;
    }

    // The parts.

    /// <summary>Clears and refills a list only when what it shows changed, so a redraw a second keeps the pointer's hover.</summary>
    private void Redraw(Panel list, string key, Action fill)
    {
        if (_drawn.TryGetValue(list, out var drawn) && drawn == key) return;
        _drawn[list] = key;
        list.Children.Clear();
        fill();
    }

    private Control Row(string name, string value, bool selected, Action click, IBrush nameBrush, IBrush? valueBrush = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6, Height = RowHeight };
        grid.Children.Add(Label(name, 13, nameBrush, title: true));
        var text = Label(value, 12.5, valueBrush ?? DerethPalette.TextBrush);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return Selectable(grid, selected, click, new Thickness(4, 0));
    }

    private static Control Selectable(Control child, bool selected, Action click, Thickness padding)
    {
        var border = new Border
        {
            Padding = padding, CornerRadius = new CornerRadius(2), Child = child, Background = selected ? SelectedBrush : Brushes.Transparent,
            BorderBrush = selected ? DerethPalette.GoldDarkBrush : Brushes.Transparent, BorderThickness = new Thickness(0, 1)
        };
        border.PointerEntered += (_, _) => { if (!selected) border.Background = HoverBrush; };
        border.PointerExited += (_, _) => { if (!selected) border.Background = Brushes.Transparent; };
        border.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            click();
        };
        return border;
    }

    /// <summary>A dark list well. A click on its empty space clears the selection, as in retail.</summary>
    private static Control Well(StackPanel list, Action clear)
    {
        var scroll = new ScrollViewer
        {
            Content = list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Transparent
        };
        scroll.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(scroll).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            clear();
        };
        return new Border
        {
            Background = WellBrush, BorderBrush = LineBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2),
            Padding = new Thickness(2), Margin = new Thickness(6, 0), Child = scroll
        };
    }

    private static Control Columns(string left, string right)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(10, 4, 12, 2) };
        grid.Children.Add(Label(left, 13, DerethPalette.TextBrush));
        var text = Label(right, 13, DerethPalette.TextBrush);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private static TextBox Entry(int maxLength, Action changed)
    {
        var box = new TextBox
        {
            Foreground = DerethPalette.CreamBrush, CaretBrush = DerethPalette.GoldBrush, FontFamily = DerethPalette.Title, FontSize = 13,
            Background = EntryBrush, BorderBrush = LineBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2),
            Padding = new Thickness(5, 0), MinHeight = 0, Height = 20, MaxLength = maxLength, VerticalContentAlignment = VerticalAlignment.Center
        };
        box.TextChanged += (_, _) => changed();
        return box;
    }

    private static Control Field(string label, TextBox box, Thickness margin)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 6, Margin = margin };
        grid.Children.Add(Label(label, 13, DerethPalette.TextBrush));
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);
        return grid;
    }

    private static DerethButton Button(string text, Action click, double width = ButtonWidth)
    {
        var button = new DerethButton { Content = Label(text, 13, DerethPalette.CreamBrush), Width = width, Height = ButtonHeight, Padding = new Thickness(0) };
        button.Click += (_, _) => click();
        return button;
    }

    private static string Text(DerethButton button) => (button.Content as TextBlock)?.Text ?? string.Empty;

    private static void Text(DerethButton button, string text)
    {
        if (button.Content is TextBlock label && label.Text != text) label.Text = text;
    }

    private static Control Buttons(params DerethButton[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4) };
        foreach (var button in buttons) row.Children.Add(button);
        return row;
    }

    /// <summary>Rows top to bottom; the one marked true takes the spare height.</summary>
    private static Grid Stack(params (Control Child, bool Fill)[] rows)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions(string.Join(",", rows.Select(row => row.Fill ? "*" : "Auto"))) };
        for (var i = 0; i < rows.Length; i++) Place(grid, rows[i].Child, i);
        return grid;
    }

    private static void Place(Grid grid, Control child, int row)
    {
        Grid.SetRow(child, row);
        grid.Children.Add(child);
    }

    private static T Centred<T>(T control) where T : Control
    {
        control.HorizontalAlignment = HorizontalAlignment.Center;
        if (control is TextBlock text) text.TextAlignment = TextAlignment.Center;
        return control;
    }

    private static TextBlock Label(string text, double size, IBrush brush, bool title = false)
    {
        var label = new TextBlock
        {
            Text = text, FontSize = size, Foreground = brush, FontFamily = title ? DerethPalette.Title : DerethPalette.Body,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
        };
        // Palatino's tall ascent would sit names high in their rows.
        return title ? DerethPalette.CentreInk(label) : label;
    }

    /// <summary>
    /// Retail's option switch: a small orb, lit green when on, and its words. A click flips the option in the game; an option that cannot be
    /// read is dimmed and takes no clicks.
    /// </summary>
    private sealed class Orb : StackPanel
    {
        private static readonly IBrush LitBrush = new RadialGradientBrush
        {
            GradientStops = { new GradientStop(Color.Parse("#E8FFB0"), 0), new GradientStop(Color.Parse("#8EE02A"), 0.45), new GradientStop(Color.Parse("#3C7A0C"), 1) }
        };
        private static readonly IBrush DarkBrush = new RadialGradientBrush
        {
            GradientStops = { new GradientStop(Color.Parse("#6A6458"), 0), new GradientStop(Color.Parse("#2E2A24"), 1) }
        };
        private readonly Ellipse _orb = new() { Width = 10, Height = 10, Stroke = Brushes.Black, StrokeThickness = 0.8, VerticalAlignment = VerticalAlignment.Center };
        private readonly ISocialGame _game;
        private readonly SocialOption _option;
        private bool _on;

        public Orb(ISocialGame game, SocialOption option, string text)
        {
            _game = game;
            _option = option;
            Orientation = Orientation.Horizontal;
            Spacing = 6;
            Background = Brushes.Transparent;
            Cursor = new Cursor(StandardCursorType.Hand);
            Children.Add(_orb);
            Children.Add(Label(text, 11, DerethPalette.TextBrush));
            PointerPressed += (_, e) =>
            {
                if (!IsEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                Show(!_on);
                _game.SetOption(_option, _on);
            };
            Show(false);
        }

        /// <summary>Reads the option from the game again.</summary>
        public void Refresh() => Show(_game.Option(_option));

        private void Show(bool? on)
        {
            IsEnabled = on != null;
            Opacity = on == null ? 0.45 : 1;
            _on = on == true;
            _orb.Fill = _on ? LitBrush : DarkBrush;
        }
    }
}
