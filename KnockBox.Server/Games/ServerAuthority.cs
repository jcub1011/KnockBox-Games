using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using KnockBox.Contracts;
using KnockBox.Server.Lobbies;
using KnockBox.Server.Networking;
using KnockBox.Server.Serialization;

namespace KnockBox.Server.Games;

/// <summary>
/// The per-lobby authority actor (design §6): a bounded inbound channel drained by ONE task —
/// mandatory because the runtime's engine is not thread-safe, so every module call happens on the
/// drain task. It runs the KBAuthority host loop server-side: intent → applyIntent → delta
/// broadcast (per-recipient mode → per-member re-projection); sync → snapshot(fromId) → state to
/// the requester; roster change → optional hook, then re-broadcast state; tick → optional periodic
/// patches. Outbound frames fan out through <see cref="ConnectionManager.SendRawToGame"/> with the
/// reserved sender id "server".
///
/// Overflow policy is two-tier: intents use TryWrite and are dropped with a warning when the
/// channel is full (a lost intent is recoverable — the client resyncs); ticks are coalesced (never
/// enqueued while one is pending); roster work is NEVER dropped — losing a one-shot membership
/// event would permanently desynchronize the module's roster view (its rate is control-plane
/// bounded, so an async write is safe).
/// </summary>
public sealed class ServerAuthority
{
    private abstract record AuthorityWork;
    private sealed record IntentWork(string FromId, string PayloadJson) : AuthorityWork;
    private sealed record PlayerJoinedWork(Player Player) : AuthorityWork;
    private sealed record PlayerLeftWork(string PlayerId) : AuthorityWork;
    private sealed record PlayerDisconnectedWork(string PlayerId) : AuthorityWork;
    private sealed record PlayerConnectedWork(string PlayerId) : AuthorityWork;
    private sealed record TickWork : AuthorityWork { public static readonly TickWork Instance = new(); }

    private const int MaxConsecutiveContainedFailures = 5;

    private readonly Lobby _lobby;
    private readonly IAuthorityRuntime _runtime;
    private readonly ConnectionManager _connections;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;          // server diagnostics
    private readonly ILogger _authorityLogger; // the module's own kb.log output ("KnockBox.Authority")
    private readonly bool _relayContainedErrors;
    private readonly Action<ServerAuthority, string> _onFatal;
    // Optional so the many tests that build an actor directly need no extra argument, and so an operator
    // who never opens the dashboard pays nothing for it.
    private readonly AuthorityMetrics? _metrics;

    /// <summary>This lobby's per-call wall-clock budget and the fraction of it that earns a warning.
    /// Held here rather than read from options each time because the drain loop touches them per call.</summary>
    private readonly TimeSpan _callBudget;
    private readonly long _slowCallWarnTicks;   // 0 = warnings disabled
    private readonly int _maxConsecutiveOverruns;

    private readonly Channel<AuthorityWork> _channel;
    private ITimer? _tickTimer;
    private int _tickPending;
    private DateTimeOffset _lastTick;
    private int _consecutiveFailures;
    /// <summary>Consecutive recoverable overruns (see AuthorityConstraintException.IsRecoverable).
    /// Separate from _consecutiveFailures because they mean different things: a module that THROWS is
    /// buggy, a module that overruns is too slow, and a lobby can be some of one without being any of
    /// the other. Reset by any call that completes.</summary>
    private int _consecutiveOverruns;
    /// <summary>Slowest call seen, so the near-budget warning fires on each NEW worst rather than on
    /// every call once a module is generally slow — a per-call log line at 20 Hz is its own outage.</summary>
    private long _worstCallTicks;

    public Lobby Lobby => _lobby;

    /// <summary>The drain task; completes after teardown (runtime disposed). Awaitable by tests.</summary>
    public Task Completion { get; }

