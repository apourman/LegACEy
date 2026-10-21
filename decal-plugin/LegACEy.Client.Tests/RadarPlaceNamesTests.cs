using System;
using System.Collections.Generic;
using LegACEy.Client.Demo;
using LegACEy.Client.GameArt;
using Xunit;

namespace LegACEy.Client.Tests;

/// <summary>The server's place names for the radar's plaque: asked once per landblock when hello lists place.name, and kept for the login.</summary>
public sealed class RadarPlaceNamesTests
{
    // 0x00C9 holds four Chambers on the shared list, so the list names none of them.
    private const uint SharedCell = 0x00C90101;

    [Fact]
    public void Without_place_name_in_hello_the_server_is_never_asked_and_the_list_names_the_place()
    {
        var channel = new AnsweringChannel();
        var names = new RadarPlaceNames();
        names.Reset(channel, new[] { ChannelHello.Action, "vault.list" });

        Assert.Equal("Black Totem… · Level 2 of 3", Plaque(0x00990101, names.NameOf));
        Assert.Empty(channel.Requests);
    }

    [Fact]
    public void Listed_the_server_is_asked_once_per_visit_to_a_landblock_and_its_name_replaces_the_list()
    {
        var channel = new AnsweringChannel();
        var names = new RadarPlaceNames();
        names.Reset(channel, new[] { ChannelHello.Action, PlaceProtocol.Name });

        Assert.Equal("Level 2 of 3", Plaque(SharedCell, names.NameOf));
        Assert.Single(channel.Requests);
        Assert.Equal(PlaceProtocol.Name, channel.Requests[0].Action);

        channel.Answer(0, "Chamber 8");
        Assert.Equal("Chamber 8 · Level 2 of 3", Plaque(SharedCell, names.NameOf));
        Assert.Single(channel.Requests);

        // Leaving the landblock drops its name, and coming back asks again: a copy shares its landblock ids with the base world.
        Plaque(0x00990101, names.NameOf);
        Assert.Equal("Level 2 of 3", Plaque(SharedCell, names.NameOf));
        Assert.Equal(3, channel.Requests.Count);
    }

    [Fact]
    public void A_new_hello_forgets_the_answers_and_an_answer_to_an_earlier_one_is_dropped()
    {
        var channel = new AnsweringChannel();
        var names = new RadarPlaceNames();
        names.Reset(channel, new[] { PlaceProtocol.Name });
        names.NameOf(SharedCell);

        names.Reset(channel, new[] { PlaceProtocol.Name });
        channel.Answer(0, "Chamber 8");

        Assert.Null(names.NameOf(SharedCell));
        Assert.Equal(2, channel.Requests.Count);
    }

    // The plaque of a dungeon cell, with the server's names as the window gets them.
    private static string? Plaque(uint cell, Func<uint, string?> serverName)
    {
        var plan = new RadarPlan(RadarPlanFixtures.Dungeon((int)(cell >> 24), (int)((cell >> 16) & 0xFF)), (ushort)(cell & 0xFFFF));
        var player = new RadarPlayer(new RadarPosition(cell, 60, 84, 0), 0);
        return RadarScope.Build(player, Array.Empty<RadarObject>(), new RadarOptions(), 59, plan, serverName: serverName).Plaque;
    }

    /// <summary>Records each request and answers it when the test says so, as the host's channel does.</summary>
    private sealed class AnsweringChannel : IServerChannel
    {
        public List<(string Action, byte[] Body, Action<ChannelReply> Completed)> Requests { get; } = new();

        public bool IsAvailable => true;

        public IDisposable Request(string action, byte[] body, Action<ChannelReply> completed, TimeSpan? timeout = null)
        {
            Requests.Add((action, body, completed));
            return new Handle();
        }

        public IDisposable Subscribe(string topic, Action<byte[]> handler) => new Handle();

        public IDisposable Schedule(TimeSpan delay, Action action) => new Handle();

        public void Answer(int index, string name) =>
            Requests[index].Completed(new ChannelReply(ChannelStatus.Ok, ChannelWire.Body(w => ChannelWire.WriteString(w, name)), TimeSpan.Zero));

        private sealed class Handle : IDisposable
        {
            public void Dispose() { }
        }
    }
}
