using System.Text.Json;
using KnockBox.Server.Hosting;
using KnockBox.Server.Serialization;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// Guards the one rule <c>AdminApiModels.cs</c> cannot enforce for itself: every admin request and
/// response type must be registered in <see cref="KnockBoxProtocolContext"/>.
/// </summary>
/// <remarks>
/// Reflection-based JSON is not Native-AOT-safe, so <c>AdminApi.WriteJson</c> only ever serializes
/// through a source-generated <c>JsonTypeInfo</c>. Adding a record to <c>AdminApiModels.cs</c> and
/// forgetting the matching <c>[JsonSerializable]</c> attribute compiles, passes every other test, and
/// then fails in the <c>aot</c> CI job — or, worse, in a published binary. This test moves that failure
/// to the moment the record is added.
///
/// Reflection is fine here: the test project is JIT, and the types being enumerated are the very ones
/// whose reflection-free serialization is being asserted.
/// </remarks>
public class AdminApiSerializationTests
{
    /// <summary>Every <c>Admin*Request</c> / <c>Admin*Response</c> record the admin API declares.</summary>
    public static TheoryData<Type> AdminWireTypes()
    {
        var data = new TheoryData<Type>();
        foreach (var type in typeof(AdminGamesResponse).Assembly
                     .GetTypes()
                     .Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true }
                                 && t.Namespace == typeof(AdminGamesResponse).Namespace
                                 && t.Name.StartsWith("Admin", StringComparison.Ordinal)
                                 && (t.Name.EndsWith("Request", StringComparison.Ordinal)
                                     || t.Name.EndsWith("Response", StringComparison.Ordinal)))
                     .OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            data.Add(type);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(AdminWireTypes))]
    public void Every_admin_request_and_response_is_registered_in_the_source_generated_context(Type type)
    {
        Assert.True(
            KnockBoxProtocolContext.Default.GetTypeInfo(type) is not null,
            $"{type.Name} is an admin wire type but has no [JsonSerializable(typeof({type.Name}))] entry in " +
            "KnockBoxProtocolContext. Without one, serializing it falls back to reflection, which the " +
            "Native AOT publish rejects.");
    }

    [Fact]
    public void The_theory_actually_found_types()
    {
        // A silent zero here would make every assertion above vacuous — the exact way a reflection-driven
        // guard rots when a namespace or naming convention moves.
        Assert.True(AdminWireTypes().Count >= 10);
    }

    [Fact]
    public void Registered_responses_serialize_without_reflection()
    {
        // One representative round trip proving the registered metadata is usable, not merely present.
        var summary = new AdminGameSummary(
            "demo", "Demo", "1.0.0", "available", 4, false, "/games/demo", "games",
            PackageBacked: true, PackageRoot: "managed",
            DiskBytes: 10, DirectoryBytes: 4, CompressedBytes: 3, PackageBytes: 2, BackupBytes: 1,
            ActiveLobbies: 0, ActivePlayers: 0, Deletable: true, DeleteBlockedReason: null,
            Lifecycle: "ready", UpdatePolicy: "manual", PendingJobId: null);
        var response = new AdminGamesResponse(
            [summary], "/games", "/games-unpacked", null, "2026-01-01T00:00:00.0000000Z", 0, 0,
            "/games-managed", 0);

        var json = JsonSerializer.Serialize(response, KnockBoxProtocolContext.Default.AdminGamesResponse);
        var back = JsonSerializer.Deserialize(json, KnockBoxProtocolContext.Default.AdminGamesResponse);

        Assert.NotNull(back);
        Assert.Equal("managed", back.Games[0].PackageRoot);
        Assert.Equal(1, back.Games[0].BackupBytes);
        Assert.Equal("/games-managed", back.ManagedRoot);
        // camelCase on the wire, per the context's naming policy.
        Assert.Contains("\"packageRoot\":", json, StringComparison.Ordinal);
    }
}
