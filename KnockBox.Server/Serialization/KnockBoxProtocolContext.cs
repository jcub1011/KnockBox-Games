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
[JsonSerializable(typeof(TicketPayload))]
[JsonSerializable(typeof(IdentityPayload))]
// Not a wire type, but it goes through the same source-generated serializer for the same reason:
// reflection-based JSON is not Native-AOT-safe. See docs/KBG_FORMAT.md.
[JsonSerializable(typeof(GamePackageHeader))]
public partial class KnockBoxProtocolContext : JsonSerializerContext { }
