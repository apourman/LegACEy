using System;
using System.Collections.Generic;
using System.IO;

namespace LegACEy.Client.Demo;

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

public sealed class VaultSnapshot
{
    public VaultSnapshot(bool available, long balance, int capacity, IReadOnlyList<VaultItemView> items)
    { Available = available; Balance = balance; Capacity = capacity; Items = items; }
    public bool Available { get; }
    public long Balance { get; }
    public int Capacity { get; }
    public IReadOnlyList<VaultItemView> Items { get; }
}

/// <summary>Bodies of the Vault's channel actions; they must match ACE.Server.Market.VaultChannelActions.</summary>
public static class VaultProtocol
{
    public const string Hello = "channel.hello";
    public const string List = "vault.list";
    public const string Deposit = "vault.deposit";
    public const string Withdraw = "vault.withdraw";
    public const string Changed = "vault.changed";

    public static VaultSnapshot ReadList(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        if (reader.ReadByte() == 0) return new VaultSnapshot(false, 0, 0, Array.Empty<VaultItemView>());
        var balance = reader.ReadInt64();
        var capacity = reader.ReadInt32();
        var count = reader.ReadInt32();
        if (count < 0 || count > 100_000) throw new InvalidDataException($"Vault item count {count} is not plausible.");
        var items = new List<VaultItemView>(count);
        for (var i = 0; i < count; i++)
            items.Add(new VaultItemView(reader.ReadUInt32(), ChannelWire.ReadString(reader), reader.ReadUInt32(), reader.ReadInt32(), reader.ReadInt32(),
                ChannelWire.ReadString(reader), ChannelWire.ReadString(reader), DateTimeOffset.FromUnixTimeSeconds(reader.ReadInt64()),
                reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadInt32()));
        return new VaultSnapshot(true, balance, capacity, items);
    }

    public static byte[] WriteList(VaultSnapshot snapshot) => ChannelWire.Body(w =>
    {
        w.Write((byte)(snapshot.Available ? 1 : 0));
        if (!snapshot.Available) return;
        w.Write(snapshot.Balance);
        w.Write(snapshot.Capacity);
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

    public static (ushort Version, string Character, IReadOnlyList<string> Actions) ReadHello(byte[] body)
    {
        var reader = ChannelWire.Reader(body);
        var version = reader.ReadUInt16();
        var character = ChannelWire.ReadString(reader);
        var count = reader.ReadInt32();
        var actions = new List<string>();
        for (var i = 0; i < count && i < 256; i++) actions.Add(ChannelWire.ReadString(reader));
        return (version, character, actions);
    }

    public static byte[] WriteHello(string character, IEnumerable<string> actions) => ChannelWire.Body(w =>
    {
        var list = new List<string>(actions);
        w.Write(ChannelWire.Version);
        ChannelWire.WriteString(w, character);
        w.Write(list.Count);
        foreach (var action in list) ChannelWire.WriteString(w, action);
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
    private readonly Func<uint> _currentSelection;
    private readonly List<IDisposable> _requests = new();
    private readonly IDisposable _changed;
    private bool _disposed;

    public VaultClient(IServerChannel channel, Func<uint>? currentSelection = null)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _currentSelection = currentSelection ?? (() => 0);
        _changed = _channel.Subscribe(VaultProtocol.Changed, OnChanged);
    }

    public VaultConnection Connection { get; private set; } = VaultConnection.Connecting;
    public VaultSnapshot? Snapshot { get; private set; }
    /// <summary>The latest thing to tell the player: a transfer result, refusal or error.</summary>
    public string Notice { get; private set; } = string.Empty;
    public TimeSpan? LastRoundTrip { get; private set; }
    public string ServerCharacter { get; private set; } = string.Empty;
    public int PushesReceived { get; private set; }
    public bool TransferPending { get; private set; }
    public event EventHandler? Changed;

    /// <summary>Greets the server, then loads the Vault.</summary>
    public void Start()
    {
        if (!_channel.IsAvailable)
        {
            Set(VaultConnection.Unavailable, "The LegACEy server channel is not available in this client.");
            return;
        }
        Send(VaultProtocol.Hello, null, reply =>
        {
            if (!reply.Ok) { Set(VaultConnection.Failed, reply.Message); return; }
            ServerCharacter = VaultProtocol.ReadHello(reply.Body).Character;
            Refresh();
        });
    }

    public void Refresh() => Send(VaultProtocol.List, null, reply =>
    {
        if (!reply.Ok) { Set(VaultConnection.Failed, reply.Message); return; }
        Snapshot = VaultProtocol.ReadList(reply.Body);
        Set(Snapshot.Available ? VaultConnection.Live : VaultConnection.Unavailable,
            Snapshot.Available ? Notice : "The Vault is not available on this server.");
    });

    /// <summary>Deposits the item selected in the game.</summary>
    public void DepositSelection()
    {
        var guid = _currentSelection();
        if (guid == 0) { Set(Connection, "Select an item in your pack, then press Deposit."); return; }
        Transfer(VaultProtocol.Deposit, guid);
    }

    public void Withdraw(uint guid) => Transfer(VaultProtocol.Withdraw, guid);

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
    }
}
