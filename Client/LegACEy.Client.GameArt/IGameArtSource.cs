namespace LegACEy.Client.GameArt;

/// <summary>Supplies decoded portal.dat render surfaces to themes and preview tools.</summary>
public interface IGameArtSource
{
    GameImage? ReadImage(uint id);
}
