using System;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Takes retail's examination panel over for the session, with the inventory takeover's park and return. Whenever the panel shows (the
/// E key on any selection, or any other way retail opens it), the current selection is handed to the appraisal window, and the panel is
/// closed again so that the next examination is a new show. With no selection to examine, the panel is left to retail. Retail's
/// examination text never shows while this holds the panel. Call <see cref="Tick"/> on the game thread once a frame.
/// </summary>
internal sealed class RetailExaminationTakeover : IDisposable
{
    private readonly RetailPanelTakeover _panel;
    private readonly Func<uint> _selection;
    private readonly Action<uint> _examine;
    private readonly Action<string> _log;
    private uint _selected;
    private bool _selectionFailed;
    private bool _closeAfterShow;

    /// <param name="selection">The object retail is examining: the current selection, read each enabled frame before the panel is ticked.</param>
    /// <param name="examine">Shows the appraisal window for an object, now or when its response arrives.</param>
    public RetailExaminationTakeover(IRetailPanelPort port, Func<uint> selection, Action<uint> examine, Action<string> log)
    {
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _examine = examine ?? throw new ArgumentNullException(nameof(examine));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _panel = new RetailPanelTakeover(port ?? throw new ArgumentNullException(nameof(port)), OnOpenChanged, _log);
    }

    /// <summary>Ticks the panel takeover; <paramref name="enabled"/> false gives the panel back, as the inventory takeover does.</summary>
    public void Tick(bool enabled, bool canShow = true)
    {
        // The selection is read first, so that the panel shown this frame is judged by the object it is for. An empty selection is no
        // examination: the panel is not parked, unless it is already held for an object.
        if (enabled) _selected = ReadSelection();
        _panel.Tick(enabled && (_selected != 0 || _panel.Holds), canShow);
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

    private uint ReadSelection()
    {
        try
        {
            return _selection();
        }
        catch (Exception exception)
        {
            if (!_selectionFailed) _log($"Could not read the selection; retail's examination is left to retail: {exception.Message}");
            _selectionFailed = true;
            return 0;
        }
    }

    private void OnOpenChanged(bool open)
    {
        if (!open) return;
        try
        {
            if (_selected != 0) _examine(_selected);
        }
        catch (Exception exception)
        {
            _log($"Could not show the examined object: {exception.Message}");
        }
        _closeAfterShow = true;
    }
}
