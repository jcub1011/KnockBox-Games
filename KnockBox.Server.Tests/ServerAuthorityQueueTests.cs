using KnockBox.Contracts;
using KnockBox.Server.Games;
using KnockBox.Server.Lobbies;
using KnockBox.Server.Networking;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// Design §6 two-tier inbound-channel overflow policy, pinned deterministically: intents use
/// TryWrite and are DROPPED when the channel is full (a lost intent is recoverable — the client
/// resyncs); roster events use WriteAsync and are NEVER dropped (losing a one-shot membership event
/// would permanently desync the module's roster view). The real Jint runtime can't block the drain
/// task on a host gate, so a fake <see cref="IAuthorityRuntime"/> whose Invoke blocks provides the
/// seam: it stalls the drain while the test fills the channel, making drop-vs-block observable.
/// </summary>
public class ServerAuthorityQueueTests : IDisposable
{
    private const string Intent = """{"_kb":"intent","action":{}}""";
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(15));

    public void Dispose() => _cts.Dispose();

    [Fact]
    public async Task Full_queue_drops_intents_but_never_roster_work()
    {
        using var gate = new ManualResetEventSlim(false);
        var runtime = new GatedRuntime(gate);
        var log = new CapturingLogger();

        var connections = new ConnectionManager();
        var lobbies = new LobbyManager();
        Assert.True(lobbies.TryCreate("g", "p1", 8, out var lobby, isServerAuthority: true));
        Assert.True(lobby.TryAdd(new Player("p1", "p1")));

        var options = AuthorityOptions.FromConfiguration(
            ConfigFactory.FromPairs(("KnockBox:AuthorityQueueCapacity", "1")));

        var fatal = false;
        var actor = new ServerAuthority(lobby, runtime, options, connections, TimeProvider.System,
            log, NullLogger.Instance, relayContainedErrors: false, onFatal: (_, _) => fatal = true);

        // Prime: the drain task dequeues this intent, enters Invoke, and blocks on the gate — so the
        // one-slot channel is now empty and the drain is stalled until the test releases it.
        actor.PostIntent("p1", Intent);
        await runtime.FirstInvokeEntered.Task.WaitAsync(_cts.Token);

        actor.PostIntent("p1", Intent);  // fills the single slot
        actor.PostIntent("p1", Intent);  // slot full → dropped
        actor.PostIntent("p1", Intent);  // slot full → dropped
        // Roster work never drops: TryWrite fails (slot full) so it awaits a free slot instead.
        actor.PostPlayerJoined(new Player("p2", "p2"));

        Assert.Equal(2, log.Warnings.Count(w => w.Contains("dropping an intent")));

        // Release the drain; the buffered intent frees the slot, the awaited roster write lands, and
        // the roster hook runs — proving the roster event survived the overflow.
        gate.Set();
        await runtime.RosterInvoked.Task.WaitAsync(_cts.Token);

        actor.Stop();
        await actor.Completion;

        Assert.False(fatal);
        Assert.Contains("onPlayerJoined", runtime.Invoked);
    }

    /// <summary>A runtime whose every Invoke blocks on a gate, so the actor's drain task can be
    /// stalled on command. Records which hooks ran; signals on first entry and on the roster hook.</summary>
    private sealed class GatedRuntime(ManualResetEventSlim gate) : IAuthorityRuntime
    {
        private readonly object _lock = new();
        public List<string> Invoked { get; } = [];
        public TaskCompletionSource FirstInvokeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RosterInvoked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlySet<string> Exports { get; } = new HashSet<string> { "applyIntent", "snapshot", "onPlayerJoined" };
        public AuthorityConfig Config { get; } = new();

        public void Initialize(string playersJson) { }

        public string Invoke(string export, params string[] jsonArgs)
        {
            lock (_lock) Invoked.Add(export);
            if (export == "onPlayerJoined") RosterInvoked.TrySetResult();
            FirstInvokeEntered.TrySetResult();
            gate.Wait();
            return export == "applyIntent" ? "null" : "{}";
        }

        public AuthorityEffects DrainEffects() => AuthorityEffects.None;
        public void Dispose() { }
    }

    /// <summary>Captures rendered warning messages (the drop path logs at Warning).</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
