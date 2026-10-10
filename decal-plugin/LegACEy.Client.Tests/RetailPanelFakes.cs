using System;
using System.Collections.Generic;
using LegACEy.Client.Demo;

namespace LegACEy.Client.Tests;

/// <summary>
/// A retail panel as a plugin sees it. The test is the client: <see cref="Retail"/> opens or closes the panel and reports it, and
/// <see cref="Close"/> closes it the way the takeover does, reporting the close back.
/// </summary>
internal sealed class FakeRetailPanel : IRetailPanel
{
    private readonly Action<bool> _changed;

    public FakeRetailPanel(uint rootId, Action<bool> changed)
    {
        RootId = rootId;
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
    }

    public uint RootId { get; }
    public bool Open { get; private set; }
    public int CloseCalls { get; private set; }
    public bool Disposed { get; private set; }

    /// <summary>Retail opened (true) or closed (false) its panel.</summary>
    public void Retail(bool open)
    {
        Open = open;
        _changed(open);
    }

    public void Close()
    {
        CloseCalls++;
        if (Open) Retail(false);
    }

    public void Dispose() => Disposed = true;
}

/// <summary>Records what an inventory drop delivers, and delivers it to the LegACEy window under the pointer, as the client does.</summary>
internal sealed class FakeItemDropRelay : IItemDropRelay
{
    public System.Drawing.Point Pointer { get; set; }
    public IEnumerable<DropArea> Areas { get; set; } = Array.Empty<DropArea>();
    public List<(uint Id, string Name)> Delivered { get; } = new();

    public bool DeliverAtPointer(uint itemId, string itemName)
    {
        Delivered.Add((itemId, itemName));
        return ItemDropRouting.Deliver(Pointer, Areas, itemId, itemName);
    }
}
