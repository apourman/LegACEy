using Avalonia;
using Avalonia.Input;

namespace LegACEy.Client.Demo;

/// <summary>
/// A LegACEy window that takes some input the game would otherwise get: a key pressed while the window has focus and no
/// text box in it does, and a right-click on the window.
/// </summary>
public interface IGameInputTarget
{
    /// <summary>A key went down. True if the window used it, so the game never sees it.</summary>
    bool GameKeyDown(Key key);

    /// <summary>The right button went down at the position, in this control's coordinates.</summary>
    void RightClick(Point position);
}
