using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KnockBox.Contracts;
using KnockBox.Server.Games.Words;
using KnockBox.Server.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

namespace KnockBox.Server.Games;

/// <summary>
/// <c>KnockBox.Server --authority-bench &lt;game-dir&gt;</c> — runs a game's authority module under the
/// REAL runtime and reports how close each export gets to its per-call budget, then exits.
///
/// <para><b>Why this exists.</b> A server-authority module is written and tested in a browser, where it
/// is JIT-compiled JavaScript over an in-memory dictionary. On the server it is interpreted, and every
/// <c>kb.words</c> query crosses into the host. The gap is one to two orders of magnitude, and nothing
/// in a developer's loop reveals it — the first sign that a module does not fit its 250 ms call budget
/// used to be a lobby closing on real players, mid-match. This turns that into something a developer
/// can run before shipping, and CI can run for them: same Jint engine, same constraints, same word
/// pools, and a non-zero exit code when a call blows the budget.</para>
///
/// <para>It measures ticks by default because the tick is the export that stalls: it runs on a timer,
/// it is where phase transitions and turn setup land, and it is the one call a developer never
/// explicitly triggers. Pass <c>--script</c> to drive intents as well, which is what it takes to reach
/// the interesting states (most games do nothing until someone starts a match).</para>
/// </summary>
internal static class AuthorityBench
{
    private const string Flag = "--authority-bench";

    /// <summary>Runs the bench when <paramref name="args"/> asks for it. Returns false for a normal
    /// server start, so the caller can fall through to building the web host.</summary>
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        var at = Array.IndexOf(args, Flag);
        if (at < 0) return false;

