using System;
using System.Collections.Generic;
using System.Linq;

namespace LegACEy.Client.Demo;

/// <summary>One kind of chat line. Each has its own colour; a tab shows a set of them.</summary>
[Flags]
public enum RetailChatKind
{
    Local = 1,
    Emote = 2,
    TellIn = 4,
    TellOut = 8,
    Fellowship = 16,
    Allegiance = 32,
    Channel = 64,
    System = 128,
    Plugin = 256,
    Combat = 512
}

/// <summary>The channels the player hears, as retail's options say. A talk target for a room is off when its bit is not set.</summary>
[Flags]
public enum ChatChannels
{
    None = 0,
    General = 1,
    Trade = 2,
    Lfg = 4,
    Roleplay = 8,
    Society = 16
}

/// <summary>
/// A line as the chat window shows it. <see cref="Text"/> is the line as retail words it, with the sender's name in it; <see cref="Sender"/>
/// is the name a click answers with a tell (the other party's, for a tell either way); <see cref="Channel"/> is a channel's name.
/// </summary>
public sealed record RetailChatLine(RetailChatKind Kind, string Text, DateTime Time, string? Sender = null, string? Channel = null);

/// <summary>What the player is in, for dimming talk targets: fellowship, allegiance and its ranks, society, Olthoi, and the open channels.</summary>
public sealed record RetailChatMembership(bool Fellowship, bool Allegiance, bool Monarch, bool Patron, bool Vassals, bool Society, bool Olthoi,
    ChatChannels Open);

/// <summary>
/// The retail chat the window reads and talks through. The client implements it over retail's chat and Decal; tests fake it.
/// </summary>
public interface IRetailChatPort
{
    /// <summary>Raised for each line retail shows in chat, in order, on the game thread.</summary>
    event Action<RetailChatLine>? LineReceived;
    /// <summary>Sends text as the player typed it: the command or message goes to retail's chat parser, as the chat box's own send does.</summary>
    void Send(string text);
    /// <summary>What the player is in now. Read when the talk-target menu opens.</summary>
    RetailChatMembership Membership { get; }
    /// <summary>The selected object's name, or null when nothing is selected.</summary>
    string? SelectedName { get; }
    /// <summary>The game's chat timestamp option.</summary>
    bool TimestampsOn { get; }
    /// <summary>Raised when the timestamp option changes; the window redraws its lines.</summary>
    event Action? TimestampsChanged;
}

/// <summary>
/// The lines the chat window keeps: the last <see cref="Capacity"/>, nothing is written to disk. Older lines go when newer ones arrive.
/// </summary>
public sealed class RetailChatHistory
{
    public const int Capacity = 1000;
    private readonly List<RetailChatLine> _lines = new();

    public IReadOnlyList<RetailChatLine> Lines => _lines;

    /// <summary>Adds a line, and returns the oldest line that was dropped to keep the history at its capacity, or null.</summary>
    public RetailChatLine? Add(RetailChatLine line)
    {
        _lines.Add(line);
        if (_lines.Count <= Capacity) return null;
        var dropped = _lines[0];
        _lines.RemoveAt(0);
        return dropped;
    }
}

/// <summary>A talk target. <see cref="Command"/> is the command retail's chat uses for it; a tell carries <see cref="Name"/>.</summary>
/// <param name="Id">A stable key: "local", "selected", "fellows", "tell:Name" and the like.</param>
/// <param name="Label">What the chip and the menu call it.</param>
/// <param name="Command">The chat command prefix: "/s", "/f", "/gu" and so on, or "/t" for a tell.</param>
/// <param name="Colour">The kind whose colour the chip and the menu show.</param>
/// <param name="Need">Who can use it: a membership test, or none.</param>
/// <param name="Channel">The channel option that must be on, or None.</param>
/// <param name="Name">The name a tell goes to, or null when the target is a command.</param>
public sealed record RetailTalkTarget(string Id, string Label, string Command, RetailChatKind Colour, TalkNeed Need = TalkNeed.None,
    ChatChannels Channel = ChatChannels.None, string? Name = null)
{
    /// <summary>A tell to a player's name.</summary>
    public static RetailTalkTarget Tell(string name) => new("tell:" + name, "Tell " + name, "/t", RetailChatKind.TellIn, TalkNeed.None, ChatChannels.None, name);

    /// <summary>Whether the target is the selected object, whose name the command takes.</summary>
    public bool IsSelected => Id == RetailTalkTargets.SelectedId;
}

/// <summary>What a talk target needs before the player can use it.</summary>
public enum TalkNeed { None, Selection, Fellowship, Allegiance, Monarch, Patron, Vassals, Society, Olthoi }

/// <summary>Retail's talk targets, grouped as its menu groups them, and the command each sends.</summary>
public static class RetailTalkTargets
{
    public const string SelectedId = "selected";

