namespace LegACEy.Client.Demo;

/// <summary>Bodies of the radar ping's channel actions; they must match ACE.Server.ClientChannel.PingChannelActions.</summary>
public static class PingProtocol
{
    public const string Place = "ping.place";
    public const string Placed = "ping.placed";

    /// <summary>A ping request: the spot's cell id, then x, y and z in landblock metres.</summary>
    public static byte[] WritePlace(RadarPosition at) => ChannelWire.Body(writer =>
    {
        writer.Write(at.Cell);
        writer.Write(at.X);
        writer.Write(at.Y);
        writer.Write(at.Z);
    });

    /// <summary>A placed ping: who pinged, and the spot.</summary>
    public static (string Sender, RadarPosition At) ReadPlaced(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        var sender = ChannelWire.ReadString(reader);
        return (sender, new RadarPosition(reader.ReadUInt32(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
    }
}
