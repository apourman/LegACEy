using System.Collections.Generic;
using System.Drawing;
using Avalonia.Controls;
using LegACEy.Client.Demo;

namespace LegACEy.Plugin.Paperdoll;

/// <summary>The 3D paperdoll, opened from the LegACEy menu. The first plugin, and the example for plugin authors.</summary>
public sealed class PaperdollPlugin : ILegACEyPlugin
{
    private const string WindowId = "paperdoll";
    private const uint MenuIcon = 0x06004D20;

    public string Name => "Paperdoll";
    public string Version => typeof(PaperdollPlugin).Assembly.GetName().Version.ToString(3);
    public IReadOnlyCollection<string> RequiredActions { get; } = new[] { PaperdollProtocol.Look };

    public void Start(ILegACEyClient client) =>
        client.AddMenuEntry("Paperdoll", MenuIcon, () => client.ToggleWindow(WindowId, "Paperdoll",
            PaperdollPanel.WindowWidth, PaperdollPanel.WindowHeight, new Point(260, 120), () => CreatePanel(client)));

    private static Control CreatePanel(ILegACEyClient client)
    {
        return new PaperdollPanel(client.ServerChannel, client.PortalPath);
    }
}
