using System;
using System.Drawing;

namespace LegACEy.Client.Demo;

/// <summary>
/// The vitals window's layout, per character: stacked (A) or side by side (D), and whether its bars are retail-style. Both are kept in the
/// store the client keeps plugin settings in, per server and character. A character with nothing saved has stacked retail-style bars.
/// </summary>
public sealed class VitalsLayoutChoice
{
    private const string Key = "dereth-vitals-layout", RetailBarsKey = "dereth-vitals-retail-bars";
    private readonly IWindowPositionStore _store;

    public VitalsLayoutChoice(IWindowPositionStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

    public VitalsLayout Load(string server, string character) =>
        _store.Load(server, character, Key) is { Location.X: (int)VitalsLayout.SideBySide } ? VitalsLayout.SideBySide : VitalsLayout.Stacked;

    public void Save(string server, string character, VitalsLayout layout) =>
        _store.Save(server, character, Key, (new Point((int)layout, 0), null));

    public bool RetailBars(string server, string character) => _store.Load(server, character, RetailBarsKey) is not { Location.X: 0 };

    public void SaveRetailBars(string server, string character, bool on) =>
        _store.Save(server, character, RetailBarsKey, (new Point(on ? 1 : 0, 0), null));
}
