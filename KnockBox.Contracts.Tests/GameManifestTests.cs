using System.Text.Json;
using KnockBox.Contracts;
using Xunit;

namespace KnockBox.Contracts.Tests;

public class GameManifestTests
{
    // Reflection-based, because the source-generated KnockBoxProtocolContext — which is what every
    // production read of a GAME.json actually uses — lives in KnockBox.Server and is not visible from
    // this project. The constructor defaults are therefore pinned against the generated context in
    // KnockBox.Server.Tests/GameManifestSourceGenTests.cs; what is asserted here is the record's shape,
    // not the path the server takes. Keep both when adding a field.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void CrossOriginIsolated_defaults_to_false_when_absent()
    {
        const string json = """
        { "id": "ttt", "name": "Tic-Tac-Toe", "entry": "index.html",
          "thumbnail": "thumb.svg", "minPlayers": 2, "maxPlayers": 2 }
        """;

        var manifest = JsonSerializer.Deserialize<GameManifest>(json, Options);

        Assert.NotNull(manifest);
        Assert.False(manifest!.CrossOriginIsolated);
        Assert.Equal("ttt", manifest.Id);
        Assert.Equal(2, manifest.MaxPlayers);
    }

    [Fact]
    public void CrossOriginIsolated_parses_from_camelCase()
    {
        const string json = """
        { "id": "godot3d", "name": "Threaded", "entry": "index.html",
          "thumbnail": null, "minPlayers": 1, "maxPlayers": 8, "crossOriginIsolated": true }
        """;

        var manifest = JsonSerializer.Deserialize<GameManifest>(json, Options);

        Assert.True(manifest!.CrossOriginIsolated);
    }

    [Fact]
    public void ServerAuthority_defaults_to_null_when_absent()
    {
        const string json = """
        { "id": "ttt", "name": "Tic-Tac-Toe", "entry": "index.html", "maxPlayers": 2 }
        """;

        var manifest = JsonSerializer.Deserialize<GameManifest>(json, Options);

        Assert.NotNull(manifest);
        Assert.Null(manifest!.ServerAuthority);
    }

    [Fact]
    public void ServerAuthority_parses_from_camelCase()
    {
        const string json = """
        { "id": "tictactoe-server", "name": "Tic-Tac-Toe (server)", "entry": "index.html",
          "maxPlayers": 2, "serverAuthority": "authority.js" }
        """;

        var manifest = JsonSerializer.Deserialize<GameManifest>(json, Options);

        Assert.Equal("authority.js", manifest!.ServerAuthority);
    }

    [Fact]
    public void MinPlayers_defaults_to_one_when_absent()
    {
        const string json = """
        { "id": "ttt", "name": "Tic-Tac-Toe", "entry": "index.html", "maxPlayers": 4 }
        """;

        var manifest = JsonSerializer.Deserialize<GameManifest>(json, Options);

        Assert.NotNull(manifest);
        Assert.Equal(1, manifest!.MinPlayers);
    }

    [Fact]
    public void Tags_and_MinPlayers_and_Dates_parse_correctly()
    {
        const string json = """
        {
          "id": "alpha-chain",
          "name": "Alpha Chain",
          "entry": "index.html",
          "minPlayers": 2,
          "maxPlayers": 8,
          "tags": ["word-game", "party"],
          "description": "A fun word game",
          "createdAt": "2026-01-15T10:00:00Z",
          "updatedAt": "2026-02-20T12:30:00Z"
        }
        """;

        var manifest = JsonSerializer.Deserialize<GameManifest>(json, Options);

        Assert.NotNull(manifest);
        Assert.Equal(2, manifest!.MinPlayers);
        Assert.Equal(8, manifest.MaxPlayers);
        Assert.Equal(["word-game", "party"], manifest.Tags);
        Assert.Equal("A fun word game", manifest.Description);
        Assert.Equal(DateTimeOffset.Parse("2026-01-15T10:00:00Z"), manifest.CreatedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-02-20T12:30:00Z"), manifest.UpdatedAt);
    }
}
