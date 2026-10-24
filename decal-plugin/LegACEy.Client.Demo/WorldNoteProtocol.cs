namespace LegACEy.Client.Demo;

/// <summary>A server's world, as its note at character select gives it: the name to show, its cell dat edition and its update-list address.</summary>
public sealed record WorldNote(string Name, int Edition, string UpdateList);

/// <summary>The note a LegACEy server with a world of its own pushes at character select; it must match ACE.Server.ClientChannel.WorldNote.</summary>
public static class WorldNoteProtocol
{
    public const string Topic = "legacey.world";

    public static WorldNote Read(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        return new WorldNote(ChannelWire.ReadString(reader), reader.ReadInt32(), ChannelWire.ReadString(reader));
    }
}
