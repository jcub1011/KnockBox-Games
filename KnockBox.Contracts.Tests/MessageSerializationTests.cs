using System.Text.Json;
using KnockBox.Contracts;
using Xunit;

namespace KnockBox.Contracts.Tests;

/// <summary>
/// The wire contract: every envelope is (de)serialized polymorphically on a camelCase <c>"type"</c>
/// discriminator, matching what the server and the JS clients exchange. These guard that contract.
/// </summary>
public class MessageSerializationTests
{
    // Same options the server uses (ConnectionManager.SerializerOptions).
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Hello_round_trips_through_the_base_type()
    {
        Message original = new HelloMessage(PlayerId: null, DisplayName: "Ann", Token: "tok");

        var json = JsonSerializer.Serialize(original, Options);
        var back = JsonSerializer.Deserialize<Message>(json, Options);

        var hello = Assert.IsType<HelloMessage>(back);
        Assert.Equal("Ann", hello.DisplayName);
        Assert.Equal("tok", hello.Token);
        Assert.Null(hello.PlayerId);
    }

    [Fact]
    public void Discriminator_is_camelCased_type_property()
    {
        var json = JsonSerializer.Serialize<Message>(new WelcomeMessage("p1", "tok", "https://games.example"), Options);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Welcome", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("p1", doc.RootElement.GetProperty("playerId").GetString());
        Assert.Equal("https://games.example", doc.RootElement.GetProperty("gameOrigin").GetString());
    }

    [Fact]
    public void Game_message_carries_an_opaque_payload_round_trip()
    {
        var payload = JsonSerializer.Deserialize<JsonElement>("""{ "kind": "move", "cell": 4 }""");
        Message original = new GameMessage(To: "host", Payload: payload, From: null);

        var json = JsonSerializer.Serialize(original, Options);
        var back = Assert.IsType<GameMessage>(JsonSerializer.Deserialize<Message>(json, Options));

        Assert.Equal("host", back.To);
        Assert.Equal(4, back.Payload.GetProperty("cell").GetInt32());
    }

    [Fact]
    public void KickPlayer_round_trips_with_type_first()
    {
        var json = JsonSerializer.Serialize<Message>(new KickPlayerMessage("p2"), Options);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("KickPlayer", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("p2", doc.RootElement.GetProperty("targetPlayerId").GetString());

        var back = Assert.IsType<KickPlayerMessage>(JsonSerializer.Deserialize<Message>(json, Options));
        Assert.Equal("p2", back.TargetPlayerId);
    }

    [Fact]
    public void Kicked_round_trips_with_type_first()
    {
        var json = JsonSerializer.Serialize<Message>(new KickedMessage("ABCD"), Options);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Kicked", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("ABCD", doc.RootElement.GetProperty("lobbyId").GetString());

        var back = Assert.IsType<KickedMessage>(JsonSerializer.Deserialize<Message>(json, Options));
        Assert.Equal("ABCD", back.LobbyId);
    }

    [Fact]
    public void Missing_proto_reads_as_zero_for_pre_versioning_clients()
    {
        var hello = Assert.IsType<HelloMessage>(JsonSerializer.Deserialize<Message>(
            """{ "type": "Hello", "displayName": "Ann" }""", Options));
        var attach = Assert.IsType<AttachMessage>(JsonSerializer.Deserialize<Message>(
            """{ "type": "Attach", "ticket": "t" }""", Options));

        Assert.Equal(0, hello.Proto);
        Assert.Equal(0, attach.Proto);
    }

    [Fact]
    public void Server_replies_echo_the_protocol_version()
    {
        var welcome = JsonSerializer.Serialize<Message>(new WelcomeMessage("p1", "tok", "https://games.example"), Options);
        var ready = JsonSerializer.Serialize<Message>(new ReadyMessage("p1", [], IsHost: true), Options);

        using var w = JsonDocument.Parse(welcome);
        using var r = JsonDocument.Parse(ready);
        Assert.Equal(KnockBoxProtocol.Version, w.RootElement.GetProperty("proto").GetInt32());
        Assert.Equal(KnockBoxProtocol.Version, r.RootElement.GetProperty("proto").GetInt32());
    }

    [Theory]
    [InlineData(typeof(AttachMessage))]
    [InlineData(typeof(ReadyMessage))]
    [InlineData(typeof(GameTicketMessage))]
    [InlineData(typeof(RequestGameTicketMessage))]
    [InlineData(typeof(GamePlayerJoinedMessage))]
    [InlineData(typeof(GamePlayerLeftMessage))]
    [InlineData(typeof(KickPlayerMessage))]
    [InlineData(typeof(KickedMessage))]
    public void Every_new_message_type_has_a_registered_discriminator(Type messageType)
    {
        // Constructing each is overkill; we only assert the polymorphism attribute knows the subtype,
        // which is what lets the server deserialize an incoming frame of that type at all.
        var derived = typeof(Message)
            .GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonDerivedTypeAttribute), false)
            .Cast<System.Text.Json.Serialization.JsonDerivedTypeAttribute>();

        Assert.Contains(derived, a => a.DerivedType == messageType);
    }
}
