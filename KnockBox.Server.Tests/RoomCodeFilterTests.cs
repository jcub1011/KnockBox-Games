using KnockBox.Server.Lobbies;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// The room-code blocklist (spec §2.4): substring words, whole-code globs, and the two properties that stop
/// it becoming a foot-gun — an exact count of what it removes, and a report of entries that can never match.
/// </summary>
public class RoomCodeFilterTests
{
    [Fact]
    public void An_empty_filter_blocks_nothing_and_says_so()
    {
        Assert.True(RoomCodeFilter.Empty.IsEmpty);
        Assert.False(RoomCodeFilter.Empty.IsBlocked("ABCD"));
        Assert.Equal(0, RoomCodeFilter.Empty.CountBlocked());
        Assert.True(RoomCodeFilter.Compile(null, null).IsEmpty);
        Assert.True(RoomCodeFilter.Compile([], []).IsEmpty);
    }

    [Fact]
    public void A_word_matches_anywhere_in_the_code()
    {
        var filter = RoomCodeFilter.Compile(["XQ7"], null);

        Assert.True(filter.IsBlocked("XQ7Y"));  // at the start
        Assert.True(filter.IsBlocked("KXQ7"));  // at the end
        Assert.True(filter.IsBlocked("XQ7"));   // exactly
        Assert.False(filter.IsBlocked("ABCD"));
    }

    [Fact]
    public void Matching_ignores_case_because_the_join_side_does()
    {
        // The lobby dictionary is OrdinalIgnoreCase and the shell upper-cases what a player types. A
        // case-sensitive blocklist would be bypassable by typing the code in lower case.
        var filter = RoomCodeFilter.Compile(["kq7"], null);
        Assert.True(filter.IsBlocked("KQ7X"));
        Assert.True(filter.IsBlocked("kq7x"));
        Assert.True(filter.IsBlocked("Kq7X"));
    }

    [Theory]
    [InlineData("Q7??", "Q7ZZ", true)]
    [InlineData("Q7??", "Q7Z", false)]     // ? is exactly one character, not "up to one"
    [InlineData("Q7*", "Q7ZZ", true)]
    [InlineData("Q7*", "Q7", true)]        // * matches an empty run
    [InlineData("*ZZ", "Q7ZZ", true)]
    [InlineData("*ZZ*", "AZZB", true)]
    [InlineData("A*D", "ABCD", true)]
    [InlineData("A*D", "ABCE", false)]
    [InlineData("????", "ABCD", true)]
    [InlineData("???", "ABCD", false)]     // a pattern matches the WHOLE code
    public void A_pattern_matches_the_whole_code_with_wildcards(string pattern, string code, bool blocked)
    {
        Assert.Equal(blocked, RoomCodeFilter.Compile(null, [pattern]).IsBlocked(code));
    }

    [Fact]
    public void Entries_are_normalized_and_de_duplicated()
    {
        var filter = RoomCodeFilter.Compile(["  kq7 ", "KQ7", "kQ7"], ["a*", "A*"]);
        Assert.Equal(["KQ7"], filter.Words);
        Assert.Equal(["A*"], filter.Patterns);
    }

    [Fact]
    public void Compile_drops_junk_rather_than_rejecting_the_whole_list()
    {
        // What a hand-edited settings file can contain. One unusable row must not cost the operator the
        // rest of their blocklist — the same rule marketplace rows and availability overrides follow.
        var filter = RoomCodeFilter.Compile(["K3", "", "   ", "TOOLONG", "b-d", null!], ["A?", "!!"]);
        Assert.Equal(["K3"], filter.Words);
        Assert.Equal(["A?"], filter.Patterns);
    }

