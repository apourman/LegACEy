using System;
using LegACEy.Client.Demo;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Takes retail's examination panel over for the session, with the inventory takeover's park and return. Whenever the panel shows (the
/// E key on any selection, or any other way retail opens it), the current selection is handed to the appraisal window. When the window
/// shows it, the panel is closed again so that the next examination is a new show. While the window waits for the object's response, the
/// panel stays parked (hidden). If the window cannot show it, or no response arrives within the request timeout, retail's panel is given
/// back, so examining always shows something. With no selection to examine, the panel is left to retail. Call <see cref="Tick"/> on the
/// game thread once a frame.
/// </summary>
internal sealed class RetailExaminationTakeover : IDisposable
{
    private readonly RetailPanelTakeover _panel;
    private readonly Func<uint> _selection;
    private readonly AppraisalFeature _appraisals;
    private readonly Action<string> _log;
    private uint _selected;
    // The object whose response the window is waiting for, while retail's panel is parked (hidden) for it. Zero when none.
    private uint _awaiting;
    private bool _selectionFailed;
    private bool _closeAfterShow;

    /// <param name="selection">The object retail is examining: the current selection, read each enabled frame before the panel is ticked.</param>
    /// <param name="appraisals">Shows the appraisal window for an object, now or when its response arrives, and reports how that went.</param>
    public RetailExaminationTakeover(IRetailPanelPort port, Func<uint> selection, AppraisalFeature appraisals, Action<string> log)
    {
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _appraisals = appraisals ?? throw new ArgumentNullException(nameof(appraisals));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _panel = new RetailPanelTakeover(port ?? throw new ArgumentNullException(nameof(port)), OnOpenChanged, _log);
    }

    /// <summary>Ticks the panel takeover; <paramref name="enabled"/> false gives the panel back, as the inventory takeover does.</summary>
    public void Tick(bool enabled, bool canShow = true)
    {
        // The selection is read first, so that the panel shown this frame is judged by the object it is for. An empty selection is no
        // examination: the panel is not parked, unless it is already held for an object.
        if (enabled) _selected = ReadSelection();
        if (!enabled) _awaiting = 0;
        else if (_awaiting != 0) Resolve();
        _panel.Tick(enabled && (_selected != 0 || _panel.Holds), canShow);
        // The close comes after the takeover has parked the panel and reported it, so it is a second step, not a reentrant call.
        if (!_closeAfterShow) return;
        _closeAfterShow = false;
        _panel.Close();
    }

    /// <summary>Logoff or unload: gives the panel back and forgets the session.</summary>
    public void EndSession()
    {
        _awaiting = 0;
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

    /// <summary>The awaited response has arrived or timed out: the window shows it (retail's panel is closed), or it gives retail its panel back.</summary>
    private void Resolve()
    {
        switch (_appraisals.Status(_awaiting))
        {
            case ExamineResult.Pending:
                return;
            case ExamineResult.Shown:
                _awaiting = 0;
                _closeAfterShow = true;
                return;
            default:
                _log("Retail's examination panel is given back: no response for the object came in time, or the window did not show it.");
                GiveBack();
                return;
        }
    }

    private void OnOpenChanged(bool open)
    {
        if (!open) return;
        // With no object to examine there is nothing for our window to show, so retail's panel is given back.
        if (_selected == 0)
        {
            GiveBack();
            return;
        }
        try
        {
            switch (_appraisals.Examine(_selected))
            {
                case ExamineResult.Shown:
                    _closeAfterShow = true;
                    break;
                case ExamineResult.Pending:
                    _awaiting = _selected;
                    break;
                default:
                    _log("Retail's examination panel is given back: the appraisal window could not show the object.");
                    GiveBack();
                    break;
            }
        }
        catch (Exception exception)
        {
            _log($"Could not show the examined object: {exception.Message}");
            GiveBack();
        }
    }

    /// <summary>Gives retail its panel back, open, and stops waiting for the object's response.</summary>
    private void GiveBack()
    {
        _awaiting = 0;
        _panel.Yield();
    }
}
