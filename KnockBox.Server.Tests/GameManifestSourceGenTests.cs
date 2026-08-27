using System.Text.Json;
using KnockBox.Contracts;
using KnockBox.Server.Serialization;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// GAME.json defaults, read through the SOURCE-GENERATED context the server actually uses.
/// </summary>
/// <remarks>
/// <c>GameManifestTests</c> in <c>KnockBox.Contracts.Tests</c> covers the same manifest shape, but it
/// deserializes with a reflection-based <see cref="JsonSerializerOptions"/> — and that project cannot
/// see <see cref="KnockBoxProtocolContext"/>, which lives here. Every production read
/// (<c>GameCatalog.TryAddGame</c>, <c>GamePackageInstaller</c>, <c>PackageManager</c>) goes through the
/// generated context, so the constructor defaults have to be pinned against THAT.
///
/// The defaults are not cosmetic: if source-gen ever stopped honouring them, <c>MinPlayers</c> would be
/// 0 and every game in the catalog would match the shell's "1 Player" filter — a wrong answer that
/// looks entirely plausible, on a path the reflection-based test would keep reporting as green.
/// </remarks>
public class GameManifestSourceGenTests
{
    private static GameManifest Parse(string json) =>
        JsonSerializer.Deserialize(json, KnockBoxProtocolContext.Default.GameManifest)!;

    [Fact]
    public void MinPlayers_defaults_to_one_when_absent()
    {
        var manifest = Parse("""
        { "id": "ttt", "name": "Tic-Tac-Toe", "entry": "index.html", "maxPlayers": 4 }
        """);

        Assert.Equal(1, manifest.MinPlayers);
    }

    [Fact]
    public void The_optional_display_fields_default_to_null_when_absent()
    {
        var manifest = Parse("""
        { "id": "ttt", "name": "Tic-Tac-Toe", "entry": "index.html", "maxPlayers": 4 }
        """);

        // GameCatalog distinguishes "the author declared no date" from a declared one, so null has to
        // survive the round trip — a default-constructed DateTimeOffset would read as year 1.
        Assert.Null(manifest.CreatedAt);
        Assert.Null(manifest.UpdatedAt);
        Assert.Null(manifest.Tags);
        Assert.Null(manifest.Description);
    }

    [Fact]
    public void The_new_fields_parse_from_camelCase()
    {
        var manifest = Parse("""
        {
          "id": "alpha-chain", "name": "Alpha Chain", "entry": "index.html",
          "minPlayers": 2, "maxPlayers": 8,
          "tags": ["word-game", "party"],
          "description": "A fun word game",
          "createdAt": "2026-01-15T10:00:00Z",
          "updatedAt": "2026-02-20T12:30:00Z"
        }
        """);

        Assert.Equal(2, manifest.MinPlayers);
        Assert.Equal(["word-game", "party"], manifest.Tags);
        Assert.Equal("A fun word game", manifest.Description);
        Assert.Equal(DateTimeOffset.Parse("2026-01-15T10:00:00Z"), manifest.CreatedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-02-20T12:30:00Z"), manifest.UpdatedAt);
    }
}
