using KnockBox.Contracts;
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
public partial class KnockBoxProtocolContext : JsonSerializerContext { }