    [Fact]
    public void Validate_explains_a_bad_entry_instead_of_silently_dropping_it()
    {
        Assert.Null(RoomCodeFilter.ValidateEntry("KQ7", pattern: false));
        Assert.Null(RoomCodeFilter.ValidateEntry("A*", pattern: true));

        Assert.NotNull(RoomCodeFilter.ValidateEntry("", pattern: false));
        Assert.NotNull(RoomCodeFilter.ValidateEntry("   ", pattern: false));
        // Longer than a code, so it could never occur inside one.
        Assert.Contains("4 characters", RoomCodeFilter.ValidateEntry("TOOLONG", pattern: false)!);
        // Wildcards are for patterns; a word containing one would look like it worked and never match.
        Assert.NotNull(RoomCodeFilter.ValidateEntry("A*", pattern: false));
        Assert.NotNull(RoomCodeFilter.ValidateEntry("A$", pattern: true));
    }

    [Fact]
    public void An_entry_using_letters_the_alphabet_leaves_out_is_reported_as_unreachable()
    {
        // The generator's alphabet has no O, 0, I or 1 — they are too easily misread aloud. Blocking a word
        // containing one is harmless but pointless, and an operator deserves to be told rather than to
        // assume it worked.
        Assert.True(RoomCodeFilter.IsUnreachable("XO"));   // no O in the alphabet
        Assert.True(RoomCodeFilter.IsUnreachable("A1"));   // nor 1
        Assert.False(RoomCodeFilter.IsUnreachable("KQ7"));
        Assert.False(RoomCodeFilter.IsUnreachable("A*"));  // wildcards are not alphabet characters
    }

    [Fact]
    public void The_blocked_count_is_exact()
    {
        var space = RoomCodeFilter.CodeSpaceSize();
        Assert.Equal(32 * 32 * 32 * 32, space);

        // One fully-specified code: exactly one of the possible codes.
        Assert.Equal(1, RoomCodeFilter.Compile(["ABCD"], null).CountBlocked());
        // A leading pair: the remaining two characters are free, so 32².
        Assert.Equal(32 * 32, RoomCodeFilter.Compile(null, ["AB??"]).CountBlocked());
        // A one-character word anywhere in the code: everything except the codes with none of it, which is
        // what makes the exact walk worth having rather than a guess at inclusion-exclusion.
        Assert.Equal(space - (31 * 31 * 31 * 31), RoomCodeFilter.Compile(["A"], null).CountBlocked());
    }

    [Fact]
    public void Overlapping_entries_are_counted_once()
    {
        // "AB" as a word already covers everything "AB??" does. A naive sum would double-count.
        var filter = RoomCodeFilter.Compile(["AB"], ["AB??"]);
        var words = RoomCodeFilter.Compile(["AB"], null);
        Assert.Equal(words.CountBlocked(), filter.CountBlocked());
    }

    [Fact]
    public void The_entry_count_is_capped()
    {
        var many = Enumerable.Range(0, 100).Select(i => $"{(char)('A' + i % 24)}{i % 10}").ToArray();
        Assert.True(RoomCodeFilter.Compile(many, null).Count <= RoomCodeFilter.MaxEntries);
    }

    [Fact]
    public void Compile_reports_what_the_cap_made_it_drop()
    {
        // The count is the ONLY way a caller can tell an over-cap list from one that just fits: the
        // returned filter has already been trimmed, so testing its Count against the cap can never fail.
        // The admin API's over-cap 400 was written that way and was unreachable, so an over-cap save
        // answered 200 and silently discarded the overflow.
        var many = Enumerable.Range(0, RoomCodeFilter.MaxEntries + 5)
            .Select(i => $"{(char)('A' + i % 24)}{i % 10}{i % 8}").ToArray();

        RoomCodeFilter.Compile(many, null, out var dropped);

        Assert.Equal(5, dropped);
        RoomCodeFilter.Compile(many.Take(RoomCodeFilter.MaxEntries), null, out var none);
        Assert.Equal(0, none);
    }

