using System.Text.Json;
using KnockBox.Server.Marketplace;

namespace KnockBox.Server.Hosting;

/// <summary>
/// The client-SDK version this server shipped with — the reference point for judging whether an
/// installed game was built against a current addon.
/// </summary>
/// <remarks>
/// Distinct from <see cref="KnockBoxVersion"/> on purpose. That is the server's own version, read off
/// the assembly; this is the version of the *client* addons (the Godot addon, the Phaser client, the
/// vanilla JS SDK) that shipped alongside it. The two move independently: an addon release does not
/// force a server release, or the reverse, and compatibility between them is expressed by the addons'
/// <c>minAppVersion</c>/<c>maxAppVersion</c> rather than by the numbers matching.
///
/// <para><b>Read from the embedded <c>clients/addons.manifest.json</c>, not declared here.</b> That
/// manifest holds the single authoritative <c>sdkVersion</c> for every addon and the CLI. A const in
/// this file would be a seventh copy of that number to reconcile by hand at release time — which is
/// exactly the drift the manifest exists to end (there were five copies before it, already
/// disagreeing three ways). Embedded rather than copied into the publish output so there is no file
/// to lose in a deployment and no path to resolve.</para>
///
/// <para>If the manifest cannot be read or parsed, <see cref="Current"/> is null and every game
/// reports <see cref="Unknown"/>. That is the safe direction: showing no badge is a smaller failure
/// than confidently showing the wrong one, and a fallback of <c>0.0.0</c> would label every stamped
/// game as <see cref="Ahead"/> at once.</para>
/// </remarks>
public static class KnockBoxSdk
{
    private const string ResourceName = "KnockBox.Server.addons.manifest.json";

    /// <summary>
    /// The <c>sdkVersion</c> from the embedded addon manifest, or <c>"unknown"</c> if it could not be
    /// read. Reported to the admin portal as the yardstick each game's SDK stamp is judged against.
    /// </summary>
    public static string VersionString { get; } = ReadSdkVersion() ?? "unknown";

    /// <summary>
    /// This server's shipped SDK version, or null when the manifest was unreadable or declared
    /// something unparseable. Null makes every comparison report <see cref="Unknown"/>.
    /// </summary>
    public static SemVer? Current { get; } =
        SemVer.TryParse(ReadSdkVersion(), out var parsed) ? parsed : null;

    private static string? ReadSdkVersion()
    {
        try
        {
            using var stream = typeof(KnockBoxSdk).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null) return null;

            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
            });
            return document.RootElement.TryGetProperty("sdkVersion", out var value)
                ? value.GetString()
                : null;
        }
        catch (Exception)
        {
            // A malformed manifest must not stop the server from starting: this value drives one
            // informational column in the portal, nothing on any player-facing path.
            return null;
        }
    }

    /// <summary>A game's SDK stamp judged against this server's: unknown/current/behind/ahead.</summary>
    public const string Unknown = "unknown";
    public const string Uptodate = "current";
    public const string Behind = "behind";
    public const string Ahead = "ahead";

    /// <summary>
    /// Classify a game's <c>GAME.json</c> <c>sdk</c> map against <see cref="Current"/>.
    /// </summary>
    /// <remarks>
    /// <b>unknown</b> is not a failure state and must stay distinct from <b>behind</b>: every
    /// hand-written game, and every package built before the stamp existed, has no entry here.
    /// Reporting those as out of date would flag most of a typical deployment and train an operator
    /// to ignore the column.
    ///
    /// With several addons stamped, the WORST answer wins, and <b>behind</b> outranks <b>ahead</b> —
    /// behind is the one an operator can act on (update the game), while ahead only means the game
    /// was built against a newer SDK than this server shipped, which the wire-version gate already
    /// handles at connect time if it actually matters.
    ///
    /// An unparseable version contributes nothing rather than counting as behind: a game is free to
    /// label its addons however it likes, and guessing would invent a problem.
    /// </remarks>
    public static string StatusOf(IReadOnlyDictionary<string, string>? sdk)
    {
        if (sdk is null || sdk.Count == 0) return Unknown;
        if (Current is not { } current) return Unknown;   // no yardstick — see the remarks above

        var sawBehind = false;
        var sawAhead = false;
        var sawAny = false;

        foreach (var declared in sdk.Values)
        {
            if (!SemVer.TryParse(declared, out var version)) continue;
            sawAny = true;
            var order = version.CompareTo(current);
            if (order < 0) sawBehind = true;
            else if (order > 0) sawAhead = true;
        }

        if (!sawAny) return Unknown;
        if (sawBehind) return Behind;
        return sawAhead ? Ahead : Uptodate;
    }
}
