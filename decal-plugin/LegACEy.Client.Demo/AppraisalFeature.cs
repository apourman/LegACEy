using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using LegACEy.Client.Themes;

namespace LegACEy.Client.Demo;

/// <summary>
/// The appraisal window and the responses behind it. It keeps the server's appraisal of each object it has seen, and opens the window for
/// the item the player asked to appraise (right-click, through <see cref="AppraisingInventoryPort"/>) when that item's response arrives.
/// Any other response, including a plugin's silent ID, only caches; it refreshes the window when the window already shows that object.
/// </summary>
public sealed class AppraisalFeature
{
    /// <summary>The window's id, and the one window the feature reuses for every appraisal.</summary>
    public const string WindowId = "appraisal";

    // The cache is for the current character only; logoff clears it, and it starts over when it grows past this many objects.
    private const int CacheLimit = 64;
    private static readonly Point DefaultLocation = new(240, 100);

    private readonly ILegACEyPluginHost _host;
    private readonly Action<string> _log;
    private readonly Dictionary<uint, AppraisalResponse> _responses = new();
    private uint _requested;
    private uint _shownId;

    public AppraisalFeature(ILegACEyPluginHost host, Action<string> log)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>What the window shows: the content of the last appraisal the window opened for, or null when it has opened for none.</summary>
    public AppraisalContent? Shown { get; private set; }

    /// <summary>The last response for an object this session, or null when none has arrived.</summary>
    public AppraisalResponse? Response(uint objectId) => _responses.TryGetValue(objectId, out var response) ? response : null;

    /// <summary>The player asked to appraise this item: its response opens the window.</summary>
    public void Request(uint objectId) => _requested = objectId;

    /// <summary>Takes one 0x00C9 payload: the object's id, then its appraisal. A payload that does not read is logged and ignored.</summary>
    public void Receive(byte[] payload)
    {
        AppraisalResponse response;
        try { response = AppraisalResponse.Read(payload); }
        catch (EndOfStreamException exception)
        {
            _log($"Could not read an appraisal response: {exception.Message}");
            return;
        }
        if (_responses.Count >= CacheLimit) _responses.Clear();
        _responses[response.ObjectId] = response;
        if (response.ObjectId == _requested)
        {
            _requested = 0;
            Show(response);
        }
        else if (response.ObjectId == _shownId && _host.IsWindowOpen(WindowId))
        {
            Show(response);
        }
    }

    /// <summary>Logoff: the object ids and the window's content belong to the character that left, so the window closes too.</summary>
    public void Clear()
    {
        _responses.Clear();
        _requested = 0;
        _shownId = 0;
        Shown = null;
        _host.CloseWindow(WindowId);
    }

    private void Show(AppraisalResponse response)
    {
        var content = AppraisalContent.From(response);
        var window = AppraisalWindow.Build(content, _host.Art, out var height);
        // The window is as tall as its content, up to the limit. The client keeps a hidden window's size, so the window is released
        // and opened again: its place is saved when it is released, so it reopens where it was.
        _host.CloseWindow(WindowId);
        var definition = new WindowDefinition(WindowId, "Appraisal", AppraisalWindow.Width, height, DerethWindow.TitleBarHeight, new DerethClientTheme());
        var opened = _host.OpenWindow(definition, DefaultLocation, close =>
        {
            window.CloseRequested += (_, _) => close();
            return window;
        }, error => _log($"The appraisal window failed: {error.Message}"), ownChrome: true);
        if (!opened) return;
        _shownId = response.ObjectId;
        Shown = content;
    }
}

/// <summary>
/// The inventory port as the client gives it to plugins and windows. Every command is the bridge port's; an Assess also asks the appraisal
/// window to show the item when its response arrives, so a right-click opens the window and nothing else does.
/// </summary>
public sealed class AppraisingInventoryPort : IInventoryPort
{
    private readonly IInventoryPort _inner;
    private readonly AppraisalFeature _appraisals;

    public AppraisingInventoryPort(IInventoryPort inner, AppraisalFeature appraisals)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _appraisals = appraisals ?? throw new ArgumentNullException(nameof(appraisals));
    }

    public InventorySnapshot Snapshot => _inner.Snapshot;

    public event Action? Changed
    {
        add => _inner.Changed += value;
        remove => _inner.Changed -= value;
    }

    public void OpenContainer(uint containerId) => _inner.OpenContainer(containerId);
    public void Select(uint itemId) => _inner.Select(itemId);
    public void Use(uint itemId) => _inner.Use(itemId);

    public void Assess(uint itemId)
    {
        _appraisals.Request(itemId);
        _inner.Assess(itemId);
    }

    public void DropOnGround(uint itemId) => _inner.DropOnGround(itemId);
    public void MoveToContainer(uint itemId, uint containerId, int slotIndex) => _inner.MoveToContainer(itemId, containerId, slotIndex);
    public uint WieldMask(uint itemId, PaperdollSlot slot) => _inner.WieldMask(itemId, slot);
    public void Wield(uint itemId, PaperdollSlot slot) => _inner.Wield(itemId, slot);
    public void MergeStack(uint itemId, uint targetStackId) => _inner.MergeStack(itemId, targetStackId);
}
