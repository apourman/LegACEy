using System;
using LegACEy.Client.Demo;

namespace LegACEy.Client.Tests;

/// <summary>
/// Retail's inventory panel as a plugin sees it. The test is the client: <see cref="Retail"/> opens or closes the panel and reports it,
/// and <see cref="IRetailPanel.Close"/> acts the way the takeover does, reporting the change back.
/// </summary>
internal sealed class FakeRetailPanel : IRetailPanel
{
    private readonly Action<bool> _changed;

    public FakeRetailPanel(Action<bool> changed) => _changed = changed ?? throw new ArgumentNullException(nameof(changed));

    public bool Open { get; private set; }
    public int CloseCalls { get; private set; }
    public bool Holds => true;

    /// <summary>Retail opened (true) or closed (false) its panel.</summary>
    public void Retail(bool open)
    {
        Open = open;
        _changed(open);
    }

    /// <summary>The player opens or closes retail's panel (key, toolbar or item).</summary>
    public void Toggle() => Retail(!Open);

    void IRetailPanel.Close()
    {
        CloseCalls++;
        if (Open) Retail(false);
    }

    public void Dispose() { }
}
