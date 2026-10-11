using System;

namespace LegACEy.Client.DecalPlugin;

/// <summary>Keeps transition surfaces above retail's portal drawing without changing normal UI layering.</summary>
internal sealed class RetailSurfaceRenderer
{
    private readonly Action _prepare;
    private readonly Action _draw;
    private bool _pendingPostUiDraw;

    internal RetailSurfaceRenderer(Action prepare, Action draw)
    {
        _prepare = prepare;
        _draw = draw;
    }

    internal void RenderFrame(bool isSessionReady, bool postUiAvailable)
    {
        var afterRetailUi = !isSessionReady && postUiAvailable;
        _pendingPostUiDraw = afterRetailUi;
        _prepare();
        if (!afterRetailUi) _draw();
    }

    internal void DrawAfterRetailUi()
    {
        if (!_pendingPostUiDraw) return;
        _pendingPostUiDraw = false;
        _draw();
    }
}
