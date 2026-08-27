using System.Text.Json;
using System.Text.Json.Serialization;

namespace KnockBox.Server.Marketplace;

/// <summary>
/// The marketplace catalog index — the deserialized shape of <c>.plugins/CATALOG.json</c>.
/// Normative schema: <c>schemas/marketplace.schema.json</c> in the marketplace repository.
/// </summary>
/// <remarks>
/// Every member is nullable and every collection may be absent. The catalog is fetched over the
/// network and is not something this server controls, so it is treated exactly like a <c>.kbg</c>
/// header (see <see cref="Games.GamePackageHeader"/>): deserialization must never throw on a
/// malformed document, and the *checking* happens afterwards in code that can produce an error
/// message naming what was wrong. A required-looking property modelled as non-nullable would turn a
/// typo in a published catalog into an unhandled exception at startup.
/// </remarks>
public sealed record MarketplaceCatalog(
    string? SchemaVersion,
    string? Name,
    string? Description,
    DateTimeOffset? LastUpdated,
    int Revision,
    IReadOnlyList<MarketplacePlugin>? Plugins)
{
    /// <summary>
    /// The highest catalog <c>schemaVersion</c> MAJOR this build understands. A catalog published
    /// with a newer major is refused rather than half-read, mirroring
    /// <see cref="Games.GamePackage.MaxFormatVersion"/>: within a major, added properties are
    /// ignored by the deserializer, which is what makes minor bumps backward compatible.
    /// </summary>
    public const int MaxSchemaVersionMajor = 1;
}

/// <summary>One game plugin's entry in the catalog.</summary>
/// <remarks>
/// Field order mirrors <c>marketplace.schema.json</c>'s <c>catalogPluginEntry</c> so the two can be
/// read side by side. Nothing constructs this type in C# — it exists only to be deserialized — which
/// is why a field can be inserted in schema order rather than appended to keep positional callers
/// compiling.
/// </remarks>
/// <param name="License">
/// SPDX identifier or expression, derived from the game's own <c>GAME.json</c> at publish time.
/// Display only; the server neither validates nor acts on it.
/// </param>
/// <param name="Homepage">
/// The game's own page or repository. Author-supplied and rendered as a link by the admin portal,
/// unlike the package download URL which is *derived* from repo/tag/asset and never carried — so the
/// schema constrains this to <c>https://</c> and the portal opens it with <c>rel="noopener
/// noreferrer"</c>. Treat it as untrusted text either way.
/// </param>
/// <param name="Bugs">Where to report problems with the game. Same provenance and caveats as <see cref="Homepage"/>.</param>
/// <param name="ContentRating">
/// The author's self-declaration of what the game contains (<c>"everyone"</c>, <c>"teen"</c>,
/// <c>"mature"</c>) — a platform label for browsing, not a legal classification. A string, not an
/// enum, for the reason every member here is nullable: an unrecognised value published tomorrow must
/// not make today's catalog unreadable. Absent means the author never declared one, which is
/// deliberately distinct from any value they could have chosen.
/// </param>
/// <param name="MinPlayers">
/// Player count the game is meant for, so the portal can show and filter the range before anything is
/// downloaded. Nullable rather than defaulted to 1: absent means the entry did not say, and the
/// installed manifest's own default is not this catalog's to assume.
/// </param>
/// <param name="MaxPlayers">Upper end of that range. Same nullability reasoning as <see cref="MinPlayers"/>.</param>
public sealed record MarketplacePlugin(
    string? Id,
    string? Name,
    string? Description,
    string? Version,
    MarketplaceAuthor? Author,
    DateTimeOffset? LastUpdated,
    string? MinAppVersion,
    string? MaxAppVersion,
    IReadOnlyList<string>? Tags,
    string? License,
    string? Homepage,
    string? Bugs,
    string? ContentRating,
    int? MinPlayers,
    int? MaxPlayers,
    MarketplaceSource? Source);

/// <summary>
/// Where a plugin's package can be obtained. Only <c>github-release</c> is supported; the schema
/// also allows a <c>local-path</c> source, which this server refuses by name rather than
/// half-handling — see <see cref="MarketplaceClient"/>.
/// </summary>
/// <param name="Sha256">
/// SHA-256 of the <c>.kbg</c>, recorded when the catalog entry was published. Required by the
/// schema and by this server: it is what ties the entry to a specific set of bytes. A GitHub
/// release asset can be deleted and re-uploaded in place, so the release alone is not evidence of
/// anything; the catalog's commit history is the trust root, and this hash is what that history
/// commits to.
/// </param>
/// <param name="Size">Optional size in bytes, used only for a pre-flight limit check.</param>
public sealed record MarketplaceSource(
    string? Type,
    string? Repo,
    string? Tag,
    string? Asset,
    string? Sha256,
    long? Size,
    string? Path);

/// <summary>
/// A plugin's author. The schema permits either a bare string or an object with
/// <c>name</c>/<c>email</c>, so both land here — see <see cref="MarketplaceAuthorConverter"/>.
/// </summary>
[JsonConverter(typeof(MarketplaceAuthorConverter))]
public sealed record MarketplaceAuthor(string? Name, string? Email);

/// <summary>
/// Reads <c>author</c> in either of its two published shapes: <c>"jcub1011"</c> or
/// <c>{ "name": "jcub1011", "email": "…" }</c>.
/// </summary>
/// <remarks>
/// Hand-written rather than reflection-driven because the server publishes Native AOT: a converter
/// that inspected types at runtime would be trimmed away or warn at publish. Unknown properties are
/// skipped so an author object gaining a field later does not break older servers.
/// </remarks>
public sealed class MarketplaceAuthorConverter : JsonConverter<MarketplaceAuthor>
{
    public override MarketplaceAuthor? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.String:
                return new MarketplaceAuthor(reader.GetString(), null);

            case JsonTokenType.StartObject:
                string? name = null, email = null;
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject) return new MarketplaceAuthor(name, email);
                    if (reader.TokenType != JsonTokenType.PropertyName) continue;

                    var property = reader.GetString();
                    if (!reader.Read()) break;

                    if (string.Equals(property, "name", StringComparison.OrdinalIgnoreCase))
                        name = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    else if (string.Equals(property, "email", StringComparison.OrdinalIgnoreCase))
                        email = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    else
                        reader.Skip();
                }
                throw new JsonException("Unterminated 'author' object in the marketplace catalog.");

            default:
                throw new JsonException($"'author' must be a string or an object, but was {reader.TokenType}.");
        }
    }

    public override void Write(Utf8JsonWriter writer, MarketplaceAuthor value, JsonSerializerOptions options)
    {
        // Always the object form: this server only ever writes an author back out for diagnostics,
        // and one shape keeps that output predictable.
        writer.WriteStartObject();
        writer.WriteString("name", value.Name);
        if (value.Email is not null) writer.WriteString("email", value.Email);
        writer.WriteEndObject();
    }
}
