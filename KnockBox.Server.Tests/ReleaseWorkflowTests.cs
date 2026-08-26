using System.Text.RegularExpressions;
using KnockBox.Server.Marketplace;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// Guards the invariants the manual release workflow (<c>.github/workflows/release.yml</c>) depends
/// on: the csproj <c>&lt;Version&gt;</c> is its ONLY input, and it is the ONLY path to a platform
/// release.
/// </summary>
/// <remarks>
/// The version is deliberately not a workflow input. It is read from the csproj because that is what
/// <see cref="Hosting.KnockBoxVersion"/> reports off the built assembly, and what marketplace
/// <c>minAppVersion</c> bounds are judged against — a hand-typed tag could disagree with the binary
/// it labels, which is the exact failure the <c>addons-v*</c> namespace split was created to fix.
/// The cost of that choice is a new dependency: a malformed <c>&lt;Version&gt;</c> now fails a
/// release run rather than a build. So it is asserted here, where it fails on a PR instead.
///
/// Like <see cref="OriginPortBindingTests"/> and <see cref="AddonManifestTests"/>, these assertions
/// are about the repo FILES rather than a running host, and they no-op outside a checkout — the CI
/// job that matters (<c>dotnet</c>) always has one.
/// </remarks>
public class ReleaseWorkflowTests
{
    private const string Csproj = "KnockBox.Server/KnockBox.Server.csproj";
    private const string CiWorkflow = ".github/workflows/ci.yml";
    private const string GateWorkflow = ".github/workflows/gate.yml";
    private const string ReleaseWorkflow = ".github/workflows/release.yml";

    /// <summary>
    /// The release tag is <c>v</c> + this value, so a version <see cref="SemVer"/> cannot parse is a
    /// release that either fails at the gate or — worse — ships and then reads as
    /// <c>Incompatible</c> to every marketplace entry with a version bound, since an unparseable
    /// bound counts as incompatible rather than as no bound.
    /// </summary>
    [Fact]
    public void CsprojVersion_IsRealSemver()
    {
        var xml = RepoFile.Read(Csproj);
        if (xml is null) return; // not run from a repo checkout — nothing to check

        var match = Regex.Match(xml, @"<Version>\s*([^<\s]+)\s*</Version>");
        Assert.True(match.Success, $"{Csproj} has no <Version> element; release.yml reads it as the release version.");

        var declared = match.Groups[1].Value;
        Assert.True(
            SemVer.TryParse(declared, out _),
            $"{Csproj} <Version> is '{declared}', which SemVer cannot parse. release.yml derives the " +
            "release tag from it, and Hosting/KnockBoxVersion.cs reports it as the running version.");

        // release.yml's own gate is a grep, and it is stricter than SemVer on one point: no build
        // metadata. Keep the two in agreement so the workflow can never reject a version this test
        // accepted — SemVer accepts `+sha` and then discards it (§10), which would leave the tag and
        // the reported version spelled differently.
        Assert.DoesNotContain('+', declared);
    }

    /// <summary>
    /// <c>v*</c> must NOT be a ci.yml push trigger. It used to be, and a hand-pushed tag was then a
    /// second path to a platform release — one that bypassed every guard release.yml adds
    /// (version-matches-assembly, tag-not-reused, :latest-never-moves-backwards, and the
    /// build-everything-before-pushing-anything gate). Two paths to one artifact drift, and the
    /// unguarded one wins by being easier to reach.
    /// </summary>
    [Fact]
    public void CiWorkflow_DoesNotTriggerOnPlatformTags()
    {
        var ci = RepoFile.Read(CiWorkflow);
        if (ci is null) return;

        var tags = Regex.Match(ci, @"^\s*tags:\s*(\[.*\])\s*$", RegexOptions.Multiline);
        Assert.True(tags.Success, $"{CiWorkflow} has no `tags:` list; this test can no longer guard it.");

        var list = tags.Groups[1].Value;
        Assert.Contains("addons-v*", list);
        // Anchored on the quote so `addons-v*` does not itself count as a match.
        Assert.DoesNotMatch(new Regex(@"['""]v\*['""]"), list);
    }

