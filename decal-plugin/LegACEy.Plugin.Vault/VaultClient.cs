using System;
using System.Collections.Generic;
using System.IO;
using LegACEy.Client.Demo;

namespace LegACEy.Plugin.Vault;

/// <summary>One Vault item as the server describes it, with the icon layers the client draws bottom to top.</summary>
public sealed class VaultItemView
{
    public VaultItemView(uint guid, string name, uint itemType, int stackSize, int value, string state, string depositedBy,
        DateTimeOffset depositedAt, uint plate, uint underlay, uint icon, uint overlay, uint overlaySecondary, int uiEffects)
    {
        Guid = guid; Name = name; ItemType = itemType; StackSize = stackSize; Value = value; State = state; DepositedBy = depositedBy;
        DepositedAt = depositedAt; Plate = plate; Underlay = underlay; Icon = icon; Overlay = overlay; OverlaySecondary = overlaySecondary; UiEffects = uiEffects;
    }

    public uint Guid { get; }
    public string Name { get; }
    public uint ItemType { get; }
    public int StackSize { get; }
    public int Value { get; }
    /// <summary>held, listed or withdrawing</summary>
    public string State { get; }
    public string DepositedBy { get; }
    public DateTimeOffset DepositedAt { get; }
    public uint Plate { get; }
    public uint Underlay { get; }
    public uint Icon { get; }
    public uint Overlay { get; }
    public uint OverlaySecondary { get; }
    public int UiEffects { get; }
    public IEnumerable<uint> IconLayers
    {
        get
        {
            foreach (var id in new[] { Plate, Underlay, Icon, Overlay, OverlaySecondary })
                if (id != 0) yield return id;
        }
    }
}

/// <summary>One page of the Vault as the server answered it, with the counts around the page.</summary>
public sealed class VaultSnapshot
{
    public VaultSnapshot(bool available, long balance, int capacity, int vaultCount, int total, IReadOnlyList<VaultItemView> items)
    { Available = available; Balance = balance; Capacity = capacity; VaultCount = vaultCount; Total = total; Items = items; }
    public bool Available { get; }
    /// <summary>The account's MMD; negative while the server's marketplace is closed</summary>
    public long Balance { get; }
    public bool HasBalance => Balance >= 0;
    public int Capacity { get; }
    /// <summary>Every item in the Vault, whatever the search</summary>
    public int VaultCount { get; }
    /// <summary>How many items match the search, across every page</summary>
    public int Total { get; }
    /// <summary>This page's items, in the Vault's order</summary>
    public IReadOnlyList<VaultItemView> Items { get; }
}

/// <summary>Bodies of the Vault's channel actions; they must match ACE.Server.Market.VaultChannelActions.</summary>
public static class VaultProtocol
{
    public const string List = "vault.list";
    public const string Deposit = "vault.deposit";
    public const string Withdraw = "vault.withdraw";
    public const string Changed = "vault.changed";
    public const string Check = "vault.check";
    public const string Move = "vault.move";

    /// <summary>The items the window asks for at a time; the server's page size.</summary>
    public const int PageSize = 100;

    /// <summary>A page request: the search text (empty for none), the offset of the page's first match and the count.</summary>
    public static byte[] ListRequest(string search, int offset, int count) => ChannelWire.Body(w =>
    {
        ChannelWire.WriteString(w, search);
        w.Write(offset);
        w.Write(count);
    });

    public static byte[] MoveRequest(uint guid, int index) => ChannelWire.Body(w => { w.Write(guid); w.Write(index); });

    public static (uint Guid, bool Ok, string Message) ReadCheck(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        return (reader.ReadUInt32(), reader.ReadByte() != 0, ChannelWire.ReadString(reader));
    }

    public static byte[] WriteCheck(uint guid, bool ok, string message) => ChannelWire.Body(w => { w.Write(guid); w.Write((byte)(ok ? 1 : 0)); ChannelWire.WriteString(w, message); });