    public ServerAuthority(
        Lobby lobby,
        IAuthorityRuntime runtime,
        AuthorityOptions options,
        ConnectionManager connections,
        TimeProvider time,
        ILogger logger,
        ILogger authorityLogger,
        bool relayContainedErrors,
        Action<ServerAuthority, string> onFatal,
        AuthorityMetrics? metrics = null)
    {
        _metrics = metrics;
        _lobby = lobby;
        _runtime = runtime;
        _connections = connections;
        _time = time;
        _logger = logger;
        _authorityLogger = authorityLogger;
        _relayContainedErrors = relayContainedErrors;
        _onFatal = onFatal;

        _callBudget = options.CallTimeoutFor(lobby.GameId);
        _maxConsecutiveOverruns = Math.Max(1, options.MaxConsecutiveOverruns);
        _slowCallWarnTicks = options.SlowCallWarnFraction > 0
            ? (long)(_callBudget.TotalSeconds * options.SlowCallWarnFraction * Stopwatch.Frequency)
            : 0;

        _channel = Channel.CreateBounded<AuthorityWork>(new BoundedChannelOptions(options.QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait, // posting APIs below decide drop vs. wait per tier
        });

        _lastTick = time.GetUtcNow();
        var tickHz = Math.Min(runtime.Config.TickHz, options.TickHzMax);
        if (runtime.Exports.Contains("tick") && tickHz > 0)
        {
            var period = TimeSpan.FromSeconds(1 / tickHz);
            _tickTimer = time.CreateTimer(_ => RequestTick(), null, period, period);
        }

        Completion = Task.Run(RunAsync);
    }

    // ── Posting (any thread) ──────────────────────────────────────────────────

    /// <summary>Data-plane tier: a full channel drops the intent (client resyncs; state is ephemeral).</summary>
    public void PostIntent(string fromId, string payloadJson)
    {
        if (!_channel.Writer.TryWrite(new IntentWork(fromId, payloadJson)))
            _logger.LogWarning("Authority queue full for lobby {LobbyId}; dropping an intent from {PlayerId}",
                _lobby.Id, fromId);
    }

    public void PostPlayerJoined(Player player) => PostRoster(new PlayerJoinedWork(player));
    public void PostPlayerLeft(string playerId) => PostRoster(new PlayerLeftWork(playerId));
    public void PostPlayerDisconnected(string playerId) => PostRoster(new PlayerDisconnectedWork(playerId));
    public void PostPlayerConnected(string playerId) => PostRoster(new PlayerConnectedWork(playerId));

    // Control-plane tier: roster events are one-shot; losing one would leave the module's roster
    // view permanently wrong (owner succession that never fires, projections for a stale roster).
    // The async fallback never blocks the socket thread, and roster rate is already bounded by the
    // control plane's limits.
    private void PostRoster(AuthorityWork work)
    {
        if (_channel.Writer.TryWrite(work)) return;
        var pending = _channel.Writer.WriteAsync(work);
        if (!pending.IsCompletedSuccessfully)
            _ = Observe(pending);

        async Task Observe(ValueTask write)
        {
            try { await write; }
            catch (ChannelClosedException) { /* actor stopped — lobby is going away anyway */ }
        }
    }

    /// <summary>Coalescing tick request (the timer callback): never more than one TickWork queued.
    /// Internal so tests can drive ticks deterministically without the wall-clock timer.</summary>
    internal void RequestTick()
    {
        if (Interlocked.CompareExchange(ref _tickPending, 1, 0) != 0) return;
        if (!_channel.Writer.TryWrite(TickWork.Instance))
            Interlocked.Exchange(ref _tickPending, 0); // full — drop; the next timer fire retries
    }

    /// <summary>Stops accepting work; the drain task finishes the backlog and disposes the runtime.</summary>
    public void Stop() => _channel.Writer.TryComplete();

    // ── The drain task ────────────────────────────────────────────────────────