        try
        {
            exitCode = Run(new Options(args, at));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"authority-bench: {ex.Message}");
            exitCode = 2;
        }
        return true;
    }

    private sealed class Options
    {
        public readonly string GameDir;
        public readonly int Players;
        public readonly int Ticks;
        public readonly double TickMs;
        public readonly string? ScriptPath;
        public readonly double BudgetMs;

        public Options(string[] args, int flagIndex)
        {
            GameDir = Path.GetFullPath(Value(args, flagIndex + 1)
                ?? throw new ArgumentException($"usage: {Flag} <game-dir> [--players N] [--ticks N] "
                    + "[--tick-ms MS] [--budget-ms MS] [--script FILE]"));
            Players = Int(args, "--players", 2);
            Ticks = Int(args, "--ticks", 1200);
            TickMs = Dbl(args, "--tick-ms", 50);
            BudgetMs = Dbl(args, "--budget-ms", 250);
            ScriptPath = Named(args, "--script");
        }

        private static string? Value(string[] args, int i) =>
            i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal) ? args[i] : null;

        private static string? Named(string[] args, string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 ? Value(args, i + 1) : null;
        }

        private static int Int(string[] args, string name, int fallback) =>
            int.TryParse(Named(args, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                ? v : fallback;

        private static double Dbl(string[] args, string name, double fallback) =>
            double.TryParse(Named(args, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? v : fallback;
    }

    /// <summary>Every call of one export, so the report can separate a slow tick from a slow intent.</summary>
    private sealed class Timings(string export)
    {
        public string Export { get; } = export;
        public List<double> Ms { get; } = [];
        public long Crossings;

        public double Percentile(double q)
        {
            var sorted = Ms.Order().ToList();
            return sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * q))];
        }
    }

    /// <summary>Counts every kb.words query so the report can say whether the cost is boundary
    /// crossings or interpreted work — the two have very different fixes.</summary>
    private sealed class CountingPool(IWordPool inner) : IWordPool
    {
        public long Queries;
        public int TotalWordCount { get { Queries++; return inner.TotalWordCount; } }
        public IReadOnlyList<int> AvailableLengths => inner.AvailableLengths;
        public int GetWordCount(int length) { Queries++; return inner.GetWordCount(length); }
        public ReadOnlySpan<byte> GetWord(int length, int index) { Queries++; return inner.GetWord(length, index); }
        public ReadOnlySpan<byte> GetWord(int globalIndex) { Queries++; return inner.GetWord(globalIndex); }
        public bool Contains(ReadOnlySpan<char> word) { Queries++; return inner.Contains(word); }
        public (int Start, int End) RangeOfPrefix(int length, ReadOnlySpan<char> prefix)
        {
            Queries++;
            return inner.RangeOfPrefix(length, prefix);
        }
    }

    private static int Run(Options o)
    {
        var manifestPath = Path.Combine(o.GameDir, "GAME.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"No GAME.json in {o.GameDir}.", manifestPath);

        var manifest = JsonSerializer.Deserialize(File.ReadAllBytes(manifestPath),
                           KnockBoxProtocolContext.Default.GameManifest)
                       ?? throw new InvalidOperationException("GAME.json could not be read.");
        if (string.IsNullOrWhiteSpace(manifest.ServerAuthority))
            throw new InvalidOperationException($"{manifest.Id} declares no serverAuthority module.");

        var modulePath = Path.GetFullPath(Path.Combine(o.GameDir, manifest.ServerAuthority));
        if (!File.Exists(modulePath)) throw new FileNotFoundException("Authority module not found.", modulePath);

        // The real word service, so pool ordering and case folding are the server's, not a stub's.
        var wordService = new AuthorityWordService(NullLogger<AuthorityWordService>.Instance);
        var pools = new Dictionary<string, IWordPool>(StringComparer.Ordinal);
        var counters = new List<CountingPool>();
        foreach (var (key, decl) in manifest.AuthorityWords ?? new Dictionary<string, AuthorityWordDeclaration>())
        {
            var path = Path.GetFullPath(Path.Combine(o.GameDir, decl.File));
            wordService.Load(manifest.Id, key, path, decl.CaseInsensitive);
            var counting = new CountingPool(wordService.Get(manifest.Id, key)!);
            counters.Add(counting);
            pools[key] = counting;
        }

        // Production defaults, overridden only by --budget-ms. Reading them from configuration rather
        // than hand-writing them is what keeps the bench honest as the constraint set changes.
        var options = AuthorityOptions.FromConfiguration(new EmptyConfiguration())
            with { CallTimeout = TimeSpan.FromMilliseconds(o.BudgetMs) };

        Console.WriteLine($"module   {modulePath} ({new FileInfo(modulePath).Length:N0} bytes)");
        Console.WriteLine($"budget   {o.BudgetMs:N0} ms per call   "
            + $"(maxStatements={options.MaxStatements}, recursionLimit={options.RecursionLimit})");
        foreach (var (key, pool) in pools)
            Console.WriteLine($"words    {key}: {pool.TotalWordCount:N0}");

        // Warm the interpreter on a throwaway module first. Jint's own code paths are tiered-JIT'd like
        // any other .NET, and in a cold process that warm-up lands on whatever runs first — which here is
        // the module load, making a perfectly healthy game look like it blows its budget on load. The
        // server never sees this shape (its process has been serving HTTP for a while, and the prepared
        // module is shared across lobbies), so charging it to the game under test would be a lie.
        Warm(options);

        var loadSw = Stopwatch.StartNew();
        var runtime = new JsAuthorityRuntime(modulePath, new AuthorityModuleCache(TimeProvider.System),
            options, TimeProvider.System, pools, manifest.Id);

        var players = Enumerable.Range(0, Math.Max(1, o.Players))
            .Select(i => new Player($"p{i}", $"Player {i + 1}")).ToList();

        var byExport = new Dictionary<string, Timings>(StringComparer.Ordinal);
        var sw = new Stopwatch();
        long QueryTotal() => counters.Sum(c => c.Queries);

        void Call(string export, params string[] jsonArgs)
        {
            if (!byExport.TryGetValue(export, out var t)) byExport[export] = t = new Timings(export);
            var before = QueryTotal();
            sw.Restart();
            runtime.Invoke(export, jsonArgs);
            sw.Stop();
            t.Ms.Add(sw.Elapsed.TotalMilliseconds);
            t.Crossings += QueryTotal() - before;
        }

        // The same roster shape the manager passes, through the same source-generated context, so the
        // module sees exactly what a real lobby would hand it.
        runtime.Initialize(JsonSerializer.Serialize<IReadOnlyList<Player>>(
            players, KnockBoxProtocolContext.Default.IReadOnlyListPlayer));
        loadSw.Stop();

        // Reported separately from the per-call table because it is a different kind of cost: parsing and
        // evaluating the module plus init, paid once per lobby, and shared across lobbies of the same game.
        // It is still bounded by the same per-call budget, so a module large enough to overrun it here
        // cannot start a lobby at all.
        var loadMs = loadSw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"load     {loadMs:F1} ms ({loadMs * 100 / o.BudgetMs:F0}% of budget)");
        if (loadMs > o.BudgetMs / 2)
            Console.WriteLine("WARN     module load is over half the per-call budget. Load is bounded by the "
                + "same budget as a tick, so a module much larger than this cannot start a lobby at all — "
                + "and the first lobby after a restart is the slowest, before anything is warm.");

        var overran = false;
        try
        {
            foreach (var step in Script(o, players[0].Id))
            {
                if (step.Intent is { } intent) Call("applyIntent", Quote(step.From ?? players[0].Id), intent);
                for (var i = 0; i < step.Ticks; i++)
                    Call("tick", o.TickMs.ToString(CultureInfo.InvariantCulture));
            }
        }
        catch (AuthorityConstraintException ex)
        {
            overran = true;
            Console.Error.WriteLine();
            Console.Error.WriteLine($"FATAL  {ex.Message}");
            Console.Error.WriteLine(
                "       On a live server this closes the lobby and disconnects everyone in it.");
        }
        finally
        {
            runtime.Dispose();
        }

        return Report(byExport, o.BudgetMs, overran);
    }

    /// <summary>Runs a trivial module through a throwaway engine so the measurements that follow are of
    /// the game, not of .NET's tiered JIT warming up Jint.</summary>
    private static void Warm(AuthorityOptions options)
    {
        var dir = Directory.CreateTempSubdirectory("kb-bench-warm");
        try
        {
            var path = Path.Combine(dir.FullName, "warm.js");
            File.WriteAllText(path, "export function createAuthority(kb) { return { init() {}, "
                + "applyIntent() { return null; }, snapshot() { return {}; }, tick() { return null; } }; }");
            using var warm = new JsAuthorityRuntime(path, new AuthorityModuleCache(TimeProvider.System),
                options, TimeProvider.System, new Dictionary<string, IWordPool>(), "warm");
            warm.Initialize("[]");
            for (var i = 0; i < 50; i++) warm.Invoke("tick", "50");
        }
        catch (Exception)
        {
            // A warm-up that fails is not a result. Whatever is wrong will resurface on the real module,
            // where it can be reported against the game rather than against a synthetic stand-in.
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>The steps to run: the script file if given, else "tick until the count runs out".</summary>
    private static IEnumerable<Step> Script(Options o, string defaultFrom)
    {
        if (o.ScriptPath is null)
        {
            yield return new Step(null, null, o.Ticks);
            yield break;
        }

        using var doc = JsonDocument.Parse(File.ReadAllBytes(o.ScriptPath));
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            var intent = element.TryGetProperty("intent", out var i) ? i.GetRawText() : null;
            var from = element.TryGetProperty("from", out var f) ? f.GetString() : defaultFrom;
            var ticks = element.TryGetProperty("ticks", out var t) ? t.GetInt32() : 0;
            yield return new Step(intent, from, ticks);
        }
    }

    private readonly record struct Step(string? Intent, string? From, int Ticks);

    private static string Quote(string value) => JsonSerializer.Serialize(value, BenchJson.Default.String);

    private static int Report(Dictionary<string, Timings> byExport, double budgetMs, bool overran)
    {
        Console.WriteLine();
        if (byExport.Count == 0 || byExport.Values.All(t => t.Ms.Count == 0))
        {
            Console.WriteLine("No calls were measured.");
            return overran ? 1 : 0;
        }
        Console.WriteLine($"{"export",-14}{"calls",8}{"p50",9}{"p90",9}{"p99",9}{"max",9}{"% budget",10}{"queries",12}");
        var worst = 0d;
        foreach (var t in byExport.Values.OrderByDescending(t => t.Ms.Count == 0 ? 0 : t.Ms.Max()))
        {
            if (t.Ms.Count == 0) continue;
            var max = t.Ms.Max();
            worst = Math.Max(worst, max);
            Console.WriteLine($"{t.Export,-14}{t.Ms.Count,8}{t.Percentile(0.5),8:F1}{"",1}"
                + $"{t.Percentile(0.9),8:F1}{"",1}{t.Percentile(0.99),8:F1}{"",1}{max,8:F1}"
                + $"{max * 100 / budgetMs,9:F0}%{t.Crossings,12:N0}");
        }

        Console.WriteLine();
        if (overran)
        {
            Console.WriteLine($"FAIL   a call exceeded the {budgetMs:N0} ms budget.");
            return 1;
        }
        var headroom = budgetMs / Math.Max(worst, 0.001);
        Console.WriteLine($"worst call {worst:F1} ms of a {budgetMs:N0} ms budget — {headroom:F1}x headroom.");
        if (byExport.Values.Sum(t => t.Crossings) == 0)
        {
            // Almost always means the module is sitting in a lobby state doing nothing, so the run proved
            // nothing about the calls that matter. Say so rather than reporting a reassuring 0.0 ms.
            Console.WriteLine("NOTE   the module made no dictionary queries. Most games idle until an "
                + "intent starts a match — drive one with --script to measure the calls that actually work.");
        }
        if (headroom < 2)
        {
            // Not a failure, but the number that should stop a release. Real hosts are busier than a
            // developer's machine, a GC pause counts against the budget, and the worst turn a player
            // reaches is rarely the worst turn a bench happened to generate.
            Console.WriteLine("WARN   under 2x headroom. A busier host, a GC pause, or an unluckier match "
                + "will close lobbies. Reduce the work in the worst call.");
            return 3;
        }
        return 0;
    }

    /// <summary>An empty <see cref="IConfiguration"/>, so the bench reads the same DEFAULTS the server
    /// does through the same <see cref="AuthorityOptions.FromConfiguration"/> rather than a hand-written
    /// copy that can drift from it — the drift would make the bench pass a module the server refuses.</summary>
    private sealed class EmptyConfiguration : IConfiguration
    {
        public string? this[string key] { get => null; set { } }
        public IEnumerable<IConfigurationSection> GetChildren() => [];
        public IChangeToken GetReloadToken() => new CancellationChangeToken(CancellationToken.None);
        public IConfigurationSection GetSection(string key) => new Section(key);

        private sealed class Section(string path) : IConfigurationSection
        {
            public string? this[string key] { get => null; set { } }
            public string Key => path;
            public string Path => path;
            public string? Value { get => null; set { } }
            public IEnumerable<IConfigurationSection> GetChildren() => [];
            public IChangeToken GetReloadToken() => new CancellationChangeToken(CancellationToken.None);
            public IConfigurationSection GetSection(string key) => new Section($"{path}:{key}");
        }
    }
}

/// <summary>Source-generated JSON for the one shape the bench serialises itself, so it stays AOT-clean
/// under the repo's <c>aot</c> CI gate.</summary>
[JsonSerializable(typeof(string))]
internal sealed partial class BenchJson : JsonSerializerContext;
