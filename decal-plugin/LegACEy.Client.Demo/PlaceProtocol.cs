namespace LegACEy.Client.Demo;

/// <summary>Bodies of the place name action; they must match ACE.Server.ClientChannel.PlaceChannelActions.</summary>
public static class PlaceProtocol
{
    public const string Name = "place.name";

    /// <summary>The request: the full cell id, landblock in the high word.</summary>
    public static byte[] Request(uint cell) => ChannelWire.Body(writer => writer.Write(cell));

    /// <summary>The server's name for the place, or empty when it names none.</summary>
    public static string ReadName(byte[] body) => ChannelWire.ReadString(ChannelWire.Reader(body));
}
