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

    [Theory]
    [InlineData(typeof(AttachMessage))]
    [InlineData(typeof(ReadyMessage))]
    [InlineData(typeof(GameTicketMessage))]
    [InlineData(typeof(RequestGameTicketMessage))]
    [InlineData(typeof(GamePlayerJoinedMessage))]
    [InlineData(typeof(GamePlayerLeftMessage))]
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
