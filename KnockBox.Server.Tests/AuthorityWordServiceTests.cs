using KnockBox.Server.Games.Words;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnockBox.Server.Tests;

public class AuthorityWordServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kb-words-" + Guid.NewGuid().ToString("N"));

    public AuthorityWordServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } }

    private string WriteWords(string name, params string[] words)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllLines(path, words);
        return path;
    }

    private static AuthorityWordService NewService() => new(NullLogger<AuthorityWordService>.Instance);

    [Fact]
    public void Load_registers_a_pool_reachable_by_game_and_key()
    {
        var svc = NewService();
        svc.Load("game1", "en", WriteWords("en.txt", "apple", "brave", "crane"), caseInsensitive: true);

        var pool = svc.Get("game1", "en");
        Assert.NotNull(pool);
        Assert.Equal(3, pool!.TotalWordCount);
        Assert.True(pool.Contains("APPLE"));
    }

    [Fact]
    public void Get_returns_null_for_unknown_game_or_key()
    {
        var svc = NewService();
        svc.Load("game1", "en", WriteWords("en.txt", "apple"), caseInsensitive: true);

        Assert.Null(svc.Get("game1", "fr"));
        Assert.Null(svc.Get("other", "en"));
    }

    [Fact]
    public void Game_id_lookup_is_case_insensitive()
    {
        var svc = NewService();
        svc.Load("Game1", "en", WriteWords("en.txt", "apple"), caseInsensitive: true);
        Assert.NotNull(svc.Get("game1", "en"));
    }

    [Fact]
    public void Load_is_idempotent()
    {
        var svc = NewService();
        var path = WriteWords("en.txt", "apple");
        svc.Load("game1", "en", path, caseInsensitive: true);
        var first = svc.Get("game1", "en");
        svc.Load("game1", "en", path, caseInsensitive: true);
        Assert.Same(first, svc.Get("game1", "en"));
    }

    [Fact]
    public void Identical_files_are_shared_across_games()
    {
        var svc = NewService();
        var path = WriteWords("shared.txt", "apple", "brave");
        svc.Load("game1", "en", path, caseInsensitive: true);
        svc.Load("game2", "words", path, caseInsensitive: true);

        // Same file + same flag -> one built structure, shared.
        Assert.Same(svc.Get("game1", "en"), svc.Get("game2", "words"));
    }

    [Fact]
    public void Same_file_different_case_flag_is_not_shared()
    {
        var svc = NewService();
        var path = WriteWords("shared.txt", "Apple");
        svc.Load("game1", "ci", path, caseInsensitive: true);
        svc.Load("game2", "cs", path, caseInsensitive: false);

        Assert.NotSame(svc.Get("game1", "ci"), svc.Get("game2", "cs"));
        Assert.True(svc.Get("game1", "ci")!.Contains("apple"));  // folded
        Assert.False(svc.Get("game2", "cs")!.Contains("apple")); // exact only
    }

    [Fact]
    public void Load_throws_for_a_missing_file()
    {
        var svc = NewService();
        Assert.Throws<FileNotFoundException>(() =>
            svc.Load("game1", "en", Path.Combine(_dir, "nope.txt"), caseInsensitive: true));
    }
}
