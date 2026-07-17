using KnockBox.Contracts;
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
public partial class KnockBoxProtocolContext : JsonSerializerContext { }
