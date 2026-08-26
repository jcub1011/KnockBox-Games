using System.Text.Json;
using System.Text.RegularExpressions;
using KnockBox.Server.Hosting;
using KnockBox.Server.Marketplace;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// Guards the invariant that makes addon distribution possible: <c>clients/addons.manifest.json</c>
/// is THE version number, and every file that declares one agrees with it.
/// </summary>
/// <remarks>
/// Before the manifest there were five hand-maintained copies of "the SDK version" — the Godot
/// addon's plugin.cfg, three package.json files, and a duplicated constant inside pack-game.mjs —
/// and they had already drifted to three different values (0.1.0, 0.2.0, 0.1.0) for artifacts the
/// developer guide claimed were "versioned with web/knockbox.js". A published release built from
/// disagreeing numbers is worse than no release: the archive, the index entry and the file the
/// consumer ends up reading would each claim something different.
///
/// Like <see cref="OriginPortBindingTests"/>, these assertions are about the repo FILES rather than
/// about a running host, and they no-op outside a checkout — the CI job that matters
/// (<c>dotnet</c>) always has one.
/// </remarks>
public class AddonManifestTests
{
    private const string ManifestPath = "clients/addons.manifest.json";

    /// <summary>The generated index — committed, and served from raw.githubusercontent as the trust root.</summary>
    private const string IndexPath = ".addons/ADDONS.json";

    /// <summary>Mirrors DEV_VERSION in tools/pack-game/addon.mjs.</summary>
    private const string DevVersion = "0.0.0-dev";

