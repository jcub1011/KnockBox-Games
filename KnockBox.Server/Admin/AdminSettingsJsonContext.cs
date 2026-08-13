using System.Text.Json.Serialization;

namespace KnockBox.Server.Admin;

/// <summary>
/// Source-generated serializer for the admin settings file. Separate from
/// <c>KnockBoxProtocolContext</c> for one reason: this file is written for a human to open and
/// hand-edit (it is also how you reset policy without the portal), so it is indented, whereas every
/// wire message deliberately isn't.
/// </summary>
/// <remarks>
/// Source-generated rather than reflection-based for the same reason as everything else that
/// serializes here — <c>PublishAot</c> is on and the <c>aot</c> CI job treats trim warnings as errors.
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AdminSettings))]
// Nested in AdminSettings, but a list of records needs its own entry the same way
// GameManifest.AuthorityWords does — source generation covers a registered type's members, not
// arbitrary collection element types reached through them.
[JsonSerializable(typeof(RegisteredMarketplace))]
[JsonSerializable(typeof(IReadOnlyList<RegisteredMarketplace>))]
public partial class AdminSettingsJsonContext : JsonSerializerContext { }
