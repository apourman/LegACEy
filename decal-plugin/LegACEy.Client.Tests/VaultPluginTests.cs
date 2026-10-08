using System;
using System.Linq;
using LegACEy.Client.Demo;
using LegACEy.Plugin.Vault;
using Xunit;

namespace LegACEy.Client.Tests;

public sealed class VaultPluginTests
{
    [Fact]
    public void The_plugin_requires_exactly_the_vault_actions_its_client_requests()
    {
        var now = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
        var server = new FakeVaultServer(() => now) { Latency = TimeSpan.Zero, TransferTime = TimeSpan.Zero };
        var channel = new ServerChannelClient(server, () => now);
        server.Deliver = channel.Receive;
        var client = new VaultClient(channel);

        // Hello, then the list, which the Vault needs before it can move or transfer items.
        client.Start();
        for (var i = 0; i < 3; i++) { server.Pump(); channel.Tick(); }
        var guid = FakeVaultServer.SampleItems().First().Guid;
        client.Deposit(guid);
        client.Withdraw(guid);
        client.CheckDeposit(guid);
        client.Move(guid, 1);
        for (var i = 0; i < 3; i++) { server.Pump(); channel.Tick(); }

        var requested = server.Received.Where(action => action != ChannelHello.Action).Distinct().ToArray();
        Assert.Equal(new[] { VaultProtocol.List, VaultProtocol.Deposit, VaultProtocol.Withdraw, VaultProtocol.Check, VaultProtocol.Move }.Order(), requested.Order());
        // Closing the Vault window sends station.leave, which is not a request this test makes.
        Assert.Equal(requested.Append(StationProtocol.Leave).Order(), new VaultPlugin().RequiredActions.Order());
        // The Vault's push is not a request; the server never lists it, so requiring it would hide the Vault.
        Assert.DoesNotContain(VaultProtocol.Changed, new VaultPlugin().RequiredActions);
    }
}