    private static JsonElement? Manifest()
    {
        var text = RepoFile.Read(ManifestPath);
        if (text is null) return null;
        return JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }).RootElement;
    }

    private static string SdkVersion(JsonElement manifest) => manifest.GetProperty("sdkVersion").GetString()!;

    /// <summary>Every (file, declared version) pair the manifest points at, addons plus the CLI.</summary>
    private static IEnumerable<(string File, string? Declared)> DeclaredVersions(JsonElement manifest)
    {
        foreach (var addon in manifest.GetProperty("addons").EnumerateObject())
            foreach (var file in addon.Value.GetProperty("versionFiles").EnumerateArray())
                yield return (file.GetString()!, VersionIn(file.GetString()!));

        foreach (var file in manifest.GetProperty("cli").GetProperty("versionFiles").EnumerateArray())
            yield return (file.GetString()!, VersionIn(file.GetString()!));
    }

    /// <summary>
    /// Pulls the declared version out of a version file. Two formats only — a package.json
    /// <c>"version"</c> field and a Godot <c>plugin.cfg</c> <c>version="…"</c> line — so an
    /// unrecognised file is a hard failure rather than a silent pass; a version file this test
    /// cannot read is a version file it cannot guard.
    /// </summary>
    private static string? VersionIn(string relativePath)
    {
        var text = RepoFile.Read(relativePath);
        if (text is null) return null;

        if (relativePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return JsonDocument.Parse(text).RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;

        if (relativePath.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase))
        {
            var match = Regex.Match(text, """^version\s*=\s*"([^"]*)"\s*$""", RegexOptions.Multiline);
            return match.Success ? match.Groups[1].Value : null;
        }

        throw new InvalidOperationException(
            $"AddonManifestTests does not know how to read a version out of '{relativePath}'. " +
            "Teach VersionIn its format — a version file this test cannot parse is one it silently stops guarding.");
    }

    [Fact]
    public void Manifest_declares_a_parseable_sdk_version_and_app_version_floor()
    {
        if (Manifest() is not { } manifest) return;

        Assert.True(SemVer.TryParse(SdkVersion(manifest), out _),
            $"{ManifestPath} sdkVersion '{SdkVersion(manifest)}' is not a valid semver. It is published in " +
            "ADDONS.json and compared with SemVer, so an unparseable value makes every update check indeterminate.");

        var min = manifest.GetProperty("minAppVersion").GetString();
        Assert.True(SemVer.TryParse(min, out _),
            $"{ManifestPath} minAppVersion '{min}' is not a valid semver. PluginUpdateEvaluator treats a bound " +
            "it cannot parse as INCOMPATIBLE, so this would make every addon look unrunnable on every server.");

        // maxAppVersion is optional (null = no ceiling), but a non-null one must still parse.
        var max = manifest.GetProperty("maxAppVersion");
        if (max.ValueKind is not JsonValueKind.Null)
            Assert.True(SemVer.TryParse(max.GetString(), out _),
                $"{ManifestPath} maxAppVersion '{max.GetString()}' is not a valid semver. Use null for no ceiling.");
    }

    /// <summary>
    /// The PUBLISHED index must not disagree with the manifest about a compatibility bound it copied
    /// from it — unless a version bump is staged, which is what makes the disagreement temporary.
    /// </summary>
    /// <remarks>
    /// <c>.addons/ADDONS.json</c> is generated by <c>tools/build-addons.mjs</c>, which stamps the
    /// manifest's <c>minAppVersion</c> into every record, and it is regenerated and committed by an
    /// <c>addons-v*</c> tag and by NOTHING else. So a manifest edit that lands without a release
    /// leaves the served index — the trust root every <c>knockbox addon</c> install and the Godot
    /// updater read — still advertising the old bound. That shipped once: lowering the platform
    /// <c>&lt;Version&gt;</c> to 0.1.0 and following it in the manifest left all three published
    /// records claiming <c>minAppVersion: 1.0.0</c>, i.e. incompatible with the very first release.
    ///
    /// The check is conditional on <c>sdkVersion</c> for a reason: a DIFFERING one means the bump is
    /// staged and the release that regenerates the index has not run yet, so the drift is the
    /// intended state and failing on it would trap every such PR. The same <c>sdkVersion</c> with a
    /// different <c>minAppVersion</c> is only ever the bug above.
    /// </remarks>
    [Fact]
    public void Published_index_agrees_with_the_manifest_or_a_bump_is_staged()
    {
        if (Manifest() is not { } manifest) return;

        var indexText = RepoFile.Read(IndexPath);
        if (indexText is null) return;
        var index = JsonDocument.Parse(indexText).RootElement;

        // A bump not yet released — the tag regenerates the index, so nothing to compare.
        if (index.GetProperty("sdkVersion").GetString() != SdkVersion(manifest)) return;

        var expected = manifest.GetProperty("minAppVersion").GetString();
        foreach (var addon in index.GetProperty("addons").EnumerateObject())
        {
            var declared = addon.Value.TryGetProperty("minAppVersion", out var m) ? m.GetString() : null;
            Assert.True(declared == expected,
                $"{IndexPath} addon '{addon.Name}' declares minAppVersion '{declared}' but {ManifestPath} " +
                $"says '{expected}', and both claim sdkVersion '{SdkVersion(manifest)}'. The index is what " +
                "installs are judged against and it is only regenerated by an addons-v* tag — bump sdkVersion " +
                "and cut that release, or the published bound stays wrong. See docs/ADDONS.md.");
        }
    }

    [Fact]
    public void No_file_in_the_repo_declares_a_real_version()
    {
        if (Manifest() is not { } manifest) return;

        // The centralization invariant. Files that must carry a version for their own format's sake
        // (the Godot plugin.cfg, the CLI's package.json) hold the DEV SENTINEL in the repo and get the
        // real value stamped in at build time: tools/build-addons.mjs for the release archives, CI for
        // the npm publish. So a release bumps exactly one file, and a stale number cannot exist to
        // drift, because no committed file claims a real version at all.
        //
        // Asserting the sentinel rather than equality with sdkVersion is the whole point. An equality
        // check still permits six real numbers that all have to be edited together, which is precisely
        // the arrangement that kept going wrong.
        foreach (var (file, declared) in DeclaredVersions(manifest))
        {
            Assert.NotNull(declared);
            Assert.True(declared == DevVersion,
                $"{file} declares version '{declared}', but every in-repo version declaration must be " +
                $"the sentinel '{DevVersion}'. clients/addons.manifest.json's sdkVersion is the only " +
                "real version number; the build stamps it into the release archives and the npm " +
                "package. Putting a real version back here reintroduces a copy that can drift.");
        }
    }

    [Fact]
    public void The_server_reads_the_shipped_sdk_version_out_of_the_embedded_manifest()
    {
        if (Manifest() is not { } manifest) return;

        // KnockBoxSdk holds no version of its own: it reads the manifest embedded into the assembly by
        // the csproj. This asserts that plumbing works. If the EmbeddedResource item or its LogicalName
        // were wrong, VersionString would quietly read "unknown" and every game's badge would go blank
        // -- a failure that looks exactly like "nothing to report".
        Assert.True(SdkVersion(manifest) == KnockBoxSdk.VersionString,
            $"{ManifestPath} says sdkVersion '{SdkVersion(manifest)}' but KnockBoxSdk.VersionString is " +
            $"'{KnockBoxSdk.VersionString}'. The embedded manifest is the only source for this value -- " +
            "check the <EmbeddedResource> LogicalName in KnockBox.Server.csproj.");

        Assert.NotNull(KnockBoxSdk.Current);
        Assert.True(SemVer.TryParse(SdkVersion(manifest), out var expected)
                && expected.CompareTo(KnockBoxSdk.Current!.Value) == 0,
            "KnockBoxSdk.Current must equal the manifest's sdkVersion. Null means every game reports " +
            "'unknown' -- safe, but the portal column silently stops working.");
    }


    [Fact]
    public void Every_file_the_manifest_declares_exists()
    {
        if (Manifest() is not { } manifest) return;

        foreach (var addon in manifest.GetProperty("addons").EnumerateObject())
        {
            var root = addon.Value.GetProperty("root").GetString()!;
            Assert.True(RepoFile.Exists(root), $"addon '{addon.Name}' declares root '{root}', which does not exist.");

            foreach (var file in addon.Value.GetProperty("files").EnumerateArray())
            {
                var name = file.GetString()!;
                if (name == "**") continue;   // whole-directory addon; the root check above covers it
                var path = $"{root}/{name}";
                Assert.True(RepoFile.Exists(path),
                    $"addon '{addon.Name}' declares file '{name}', but {path} does not exist. " +
                    "CI builds the release archive from this list, so a stale entry ships a broken addon.");
            }

            if (addon.Value.TryGetProperty("docs", out var docs))
                foreach (var doc in docs.EnumerateArray())
                    Assert.True(RepoFile.Exists(doc.GetString()!),
                        $"addon '{addon.Name}' declares doc '{doc.GetString()}', which does not exist.");
        }

        var cli = manifest.GetProperty("cli").GetProperty("package").GetString()!;
        Assert.True(RepoFile.Exists(cli), $"cli.package '{cli}' does not exist.");
    }

    /// <summary>
    /// The vanilla addon ships knockbox.js + kb-protocol.js and nothing else, so the SDK must not
    /// reach outside that pair. It previously imported kb-core.js — 21 KB of shell UI (favicons,
    /// launch-overlay geometry, the play log, announcements) for the sake of 9 symbols — and an
    /// import added back there would be invisible here but fatal to a vendored install: the file
    /// simply would not be in the archive.
    /// </summary>
    [Fact]
    public void The_vanilla_sdk_only_imports_files_the_web_addon_ships()
    {
        if (Manifest() is not { } manifest) return;
        var web = manifest.GetProperty("addons").GetProperty("web");
        var root = web.GetProperty("root").GetString()!;
        var shipped = web.GetProperty("files").EnumerateArray().Select(f => f.GetString()!).ToHashSet();

        foreach (var file in shipped)
        {
            var source = RepoFile.Read($"{root}/{file}");
            if (source is null) continue;

            foreach (var match in Regex.Matches(source, """(?:from|import)\s*['"](\./[^'"]+)['"]""").Cast<Match>())
            {
                var target = match.Groups[1].Value[2..];
                Assert.True(shipped.Contains(target),
                    $"{root}/{file} imports './{target}', which the 'web' addon does not ship (it ships: " +
                    $"{string.Join(", ", shipped.Order())}). A vendored copy would fail at import time. " +
                    "Either add it to the manifest's file list or keep the import inside the shipped set.");
            }
        }
    }
}
