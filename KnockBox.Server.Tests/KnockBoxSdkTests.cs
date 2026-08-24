using KnockBox.Server.Hosting;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// The rule behind the admin portal's SDK column. The distinction that matters most here is
/// <c>unknown</c> vs <c>behind</c>: most games on a typical server carry no SDK stamp at all, and
/// reporting those as out of date would flag nearly everything and make the column worthless.
/// </summary>
public class KnockBoxSdkTests
{
    private static Dictionary<string, string> Sdk(params (string Id, string Version)[] entries) =>
        entries.ToDictionary(e => e.Id, e => e.Version);

    [Fact]
    public void No_stamp_is_unknown_not_behind()
    {
        Assert.Equal(KnockBoxSdk.Unknown, KnockBoxSdk.StatusOf(null));
        Assert.Equal(KnockBoxSdk.Unknown, KnockBoxSdk.StatusOf(new Dictionary<string, string>()));
    }

    [Fact]
    public void A_stamp_matching_this_server_is_current()
    {
        Assert.Equal(KnockBoxSdk.Uptodate, KnockBoxSdk.StatusOf(Sdk(("godot", KnockBoxSdk.VersionString))));
    }

    [Fact]
    public void An_older_stamp_is_behind_and_a_newer_one_is_ahead()
    {
        Assert.Equal(KnockBoxSdk.Behind, KnockBoxSdk.StatusOf(Sdk(("godot", "0.1.0"))));
        Assert.Equal(KnockBoxSdk.Ahead, KnockBoxSdk.StatusOf(Sdk(("godot", "99.0.0"))));
    }

    [Fact]
    public void An_unparseable_version_contributes_nothing_rather_than_counting_as_behind()
    {
        // A game may label its addons however it likes. Treating "dev" as old would invent a problem
        // the operator cannot act on.
        Assert.Equal(KnockBoxSdk.Unknown, KnockBoxSdk.StatusOf(Sdk(("godot", "dev"))));
        Assert.Equal(KnockBoxSdk.Unknown, KnockBoxSdk.StatusOf(Sdk(("godot", ""))));
        // ...but it must not mask a real answer from a sibling entry.
        Assert.Equal(KnockBoxSdk.Behind, KnockBoxSdk.StatusOf(Sdk(("godot", "dev"), ("phaser", "0.1.0"))));
    }

    [Fact]
    public void Behind_outranks_ahead_when_a_game_stamps_several_addons()
    {
        // Behind is the actionable one (update the game). Ahead only says the game was built against
        // a newer SDK than this server shipped, which the wire-version gate handles at connect time.
        Assert.Equal(KnockBoxSdk.Behind, KnockBoxSdk.StatusOf(Sdk(("godot", "0.1.0"), ("phaser", "99.0.0"))));
    }

    [Fact]
    public void Version_ordering_is_semver_not_string_comparison()
    {
        // Guards the reason SemVer is used at all: "0.9.0" > "0.10.0" as strings, and a prerelease
        // sorts BELOW its release. Both inversions would mislabel a game.
        Assert.Equal(KnockBoxSdk.Behind, KnockBoxSdk.StatusOf(Sdk(("a", "0.9.0"))));
        Assert.Equal(KnockBoxSdk.Behind, KnockBoxSdk.StatusOf(Sdk(("a", "0.10.0"))));
        Assert.Equal(KnockBoxSdk.Behind, KnockBoxSdk.StatusOf(Sdk(("a", $"{KnockBoxSdk.VersionString}-rc.1"))));
    }
}
