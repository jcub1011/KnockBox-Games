using System.Text.Json;
using KnockBox.Contracts;
using Xunit;

namespace KnockBox.Contracts.Tests;

public class GameManifestTests
{
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
}
