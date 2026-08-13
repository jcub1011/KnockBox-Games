using System.Text.Json;
using KnockBox.Contracts;
using Microsoft.Extensions.Logging;
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
        IMessage original = new HelloMessage(PlayerId: null, DisplayName: "Ann", Token: "tok");

        var json = JsonSerializer.Serialize(original, Options);
        var back = JsonSerializer.Deserialize<IMessage>(json, Options);

        var hello = Assert.IsType<HelloMessage>(back);
        Assert.Equal("Ann", hello.DisplayName);
        Assert.Equal("tok", hello.Token);
        Assert.Null(hello.PlayerId);
    }

    [Fact]
    public void Discriminator_is_camelCased_type_property()
    {
        var json = JsonSerializer.Serialize<IMessage>(new WelcomeMessage("p1", "tok", "https://games.example"), Options);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Welcome", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("p1", doc.RootElement.GetProperty("playerId").GetString());
        Assert.Equal("https://games.example", doc.RootElement.GetProperty("gameOrigin").GetString());
    }

    [Fact]
    public void Game_message_carries_an_opaque_payload_round_trip()
    {
        var payload = JsonSerializer.Deserialize<JsonElement>("""{ "kind": "move", "cell": 4 }""");
        IMessage original = new GameMessage(To: "host", Payload: payload, From: null);

        var json = JsonSerializer.Serialize(original, Options);
        var back = Assert.IsType<GameMessage>(JsonSerializer.Deserialize<IMessage>(json, Options));

        Assert.Equal("host", back.To);
        Assert.Equal(4, back.Payload.GetProperty("cell").GetInt32());
    }

    [Fact]
    public void KickPlayer_round_trips_with_type_first()
    {
        var json = JsonSerializer.Serialize<IMessage>(new KickPlayerMessage("p2"), Options);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("KickPlayer", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("p2", doc.RootElement.GetProperty("targetPlayerId").GetString());

        var back = Assert.IsType<KickPlayerMessage>(JsonSerializer.Deserialize<IMessage>(json, Options));
        Assert.Equal("p2", back.TargetPlayerId);
    }

    [Fact]
    public void Kicked_round_trips_with_type_first()
    {
        var json = JsonSerializer.Serialize<IMessage>(new KickedMessage("ABCD"), Options);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Kicked", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("ABCD", doc.RootElement.GetProperty("lobbyId").GetString());

        var back = Assert.IsType<KickedMessage>(JsonSerializer.Deserialize<IMessage>(json, Options));
        Assert.Equal("ABCD", back.LobbyId);
    }

    [Theory]
    [InlineData("PlayerDisconnected")]
    [InlineData("PlayerConnected")]
    public void Control_presence_messages_round_trip_with_lobby_and_player(string type)
    {
        IMessage original = type == "PlayerDisconnected"
            ? new PlayerDisconnectedMessage("ABCD", "p7")
            : new PlayerConnectedMessage("ABCD", "p7");

        var json = JsonSerializer.Serialize(original, Options);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(type, doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("ABCD", doc.RootElement.GetProperty("lobbyId").GetString());
        Assert.Equal("p7", doc.RootElement.GetProperty("playerId").GetString());

        var back = JsonSerializer.Deserialize<IMessage>(json, Options);
        var lobbyId = back switch
        {
            PlayerDisconnectedMessage d => d.LobbyId,
            PlayerConnectedMessage c => c.LobbyId,
            _ => null,
        };
        Assert.Equal("ABCD", lobbyId);
    }

    [Theory]
    [InlineData("GamePlayerDisconnected")]
    [InlineData("GamePlayerConnected")]
    public void Game_presence_messages_round_trip_with_player(string type)
    {
        IMessage original = type == "GamePlayerDisconnected"
            ? new GamePlayerDisconnectedMessage("p7")
            : new GamePlayerConnectedMessage("p7");

        var json = JsonSerializer.Serialize(original, Options);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(type, doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("p7", doc.RootElement.GetProperty("playerId").GetString());
    }

    [Fact]
    public void Log_message_serializes_its_level_as_a_name_round_trip()
    {
        IMessage original = new LogMessage(LogLevel.Warning, "something happened");

        var json = JsonSerializer.Serialize(original, Options);

        using (var doc = JsonDocument.Parse(json))
        {
            Assert.Equal("Log", doc.RootElement.GetProperty("type").GetString());
            // The level is the readable enum NAME on the wire (not the numeric 3), so a JS client can
            // send "Warning" rather than a magic number.
            Assert.Equal("Warning", doc.RootElement.GetProperty("level").GetString());
            Assert.Equal("something happened", doc.RootElement.GetProperty("message").GetString());
        }

        var back = Assert.IsType<LogMessage>(JsonSerializer.Deserialize<IMessage>(json, Options));
        Assert.Equal(LogLevel.Warning, back.Level);
        Assert.Equal("something happened", back.Message);
    }

    [Fact]
    public void Log_message_reads_a_level_name_case_insensitively()
    {
        // The JS clients send a level name; accept casing variations rather than rejecting the frame.
        var back = Assert.IsType<LogMessage>(JsonSerializer.Deserialize<IMessage>(
            """{ "type": "Log", "level": "error", "message": "boom" }""", Options));

        Assert.Equal(LogLevel.Error, back.Level);
        Assert.Equal("boom", back.Message);
    }

    [Fact]
    public void PlayLog_round_trips_with_metadata_and_server_stamped_context()
    {
        IMessage original = new PlayLogMessage(
            new Dictionary<string, string> { ["placement"] = "1", ["playerCount"] = "4" },
            GameId: "tic-tac-toe", Timestamp: DateTimeOffset.UnixEpoch, IsHost: true);

        var json = JsonSerializer.Serialize(original, Options);

        using (var doc = JsonDocument.Parse(json))
        {
            Assert.Equal("PlayLog", doc.RootElement.GetProperty("type").GetString());
            Assert.Equal("tic-tac-toe", doc.RootElement.GetProperty("gameId").GetString());
            Assert.True(doc.RootElement.GetProperty("isHost").GetBoolean());
            Assert.Equal("1", doc.RootElement.GetProperty("metadata").GetProperty("placement").GetString());
        }

        var back = Assert.IsType<PlayLogMessage>(JsonSerializer.Deserialize<IMessage>(json, Options));
        Assert.Equal("tic-tac-toe", back.GameId);
        Assert.Equal(DateTimeOffset.UnixEpoch, back.Timestamp);
        Assert.Equal(true, back.IsHost);
        Assert.Equal("4", back.Metadata["playerCount"]);
    }

    [Fact]
    public void PlayLog_from_the_game_omits_the_server_stamped_fields()
    {
        // The game supplies only metadata; gameId/timestamp/isHost are null until the server stamps them.
        var back = Assert.IsType<PlayLogMessage>(JsonSerializer.Deserialize<IMessage>(
            """{ "type": "PlayLog", "metadata": { "result": "win" } }""", Options));

        Assert.Null(back.GameId);
        Assert.Null(back.Timestamp);
        Assert.Null(back.IsHost);
        Assert.Equal("win", back.Metadata["result"]);
    }

    [Fact]
    public void Missing_proto_reads_as_zero_for_pre_versioning_clients()
    {
        var hello = Assert.IsType<HelloMessage>(JsonSerializer.Deserialize<IMessage>(
            """{ "type": "Hello", "displayName": "Ann" }""", Options));
        var attach = Assert.IsType<AttachMessage>(JsonSerializer.Deserialize<IMessage>(
            """{ "type": "Attach", "ticket": "t" }""", Options));

        Assert.Equal(0, hello.Proto);
        Assert.Equal(0, attach.Proto);
    }

    [Fact]
    public void Server_replies_echo_the_protocol_version()
    {
        var welcome = JsonSerializer.Serialize<IMessage>(new WelcomeMessage("p1", "tok", "https://games.example"), Options);
        var ready = JsonSerializer.Serialize<IMessage>(new ReadyMessage("p1", [], IsHost: true), Options);

        using var w = JsonDocument.Parse(welcome);
        using var r = JsonDocument.Parse(ready);
        Assert.Equal(KnockBoxProtocol.Version, w.RootElement.GetProperty("proto").GetInt32());
        Assert.Equal(KnockBoxProtocol.Version, r.RootElement.GetProperty("proto").GetInt32());
    }

    [Fact]
    public void Ready_carries_authority_and_owner_camelCased()
    {
        var json = JsonSerializer.Serialize<IMessage>(
            new ReadyMessage("p1", [], IsHost: false, Authority: "server", OwnerId: "p2"), Options);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("server", doc.RootElement.GetProperty("authority").GetString());
        Assert.Equal("p2", doc.RootElement.GetProperty("ownerId").GetString());
    }

    [Fact]
    public void Legacy_ready_without_authority_reads_with_host_defaults()
    {
        // A Ready from a pre-server-authority server: authority defaults to "host", ownerId to null
        // (clients derive the owner from isHost in that case).
        var ready = Assert.IsType<ReadyMessage>(JsonSerializer.Deserialize<IMessage>(
            """{ "type": "Ready", "playerId": "p1", "players": [], "isHost": true }""", Options));

        Assert.Equal("host", ready.Authority);
        Assert.Null(ready.OwnerId);
    }

    [Fact]
    public void LobbyClosed_round_trips()
    {
        var json = JsonSerializer.Serialize<IMessage>(new LobbyClosedMessage("AB12", "authority-failed"), Options);
        var back = Assert.IsType<LobbyClosedMessage>(JsonSerializer.Deserialize<IMessage>(json, Options));

        Assert.Equal("AB12", back.LobbyId);
        Assert.Equal("authority-failed", back.Reason);
    }

    [Fact]
    public void Owner_changed_messages_round_trip_on_both_planes()
    {
        var control = JsonSerializer.Serialize<IMessage>(new OwnerChangedMessage("AB12", "p2"), Options);
        var data = JsonSerializer.Serialize<IMessage>(new GameOwnerChangedMessage("p2"), Options);

        var c = Assert.IsType<OwnerChangedMessage>(JsonSerializer.Deserialize<IMessage>(control, Options));
        var d = Assert.IsType<GameOwnerChangedMessage>(JsonSerializer.Deserialize<IMessage>(data, Options));
        Assert.Equal(("AB12", "p2"), (c.LobbyId, c.OwnerId));
        Assert.Equal("p2", d.OwnerId);
    }

    [Fact]
    public void Announcement_messages_round_trip_with_their_optional_scope()
    {
        var posted = new AnnouncementPostedMessage(
            "a1", "Maintenance in 20 minutes.", DateTimeOffset.UnixEpoch, "warning", "word-rush");
        var json = JsonSerializer.Serialize<IMessage>(posted, Options);
        var back = Assert.IsType<AnnouncementPostedMessage>(JsonSerializer.Deserialize<IMessage>(json, Options));

        Assert.Equal(("a1", "Maintenance in 20 minutes.", "warning", "word-rush"),
            (back.Id, back.Text, back.Severity, back.GameId));

        // Platform-wide is the default shape: no game, info severity.
        var plain = JsonSerializer.Deserialize<IMessage>(
            """{ "type": "AnnouncementPosted", "id": "a2", "text": "Hi", "postedAt": "1970-01-01T00:00:00+00:00" }""",
            Options);
        var wide = Assert.IsType<AnnouncementPostedMessage>(plain);
        Assert.Null(wide.GameId);
        Assert.Equal("info", wide.Severity);

        var cleared = JsonSerializer.Serialize<IMessage>(new AnnouncementClearedMessage("a1"), Options);
        Assert.Equal("a1", Assert.IsType<AnnouncementClearedMessage>(
            JsonSerializer.Deserialize<IMessage>(cleared, Options)).Id);
    }

    [Theory]
    [InlineData(typeof(AttachMessage))]
    [InlineData(typeof(ReadyMessage))]
    [InlineData(typeof(LobbyClosedMessage))]
    [InlineData(typeof(OwnerChangedMessage))]
    [InlineData(typeof(GameOwnerChangedMessage))]
    [InlineData(typeof(TicketMessage))]
    [InlineData(typeof(RequestTicketMessage))]
    [InlineData(typeof(GamePlayerJoinedMessage))]
    [InlineData(typeof(GamePlayerLeftMessage))]
    [InlineData(typeof(GamePlayerDisconnectedMessage))]
    [InlineData(typeof(GamePlayerConnectedMessage))]
    [InlineData(typeof(PlayerDisconnectedMessage))]
    [InlineData(typeof(PlayerConnectedMessage))]
    [InlineData(typeof(KickPlayerMessage))]
    [InlineData(typeof(KickedMessage))]
    [InlineData(typeof(LogMessage))]
    [InlineData(typeof(PlayLogMessage))]
    [InlineData(typeof(AnnouncementPostedMessage))]
    [InlineData(typeof(AnnouncementClearedMessage))]
    public void Every_new_message_type_has_a_registered_discriminator(Type messageType)
    {
        // Constructing each is overkill; we only assert the polymorphism attribute knows the subtype,
        // which is what lets the server deserialize an incoming frame of that type at all.
        var derived = typeof(IMessage)
            .GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonDerivedTypeAttribute), false)
            .Cast<System.Text.Json.Serialization.JsonDerivedTypeAttribute>();

        Assert.Contains(derived, a => a.DerivedType == messageType);
    }
}
