using System.Security.Cryptography;
using System.Text;
using System.Net.WebSockets;
using KnockBox.Contracts;
using KnockBox.Server.Admin;
using KnockBox.Server.Games;
using KnockBox.Server.Games.Blobs;
using KnockBox.Server.Lobbies;
using KnockBox.Server.Networking;
using KnockBox.Server.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// That a lobby's blobs go when the lobby does — on <b>both</b> teardown paths, with no cleanup call
/// from the game.
/// </summary>
/// <remarks>
/// <para>There are two, they share no code, and a blob store wired to only one of them leaks silently.
/// <c>WebSocketHandler.CloseLobbyIfDark</c> is the normal path — disconnect, leave, kick, reap — and it
/// removes a lobby nobody is connected to while deliberately broadcasting nothing, so it never goes
/// through <see cref="LobbyCloser"/>. <see cref="LobbyCloser"/> is the forced path: an admin close, a
/// game uninstall, an authority-fatal teardown, closing a <em>live</em> lobby out from under its
/// players. Wire only the second and every session that simply ended keeps its art on disk forever.</para>
/// <para>The last test here reads <c>Program.cs</c> as text, which is the house pattern for a wiring
/// rule a file cannot enforce for itself (<c>AdminRouteGuardTests</c>, <c>OriginPortBindingTests</c> and
/// <c>DockerPersistenceTests</c> all do it). It is needed because <see cref="LobbyCloser.OnClosing"/> is
/// a single-subscriber property that already had an owner: the release is <em>composed</em> alongside
/// the authority stop at one line in <c>Program.cs</c>, and an assignment that replaced rather than
/// composed would pass every behavioural test in this file while breaking the other subscriber.</para>
/// </remarks>
public class BlobLobbyTeardownTests : IDisposable
{
    private const string GameOrigin = "http://game.local";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "kb-blobteardown-" + Guid.NewGuid().ToString("N"));
    private readonly BlobStore _store;
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(10));

    public BlobLobbyTeardownTests()
    {
        Directory.CreateDirectory(_root);
        _store = new BlobStore(
            new BlobOptionsProvider(BlobOptions.Default with { Root = _root }),
            TimeProvider.System, NullLogger<BlobStore>.Instance);
    }

    public void Dispose()
    {
        _cts.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task The_normal_teardown_path_releases_the_lobbys_blobs()
    {
        var connections = new ConnectionManager();
        var lobbies = new LobbyManager();
        var tokens = new TokenService(
            new ConfigurationBuilder().Build(), TimeProvider.System, NullLogger<TokenService>.Instance);
        var handler = new WebSocketHandler(
            connections, lobbies,
            new GameCatalog(Path.GetTempPath(), NullLogger<GameCatalog>.Instance),
            TestAuthorities.Manager(connections, lobbies), tokens,
            new LimitsProvider(ServerLimits.FromConfiguration(new ConfigurationBuilder().Build())),
            PlatformPolicy.OpenPlatform, new RelayMetrics(), TimeProvider.System,
            NullLoggerFactory.Instance, NullLogger<WebSocketHandler>.Instance,
            _store);

        Assert.True(lobbies.TryCreate("g", "dm", 4, out var lobby));
        Assert.True(lobby.TryAdd(new Player("dm", "The DM")));

        var hash = await Store(lobby.Id);
        _store.Register(lobby.Id, "g", "map", hash);
        var path = Path.Combine(_root, hash[..2], hash);
        Assert.True(File.Exists(path));

        // The only member's shell says goodbye. An explicit leave is immediate — no reconnect grace —
        // so the lobby goes dark and CloseLobbyIfDark runs. Note what is NOT in this script: any blob
        // call at all. That is R4 — a crashed or abandoned session never runs its own teardown, so the
        // release has to be the server's job.
        var shell = new ScriptedWebSocket(
        [
            ConnectionManager.Serialize(new HelloMessage("dm", "dm", tokens.IssueIdentity("dm"))),
            ConnectionManager.Serialize(new RejoinLobbyMessage("c1", lobby.Id)),
            ConnectionManager.Serialize(new LeaveLobbyMessage(lobby.Id)),
        ]);
        await handler.HandleAsync(shell, GameOrigin, _cts.Token);

        Assert.Null(lobbies.Get(lobby.Id));
        Assert.False(File.Exists(path), "the lobby went dark, so nothing can reference its blobs again");
        Assert.Equal(0, _store.HandleCount);
        Assert.Equal(0, _store.TotalBytes);
    }

    [Fact]
    public async Task The_forced_teardown_path_releases_the_lobbys_blobs()
    {
        var lobbies = new LobbyManager();
        var closer = new LobbyCloser(lobbies, new ConnectionManager(), NullLogger<LobbyCloser>.Instance);

        // Composed exactly as Program.cs composes it, because OnClosing is a single Action<string> and
        // the authority stop already owns it. The order is the part worth copying: stopping the actor
        // must not wait on disk I/O.
        var stopped = new List<string>();
        closer.OnClosing = lobbyId =>
        {
            stopped.Add(lobbyId);
            _store.ReleaseLobby(lobbyId);
        };

        Assert.True(lobbies.TryCreate("g", "dm", 4, out var lobby));
        var hash = await Store(lobby.Id);
        _store.Register(lobby.Id, "g", "map", hash);
        var path = Path.Combine(_root, hash[..2], hash);

        closer.Close(lobby.Id, "Closed by an administrator.");

        Assert.Equal([lobby.Id], stopped);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Closing_one_lobby_leaves_another_lobbys_blobs_alone()
    {
        // The same bytes in two sessions are one file, so a release that keyed on content rather than
        // on handles would pull a live map out from under the other table.
        var lobbies = new LobbyManager();
        var closer = new LobbyCloser(lobbies, new ConnectionManager(), NullLogger<LobbyCloser>.Instance);
        closer.OnClosing = _store.ReleaseLobby;

        Assert.True(lobbies.TryCreate("g", "dm1", 4, out var first));
        Assert.True(lobbies.TryCreate("g", "dm2", 4, out var second));
        var hash = Store(first.Id).GetAwaiter().GetResult();
        _store.Register(first.Id, "g", "map", hash);
        _store.Register(second.Id, "g", "map", hash);

        closer.Close(first.Id, "x");

        Assert.True(File.Exists(Path.Combine(_root, hash[..2], hash)));
        closer.Close(second.Id, "x");
        Assert.False(File.Exists(Path.Combine(_root, hash[..2], hash)));
    }

    [Fact]
    public void Both_teardown_paths_are_wired_to_the_blob_store_in_the_source()
    {
        var program = RepoFile.Read("KnockBox.Server/Program.cs");
        var handler = RepoFile.Read("KnockBox.Server/Networking/WebSocketHandler.cs");
        if (program is null || handler is null) return; // not a checkout (publish output / NuGet-restored run)

        // The normal path. One line inside CloseLobbyIfDark, and the behavioural test above covers it —
        // this is here so removing the line fails for a reason that names it.
        Assert.Contains("ReleaseLobby", handler);

        // The forced path, and the reason this assertion is text rather than behaviour: OnClosing is a
        // single-subscriber Action<string> that ServerAuthorityManager.Stop already owned, so the
        // release had to be COMPOSED with it. `OnClosing = blobStore.ReleaseLobby` would compile, pass
        // every test in this file, and silently stop stopping authority actors — leaking a Jint engine
        // per closed lobby, which is the exact failure LobbyCloser.OnClosing was introduced to prevent.
        var composed = program.Contains("authorityManager.Stop(lobbyId);", StringComparison.Ordinal)
                       && program.Contains("blobStore.ReleaseLobby(lobbyId);", StringComparison.Ordinal);
        Assert.True(composed,
            "Program.cs must compose BOTH subscribers into LobbyCloser.OnClosing — the authority stop " +
            "and the blob release. Assigning either one alone drops the other with no test failing.");
    }

    /// <summary>
    /// A socket that replays a script and then reports the peer closed. Private here like the three
    /// identical copies in the other flow tests: this project fakes its collaborators directly, and a
    /// shared version would need to satisfy four different scripts' needs at once.
    /// </summary>
    private sealed class ScriptedWebSocket(IEnumerable<byte[]> inbound) : WebSocket
    {
        private readonly Queue<byte[]> _inbound = new(inbound);
        private WebSocketState _state = WebSocketState.Open;

        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            if (_inbound.Count == 0)
            {
                if (_state == WebSocketState.Open) _state = WebSocketState.CloseReceived;
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            }
            var message = _inbound.Dequeue();
            message.CopyTo(buffer.Array!, buffer.Offset);
            return Task.FromResult(new WebSocketReceiveResult(message.Length, WebSocketMessageType.Text, true));
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken ct) =>
            Task.CompletedTask;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override void Dispose() { }
    }

    private async Task<string> Store(string lobbyId)
    {
        var bytes = Encoding.UTF8.GetBytes("a battlemap");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var result = await _store.ReceiveAsync(
            lobbyId, "g", hash, "image/png", new MemoryStream(bytes), TestContext.Current.CancellationToken);
        Assert.Equal(BlobIngestOutcome.Stored, result.Outcome);
        return hash;
    }
}
