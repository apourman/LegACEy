using Decal.Adapter;

namespace LegACEy.Client.DecalPlugin;

/// <summary>Decal's plugin entry point; UI behavior is owned by ClientUiRuntime and ClientUiFramework.</summary>
[FriendlyName("LegACEy Avalonia Panel")]
public sealed class Plugin : FilterBase
{
    private ClientUiRuntime? _runtime;

    protected override void Startup()
    {
        _runtime = new ClientUiRuntime();
        _runtime.Startup();
    }

    protected override void Shutdown()
    {
        _runtime?.Shutdown();
        _runtime = null;
    }
}
