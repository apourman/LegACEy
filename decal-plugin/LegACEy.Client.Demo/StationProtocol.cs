namespace LegACEy.Client.Demo;

/// <summary>The server's station channel: the pushes name a station, and the client answers with station.leave.</summary>
public static class StationProtocol
{
    public const string Open = "station.open";
    public const string Close = "station.close";
    public const string Leave = "station.leave";
}
