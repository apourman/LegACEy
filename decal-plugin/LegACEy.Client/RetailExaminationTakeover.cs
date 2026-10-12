using System;
using LegACEy.Client.Demo;

namespace LegACEy.Client.DecalPlugin;

/// <summary>
/// Takes retail's examination panel over for the session, with the inventory takeover's park and return. Whenever the panel shows (the
/// E key on any selection, or any other way retail opens it), the current selection is handed to the appraisal window. When the window
/// shows it, the panel is closed again so that the next examination is a new show. While the window waits for the object's response, the
/// panel stays parked (hidden). Retail's panel never comes back while the takeover is on: if the window cannot show the object, no response
/// arrives within the request timeout, or nothing is selected, the panel is closed and the reason logged. While our window cannot show yet
/// (portal space at login), a panel that shows is parked and held, and handed to the window once it can show. Call <see cref="Tick"/> on
/// the game thread once a frame.
/// </summary>
internal sealed class RetailExaminationTakeover : IDisposable
{
    private readonly IRetailPanelPort _port;
    private readonly RetailPanelTakeover _panel;
    private readonly Func<uint> _selection;
    private readonly AppraisalFeature _appraisals;
    private readonly Action<string> _log;
    private readonly Func<bool> _verifyRoot;
    // The root check, made on the first show of a session before the panel is parked. Null until then.
    private bool? _rootVerified;
    private uint _selected;
    // The selection the appraisal window last followed: a change is handed to the window once.
    private uint _followed;
    // The object whose response the window is waiting for, while retail's panel is parked (hidden) for it. Zero when none.
    private uint _awaiting;
    private bool _selectionFailed;
    private bool _closeAfterShow;
    // What the last enabled tick saw of retail's panel, logged on each change: whether the client has it, and whether it shows.
    private (bool Found, bool Shown)? _seen;

    /// <param name="selection">The object retail is examining: the current selection, read each enabled frame before the panel is ticked.</param>
    /// <param name="appraisals">Shows the appraisal window for an object, now or when its response arrives, and reports how that went.</param>
    /// <param name="verifyRoot">Checks at runtime that the port's root is the examination panel. Run once a session, on the first enabled
    /// tick where the panel is open and our window can show, before the panel is parked. False, or a throw, leaves retail's panel alone for
    /// the session.</param>
    public RetailExaminationTakeover(IRetailPanelPort port, Func<uint> selection, AppraisalFeature appraisals, Action<string> log, Func<bool> verifyRoot)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _appraisals = appraisals ?? throw new ArgumentNullException(nameof(appraisals));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _verifyRoot = verifyRoot ?? throw new ArgumentNullException(nameof(verifyRoot));
        _panel = new RetailPanelTakeover(_port, OnOpenChanged, Note);
    }

    /// <summary>Ticks the panel takeover; <paramref name="enabled"/> false gives the panel back, as the inventory takeover does.</summary>
    public void Tick(bool enabled, bool canShow = true)
    {
        try
        {
            TickCore(enabled, canShow);
        }
        catch (Exception exception)
        {
            Drop($"Retail's examination is dropped: {exception.Message}");
            CloseIfAsked();
        }
    }

    /// <summary>Logoff or unload: gives the panel back and forgets the session.</summary>
    public void EndSession()
    {
        _awaiting = 0;
        _closeAfterShow = false;
        _rootVerified = null;
        _followed = 0;
        _panel.EndSession();
    }

    public void Dispose() => _panel.Dispose();

    private void TickCore(bool enabled, bool canShow)
    {
        // The selection is read first, so that the panel shown this frame is judged by the object it is for.
        if (enabled)
        {
            _selected = ReadSelection();
            NoteChange();
            if (_selected != _followed)
            {
                _followed = _selected;
                _appraisals.Follow(_selected);
            }
        }
        // A failed root check leaves the panel to retail for the session: nothing is parked and nothing is closed.
        var verified = enabled && RootVerified();
        // Our window can only show with the character in the world. Until then a shown panel is parked, out of sight, and nothing waits
        // for it: the panel takeover reports it open once the window can show, and it is examined then.
        var live = verified && canShow;
        if (!live)
        {
            _awaiting = 0;
            _closeAfterShow = false;
        }
        else if (_awaiting != 0) Resolve();
        _panel.Tick(verified, canShow);
        // The close comes after the takeover has parked the panel and reported it, so it is a second step, not a reentrant call.
        CloseIfAsked();
    }

    private void CloseIfAsked()
    {
        if (!_closeAfterShow) return;
        _closeAfterShow = false;
        _panel.Close();
    }

    /// <summary>
    /// The root check, made once a session on the first enabled tick where the panel is open and our window can show (the panel exists
    /// then). While the panel is closed there is nothing to check and nothing is parked, so true with no verdict yet. A failed check, or a
    /// port that cannot say whether it is open, is logged once and stays failed for the session.
    /// </summary>
    private bool RootVerified()
    {
        if (_rootVerified is { } verified) return verified;
        bool open;
        try
        {
            open = _port.IsOpen;
        }
        catch (Exception exception)
        {
            Note($"Retail's examination panel is left to retail: its state could not be read: {exception.Message}");
            _rootVerified = false;
            return false;
        }
        if (!open) return true;
        bool passed;
        try
        {
            passed = _verifyRoot();
        }
        catch (Exception exception)
        {
            Note($"Retail's examination panel is left to retail: the root check failed: {exception.Message}");
            passed = false;
        }
        if (!passed) Note("Retail's examination panel is left to retail: its root is not confirmed as the examination panel.");
        _rootVerified = passed;
        return passed;
    }

    /// <summary>Logs each change of what the client has of retail's panel, with the selection then: the trail for a takeover that never fires.</summary>
    private void NoteChange()
    {
        var seen = (_port.Identity != IntPtr.Zero, _port.IsOpen);
        if (_seen == seen) return;
        _seen = seen;
        Note($"Retail's examination panel is {(seen.Item1 ? "present" : "absent")} and {(seen.Item2 ? "shown" : "hidden")}; selection 0x{_selected:X8}.");
    }

    private uint ReadSelection()
    {
        try
        {
            return _selection();
        }
        catch (Exception exception)
        {
            if (!_selectionFailed) Note($"Could not read the selection; retail's examination is left to retail: {exception.Message}");
            _selectionFailed = true;
            return 0;
        }
    }

    /// <summary>The awaited response has arrived or timed out: retail's panel is closed either way, and a failure is logged.</summary>
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
                Drop("Retail's examination is dropped: no response for the object came in time, or the window did not show it.");
                return;
        }
    }

    private void OnOpenChanged(bool open)
    {
        if (!open) return;
        // A plugin's selected stored item (a Vault item) is what the player examines: its own window shows it, so retail's panel closes.
        if (_appraisals.ExamineStored())
        {
            _closeAfterShow = true;
            return;
        }
        if (_selected == 0)
        {
            Drop("Retail's examination is dropped: nothing is selected to examine.");
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
                    Drop("Retail's examination is dropped: the appraisal window could not show the object.");
                    break;
            }
        }
        catch (Exception exception)
        {
            Drop($"Retail's examination is dropped: {exception.Message}");
        }
    }

    /// <summary>The log, guarded: a log that throws must not escape the frame's tick.</summary>
    private void Note(string message)
    {
        try { _log(message); }
        catch (Exception) { }
    }

    /// <summary>The one failure path: logs why, stops waiting, and closes retail's panel, so retail's examination never shows.</summary>
    private void Drop(string reason)
    {
        Note(reason);
        _awaiting = 0;
        _closeAfterShow = true;
    }
}