    public static VaultSnapshot ReadList(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        if (reader.ReadByte() == 0) return new VaultSnapshot(false, 0, 0, 0, 0, Array.Empty<VaultItemView>());
        var balance = reader.ReadInt64();
        var capacity = reader.ReadInt32();
        var vaultCount = reader.ReadInt32();
        var total = reader.ReadInt32();
        var count = reader.ReadInt32();
        if (count < 0 || count > PageSize) throw new InvalidDataException($"Vault page of {count} items is not plausible.");
        var items = new List<VaultItemView>(count);
        for (var i = 0; i < count; i++)
            items.Add(new VaultItemView(reader.ReadUInt32(), ChannelWire.ReadString(reader), reader.ReadUInt32(), reader.ReadInt32(), reader.ReadInt32(),
                ChannelWire.ReadString(reader), ChannelWire.ReadString(reader), DateTimeOffset.FromUnixTimeSeconds(reader.ReadInt64()),
                reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadInt32()));
        return new VaultSnapshot(true, balance, capacity, vaultCount, total, items);
    }

    public static byte[] WriteList(VaultSnapshot snapshot) => ChannelWire.Body(w =>
    {
        w.Write((byte)(snapshot.Available ? 1 : 0));
        if (!snapshot.Available) return;
        w.Write(snapshot.Balance);
        w.Write(snapshot.Capacity);
        w.Write(snapshot.VaultCount);
        w.Write(snapshot.Total);
        w.Write(snapshot.Items.Count);
        foreach (var item in snapshot.Items)
        {
            w.Write(item.Guid);
            ChannelWire.WriteString(w, item.Name);
            w.Write(item.ItemType);
            w.Write(item.StackSize);
            w.Write(item.Value);
            ChannelWire.WriteString(w, item.State);
            ChannelWire.WriteString(w, item.DepositedBy);
            w.Write(item.DepositedAt.ToUnixTimeSeconds());
            w.Write(item.Plate); w.Write(item.Underlay); w.Write(item.Icon); w.Write(item.Overlay); w.Write(item.OverlaySecondary);
            w.Write(item.UiEffects);
        }
    });

    public static byte[] ItemRequest(uint guid) => ChannelWire.Body(w => w.Write(guid));

    public static (bool Accepted, string Message) ReadTransfer(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        return (reader.ReadByte() != 0, ChannelWire.ReadString(reader));
    }

    public static byte[] WriteTransfer(bool accepted, string message) => ChannelWire.Body(w => { w.Write((byte)(accepted ? 1 : 0)); ChannelWire.WriteString(w, message); });

    public static (bool Success, string Outcome, string Message, uint ItemGuid) ReadChanged(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        return (reader.ReadByte() != 0, ChannelWire.ReadString(reader), ChannelWire.ReadString(reader), reader.ReadUInt32());
    }

    public static byte[] WriteChanged(bool success, string outcome, string message, uint itemGuid) => ChannelWire.Body(w =>
    {
        w.Write((byte)(success ? 1 : 0));
        ChannelWire.WriteString(w, outcome);
        ChannelWire.WriteString(w, message);
        w.Write(itemGuid);
    });

}

public enum VaultConnection { Connecting, Live, Unavailable, Failed }

/// <summary>
/// The Vault window's view of the server: loads the account Vault over the channel, starts transfers, and refreshes
/// when the server pushes vault.changed. Owns its requests and subscription; Dispose stops every callback.
/// </summary>
public sealed class VaultClient : IDisposable
{
    private readonly IServerChannel _channel;
    private readonly List<IDisposable> _requests = new();
    private readonly IDisposable _changed;
    private bool _disposed;

