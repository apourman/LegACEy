namespace LegACEy.Client.Demo;

/// <summary>Bodies of the storm fog test's channel actions; they must match ACE.Server.ClientChannel.StormChannelActions.</summary>
public static class StormProtocol
{
    public const string Zone = "storm.zone";
    public const string Changed = "storm.changed";

    /// <summary>The safe landblock the storm rings, as landblock x and y, or null when there is no storm.</summary>
    public static (int X, int Y)? ReadZone(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        return reader.ReadByte() == 0 ? null : (reader.ReadByte(), reader.ReadByte());
    }
}
