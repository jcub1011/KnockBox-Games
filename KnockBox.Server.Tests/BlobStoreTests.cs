using System.Security.Cryptography;
using System.Text;
using KnockBox.Server.Games.Blobs;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// The blob store's requirements, in the order they are easiest to break.
/// </summary>
/// <remarks>
/// <para>The first test is the one that matters most: <b>duplicate registrations must be independent
/// handles</b>. Content-addressing makes the naive implementation — refcount per <c>(lobby, hash)</c>
/// pair — pass every other test in this file, then delete a file a lobby is still using the moment the
/// same lobby registers the same bytes twice and releases one of them. Nothing about that failure is
/// visible until a DM's map turns into a broken image mid-session.</para>
/// <para>Time is <em>driven</em> rather than slept through, the <c>AuthorityModuleCacheTests</c>
/// technique: the grace window is measured against the store's own clock, so a
/// <see cref="MutableTimeProvider"/> lets the boundary be asserted exactly instead of approximately.
/// The grace boundary is worth being exact about — an off-by-one there deletes bytes a client is one
/// round trip away from registering.</para>
/// </remarks>
public class BlobStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kb-blobs-" + Guid.NewGuid().ToString("N"));
    private readonly MutableTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly BlobOptionsProvider _options;
    private readonly BlobStore _store;

    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    public BlobStoreTests()
    {
        Directory.CreateDirectory(_root);
        _options = new BlobOptionsProvider(BlobOptions.Default with { Root = _root, Grace = Grace });
        _store = new BlobStore(_options, _clock, NullLogger<BlobStore>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ── R6: dedup is invisible to the game ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Two_logical_ids_in_one_lobby_are_released_independently()
    {
        var hash = await Upload("a battlemap");

        Assert.Equal(BlobRegisterOutcome.Registered, _store.Register("L1", "g", "map-a", hash).Outcome);
        // Same bytes, same lobby, different name. The game asked for two blobs and is entitled to two
        // handles, however many files back them.
        Assert.Equal(BlobRegisterOutcome.Registered, _store.Register("L1", "g", "map-b", hash).Outcome);

        _store.Unregister("L1", "map-b");
        Assert.True(File.Exists(Path.Combine(_root, hash[..2], hash)),
            "'map-a' still holds this content — releasing 'map-b' must not have deleted it.");

        _store.ReleaseLobby("L1");
        Assert.False(File.Exists(Path.Combine(_root, hash[..2], hash)));
    }

    [Fact]
    public async Task Identical_bytes_registered_by_two_lobbies_are_stored_once()
    {
        var first = await Upload("shared art", lobbyId: "L1");
        // The second lobby is told the server already has the bytes, so a client can skip the upload
        // entirely — which is the whole point of the HEAD probe.
        var again = await Receive("shared art", lobbyId: "L2");
        Assert.Equal(BlobIngestOutcome.AlreadyPresent, again.Outcome);

        _store.Register("L1", "g", "bg", first);
        _store.Register("L2", "g", "bg", first);

        Assert.Single(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
        Assert.Equal(1, _store.ContentCount);
        Assert.Equal(2, _store.HandleCount);

        // Closing one lobby must not disturb the other, even though one file backs both.
        _store.ReleaseLobby("L1");
        Assert.True(File.Exists(Path.Combine(_root, first[..2], first)));
        _store.ReleaseLobby("L2");
        Assert.False(File.Exists(Path.Combine(_root, first[..2], first)));
    }

    [Fact]
    public async Task Re_registering_the_same_name_and_hash_does_not_take_a_second_reference()
    {
        var hash = await Upload("map");
        _store.Register("L1", "g", "map", hash);

        // A client that retried after a dropped response must not leak a reference. If it did, the
        // single Unregister below would leave the count at one and the file would outlive its lobby.
        Assert.Equal(BlobRegisterOutcome.AlreadyRegistered, _store.Register("L1", "g", "map", hash).Outcome);
        Assert.Equal(1, _store.HandleCount);

        _store.Unregister("L1", "map");
        _clock.Advance(Grace);
        _store.Sweep();
        Assert.False(File.Exists(Path.Combine(_root, hash[..2], hash)));
    }

    [Fact]
    public async Task Pointing_a_name_at_different_content_releases_the_old_content()
    {
        var first = await Upload("first map");
        var second = await Upload("second map");

        _store.Register("L1", "g", "active", first);
        _store.Register("L1", "g", "active", second);

        _clock.Advance(Grace);
        _store.Sweep();

        Assert.False(File.Exists(Path.Combine(_root, first[..2], first)), "the replaced content is unreferenced");
        Assert.True(File.Exists(Path.Combine(_root, second[..2], second)));
        Assert.Equal(1, _store.HandleCount);
    }

    // ── R5: unregister is available but never required ───────────────────────────────────────────────

    [Fact]
    public void Unregistering_something_that_was_never_registered_is_not_an_error()
    {
        // Games must be able to call this defensively without tracking what they registered, so an
        // unknown handle reports "nothing released" rather than throwing or faulting the request.
        Assert.False(_store.Unregister("no-such-lobby", "no-such-name"));

        _store.ReleaseLobby("no-such-lobby");
    }

    [Fact]
    public async Task Closing_a_lobby_releases_every_handle_it_holds()
    {
        var a = await Upload("map a");
        var b = await Upload("map b");
        _store.Register("L1", "g", "a", a);
        _store.Register("L1", "g", "b", b);

        // No game-side cleanup call anywhere in this test: that is R4. A crashed or abandoned session
        // never runs its own teardown, so anchoring lifetime to the lobby is what makes a leak
        // structurally impossible rather than merely unlikely.
        _store.ReleaseLobby("L1");

        Assert.Equal(0, _store.HandleCount);
        Assert.Equal(0, _store.ContentCount);
        Assert.Equal(0, _store.TotalBytes);
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    // ── The grace window ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Uploaded_bytes_survive_a_sweep_until_their_grace_window_expires()
    {
        // Uploaded and never registered: refcount is zero, which is exactly what an upload looks like
        // in the round trip before the client registers it. Deleting here would delete bytes the client
        // is about to claim.
        var hash = await Upload("just uploaded");
        var path = Path.Combine(_root, hash[..2], hash);

        // One tick short of the window keeps it. The boundary is the interesting part: an off-by-one
        // here is a race that only shows up under load.
        _clock.Advance(Grace - TimeSpan.FromTicks(1));
        Assert.Empty(_store.Sweep());
        Assert.True(File.Exists(path));

        _clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(new[] { hash }, _store.Sweep());
        Assert.False(File.Exists(path));
        Assert.Equal(1, _store.Evicted);
    }

    [Fact]
    public async Task Registering_inside_the_grace_window_saves_the_bytes_from_the_sweep()
    {
        var hash = await Upload("claimed in time");

        _clock.Advance(Grace - TimeSpan.FromMinutes(1));
        _store.Register("L1", "g", "map", hash);

        _clock.Advance(Grace * 10);
        Assert.Empty(_store.Sweep());
        Assert.True(File.Exists(Path.Combine(_root, hash[..2], hash)),
            "a referenced blob is never idle-evicted: the refcount is exact, so there is no window to age out of");
    }

    [Fact]
    public async Task Releasing_a_blob_deletes_it_even_though_its_grace_window_is_still_open()
    {
        var hash = await Upload("released early");
        var path = Path.Combine(_root, hash[..2], hash);
        _store.Register("L1", "g", "map", hash);

        // The grace window covers the gap before the FIRST handle, and nothing after it. Honouring the
        // original window here would mean upload-register-release inside one window leaves the bytes on
        // disk — so the last lobby to close would free nothing, which is R4 failing silently. The clock
        // is deliberately not advanced: this must delete on the release itself.
        _store.Unregister("L1", "map");
        Assert.False(File.Exists(path));
        Assert.Equal(0, _store.TotalBytes);
    }

    [Fact]
    public async Task Releasing_a_blob_deletes_it_without_waiting_for_a_sweep()
    {
        var hash = await Upload("long lived");
        var path = Path.Combine(_root, hash[..2], hash);
        _store.Register("L1", "g", "map", hash);
        _clock.Advance(Grace * 2);

        // The refcount is the mechanism and the sweep is only a backstop, so the last release deletes
        // immediately. If this needed a sweep, a server with the sweeper switched off would leak.
        _store.Unregister("L1", "map");
        Assert.False(File.Exists(path));
    }

    // ── Read tokens ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_token_resolves_to_the_content_it_was_minted_for()
    {
        var hash = await Upload("art", contentType: "image/png");
        var token = _store.Register("L1", "g", "map", hash, "image/png").Token;

        Assert.True(_store.TryResolveToken(token, out var target));
        Assert.Equal((hash, $"{hash[..2]}/{hash}", "image/png"),
            (target.Sha256, target.RelativePath, target.ContentType));
    }

    [Fact]
    public async Task A_bare_hash_is_not_a_read_key()
    {
        // The whole reason reads are keyed on a MAC rather than the hash: a hash is derived from the
        // bytes, so anyone holding the same commercial map pack can compute it. If this ever passes, an
        // unauthenticated caller can probe which maps a DM uploaded.
        var hash = await Upload("secret map");
        _store.Register("L1", "g", "map", hash);

        Assert.False(_store.TryResolveToken(hash, out _));
        Assert.False(_store.TryResolveToken($"{hash}.", out _));
        Assert.False(_store.TryResolveToken($"{hash}.{new string('0', 32)}", out _));
    }

    [Fact]
    public async Task A_token_minted_under_a_different_secret_does_not_verify()
    {
        var hash = await Upload("art");
        _store.Register("L1", "g", "map", hash);

        // A second store is a second per-process secret, so its token is a forgery from this one's
        // point of view — which is what makes the secret load-bearing rather than decorative.
        var other = new BlobStore(_options, _clock, NullLogger<BlobStore>.Instance);
        Assert.False(_store.TryResolveToken(other.TokenFor(hash), out _));
    }

    [Fact]
    public async Task Content_that_is_no_longer_stored_does_not_resolve()
    {
        var hash = await Upload("transient");
        var token = _store.TokenFor(hash);
        Assert.True(_store.TryResolveToken(token, out _));

        _clock.Advance(Grace);
        _store.Sweep();

        // The token is still a valid MAC — it always will be, for the life of the process. Resolution
        // has to check that the content is actually there, or a stale URL serves a 500 off a missing
        // file instead of a 404.
        Assert.False(_store.TryResolveToken(token, out _));
    }

    [Fact]
    public void An_unknown_hash_cannot_be_registered()
    {
        var hash = HashOf("never uploaded");
        var result = _store.Register("L1", "g", "map", hash);

        Assert.Equal(BlobRegisterOutcome.UnknownHash, result.Outcome);
        Assert.Null(result.Token);
    }

    [Fact]
    public async Task A_handle_can_be_looked_up_by_the_name_the_game_gave_it()
    {
        var hash = await Upload("art");
        Assert.Null(_store.TokenForHandle("L1", "map"));

        var registered = _store.Register("L1", "g", "map", hash).Token;
        Assert.Equal(registered, _store.TokenForHandle("L1", "map"));
        // Scoped to the lobby: one session's name never resolves in another's.
        Assert.Null(_store.TokenForHandle("L2", "map"));
    }

    // ── Input validation ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("../../etc/passwd")]
    // Uppercase is refused rather than folded: the server writes hashes lowercase, so accepting both
    // casings would put the same bytes at two file names on a case-sensitive filesystem and dedup would
    // quietly stop working.
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    public async Task A_malformed_blob_id_is_refused(string hash)
    {
        var upload = await _store.ReceiveAsync("L1", "g", hash, null, new MemoryStream([1, 2, 3]), TestContext.Current.CancellationToken);
        Assert.Equal(BlobIngestOutcome.HashRejected, upload.Outcome);
        Assert.Equal(BlobRegisterOutcome.HashRejected, _store.Register("L1", "g", "map", hash).Outcome);

        // Nothing was created anywhere: a rejected id must not reach the filesystem at all, which is
        // what makes the "hashes are safe to combine with a root" argument hold.
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_malformed_blob_name_is_refused()
    {
        var hash = await Upload("art");

        Assert.Equal(BlobRegisterOutcome.LogicalIdRejected, _store.Register("L1", "g", "", hash).Outcome);
        Assert.Equal(BlobRegisterOutcome.LogicalIdRejected,
            _store.Register("L1", "g", new string('x', BlobStore.MaxLogicalIdLength + 1), hash).Outcome);
        Assert.Equal(BlobRegisterOutcome.LogicalIdRejected, _store.Register("L1", "g", "map\n", hash).Outcome);

        // A name is a dictionary key and never a path, so anything printable is a game's business.
        Assert.Equal(BlobRegisterOutcome.Registered, _store.Register("L1", "g", "maps/城/a b.png", hash).Outcome);
    }

    // ── Hash verification ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Bytes_that_do_not_match_the_declared_hash_are_refused_and_leave_nothing_behind()
    {
        // Poisoning a well-known hash is the one real attack content-addressing admits: publish
        // arbitrary bytes under the hash of a popular battlemap and every session that dedups against
        // it serves your bytes instead. Verifying the hash is the entire defence.
        var declared = HashOf("the map everyone has");
        var result = await _store.ReceiveAsync(
            "L1", "g", declared, null, new MemoryStream(Encoding.UTF8.GetBytes("something else")),
            TestContext.Current.CancellationToken);

        Assert.Equal(BlobIngestOutcome.HashMismatch, result.Outcome);
        Assert.False(File.Exists(Path.Combine(_root, declared[..2], declared)));
        // Including the staging file. A refusal that leaves a .part behind fills the disk on retry.
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
        Assert.Equal(0, _store.TotalBytes);
    }

    // ── Caps and quotas ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_blob_past_the_per_blob_cap_is_refused_mid_stream()
    {
        _options.Apply(new OperatorBlobOptions(MaxBlobBytes: 8));

        var bytes = Encoding.UTF8.GetBytes("far more than eight bytes");
        var result = await _store.ReceiveAsync("L1", "g", HashOf(bytes), null, new MemoryStream(bytes), TestContext.Current.CancellationToken);

        Assert.Equal(BlobIngestOutcome.TooLarge, result.Outcome);
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_lobby_past_its_quota_is_refused_while_another_lobby_is_not()
    {
        // Equal-length blobs on purpose. With a quota set to exactly one of them, L1 (holding one
        // already) is over and L2 (holding none) is exactly at the line — so the ONLY thing separating
        // the two outcomes is which lobby is being charged, which is what this test is about.
        var first = await Upload("blob one");
        _store.Register("L1", "g", "a", first);
        _options.Apply(new OperatorBlobOptions(LobbyQuotaBytes: _store.TotalBytes));

        var bytes = Encoding.UTF8.GetBytes("blob two");
        Assert.Equal(BlobIngestOutcome.LobbyQuotaExceeded,
            (await _store.ReceiveAsync("L1", "g", HashOf(bytes), null, new MemoryStream(bytes), TestContext.Current.CancellationToken)).Outcome);

        // The quota is per lobby, so a different session is unaffected by L1 filling its own.
        Assert.Equal(BlobIngestOutcome.Stored,
            (await _store.ReceiveAsync("L2", "g", HashOf(bytes), null, new MemoryStream(bytes), TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task A_per_game_override_raises_the_quota_for_that_game_only()
    {
        _options.Apply(new OperatorBlobOptions(LobbyQuotaBytes: 4));
        _options.ApplyPerGameQuotas(new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["dnd-mapper"] = 1024,
        });

        var bytes = Encoding.UTF8.GetBytes("more than four bytes");
        Assert.Equal(BlobIngestOutcome.Stored,
            (await _store.ReceiveAsync("L1", "dnd-mapper", HashOf(bytes), null, new MemoryStream(bytes), TestContext.Current.CancellationToken)).Outcome);
        // Game ids are compared case-insensitively everywhere else in this server, and an override
        // that missed because an operator typed the id in a different case would be a bad surprise.
        Assert.Equal(BlobIngestOutcome.AlreadyPresent,
            (await _store.ReceiveAsync("L2", "DND-Mapper", HashOf(bytes), null, new MemoryStream(bytes), TestContext.Current.CancellationToken)).Outcome);

        var other = Encoding.UTF8.GetBytes("also more than four bytes");
        Assert.Equal(BlobIngestOutcome.LobbyQuotaExceeded,
            (await _store.ReceiveAsync("L3", "word-game", HashOf(other), null, new MemoryStream(other), TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task A_lobby_is_charged_once_for_content_it_registers_twice()
    {
        var hash = await Upload("one file, two names");
        var length = _store.TotalBytes;
        // Exactly enough room for one copy. Charging the second handle would put this over.
        _options.Apply(new OperatorBlobOptions(LobbyQuotaBytes: length));

        Assert.Equal(BlobRegisterOutcome.Registered, _store.Register("L1", "g", "map-a", hash).Outcome);
        Assert.Equal(BlobRegisterOutcome.Registered, _store.Register("L1", "g", "map-b", hash).Outcome);
    }

    [Fact]
    public async Task Registering_past_the_quota_is_refused_without_disturbing_the_existing_handle()
    {
        var small = await Upload("s");
        var large = await Upload("a considerably longer blob than the first one");
        _store.Register("L1", "g", "active", small);
        _options.Apply(new OperatorBlobOptions(LobbyQuotaBytes: 4));

        // Rehoming 'active' onto content too big for the quota must refuse — and must leave the old
        // handle exactly as it was. A rollback after releasing is impossible (the release can delete
        // the file), so the check has to happen before anything mutates.
        Assert.Equal(BlobRegisterOutcome.LobbyQuotaExceeded,
            _store.Register("L1", "g", "active", large).Outcome);
        Assert.Equal(_store.TokenFor(small), _store.TokenForHandle("L1", "active"));
        Assert.True(File.Exists(Path.Combine(_root, small[..2], small)));
    }

    [Fact]
    public async Task The_server_wide_quota_bounds_what_every_lobby_can_store_together()
    {
        var first = await Upload("first", lobbyId: "L1");
        _store.Register("L1", "g", "a", first);
        _options.Apply(new OperatorBlobOptions(TotalQuotaBytes: _store.TotalBytes));

        // Without an aggregate cap the per-lobby number is just N × cap, which is what makes
        // AuthorityWordService's per-file-only limit a cautionary example rather than a precedent.
        var bytes = Encoding.UTF8.GetBytes("second");
        Assert.Equal(BlobIngestOutcome.TotalQuotaExceeded,
            (await _store.ReceiveAsync("L2", "g", HashOf(bytes), null, new MemoryStream(bytes), TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task A_cap_of_zero_disables_that_check_rather_than_refusing_everything()
    {
        // The MaxPackageBytes=0 bug, which bit this repo twice: a value documented as "no limit" read
        // literally as a zero-byte ceiling, refusing every upload while complaining about a 0-byte
        // limit. Every one of these three has its own enforcement point, so every one needs its own
        // assertion.
        _options.Apply(new OperatorBlobOptions(MaxBlobBytes: 0, LobbyQuotaBytes: 0, TotalQuotaBytes: 0));

        var bytes = Encoding.UTF8.GetBytes("some bytes past a literal zero ceiling");
        Assert.Equal(BlobIngestOutcome.Stored,
            (await _store.ReceiveAsync("L1", "g", HashOf(bytes), null, new MemoryStream(bytes), TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(BlobRegisterOutcome.Registered,
            _store.Register("L1", "g", "map", HashOf(bytes)).Outcome);
    }

    [Fact]
    public async Task A_lobby_past_its_in_flight_upload_cap_is_refused()
    {
        _options.Apply(new OperatorBlobOptions(MaxUploadsPerLobby: 1));

        // The abandoned-PUT hole: grace is claimed before the first byte and keyed by the URL's hash, so
        // a client that opens an upload and sends nothing pins a grace-protected entry. Bounded and
        // self-expiring, but without this cap it could be used to churn the table.
        var held = new BlockingStream();
        var first = _store.ReceiveAsync("L1", "g", HashOf("held open"), null, held, TestContext.Current.CancellationToken);

        var bytes = Encoding.UTF8.GetBytes("second");
        Assert.Equal(BlobIngestOutcome.TooManyUploads,
            (await _store.ReceiveAsync("L1", "g", HashOf(bytes), null, new MemoryStream(bytes), TestContext.Current.CancellationToken)).Outcome);
        // Per lobby, not server-wide: one session cannot starve another.
        Assert.Equal(BlobIngestOutcome.Stored,
            (await _store.ReceiveAsync("L2", "g", HashOf(bytes), null, new MemoryStream(bytes), TestContext.Current.CancellationToken)).Outcome);

        held.Finish(Encoding.UTF8.GetBytes("held open"));
        Assert.Equal(BlobIngestOutcome.Stored, (await first).Outcome);

        // And the slot is released once the upload finishes, so the cap bounds concurrency rather than
        // total uploads.
        var third = Encoding.UTF8.GetBytes("third");
        Assert.Equal(BlobIngestOutcome.Stored,
            (await _store.ReceiveAsync("L1", "g", HashOf(third), null, new MemoryStream(third), TestContext.Current.CancellationToken)).Outcome);
    }

    // ── Disabled ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_disabled_store_refuses_writes_with_a_reason_rather_than_disappearing()
    {
        var disabled = new BlobStore(
            new BlobOptionsProvider(BlobOptions.Default with { Root = _root, Enabled = false }),
            _clock, NullLogger<BlobStore>.Instance);

        // The house rule: a switched-off feature is a refusal that explains itself, never a route that
        // is not there. A game gets a message it can show a player instead of an unexplained 404.
        Assert.NotNull(disabled.WriteBlockedReason());

        var bytes = Encoding.UTF8.GetBytes("art");
        var upload = await disabled.ReceiveAsync("L1", "g", HashOf(bytes), null, new MemoryStream(bytes), TestContext.Current.CancellationToken);
        Assert.Equal(BlobIngestOutcome.Disabled, upload.Outcome);
        Assert.NotNull(upload.Error);
        Assert.Equal(BlobRegisterOutcome.Disabled, disabled.Register("L1", "g", "map", HashOf(bytes)).Outcome);
    }

    [Fact]
    public async Task A_missing_blob_root_is_reported_rather_than_created_silently()
    {
        var hash = await Upload("art");
        Directory.Delete(_root, recursive: true);

        // Program.cs creates the root during bootstrap and reports a failure to the operator there. If
        // the store recreated it on demand, a mount that failed to attach would present as "every image
        // vanished on restart" instead of a startup warning naming the directory.
        Assert.NotNull(_store.WriteBlockedReason());
        Assert.Equal(BlobRegisterOutcome.Disabled, _store.Register("L1", "g", "map", hash).Outcome);
    }

    // ── Startup ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_startup_sweep_clears_the_whole_root()
    {
        var hash = await Upload("survivor?");
        _store.Register("L1", "g", "map", hash);
        Directory.CreateDirectory(BlobLayout.StagingDir(_root));
        File.WriteAllText(BlobLayout.StagingPath(_root, Guid.NewGuid()), "an interrupted upload");

        // Lobbies are in-memory and die with the process, and the ticket secret is regenerated per
        // process, so after a restart every blob on disk is orphaned BY DEFINITION — there is no live
        // handle that could ever reference one again. Startup is also the only moment nothing can be
        // using one, which is the same argument PackageManager.SweepStaging makes for itself.
        var fresh = new BlobStore(_options, _clock, NullLogger<BlobStore>.Instance);
        fresh.SweepAtStartup();

        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
        Assert.True(Directory.Exists(_root), "the root itself is a mount and must survive");
    }

    [Fact]
    public void The_startup_sweep_tolerates_a_root_that_does_not_exist_yet()
    {
        Directory.Delete(_root, recursive: true);
        _store.SweepAtStartup();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────

    private async Task<string> Upload(string content, string lobbyId = "L1", string? contentType = null)
    {
        var result = await Receive(content, lobbyId, contentType);
        Assert.Equal(BlobIngestOutcome.Stored, result.Outcome);
        return HashOf(content);
    }

    private Task<BlobIngestResult> Receive(
        string content, string lobbyId = "L1", string? contentType = null, string gameId = "g")
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return _store.ReceiveAsync(lobbyId, gameId, HashOf(bytes), contentType, new MemoryStream(bytes),
            TestContext.Current.CancellationToken);
    }

    private static string HashOf(string content) => HashOf(Encoding.UTF8.GetBytes(content));

    private static string HashOf(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// A request body that stays open until the test says otherwise, so "two uploads at once" is a real
    /// state rather than a simulated one. Hand-rolled for the same reason <c>FakeWebSocket</c> is: this
    /// project fakes its collaborators directly rather than taking on a mocking library.
    /// </summary>
    private sealed class BlockingStream : Stream
    {
        private readonly TaskCompletionSource<byte[]> _body = new();
        private int _offset;

        public void Finish(byte[] body) => _body.TrySetResult(body);

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var body = await _body.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            var remaining = body.Length - _offset;
            if (remaining <= 0) return 0;

            var take = Math.Min(remaining, buffer.Length);
            body.AsMemory(_offset, take).CopyTo(buffer);
            _offset += take;
            return take;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