    public VaultClient(IServerChannel channel)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _changed = _channel.Subscribe(VaultProtocol.Changed, OnChanged);
    }

    public VaultConnection Connection { get; private set; } = VaultConnection.Connecting;
    /// <summary>The one page the window holds: its items and the counts around them.</summary>
    public VaultSnapshot? Snapshot { get; private set; }
    /// <summary>The page on screen starts at this match of the search; it is the position in the whole Vault order too.</summary>
    public int Offset { get; private set; }
    /// <summary>The trimmed search text the page was asked with; empty for the whole Vault.</summary>
    public string Search { get; private set; } = string.Empty;
    public bool CanPageBack => Offset > 0;
    public bool CanPageForward => Snapshot is { Available: true } page && Offset + VaultProtocol.PageSize < page.Total;
    /// <summary>Numbers the page requests, so that only the newest one's reply is shown.</summary>
    private int _pageRequest;
    /// <summary>The latest thing to tell the player: a transfer result, refusal or error.</summary>
    public string Notice { get; private set; } = string.Empty;
    public TimeSpan? LastRoundTrip { get; private set; }
    public string ServerCharacter { get; private set; } = string.Empty;
    public int PushesReceived { get; private set; }
    public bool TransferPending { get; private set; }
    public event EventHandler? Changed;
    /// <summary>A deposit check finished; read it with <see cref="DepositCheck"/>.</summary>
    public event EventHandler? DepositCheckChanged;
    private readonly Dictionary<uint, (bool Ok, string Message)> _checks = new();

    /// <summary>Greets the server, then loads the Vault.</summary>
    public void Start()
    {
        if (!_channel.IsAvailable)
        {
            Set(VaultConnection.Unavailable, "The LegACEy server channel is not available in this client.");
            return;
        }
        Send(ChannelHello.Action, null, reply =>
        {
            if (!reply.Ok) { Set(VaultConnection.Failed, reply.Message); return; }
            ServerCharacter = ChannelHello.Read(reply.Body).Character;
            Refresh();
        });
    }

    /// <summary>Loads the page on screen again: after a push, or after a move.</summary>
    public void Refresh() => RequestPage(Offset, Search);

    public void NextPage()
    {
        if (CanPageForward) RequestPage(Offset + VaultProtocol.PageSize, Search);
    }

    public void PreviousPage()
    {
        if (CanPageBack) RequestPage(Math.Max(0, Offset - VaultProtocol.PageSize), Search);
    }

    /// <summary>Filters the Vault by item name, from its first match. Blank text shows the whole Vault.</summary>
    public void SetSearch(string text)
    {
        var search = (text ?? string.Empty).Trim();
        if (search != Search) RequestPage(0, search);
    }

    private void RequestPage(int offset, string search)
    {
        Offset = offset;
        Search = search;
        var request = ++_pageRequest;
        Send(VaultProtocol.List, VaultProtocol.ListRequest(search, offset, VaultProtocol.PageSize), reply =>
        {
            if (request != _pageRequest) return;
            if (!reply.Ok) { Set(VaultConnection.Failed, reply.Message); return; }
            var page = VaultProtocol.ReadList(reply.Body);
            // The page is past the end because items were taken out: the last page is the one to show.
            if (page.Available && Offset > 0 && Offset >= page.Total)
            {
                RequestPage(LastPageOffset(page.Total), Search);
                return;
            }
            Snapshot = page;
            Set(page.Available ? VaultConnection.Live : VaultConnection.Unavailable,
                page.Available ? Notice : "The Vault is not available on this server.");
        });
    }

    private static int LastPageOffset(int total) => total <= 0 ? 0 : (total - 1) / VaultProtocol.PageSize * VaultProtocol.PageSize;

    public void Deposit(uint guid) => Transfer(VaultProtocol.Deposit, guid);

    public void Withdraw(uint guid) => Transfer(VaultProtocol.Withdraw, guid);

    /// <summary>
    /// Asks the server whether the item could be deposited now (attuned, worn, too busy…), replacing any earlier answer for it.
    /// The window asks when an item starts being dragged over it.
    /// </summary>
    public void CheckDeposit(uint guid)
    {
        _checks.Remove(guid);
        Send(VaultProtocol.Check, VaultProtocol.ItemRequest(guid), reply =>
        {
            if (!reply.Ok) return;
            var (checkedGuid, ok, message) = VaultProtocol.ReadCheck(reply.Body);
            _checks[checkedGuid] = (ok, message);
            DepositCheckChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>The server's answer for the item, or null while it is unknown.</summary>
    public (bool Ok, string Message)? DepositCheck(uint guid) => _checks.TryGetValue(guid, out var check) ? check : null;

    /// <summary>
    /// Moves an item to a cell of the page on screen: at once on screen, then on the server. The server numbers the whole Vault,
    /// so the cell is offset by the page's start. A filtered page has no place in the whole order, so a search blocks the move.
    /// </summary>
    public void Move(uint guid, int index)
    {
        if (Snapshot is not { Available: true } snapshot) return;
        if (Search.Length > 0)
        {
            Tell("Clear the search to rearrange items.");
            return;
        }
        var items = new List<VaultItemView>(snapshot.Items);
        var from = items.FindIndex(item => item.Guid == guid);
        if (from < 0) return;
        index = Math.Max(0, Math.Min(index, items.Count - 1));
        if (index == from) return;
        var moved = items[from];
        items.RemoveAt(from);
        items.Insert(index, moved);
        Snapshot = new VaultSnapshot(true, snapshot.Balance, snapshot.Capacity, snapshot.VaultCount, snapshot.Total, items);
        Changed?.Invoke(this, EventArgs.Empty);
        Send(VaultProtocol.Move, VaultProtocol.MoveRequest(guid, Offset + index), reply =>
        {
            var (accepted, message) = reply.Ok ? VaultProtocol.ReadTransfer(reply.Body) : (false, reply.Message);
            if (!accepted) Notice = message;
            // The server's order wins either way.
            Refresh();
        });
    }

    /// <summary>Shows a message to the player without contacting the server.</summary>
    public void Tell(string notice) => Set(Connection, notice);

    private void Transfer(string action, uint guid)
    {
        TransferPending = true;
        Set(Connection, action == VaultProtocol.Deposit ? "Asking the server to deposit…" : "Asking the server to withdraw…");
        Send(action, VaultProtocol.ItemRequest(guid), reply =>
        {
            if (!reply.Ok) { TransferPending = false; Set(Connection, reply.Message); return; }
            var (accepted, message) = VaultProtocol.ReadTransfer(reply.Body);
            if (!accepted) TransferPending = false;
            // A started transfer is announced in chat; its outcome comes as a vault.changed push.
            Set(Connection, accepted ? "Transfer channelling — stay still until it completes." : message);
        });
    }

    private void OnChanged(byte[] body)
    {
        PushesReceived++;
        TransferPending = false;
        Notice = VaultProtocol.ReadChanged(body).Message;
        Changed?.Invoke(this, EventArgs.Empty);
        Refresh();
    }

    private void Send(string action, byte[]? body, Action<ChannelReply> completed)
    {
        if (_disposed) return;
        IDisposable? request = null;
        var done = false;
        request = _channel.Request(action, body ?? Array.Empty<byte>(), reply =>
        {
            done = true;
            if (request != null) _requests.Remove(request);
            if (_disposed) return;
            LastRoundTrip = reply.RoundTrip;
            completed(reply);
        });
        // A reply delivered synchronously (request not sent) has already run.
        if (!done && !_disposed) _requests.Add(request);
    }

    private void Set(VaultConnection connection, string notice)
    {
        Connection = connection;
        Notice = notice;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _changed.Dispose();
        foreach (var request in _requests.ToArray()) request.Dispose();
        _requests.Clear();
        Changed = null;
        DepositCheckChanged = null;
    }
}
