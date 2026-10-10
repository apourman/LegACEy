using System;
using LegACEy.Client.Demo;

namespace LegACEy.Client.Tests;

/// <summary>
/// Retail's inventory panel as a plugin sees it. The test is the client: <see cref="Retail"/> opens or closes the panel and reports it,
/// and <see cref="Open"/> and <see cref="Close"/> act the way the takeover does, reporting the change back.
/// </summary>
internal sealed class FakeRetailPanel : IRetailPanel
{
    private readonly Action<bool> _changed;

    public FakeRetailPanel(Action<bool> changed) => _changed = changed ?? throw new ArgumentNullException(nameof(changed));

    public bool Open { get; private set; }
    public int OpenCalls { get; private set; }
    public int CloseCalls { get; private set; }
    /// <summary>Whether the takeover holds the panel. False models the switch being off, when Open and Close do nothing.</summary>
    public bool Holds { get; set; } = true;
    /// <summary>Whether retail acts on a request. False models a panel switch with no effect.</summary>
    public bool Responds { get; set; } = true;

    /// <summary>Retail opened (true) or closed (false) its panel.</summary>
    public void Retail(bool open)
    {
        Open = open;
        _changed(open);
    }

    /// <summary>The player opens or closes retail's panel (key, toolbar or item).</summary>
    public void Toggle() => Retail(!Open);

    void IRetailPanel.Open()
    {
        OpenCalls++;
        if (Responds && !Open) Retail(true);
    }

    void IRetailPanel.Close()
    {
        CloseCalls++;
        if (Responds && Open) Retail(false);
    }

    public void Dispose() { }
}
