using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace KnockBox.Server.Admin;

/// <summary>
/// A bounded in-memory Serilog sink holding the most recent log events, so the admin portal can show a
/// live log stream without filesystem access.
///
/// Why a sink and not tailing <c>knockbox-YYYYMMDD.log</c>: the file is rendered text, so filtering it
/// by level or subsystem means re-parsing an output template — guesswork that breaks the moment the
/// template changes. Here the level and the <c>SourceContext</c> are still structured fields, so
/// "errors only" and "just the <c>KnockBox.GameLog</c> category" are exact. The rolling files remain the
/// history and the thing you download; this is the live view.
///
/// Every event gets a monotonic sequence number, which is what makes polling a stream: the client asks
/// for everything after the highest number it has seen, so no event is shown twice and none is missed
/// between polls. No SSE and no second WebSocket role needed.
/// </summary>
/// <remarks>
/// Hand-written rather than a <c>Serilog.Sinks.*</c> package for the same reason
/// <c>ReadFrom.Configuration</c> was rejected in Program.cs: this server publishes Native AOT with
/// warnings as errors, and every added Serilog package is a new chance of a trim warning.
/// </remarks>
public sealed class AdminLogBuffer : ILogEventSink
{
    /// <summary>Default ring size. A few thousand events is enough to see what just happened while
    /// costing a bounded, small amount of memory; anything older belongs in the rolling files.</summary>
    public const int DefaultCapacity = 2000;

    /// <summary>Messages are truncated at this length. A single pathological log line (a stack trace in
    /// a message, a game's log relay) must not be able to hold megabytes in the ring.</summary>
    private const int MaxTextLength = 4096;

    // Renders the message with the SAME "{Message:lj}" flags the console and file sinks use. Serilog's
    // own LogEvent.RenderMessage() renders structurally, which quotes every string property — an operator
    // comparing the portal against the log file would see 'Skipping game "x"' here and 'Skipping game x'
    // there, for the same event. The `l` (literal) flag is what removes that difference.
    private static readonly MessageTemplateTextFormatter MessageFormatter = new("{Message:lj}", null);

    private readonly Entry[] _ring;
    private readonly Lock _gate = new();

    private long _nextSequence = 1;
    // Total events ever written; _written - _ring.Length is how many have been evicted.
    private long _written;

    public AdminLogBuffer(int capacity = DefaultCapacity)
    {
        _ring = new Entry[Math.Max(16, capacity)];
    }

    /// <summary>One captured log event, flattened to the strings the portal renders.</summary>
    /// <param name="Sequence">Monotonic id; the client's polling cursor.</param>
    /// <param name="Category">Serilog's <c>SourceContext</c> — the subsystem, e.g.
    /// <c>KnockBox.GameLog</c> for game-relayed lines.</param>
    public readonly record struct Entry(
        long Sequence,
        DateTimeOffset Timestamp,
        LogEventLevel Level,
        string Category,
        string Message,
        string? Exception);

    /// <summary>Highest sequence number written so far; 0 when nothing has been logged.</summary>
    public long LastSequence { get { lock (_gate) return _nextSequence - 1; } }

    /// <summary>How many events the ring currently holds.</summary>
    public int Count { get { lock (_gate) return (int)Math.Min(_written, _ring.Length); } }

    /// <summary>Total events ever captured, including those since evicted — so the portal can say
    /// "showing the last 2000 of 41,233" rather than implying it has everything.</summary>
    public long TotalWritten { get { lock (_gate) return _written; } }

    void ILogEventSink.Emit(LogEvent logEvent)
    {
        // Serilog calls this on whatever thread logged, including from inside a request. Keep it to a
        // string render and an array write: a sink that blocks is a sink that stalls the server.
        var category = logEvent.Properties.TryGetValue("SourceContext", out var source)
            ? Unquote(source.ToString())
            : "";

        string message;
        try
        {
            using var writer = new StringWriter();
            MessageFormatter.Format(logEvent, writer);
            message = writer.ToString();
        }
        // A property whose ToString throws must not take down the logging call that reported it.
        catch (Exception ex) { message = $"<message could not be rendered: {ex.GetType().Name}>"; }

        var entry = new Entry(
            0, // replaced under the lock, where the sequence is assigned
            logEvent.Timestamp,
            logEvent.Level,
            Truncate(category),
            Truncate(message),
            logEvent.Exception is null ? null : Truncate(logEvent.Exception.ToString()));

        lock (_gate)
        {
            var sequence = _nextSequence++;
            _ring[(int)(_written % _ring.Length)] = entry with { Sequence = sequence };
            _written++;
        }
    }

    /// <summary>
    /// Events newer than <paramref name="afterSequence"/> matching the filters, oldest first.
    /// </summary>
    /// <param name="afterSequence">Exclusive cursor; 0 for "from the start of the ring".</param>
    /// <param name="minLevel">Lowest level to include, or null for all.</param>
    /// <param name="category">Case-insensitive substring match on the subsystem, or null for all.</param>
    /// <param name="search">Case-insensitive substring match on the message (or exception), or null.</param>
    /// <param name="limit">
    /// Maximum entries to return. When more match, the <b>newest</b> ones win — a viewer that has been
    /// away must not be pinned to stale events forever, and dropping the oldest is what a tail does.
    /// </param>
    public IReadOnlyList<Entry> Read(
        long afterSequence = 0,
        LogEventLevel? minLevel = null,
        string? category = null,
        string? search = null,
        int limit = 500)
    {
        if (limit <= 0) return [];

        Entry[] snapshot;
        int count;
        lock (_gate)
        {
            count = (int)Math.Min(_written, _ring.Length);
            if (count == 0) return [];
            snapshot = new Entry[count];
            // Copy oldest-first out of the circular buffer so the caller never sees the wrap point.
            var start = _written <= _ring.Length ? 0 : (int)(_written % _ring.Length);
            for (var i = 0; i < count; i++) snapshot[i] = _ring[(start + i) % _ring.Length];
        }

        var matches = new List<Entry>();
        foreach (var entry in snapshot)
        {
            if (entry.Sequence <= afterSequence) continue;
            if (minLevel is { } floor && entry.Level < floor) continue;
            if (!string.IsNullOrEmpty(category)
                && !entry.Category.Contains(category, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(search)
                && !entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
                && entry.Exception?.Contains(search, StringComparison.OrdinalIgnoreCase) != true) continue;
            matches.Add(entry);
        }

        if (matches.Count <= limit) return matches;
        return matches.GetRange(matches.Count - limit, limit);
    }

    // Serilog renders string property values with surrounding quotes; SourceContext is always a string,
    // so strip them rather than showing the operator "KnockBox.GameLog" complete with quote marks.
    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

    private static string Truncate(string value) =>
        value.Length <= MaxTextLength ? value : string.Concat(value.AsSpan(0, MaxTextLength), "…");
}
