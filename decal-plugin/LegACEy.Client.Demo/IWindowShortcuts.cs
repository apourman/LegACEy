using Avalonia.Input;

namespace LegACEy.Client.Demo;

/// <summary>
/// A window's keys that it takes while it has the keyboard focus without a text entry focused, such as Esc to close. Any other key
/// goes to the game, as it does for a window that takes none.
/// </summary>
public interface IWindowShortcuts
{
    /// <summary>Whether the window takes this key while it has the focus.</summary>
    bool WantsShortcut(Key key);

    /// <summary>Whether the window takes the keyboard focus when it opens, so its shortcuts work before anything is clicked.</summary>
    bool TakesFocusOnOpen { get; }
}
