using KnockBox.Contracts;
using KnockBox.Server.Games;
using System.Text.Json.Serialization;
using static KnockBox.Server.Security.TokenService;

namespace KnockBox.Server.Serialization;

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IMessage))]
[JsonSerializable(typeof(GameManifest))]
// GameManifest.AuthorityWords: a nested dictionary of records is not auto-covered by source-gen the
// way the flat ServerAuthority string is, so register both explicitly.
[JsonSerializable(typeof(AuthorityWordDeclaration))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, AuthorityWordDeclaration>))]
[JsonSerializable(typeof(TicketPayload))]
[JsonSerializable(typeof(IdentityPayload))]
// The roster projection handed to an authority module's init(players) (ServerAuthorityManager).
[JsonSerializable(typeof(IReadOnlyList<Player>))]
// Not a wire type, but it goes through the same source-generated serializer for the same reason:
// reflection-based JSON is not Native-AOT-safe. See docs/KBG_FORMAT.md.
[JsonSerializable(typeof(GamePackageHeader))]
public partial class KnockBoxProtocolContext : JsonSerializerContext { }
