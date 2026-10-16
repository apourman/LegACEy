using System;
using System.Drawing;

namespace LegACEy.Client.Demo;

/// <summary>
/// The vitals window's layout, per character: stacked (A) or side by side (D). It is kept in the store the client keeps plugin settings
/// in, one value per server and character. A character with no saved layout has the stacked one.
/// </summary>
public sealed class VitalsLayoutChoice
{
    private const string Key = "dereth-vitals-layout";
    private readonly IWindowPositionStore _store;

    public VitalsLayoutChoice(IWindowPositionStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

    public VitalsLayout Load(string server, string character) =>
        _store.Load(server, character, Key) is { Location.X: (int)VitalsLayout.SideBySide } ? VitalsLayout.SideBySide : VitalsLayout.Stacked;

    public void Save(string server, string character, VitalsLayout layout) =>
        _store.Save(server, character, Key, (new Point((int)layout, 0), null));
}