    public static readonly RetailTalkTarget Local = new("local", "Local", "/s", RetailChatKind.Local);
    public static readonly RetailTalkTarget Selected = new(SelectedId, "Selected", "/t", RetailChatKind.TellIn, TalkNeed.Selection);
    public static readonly RetailTalkTarget Fellows = new("fellows", "Fellows", "/f", RetailChatKind.Fellowship, TalkNeed.Fellowship);
    public static readonly RetailTalkTarget Allegiance = new("alleg", "Allegiance", "/gu", RetailChatKind.Allegiance, TalkNeed.Allegiance);
    public static readonly RetailTalkTarget Monarch = new("monarch", "Monarch", "/m", RetailChatKind.Allegiance, TalkNeed.Monarch);
    public static readonly RetailTalkTarget Patron = new("patron", "Patron", "/p", RetailChatKind.Allegiance, TalkNeed.Patron);
    public static readonly RetailTalkTarget Vassals = new("vassals", "Vassals", "/v", RetailChatKind.Allegiance, TalkNeed.Vassals);
    public static readonly RetailTalkTarget General = new("general", "General", "/general", RetailChatKind.Channel, TalkNeed.None, ChatChannels.General);
    public static readonly RetailTalkTarget Trade = new("trade", "Trade", "/trade", RetailChatKind.Channel, TalkNeed.None, ChatChannels.Trade);
    public static readonly RetailTalkTarget Lfg = new("lfg", "LFG", "/lfg", RetailChatKind.Channel, TalkNeed.None, ChatChannels.Lfg);
    public static readonly RetailTalkTarget Roleplay = new("roleplay", "Roleplay", "/roleplay", RetailChatKind.Channel, TalkNeed.None, ChatChannels.Roleplay);
    public static readonly RetailTalkTarget Society = new("society", "Society", "/society", RetailChatKind.Channel, TalkNeed.Society, ChatChannels.Society);
    public static readonly RetailTalkTarget Olthoi = new("olthoi", "Olthoi", "/olthoi", RetailChatKind.Channel, TalkNeed.Olthoi);

    /// <summary>The menu's groups, in its order. Recent tells follow them.</summary>
    public static readonly IReadOnlyList<(string Group, IReadOnlyList<RetailTalkTarget> Targets)> Groups = new (string, IReadOnlyList<RetailTalkTarget>)[]
    {
        ("Nearby", new[] { Local, Selected }),
        ("Fellowship", new[] { Fellows }),
        ("Allegiance", new[] { Allegiance, Monarch, Patron, Vassals }),
        ("Channels", new[] { General, Trade, Lfg, Roleplay, Society, Olthoi })
    };

    /// <summary>Every fixed target, by id.</summary>
    public static IEnumerable<RetailTalkTarget> All => Groups.SelectMany(group => group.Targets);

    /// <summary>
    /// Why a target cannot be used now, as the menu shows it; null when it can. A channel is off when its option is; a room needs its
    /// membership first.
    /// </summary>
    public static string? WhyUnusable(RetailTalkTarget target, RetailChatMembership membership, string? selectedName)
    {
        var reason = target.Need switch
        {
            TalkNeed.Selection when selectedName == null => "nothing selected",
            TalkNeed.Fellowship when !membership.Fellowship => "not in a fellowship",
            TalkNeed.Allegiance when !membership.Allegiance => "not in an allegiance",
            TalkNeed.Monarch when !membership.Monarch => "no monarch",
            TalkNeed.Patron when !membership.Patron => "no patron",
            TalkNeed.Vassals when !membership.Vassals => "no vassals",
            TalkNeed.Society when !membership.Society => "not in a society",
            TalkNeed.Olthoi when !membership.Olthoi => "Olthoi only",
            _ => null
        };
        if (reason != null) return reason;
        if (target.Channel != ChatChannels.None && !membership.Open.HasFlag(target.Channel)) return "channel is off";
        return null;
    }
}

/// <summary>The text the chat window sends for a target: the command retail's chat takes, or the typed command as it is.</summary>
public static class RetailChatCommands
{
    /// <summary>
    /// The text to send, or null when there is nothing to send. Text that starts with a slash or an @ passes through as typed; anything else
    /// gets the target's command, and a selected-object tell needs the selection's name.
    /// </summary>
    public static string? Compose(RetailTalkTarget target, string typed, string? selectedName)
    {
        var text = typed.Trim();
        if (text.Length == 0) return null;
        if (text[0] == '/' || text[0] == '@') return text;
        if (target.IsSelected)
            return selectedName == null ? null : $"/t {selectedName}, {text}";
        if (target.Name != null) return $"/t {target.Name}, {text}";
        return $"{target.Command} {text}";
    }
}

/// <summary>A tab: the kinds of line it shows, and the talk target it sets when picked. The Tells tab's target is the last tell's partner.</summary>
public sealed record RetailChatTab(string Name, RetailChatKind Kinds, string? TargetId)
{
    /// <summary>The target key that means "the last tell's partner".</summary>
    public const string LastTellId = "last-tell";

    /// <summary>The six tabs every player starts with.</summary>
    public static readonly IReadOnlyList<RetailChatTab> Defaults = new[]
    {
        new RetailChatTab("All", (RetailChatKind)0x3FF, null),
        new RetailChatTab("Speech", RetailChatKind.Local | RetailChatKind.Emote | RetailChatKind.TellIn | RetailChatKind.TellOut, RetailTalkTargets.Local.Id),
        new RetailChatTab("Fellowship", RetailChatKind.Fellowship, RetailTalkTargets.Fellows.Id),
        new RetailChatTab("Allegiance", RetailChatKind.Allegiance, RetailTalkTargets.Allegiance.Id),
        new RetailChatTab("Channels", RetailChatKind.Channel, RetailTalkTargets.General.Id),
        new RetailChatTab("Tells", RetailChatKind.TellIn | RetailChatKind.TellOut, LastTellId)
    };

    public bool Shows(RetailChatLine line) => Kinds.HasFlag(line.Kind);
}
