using Avalonia;

namespace LegACEy.Client.Demo;

/// <summary>A LegACEy window that takes right-clicks, which the game would otherwise get.</summary>
public interface IGameInputTarget
{
    /// <summary>The right button went down at the position, in this control's coordinates.</summary>
    void RightClick(Point position);
}
