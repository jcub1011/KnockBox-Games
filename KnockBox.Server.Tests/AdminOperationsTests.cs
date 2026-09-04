using KnockBox.Contracts;
using KnockBox.Server.Admin;
using KnockBox.Server.Games;
using KnockBox.Server.Hosting;
using KnockBox.Server.Lobbies;
using KnockBox.Server.Networking;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// Pins the multi-step operator actions: how a lobby is classified for the directory, what "purge stale"
/// collects, and the all-or-nothing rules around deleting a game's files.
/// </summary>
public class AdminOperationsTests : IDisposable
{
    private readonly string _root;
    private readonly string _gamesRoot;
    private readonly string _unpackedRoot;
    private readonly string _compressedRoot;
    private readonly string _managedRoot;
    private readonly MutableTimeProvider _clock = new(DateTimeOffset.UnixEpoch);
    private readonly LobbyManager _lobbies;
    private readonly ConnectionManager _connections = new();

    public AdminOperationsTests()
    {
        // The manager must share the test's clock: it stamps Lobby.CreatedAt/LastActivityUtc, and against
        // the real clock every "advance the fake clock" assertion here would compare 1970 with today.
        _lobbies = new LobbyManager(_clock);
        _root = Path.Combine(Path.GetTempPath(), $"kb-admin-ops-{Guid.NewGuid():N}");
        _gamesRoot = Path.Combine(_root, "games");
        _unpackedRoot = Path.Combine(_root, "games-unpacked");
        _compressedRoot = Path.Combine(_root, "games-compressed");
        _managedRoot = Path.Combine(_root, "games-managed");
        foreach (var dir in new[] { _gamesRoot, _unpackedRoot, _compressedRoot, _managedRoot })
            Directory.CreateDirectory(dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private GameCatalog Catalog() =>
        new([_gamesRoot, _unpackedRoot], NullLogger<GameCatalog>.Instance, 1 << 20, 1 << 20);

    private AdminOperations Operations(GameCatalog catalog, LobbyCloser? closer = null) =>
        new(_lobbies,
            closer ?? new LobbyCloser(_lobbies, _connections, NullLogger<LobbyCloser>.Instance),
            catalog,
            _connections,
            new ContentPaths.Resolved(
                Path.Combine(_root, "web"), _gamesRoot, Path.Combine(_root, "logs"),
                _compressedRoot, _unpackedRoot, _managedRoot)
            { BlobsRoot = Path.Combine(_root, "blobs") },
            _clock,
            NullLogger<AdminOperations>.Instance);

    // A minimal discoverable game: GAME.json plus the entry file it names.
    private string WriteGame(string root, string id, string name = "A Game")
    {
        var dir = Path.Combine(root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "GAME.json"),
            $$"""{ "id": "{{id}}", "name": "{{name}}", "entry": "index.html", "maxPlayers": 4 }""");
        File.WriteAllText(Path.Combine(dir, "index.html"), "<html></html>");
        return dir;
    }

    private Lobby NewLobby(string gameId = "ttt", params string[] members)
    {
        Assert.True(_lobbies.TryCreate(gameId, members.FirstOrDefault() ?? "p1", 4, out var lobby));
        foreach (var id in members) lobby.TryAdd(new Player(id, id));
        return lobby;
    }

    private (Connection Connection, FakeWebSocket Socket) Connect(string playerId)
    {
        var socket = new FakeWebSocket();
        var connection = new Connection(playerId, playerId, socket,
            NullLogger<Connection>.Instance, OutboundOverflow.CloseOnFull);
        _connections.Add(connection);
        return (connection, socket);
    }

    // Drains a connection's outbound channel onto its socket so the test can read what was queued.
    private static IReadOnlyList<IMessage?> Drain(Connection connection, FakeWebSocket socket)
    {
        connection.CompleteOutbound();
        connection.SendLoopAsync(CancellationToken.None).GetAwaiter().GetResult();
        return [.. socket.Sent.Select(b =>
            System.Text.Json.JsonSerializer.Deserialize(
                b, KnockBox.Server.Serialization.KnockBoxProtocolContext.Default.IMessage))];
    }

    // ── Classification ────────────────────────────────────────────────────────

    [Fact]
    public void A_lobby_with_no_members_is_empty()
    {
        var ops = Operations(Catalog());
        var lobby = NewLobby();
        Assert.Equal(AdminOperations.LobbyState.Empty, ops.Classify(lobby, _clock.GetUtcNow(), TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void An_open_lobby_whose_members_are_connected_is_waiting()
    {
        var ops = Operations(Catalog());
        var lobby = NewLobby("ttt", "p1");
        Connect("p1");
        Assert.Equal(AdminOperations.LobbyState.Waiting, ops.Classify(lobby, _clock.GetUtcNow(), TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void A_closed_lobby_is_in_game_not_draining()
    {
        var ops = Operations(Catalog());
        var lobby = NewLobby("ttt", "p1");
        Connect("p1");
        lobby.Open = false; // the game closes the lobby when play begins — the healthy case

        Assert.Equal(AdminOperations.LobbyState.InGame, ops.Classify(lobby, _clock.GetUtcNow(), TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void A_lobby_nobody_is_connected_to_is_stale_regardless_of_the_clock()
    {
        var ops = Operations(Catalog());
        var lobby = NewLobby("ttt", "p1"); // a member, but no control socket

        Assert.Equal(AdminOperations.LobbyState.Stale, ops.Classify(lobby, _clock.GetUtcNow(), TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void A_connected_lobby_goes_stale_once_it_passes_the_idle_window()
    {
        var ops = Operations(Catalog());
        var lobby = NewLobby("ttt", "p1");
        Connect("p1");

        _clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(AdminOperations.LobbyState.Waiting, ops.Classify(lobby, _clock.GetUtcNow(), TimeSpan.FromMinutes(30)));

        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(AdminOperations.LobbyState.Stale, ops.Classify(lobby, _clock.GetUtcNow(), TimeSpan.FromMinutes(30)));

        // Any sign of life resets it — this is what the relay stamps on every frame.
        lobby.Touch(_clock.GetUtcNow());
        Assert.Equal(AdminOperations.LobbyState.Waiting, ops.Classify(lobby, _clock.GetUtcNow(), TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void A_zero_idle_window_disables_the_idle_test_but_not_the_connection_test()
    {
        var ops = Operations(Catalog());
        var connected = NewLobby("ttt", "p1");
        Connect("p1");
        _clock.Advance(TimeSpan.FromDays(30));

        // 0 means "don't judge by the clock", which must not degrade into "everything is stale".
        Assert.Equal(AdminOperations.LobbyState.Waiting, ops.Classify(connected, _clock.GetUtcNow(), TimeSpan.Zero));
    }

    // ── Purge ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Purge_closes_empty_and_stale_lobbies_and_leaves_live_ones_alone()
    {
        var ops = Operations(Catalog());
        var empty = NewLobby("ttt");
        var abandoned = NewLobby("ttt", "p2");           // a member with no socket
        var live = NewLobby("ttt", "p3");
        Connect("p3");

        Assert.Equal(2, ops.PurgeStale(TimeSpan.FromMinutes(30), "idle"));
        Assert.Null(_lobbies.Get(empty.Id));
        Assert.Null(_lobbies.Get(abandoned.Id));
        Assert.NotNull(_lobbies.Get(live.Id));
    }

    [Fact]
    public void Purge_notifies_the_members_it_evicts()
    {
        var ops = Operations(Catalog());
        var lobby = NewLobby("ttt", "p1");
        var (connection, socket) = Connect("p1");
        _clock.Advance(TimeSpan.FromHours(2));

        Assert.Equal(1, ops.PurgeStale(TimeSpan.FromMinutes(30), "This lobby was idle."));
        Assert.Null(_lobbies.Get(lobby.Id));

        // Purging goes through LobbyCloser, so a purged member gets the same LobbyClosed a manual close
        // sends rather than a socket that simply stops answering.
        var closed = Assert.IsType<LobbyClosedMessage>(Assert.Single(Drain(connection, socket)));
        Assert.Equal((lobby.Id, "This lobby was idle."), (closed.LobbyId, closed.Reason));
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Fact]
    public void Deleting_an_unknown_game_fails_without_touching_anything()
    {
        var catalog = Catalog();
        catalog.Discover();
        var result = Operations(catalog).DeleteGame("no-such-game");

        Assert.False(result.Success);
        Assert.Contains("no-such-game", result.Error);
        Assert.Null(result.Blocked);
    }

    [Fact]
    public void Deleting_a_game_removes_its_directory_and_its_compressed_cache()
    {
        var dir = WriteGame(_gamesRoot, "tictactoe");
        var compressed = Path.Combine(_compressedRoot, "tictactoe");
        Directory.CreateDirectory(compressed);
        File.WriteAllText(Path.Combine(compressed, "index.html.br"), "compressed");

        var catalog = Catalog();
        catalog.Discover();
        var result = Operations(catalog).DeleteGame("tictactoe");

        Assert.True(result.Success, result.Error);
        Assert.False(Directory.Exists(dir));
        // The derived cache goes too, or the game's bytes linger on disk with nothing to serve them.
        Assert.False(Directory.Exists(compressed));
    }

    [Fact]
    public void Deleting_a_package_backed_game_removes_the_source_kbg_as_well()
    {
        // A .kbg in the games folder extracted into the unpacked root: the archive is what the installer
        // watches, so leaving it behind means the game reinstalls itself on the next pass and the operator
        // watches a deletion undo itself.
        var unpacked = WriteGame(_unpackedRoot, "packaged");
        var package = Path.Combine(_gamesRoot, "packaged.kbg");
        File.WriteAllBytes(package, [1, 2, 3, 4]);

        var catalog = Catalog();
        catalog.Discover();
        var result = Operations(catalog).DeleteGame("packaged");

        Assert.True(result.Success, result.Error);
        Assert.False(Directory.Exists(unpacked));
        Assert.False(File.Exists(package));
        Assert.Equal(2, result.Removed!.Count);
    }

    [Fact]
    public void Deleting_a_package_whose_file_name_is_not_the_game_id_still_removes_the_kbg()
    {
        // The installer takes the id from the header INSIDE the archive and accepts any file name, so
        // deriving the package path as "<id>.kbg" missed this one entirely: the unpacked copy went, the
        // archive stayed, and the next reconcile pass put the game straight back. The marker inside the
        // extracted folder is what names the real file.
        var unpacked = WriteGame(_unpackedRoot, "packaged");
        var package = Path.Combine(_gamesRoot, "packaged-v2-final.kbg");
        File.WriteAllBytes(package, [1, 2, 3, 4]);
        PackageMarker.Write(unpacked, package, PackageMarker.GamesRoot, (1L, 4L));

        var catalog = Catalog();
        catalog.Discover();
        var result = Operations(catalog).DeleteGame("packaged");

        Assert.True(result.Success, result.Error);
        Assert.False(Directory.Exists(unpacked));
        Assert.False(File.Exists(package));
    }

    [Fact]
    public void Deleting_a_managed_game_removes_its_package_and_its_rollback_backups()
    {
        var unpacked = WriteGame(_unpackedRoot, "managed-game");
        var package = ManagedPackageLayout.PackagePath(_managedRoot, "managed-game");
        File.WriteAllBytes(package, [1, 2, 3, 4]);
        PackageMarker.Write(unpacked, package, PackageMarker.ManagedRoot, (1L, 4L));

        var backups = ManagedPackageLayout.BackupDir(_managedRoot, "managed-game");
        Directory.CreateDirectory(backups);
        File.WriteAllBytes(Path.Combine(backups, "1-1.0.0-abc123def456.kbg"), [9, 9]);

        var catalog = Catalog();
        catalog.Discover();
        var result = Operations(catalog).DeleteGame("managed-game");

        Assert.True(result.Success, result.Error);
        Assert.False(Directory.Exists(unpacked));
        Assert.False(File.Exists(package));
        // Retained versions of a game that no longer exists are pure waste — and a rollback target for a
        // game with nothing to roll back to.
        Assert.False(Directory.Exists(backups));
    }

    [Fact]
    public void Deleting_a_game_closes_its_lobbies_first_and_leaves_other_games_running()
    {
        WriteGame(_gamesRoot, "doomed");
        var catalog = Catalog();
        catalog.Discover();

        var doomedLobby = NewLobby("doomed", "p1");
        Connect("p1");
        var otherLobby = NewLobby("survivor", "p2");
        Connect("p2");

        var result = Operations(catalog).DeleteGame("doomed");

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.LobbiesClosed);
        Assert.Null(_lobbies.Get(doomedLobby.Id));
        Assert.NotNull(_lobbies.Get(otherLobby.Id));
    }

    [Fact]
    public void A_game_whose_files_have_already_gone_reports_that_rather_than_claiming_success()
    {
        var dir = WriteGame(_gamesRoot, "vanished");
        var catalog = Catalog();
        catalog.Discover();
        Directory.Delete(dir, recursive: true); // hot-reload hasn't noticed yet

        var result = Operations(catalog).DeleteGame("vanished");
        Assert.False(result.Success);
        Assert.Contains("already gone", result.Error);
    }

    [Fact]
    public void A_game_outside_both_roots_is_refused_rather_than_deleted()
    {
        // Games are found through the catalog, and this server only owns two roots. A directory anywhere
        // else means a configuration we don't understand — deleting it would be the wrong kind of surprise.
        var outside = Path.Combine(_root, "elsewhere");
        WriteGame(outside, "stray");
        var catalog = new GameCatalog([_gamesRoot, _unpackedRoot, outside],
            NullLogger<GameCatalog>.Instance, 1 << 20, 1 << 20);
        catalog.Discover();

        var result = Operations(catalog).DeleteGame("stray");

        Assert.False(result.Success);
        Assert.Contains("outside both the games root", result.Error);
        Assert.True(Directory.Exists(Path.Combine(outside, "stray")));
    }

    [Fact]
    public void A_blocked_delete_changes_nothing_at_all()
    {
        if (OperatingSystem.IsWindows()) return; // chmod is a no-op here; CI covers this on Ubuntu

        WriteGame(_gamesRoot, "readonly-game");
        var catalog = Catalog();
        catalog.Discover();
        var lobby = NewLobby("readonly-game", "p1");
        Connect("p1");

        // What production looks like: the games mount is read-only, so the parent of the game's directory
        // can't be written to.
        File.SetUnixFileMode(_gamesRoot, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var result = Operations(catalog).DeleteGame("readonly-game");

            Assert.False(result.Success);
            Assert.NotNull(result.Blocked);
            Assert.Contains("disable the game instead", result.Error, StringComparison.OrdinalIgnoreCase);
            // The whole point of probing before acting: the lobbies were NOT torn down for a delete that
            // could never have happened.
            Assert.Equal(0, result.LobbiesClosed);
            Assert.NotNull(_lobbies.Get(lobby.Id));
            Assert.True(Directory.Exists(Path.Combine(_gamesRoot, "readonly-game")));
        }
        finally
        {
            File.SetUnixFileMode(_gamesRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task SetBlobQuota_refuses_bytes_exceeding_1_TiB()
    {
        WriteGame(_gamesRoot, "dnd-mapper");
        var catalog = Catalog();
        catalog.Discover();

        var options = new AdminApi.Options(
            Auth: null!,
            Lobbies: _lobbies,
            Closer: null!,
            Catalog: catalog,
            Settings: null!,
            Lifecycle: null!,
            Operations: null!,
            Packages: null!,
            PackageOptions: null!,
            PackageLimits: null!,
            Marketplace: null,
            Updates: null,
            Scheduler: null,
            Logs: null!,
            Disk: null!,
            Relay: null!,
            Authority: null!,
            History: null!,
            MetricSampleSeconds: 0,
            Limits: null!,
            AuthorityLimits: null!,
            BlobLimits: null!,
            Blobs: null,
            Webhooks: null,
            WebhookLog: null,
            WebhookOptions: null!,
            Connections: null!,
            Authorities: null,
            Paths: null!,
            Diagnostics: null!,
            Time: _clock,
            Logger: NullLogger<AdminOperationsTests>.Instance,
            LoginAttemptsPerMinutePerIp: 0,
            LoginAttemptsPerMinuteGlobal: 0,
            CookieAlwaysSecure: false,
            StaleAfter: TimeSpan.Zero);

        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        const long overLimit = 1024L * 1024 * 1024 * 1024 + 1;
        var req = new AdminBlobQuotaRequest("dnd-mapper", overLimit);
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            req, Serialization.KnockBoxProtocolContext.Default.AdminBlobQuotaRequest);
        ctx.Request.ContentType = "application/json";
        ctx.Request.Body = new MemoryStream(bytes);

        await AdminApi.SetBlobQuota(ctx, options);

        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        ctx.Response.Body.Position = 0;
        var response = await System.Text.Json.JsonSerializer.DeserializeAsync(
            ctx.Response.Body, Serialization.KnockBoxProtocolContext.Default.AdminApiResponse, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(response);
        Assert.False(response.Success);
        Assert.Contains("1 TiB", response.Error);
    }
}