    /// <summary>
    /// "Only ever run manually" is asserted rather than assumed. A <c>push:</c> or
    /// <c>schedule:</c> trigger added here would publish a release as a side effect of landing a
    /// commit, which is the whole thing this workflow exists to prevent.
    /// </summary>
    [Fact]
    public void ReleaseWorkflow_IsManualOnly()
    {
        var release = RepoFile.Read(ReleaseWorkflow);
        if (release is null) return;

        // Scoped to the `on:` block — from `on:` to the next top-level key. Matching two-space keys
        // across the whole file would also collect every JOB name, so a job that happened to be
        // called `push` would fail this test for no reason, and the test would be checking something
        // other than what it claims to.
        var onBlock = Regex.Match(release, @"^on:$(?<body>(\n(  .*)?)*)", RegexOptions.Multiline);
        Assert.True(onBlock.Success, $"{ReleaseWorkflow} has no top-level `on:` block.");

        var triggers = Regex.Matches(onBlock.Groups["body"].Value, @"^  ([a-z_]+):", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.Contains("workflow_dispatch", triggers);
        Assert.DoesNotContain("push", triggers);
        Assert.DoesNotContain("pull_request", triggers);
        Assert.DoesNotContain("schedule", triggers);
    }

    /// <summary>
    /// Every mutation must live in one job that <c>needs:</c> every gate job — that <c>needs:</c>
    /// list IS the "if any build fails, upload nothing" guarantee, because a dependency that fails
    /// OR is skipped skips the dependent job. Dropping one from the list silently converts a gate
    /// into an advisory.
    /// </summary>
    [Fact]
    public void ReleaseJob_GatesOnEveryBuildAndTestJob()
    {
        var release = RepoFile.Read(ReleaseWorkflow);
        if (release is null) return;

        // The `release` job's needs: line — the last `needs:` in the file, and the only bracketed one.
        var needs = Regex.Match(release, @"^    needs:\s*\[([^\]]+)\]", RegexOptions.Multiline);
        Assert.True(needs.Success, $"{ReleaseWorkflow} has no bracketed `needs:` list on the release job.");

        var gated = needs.Groups[1].Value.Split(',').Select(s => s.Trim()).ToList();
        foreach (var job in new[] { "preflight", "gate", "assets-windows", "assets-linux", "assets-deploy" })
            Assert.Contains(job, gated);
    }

    /// <summary>
    /// The release must gate on <c>gate.yml</c>, never on <c>ci.yml</c>. Both would run the same
    /// suite, but a called workflow's jobs cannot request more <c>GITHUB_TOKEN</c> permission than the
    /// calling job holds — and that is a workflow <em>validation</em> error, raised even for jobs that
    /// will be skipped. Calling <c>ci.yml</c> therefore forces the release's gate job to grant
    /// <c>contents</c>/<c>packages</c>/<c>id-token</c> write purely to satisfy its <c>publish</c> and
    /// <c>addons</c> jobs, which never run during a release. <c>gate.yml</c> holds no publishing job,
    /// so there is nothing to grant.
    /// </summary>
    [Fact]
    public void ReleaseWorkflow_CallsTheSuite_NotTheCiWorkflow()
    {
        var release = RepoFile.Read(ReleaseWorkflow);
        if (release is null) return;

        Assert.Contains("uses: ./.github/workflows/gate.yml", release);
        Assert.DoesNotContain("uses: ./.github/workflows/ci.yml", release);

        // And the reason it can stay least-privilege: no `packages:`/`id-token:` grant anywhere in the
        // file except the one job that actually pushes and tags.
        var grants = Regex.Matches(release, @"^\s+(packages|id-token):\s*write", RegexOptions.Multiline);
        Assert.Single(grants);              // `release`'s own `packages: write`
        Assert.Contains("packages", grants[0].Value);
    }

    /// <summary>
    /// <c>gate.yml</c> must contain no job that writes anything — it is the ceiling both callers rely
    /// on, and a publishing job added there would either fail the release's validation or force the
    /// permission union back into existence. The <c>addons</c> job in particular must NOT move here:
    /// npm's trusted publisher names the publishing workflow by filename, and both OIDC claims it can
    /// be matched on resolve to the file the job is defined in.
    /// </summary>
    [Fact]
    public void GateWorkflow_IsReadOnlyAndCallableOnly()
    {
        var gate = RepoFile.Read(GateWorkflow);
        if (gate is null) return;

        // Scoped to the `on:` block, for the same reason as ReleaseWorkflow_IsManualOnly: matching
        // two-space keys across the file would also collect every job name.
        var onBlock = Regex.Match(gate, @"^on:$(?<body>(\n(  .*)?)*)", RegexOptions.Multiline);
        Assert.True(onBlock.Success, $"{GateWorkflow} has no top-level `on:` block.");

        var triggers = Regex.Matches(onBlock.Groups["body"].Value, @"^  ([a-z_]+):", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToList();
        Assert.Equal(["workflow_call"], triggers);

        // The substantive check is the permission grants, not a search for `npm publish` — a prose
        // mention of it in a comment is not a publishing step, and the token grants are what actually
        // decide whether a job here could write anything.
        Assert.DoesNotMatch(new Regex(@"^\s+(packages|id-token):\s*write", RegexOptions.Multiline), gate);
        Assert.DoesNotMatch(new Regex(@"^\s+contents:\s*write", RegexOptions.Multiline), gate);
    }
}
