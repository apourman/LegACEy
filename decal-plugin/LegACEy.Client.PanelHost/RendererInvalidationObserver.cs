using System;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Rendering;

namespace LegACEy.Client.PanelHost;

/// <summary>Bridges the pinned Avalonia renderer's internal scene event.</summary>
/// <remarks>
/// Avalonia 11.3.22 exposes the renderer through IRenderRoot, but its scene event
/// is internal on the interface. Bind the concrete renderer once per window;
/// fail explicitly if an Avalonia upgrade changes this contract instead of freezing frames.
/// </remarks>
internal sealed class RendererInvalidationObserver : IDisposable
{
    private readonly object _renderer;
    private readonly EventInfo _sceneInvalidated;
    private readonly EventHandler<SceneInvalidatedEventArgs> _handler;

    public RendererInvalidationObserver(Window window, Action invalidate)
    {
        _renderer = ((IRenderRoot)window).Renderer;
        var type = _renderer.GetType();
        _sceneInvalidated = type.GetEvent("SceneInvalidated")
            ?? throw new NotSupportedException("Avalonia renderer no longer exposes SceneInvalidated.");
        _handler = (_, _) => invalidate();
        _sceneInvalidated.AddEventHandler(_renderer, _handler);
    }

    public void Dispose() => _sceneInvalidated.RemoveEventHandler(_renderer, _handler);
}