    [Fact]
    public void Trimming_to_the_cap_does_not_empty_out_the_patterns_first()
    {
        // One glob covers a family of codes that would take many words to express, so discarding every
        // pattern before touching a single word traded away the most valuable entries first — while the
        // comment on that loop claimed it kept "its first entries".
        var words = Enumerable.Range(0, RoomCodeFilter.MaxEntries)
            .Select(i => $"{(char)('A' + i % 24)}{i % 10}{i % 8}").ToArray();

        var filter = RoomCodeFilter.Compile(words, ["A*", "B*"], out var dropped);

        Assert.Equal(2, dropped);
        Assert.Equal(RoomCodeFilter.MaxEntries, filter.Count);
        Assert.Equal(2, filter.Patterns.Count);
    }

    [Fact]
    public void A_pattern_may_be_longer_than_a_code_because_its_wildcards_are_syntax()
    {
        // A*B*C is five characters and matches four-character codes perfectly well. Applying the word
        // limit to patterns refused it with a message about code length that is simply untrue of globs —
        // and the portal's own input invites one, since it allows six characters.
        Assert.Null(RoomCodeFilter.ValidateEntry("A*B*C", pattern: true));
        Assert.True(RoomCodeFilter.Compile(null, ["A*B*C"]).Patterns.Count == 1);

        // Still bounded, and still the word limit for a word.
        Assert.NotNull(RoomCodeFilter.ValidateEntry("A*B*C*D", pattern: true));
        Assert.NotNull(RoomCodeFilter.ValidateEntry("ABCDE", pattern: false));
    }
}

/// <summary>The generator's side of the blocklist: it never emits a blocked code, and never spends the
/// collision budget avoiding one.</summary>
public class LobbyCodeGenerationTests
{
    [Fact]
    public void The_generator_never_emits_a_blocked_code()
    {
        // A pattern removing a 32nd of the space, so a naive generator would hit it within a few hundred
        // draws with near-certainty.
        var filter = RoomCodeFilter.Compile(null, ["A*"]);
        var lobbies = new LobbyManager(codeFilter: () => filter);

        for (var i = 0; i < 500; i++)
        {
            Assert.True(lobbies.TryCreate("tictactoe", $"host-{i}", 2, out var lobby));
            Assert.False(lobby.Id.StartsWith('A'));
            lobbies.Remove(lobby.Id);
        }
    }

    [Fact]
    public void A_blocked_draw_does_not_cost_a_collision_attempt()
    {
        // Half the space blocked — the most the admin API allows. Creation must still succeed every time:
        // if a blocked draw consumed one of the five placement attempts, an operator's word list would
        // quietly raise the failure rate of starting a game.
        var filter = RoomCodeFilter.Compile(null, ["A*", "B*", "C*", "D*", "E*", "F*", "G*", "H*",
            "J*", "K*", "L*", "M*", "N*", "P*", "Q*", "R*"]);
        var lobbies = new LobbyManager(codeFilter: () => filter);

        for (var i = 0; i < 200; i++)
        {
            Assert.True(lobbies.TryCreate("tictactoe", $"host-{i}", 2, out var lobby));
            lobbies.Remove(lobby.Id);
        }
    }

    [Fact]
    public void A_blocklist_that_blocks_everything_fails_the_create_rather_than_spinning()
    {
        // Not reachable through the admin API (it refuses anything over 50%), but a hand-edited settings
        // file can do this. The contract is a bounded, reported failure — not a hung request.
        var lobbies = new LobbyManager(codeFilter: () => RoomCodeFilter.Compile(null, ["*"]));
        Assert.False(lobbies.TryCreate("tictactoe", "host", 2, out var lobby));
        Assert.Null(lobby);
    }

    [Fact]
    public void An_edit_applies_to_the_next_code_without_a_restart()
    {
        var filter = RoomCodeFilter.Empty;
        var lobbies = new LobbyManager(codeFilter: () => filter);

        filter = RoomCodeFilter.Compile(null, ["A*"]);
        for (var i = 0; i < 200; i++)
        {
            Assert.True(lobbies.TryCreate("tictactoe", $"host-{i}", 2, out var lobby));
            Assert.False(lobby.Id.StartsWith('A'));
            lobbies.Remove(lobby.Id);
        }
    }
}
