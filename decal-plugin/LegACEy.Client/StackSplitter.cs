namespace LegACEy.Client.DecalPlugin;

/// <summary>Retail's stack split as the client reaches it: the selected stack, the split amount and the two split senders.</summary>
internal interface IRetailSplitPort
{
    /// <summary>The selected object's id, or 0 when nothing is selected.</summary>
    uint SelectedId { get; }
    /// <summary>Retail's split amount (0x0081D7EC).</summary>
    int SplitAmount { get; }
    /// <summary>The selected stack's size (0x0081D7F0).</summary>
    int StackSize { get; }
    /// <summary>Retail's drop of part of a stack to the ground.</summary>
    void SplitTo3D(uint stackId, int amount);
    /// <summary>Retail's move of part of a stack into a container's slot.</summary>
    void SplitToContainer(uint stackId, uint containerId, int place, int amount);
}

/// <summary>
/// A move from LegACEy. When the item is the selected stack and retail's split amount is below its size, retail's own split sends that
/// amount; the whole item is not moved. Any other move is left to the caller, which moves the whole item as before.
/// </summary>
internal sealed class StackSplitter
{
    private readonly IRetailSplitPort _port;

    public StackSplitter(IRetailSplitPort port) => _port = port;

    public System.Action<string>? Log { get; set; }

    /// <summary>Splits the item to the ground by retail's amount. False when the move is the caller's to make.</summary>
    public bool TrySplitTo3D(uint itemId)
    {
        if (Amount(itemId) is not int amount) return false;
        _port.SplitTo3D(itemId, amount);
        return true;
    }

    /// <summary>Splits the item into a container's slot by retail's amount. False when the move is the caller's to make.</summary>
    public bool TrySplitToContainer(uint itemId, uint containerId, int place)
    {
        if (Amount(itemId) is not int amount) return false;
        _port.SplitToContainer(itemId, containerId, place, amount);
        return true;
    }

    // Only the selected stack, and only when there is something left of it to keep: an amount at the size is a whole move.
    private int? Amount(uint itemId)
    {
        var amount = _port.SplitAmount;
        // ponytail: diagnostic for the split default; remove once it is understood.
        Log?.Invoke($"Split diag: move of 0x{itemId:X8}, selected 0x{_port.SelectedId:X8}, retail amount {amount}, retail size {_port.StackSize}.");
        return itemId != 0 && _port.SelectedId == itemId && amount >= 1 && amount < _port.StackSize ? amount : null;
    }
}