    private async Task RunAsync()
    {
        try
        {
            await foreach (var work in _channel.Reader.ReadAllAsync())
            {
                if (work is TickWork) Interlocked.Exchange(ref _tickPending, 0);
                // One measurement point for every kind of work, rather than five around the individual
                // runtime calls: this is exactly the window in which the game's module owns the thread, so
                // it is the honest boundary for "what this game costs the server".
                var started = Stopwatch.GetTimestamp();
                try
                {
                    Process(work);
                    var elapsed = Stopwatch.GetTimestamp() - started;
                    _metrics?.RecordCall(_lobby.GameId, elapsed,
                        nearBudget: _slowCallWarnTicks > 0 && elapsed >= _slowCallWarnTicks);
                    WarnIfNearBudget(work, elapsed);
                    _consecutiveFailures = 0;
                    _consecutiveOverruns = 0;
                }
                catch (AuthorityScriptException ex)
                {
                    // A failed call still consumed CPU — often MORE than a successful one, since it ran to
                    // the point of throwing. Excluding it would understate a module that mostly fails.
                    _metrics?.RecordCall(_lobby.GameId, Stopwatch.GetTimestamp() - started, failed: true);
                    if (!HandleContainedFailure(work, ex)) break; // escalated to fatal
                }
                catch (AuthorityConstraintException ex) when (ex.IsRecoverable && work is TickWork)
                {
                    _metrics?.RecordCall(_lobby.GameId, Stopwatch.GetTimestamp() - started, failed: true);
                    if (!HandleOverrun(ex)) break; // escalated to fatal
                }
                catch (Exception ex) // AuthorityConstraintException or anything unexpected
                {
                    _metrics?.RecordCall(_lobby.GameId, Stopwatch.GetTimestamp() - started, failed: true);
                    _logger.LogError(ex, "Authority for lobby {LobbyId} (game {GameId}) failed fatally", _lobby.Id, _lobby.GameId);
                    _onFatal(this, "authority-failed");
                    break;
                }
            }
        }
        finally
        {
            _tickTimer?.Dispose();
            _tickTimer = null;
            _runtime.Dispose(); // the engine is single-threaded: dispose HERE, on the drain task
        }
    }

    /// <summary>Logs when one call reaches a configured fraction of its budget, on each new worst.
    ///
    /// This is the signal whose absence let a fatal overrun ship. A game developer runs their module in a
    /// browser, where it is JIT-compiled V8 over an in-memory dictionary and a turn costs microseconds;
    /// the same call here is interpreted, and the first time anyone learned it was near 250 ms was when
    /// the lobby died mid-match. Naming the game and the export makes the warning actionable from a log
    /// nobody was watching for it.</summary>
    private void WarnIfNearBudget(AuthorityWork work, long elapsedTicks)
    {
        if (_slowCallWarnTicks == 0 || elapsedTicks < _slowCallWarnTicks) return;
        // Only a NEW worst is worth a line: a module that sits at 60% of budget every tick would
        // otherwise emit 20 identical warnings a second, which buries the one that matters.
        if (elapsedTicks <= _worstCallTicks) return;
        _worstCallTicks = elapsedTicks;

        var ms = elapsedTicks * 1000d / Stopwatch.Frequency;
        _logger.LogWarning(
            "Authority for game {GameId} (lobby {LobbyId}) spent {ElapsedMs:F1} ms on {Work} — {Percent:F0}% of its "
            + "{BudgetMs:F0} ms per-call budget. Exceeding it closes the lobby. Browser timings do not predict this: "
            + "the module runs interpreted here, with every kb.words query crossing the sandbox boundary.",
            _lobby.GameId, _lobby.Id, ms, work.GetType().Name, ms * 100 / _callBudget.TotalMilliseconds,
            _callBudget.TotalMilliseconds);
    }

