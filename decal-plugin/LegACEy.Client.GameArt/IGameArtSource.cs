namespace LegACEy.Client.GameArt;

/// <summary>Supplies decoded portal.dat render surfaces to themes and tools.</summary>
public interface IGameArtSource
{
    GameImage? ReadImage(uint id);
}
