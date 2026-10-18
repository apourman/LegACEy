namespace LegACEy.Client.Demo;

public enum SnowIntensity : byte { Light = 1, Blizzard = 2 }

/// <summary>Bodies of the snow test's channel actions; they must match ACE.Server.ClientChannel.SnowChannelActions.</summary>
public static class SnowProtocol
{
    public const string Zone = "snow.zone";
    public const string Changed = "snow.changed";

    /// <summary>How hard it snows and the landblock at the middle of the 3×3 landblocks it snows on, or null when it does not snow.</summary>
    public static (SnowIntensity Intensity, int X, int Y)? ReadSnow(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        var intensity = reader.ReadByte();
        return intensity == 0 ? null : ((SnowIntensity)intensity, reader.ReadByte(), reader.ReadByte());
    }
}
