namespace LegACEy.Client.Demo;

public enum WeatherKind : byte { None = 0, Snow = 1, Blizzard = 2, Thunderstorm = 3 }

/// <summary>The weather a tester turns on from the Settings window: drawn around the player wherever they are, with no word from the server.</summary>
public enum TestWeather { Off, Snow, Blizzard, Thunderstorm, Fog }

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