    /// <summary>A tick blew its budget. Drop it and keep the lobby, until that stops being defensible.
    ///
    /// Ticks are the one work item the actor already treats as droppable — they coalesce, and RequestTick
    /// discards one outright when the channel is full — so a single slow one is a hitch, not a reason to
    /// end everybody's game. Being killed by the FIRST one is what made a game that was merely too slow
    /// on its worst turn indistinguishable from a game that was broken. A module that cannot get through
    /// a tick at all still dies, just after MaxConsecutiveOverruns of them.
    ///
    /// The engine survives this the same way it survives a module throw: Jint unwound the call and its
    /// own state is consistent. What may be partial is the MODULE's state, so the response is the
    /// contained path's — discard the call's effects and re-broadcast, converging every client on
    /// whatever the module now believes. Returns false when escalating to fatal.</summary>
    private bool HandleOverrun(AuthorityConstraintException ex)
    {
        _metrics?.RecordOverrun(_lobby.GameId);
        _runtime.DrainEffects(); // a failed call's partial effects must not leak into the next one

        if (++_consecutiveOverruns >= _maxConsecutiveOverruns)
        {
            _logger.LogError(ex,
                "Authority for lobby {LobbyId} (game {GameId}) exceeded its {BudgetMs:F0} ms per-call budget "
                + "{Count} times in a row — closing the lobby",
                _lobby.Id, _lobby.GameId, _callBudget.TotalMilliseconds, _consecutiveOverruns);
            _onFatal(this, "authority-failed");
            return false;
        }

        _logger.LogWarning(ex,
            "Authority for game {GameId} (lobby {LobbyId}) exceeded its {BudgetMs:F0} ms per-call budget on a tick "
            + "({Count} of {Max} before the lobby closes) — dropping the tick. The module is doing too much work in "
            + "one call; kb.budgetRemainingMs() lets it stop on its own terms instead.",
            _lobby.GameId, _lobby.Id, _callBudget.TotalMilliseconds, _consecutiveOverruns, _maxConsecutiveOverruns);
        BroadcastState();
        return true;
    }

    // The §7 contained path: the module threw inside one call — engine state is still consistent.
    // Drop the work, discard its partial effects, and re-broadcast the UNCHANGED snapshot so every
    // client converges (the guide's illegal-intent rule). Returns false when escalating to fatal.
    private bool HandleContainedFailure(AuthorityWork work, AuthorityScriptException ex)
    {
        _runtime.DrainEffects(); // a failed call's partial effects must not leak into the next one
        _logger.LogWarning(ex, "Authority module error in lobby {LobbyId} (game {GameId}) handling {Work}",
            _lobby.Id, _lobby.GameId, work.GetType().Name);

        // Developer experience: in Development the thrown message is relayed to the lobby as a
        // debug frame so the game dev sees their applyIntent exception in the browser console.
        // Production sends nothing — no internals leak to clients.
        if (_relayContainedErrors)
            SendEnvelope("all", $"{{\"_kb\":\"error\",\"message\":\"{JsonEncodedText.Encode(ex.Message)}\"}}");

        try
        {
            BroadcastState();
            _runtime.DrainEffects(); // snapshot() shouldn't request effects; don't let any leak
        }
        catch (Exception resyncEx) // snapshot itself failing means clients can never converge
        {
            _logger.LogError(resyncEx, "Authority for lobby {LobbyId} could not re-sync after a module error", _lobby.Id);
            _onFatal(this, "authority-failed");
            return false;
        }

        if (++_consecutiveFailures >= MaxConsecutiveContainedFailures)
        {
            _logger.LogError("Authority for lobby {LobbyId} hit {Count} consecutive module errors; closing the lobby",
                _lobby.Id, _consecutiveFailures);
            _onFatal(this, "authority-failed");
            return false;
        }
        return true;
    }

