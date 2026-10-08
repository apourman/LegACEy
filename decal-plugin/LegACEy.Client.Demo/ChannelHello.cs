using System.Collections.Generic;

namespace LegACEy.Client.Demo;

/// <summary>
/// The channel.hello action: the client's login hello, and the server's list of every action it has registered. It lives
/// in the channel core, not in any plugin, so the client can ask what the server serves before any plugin loads.
/// </summary>
public static class ChannelHello
{
    public const string Action = "channel.hello";

    public static (ushort Version, string Character, IReadOnlyList<string> Actions) Read(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        var version = reader.ReadUInt16();
        var character = ChannelWire.ReadString(reader);
        var count = reader.ReadInt32();
        var actions = new List<string>();
        for (var i = 0; i < count && i < 256; i++) actions.Add(ChannelWire.ReadString(reader));
        return (version, character, actions);
    }

    public static byte[] Write(string character, IEnumerable<string> actions) => ChannelWire.Body(w =>
    {
        var list = new List<string>(actions);
        w.Write(ChannelWire.Version);
        ChannelWire.WriteString(w, character);
        w.Write(list.Count);
        foreach (var action in list) ChannelWire.WriteString(w, action);
    });
}
