using System;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Takes retail's examination panel over for the session, with the inventory takeover's park and return. Whenever the panel shows (the
/// E key on any selection, or any other way retail opens it), the current selection is handed to the appraisal window, and the panel is
/// closed again so that the next examination is a new show. Retail's examination text never shows while this holds the panel.
/// Call <see cref="Tick"/> on the game thread once a frame.
/// </summary>
internal sealed class RetailExaminationTakeover : IDisposable
{
    private readonly RetailPanelTakeover _panel;
    private readonly Func<uint> _selection;
    private readonly Action<uint> _examine;
    private bool _closeAfterShow;

    /// <param name="selection">The object retail is examining: the current selection, read when the panel shows.</param>
    /// <param name="examine">Shows the appraisal window for an object, now or when its response arrives.</param>
    public RetailExaminationTakeover(IRetailPanelPort port, Func<uint> selection, Action<uint> examine, Action<string> log)
    {
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _examine = examine ?? throw new ArgumentNullException(nameof(examine));
        _panel = new RetailPanelTakeover(port ?? throw new ArgumentNullException(nameof(port)), OnOpenChanged, log ?? throw new ArgumentNullException(nameof(log)));
    }

    /// <summary>Ticks the panel takeover; <paramref name="enabled"/> false gives the panel back, as the inventory takeover does.</summary>
    public void Tick(bool enabled, bool canShow = true)
    {
        _panel.Tick(enabled, canShow);
        // The close comes after the takeover has parked the panel and reported it, so it is a second step, not a reentrant call.
        if (!_closeAfterShow) return;
        _closeAfterShow = false;
        _panel.Close();
    }

    /// <summary>Logoff or unload: gives the panel back and forgets the session.</summary>
    public void EndSession()
    {
        _closeAfterShow = false;
        _panel.EndSession();
    }

    public void Dispose() => _panel.Dispose();

    private void OnOpenChanged(bool open)
    {
        if (!open) return;
        var selected = _selection();
        if (selected != 0) _examine(selected);
        _closeAfterShow = true;
    }
}