    private void Process(AuthorityWork work)
    {
        switch (work)
        {
            case IntentWork intent:
                ProcessIntent(intent);
                break;

            case PlayerJoinedWork joined:
                InvokeHook("onPlayerJoined", JsonSerializer.Serialize(joined.Player, KnockBoxProtocolContext.Default.Player));
                BroadcastState(); // KBAuthority's roster rule: any roster change re-pushes state
                break;

            case PlayerLeftWork left:
                InvokeHook("onPlayerLeft", Quote(left.PlayerId));
                BroadcastState();
                break;

            case PlayerDisconnectedWork dropped:
                InvokeHook("onPlayerDisconnected", Quote(dropped.PlayerId));
                BroadcastState();
                break;

            case PlayerConnectedWork returned:
                InvokeHook("onPlayerConnected", Quote(returned.PlayerId));
                BroadcastState();
                break;

            case TickWork:
                if (!_runtime.Exports.Contains("tick")) break; // no timer exists without the export; belt and braces
                var now = _time.GetUtcNow();
                var dtMs = (now - _lastTick).TotalMilliseconds;
                _lastTick = now;
                var patch = _runtime.Invoke("tick", dtMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (patch != "null") BroadcastPatch(patch);
                ApplyEffects();
                break;
        }
    }

    private void ProcessIntent(IntentWork intent)
    {
        string? kind = null;
        string actionJson = "null";
        try
        {
            using var doc = JsonDocument.Parse(intent.PayloadJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("_kb", out var kb) && kb.ValueKind == JsonValueKind.String)
            {
                kind = kb.GetString();
                if (doc.RootElement.TryGetProperty("action", out var action))
                    actionJson = action.GetRawText();
            }
        }
        catch (JsonException)
        {
            // The relay only forwards frames it already parsed, so this shouldn't happen — belt and braces.
        }

        switch (kind)
        {
            case "intent":
                var patch = _runtime.Invoke("applyIntent", Quote(intent.FromId), actionJson);
                if (patch != "null") BroadcastPatch(patch);
                // null = rejected: nothing is sent (the client's own optimistic UI, if any, resyncs).
                ApplyEffects();
                break;

            case "sync":
                SendEnvelope(intent.FromId, StateEnvelope(Snapshot(intent.FromId)));
                ApplyEffects();
                break;

            default:
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("Dropping non-contract payload to the authority in lobby {LobbyId} (kind {Kind})",
                        _lobby.Id, kind ?? "<none>");
                break;
        }
    }

    private void InvokeHook(string hook, string jsonArg)
    {
        if (_runtime.Exports.Contains(hook))
            _runtime.Invoke(hook, jsonArg); // a returned patch is ignored — BroadcastState follows anyway
        ApplyEffects();
    }

    // ── Outbound ─────────────────────────────────────────────────────────────

    // A non-null applyIntent/tick result: broadcast the delta — except in per-recipient mode, where
    // there are no deltas and the truthy patch only signals "accepted"; re-project per player.
    private void BroadcastPatch(string patchJson)
    {
        if (_runtime.Config.PerRecipient) BroadcastState();
        else SendEnvelope("all", $"{{\"_kb\":\"delta\",\"patch\":{patchJson}}}");
    }

    // Full-state push to everyone: one shared snapshot, or per-member projections in
    // per-recipient (hidden information) mode. ONE member snapshot per broadcast — Lobby.Players
    // allocates a fresh list on every read, and per-recipient mode reads it once per member.
    private void BroadcastState()
    {
        var members = _lobby.Players;
        if (_runtime.Config.PerRecipient)
        {
            foreach (var p in members)
                SendEnvelope(p.Id, StateEnvelope(Snapshot(p.Id)), members);
        }
        else
        {
            SendEnvelope("all", StateEnvelope(Snapshot(null)), members);
        }
    }

    private string Snapshot(string? forPlayerId) =>
        forPlayerId is null ? _runtime.Invoke("snapshot") : _runtime.Invoke("snapshot", Quote(forPlayerId));

    private static string StateEnvelope(string stateJson) => $"{{\"_kb\":\"state\",\"state\":{stateJson}}}";

    private static string Quote(string value) => $"\"{JsonEncodedText.Encode(value)}\"";

    /// <summary>Serializes once and fans out over the lobby's member snapshot with the reserved
    /// sender id "server". Enforces the wire's message-size cap outbound (inbound-only today).</summary>
    private void SendEnvelope(string to, string envelopeJson) => SendEnvelope(to, envelopeJson, _lobby.Players);

    private void SendEnvelope(string to, string envelopeJson, IReadOnlyList<Player> members)
    {
        var bytes = SerializeGameFrame(to, envelopeJson);

        if (bytes.Length > WebSocketHandler.MaxMessageBytes)
        {
            _logger.LogError("Authority for lobby {LobbyId} produced a {Size}-byte frame (max {Max}); dropping it",
                _lobby.Id, bytes.Length, WebSocketHandler.MaxMessageBytes);
            return;
        }

        if (to == "all")
            foreach (var p in members) _connections.SendRawToGame(p.Id, bytes);
        else
            _connections.SendRawToGame(to, bytes);
    }

    /// <summary>
    /// The <c>GameMessage</c> wire frame, written directly around an envelope that is ALREADY JSON.
    /// </summary>
    /// <remarks>
    /// The same frame <c>ConnectionManager.Serialize(new GameMessage(to, payload, "server"))</c> produces —
    /// discriminator first, then camelCase properties in declaration order — but without materializing the
    /// payload as a JsonDocument, deep-cloning it into a detached JsonElement and then serializing it a
    /// third time. That round trip cost three full passes over the payload where one will do, on the tick
    /// path: a 20 Hz game with a 40 KB snapshot pays it twenty times a second per lobby, multiplied by the
    /// member count in per-recipient mode.
    ///
    /// One deliberate difference, pinned by ServerAuthorityFrameTests: the payload is copied verbatim, so
    /// it keeps whatever escaping the module's own serializer chose, where the round trip re-encoded it
    /// with <c>JavaScriptEncoder.Default</c> (non-ASCII and HTML-sensitive characters as <c>\uXXXX</c>).
    /// Both are the same JSON value to any parser, and these frames go straight into a WebSocket rather
    /// than into markup, which is the only thing that escaping buys. It also makes the frames smaller.
    ///
    /// <see cref="Utf8JsonWriter.WriteRawValue(string, bool)"/> keeps its default validation. The payload
    /// is valid by construction (the runtime's own serializer, or an envelope this class built with
    /// JsonEncodedText), so validation should never fire — but skipping it would turn a hypothetical bug
    /// into malformed JSON at the client instead of a server-side throw, which is a strictly worse trade
    /// for one scan of a buffer we are copying anyway.
    /// </remarks>
    internal static byte[] SerializeGameFrame(string to, string envelopeJson)
    {
        var buffer = new ArrayBufferWriter<byte>(envelopeJson.Length + 64);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "Game");
            writer.WriteString("to", to);
            writer.WritePropertyName("payload");
            writer.WriteRawValue(envelopeJson);
            writer.WriteString("from", "server");
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    // ── Deferred effects (design §3) ─────────────────────────────────────────
    // Applied AFTER the invocation's own delta/state sends, so OwnerChanged always follows the
    // delta of the intent that triggered it.

    private void ApplyEffects()
    {
        var effects = _runtime.DrainEffects();

        foreach (var (level, message) in effects.Logs)
            _authorityLogger.Log(level, "Game {GameId} lobby {LobbyId} authority: {AuthorityMessage}",
                _lobby.GameId, _lobby.Id, WebSocketHandler.CleanLogText(message));

        if (effects.SetLobbyOpen is { } open)
        {
            _lobby.Open = open;
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Lobby {LobbyId} set {State} by its authority module", _lobby.Id, open ? "open" : "closed");
        }

        if (effects.SetOwner is { } target)
        {
            // The reassign-and-announce sequence lives in LobbyOwnership: the admin kick performs the
            // same move when it removes the owner, and two copies of it would drift.
            if (LobbyOwnership.Reassign(_lobby, _connections, target))
            {
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation("Lobby {LobbyId} owner reassigned to {OwnerId} by its authority module", _lobby.Id, target);
            }
            else
            {
                // Contained module error (invalid target) — logged and ignored, per §5f.
                _logger.LogWarning("Authority module for lobby {LobbyId} called kb.setOwner('{Target}') but they are not a member; ignored",
                    _lobby.Id, target);
            }
        }
    }
}
