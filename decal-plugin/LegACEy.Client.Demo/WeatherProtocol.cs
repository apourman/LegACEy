namespace LegACEy.Client.Demo;

/// <summary>The weather kinds. The server sends None to Thunderstorm; Fog is the client's own, for the Settings window's test weather.</summary>
public enum WeatherKind : byte { None = 0, Snow = 1, Blizzard = 2, Thunderstorm = 3, Fog = 4 }

/// <summary>Bodies of the weather test's channel actions; they must match ACE.Server.ClientChannel.WeatherChannelActions.</summary>
public static class WeatherProtocol
{
    public const string Zone = "weather.zone";
    public const string Changed = "weather.changed";

    /// <summary>The weather and the landblock at the middle of the 3×3 landblocks it covers, or null for clear skies.</summary>
    public static (WeatherKind Kind, int X, int Y)? Read(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        var kind = (WeatherKind)reader.ReadByte();
        return kind == WeatherKind.None ? null : (kind, reader.ReadByte(), reader.ReadByte());
    }
}
