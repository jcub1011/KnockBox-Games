using KnockBox.Server.Networking;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// <see cref="WebSocketHandler.CleanLogText"/> sanitizes the untrusted game-supplied log message
/// before it reaches the sink: control characters (notably CR/LF, which would forge extra log
/// lines) are neutralized, and the length cap can't split a surrogate pair into a lone surrogate.
/// </summary>
public class GameLogSanitizationTests
{
    // Reference the real (internal) cap directly so this can't drift from the production value.
    private const int MaxGameLogLength = WebSocketHandler.MaxGameLogLength;

    [Fact]
    public void Strips_carriage_returns_and_newlines_so_a_game_cannot_forge_log_lines()
    {
        var cleaned = WebSocketHandler.CleanLogText("line1\nFORGED: server crashed\r\nmore");

        Assert.DoesNotContain('\n', cleaned);
        Assert.DoesNotContain('\r', cleaned);
        // Each control char becomes a single space; surrounding text is preserved.
        Assert.Equal("line1 FORGED: server crashed  more", cleaned);
    }

    [Fact]
    public void Strips_other_control_characters_but_keeps_tab()
    {
        var cleaned = WebSocketHandler.CleanLogText("a\0b\tc");

        Assert.Equal("a b\tc", cleaned); // NUL → space, tab survives as ordinary whitespace
    }

    [Fact]
    public void Leaves_ordinary_text_unchanged()
    {
        const string text = "match started: 4 players";

        Assert.Equal(text, WebSocketHandler.CleanLogText(text));
    }

    [Fact]
    public void Truncates_to_the_cap()
    {
        var cleaned = WebSocketHandler.CleanLogText(new string('x', MaxGameLogLength + 500));

        Assert.Equal(MaxGameLogLength, cleaned.Length);
    }

    [Fact]
    public void Does_not_leave_a_lone_surrogate_when_truncation_lands_mid_pair()
    {
        // Fill up to the cap with BMP chars, then place an astral char (a surrogate PAIR) straddling
        // the cut so the naive slice would keep only its high surrogate.
        var text = new string('x', MaxGameLogLength - 1) + "\U0001F600" + "tail";

        var cleaned = WebSocketHandler.CleanLogText(text);

        // The cut backs off by one rather than emitting a lone surrogate, so the result is valid UTF-16.
        Assert.Equal(MaxGameLogLength - 1, cleaned.Length);
        Assert.DoesNotContain(cleaned, char.IsSurrogate);
    }

    [Fact]
    public void Replaces_a_lone_surrogate_so_the_result_is_valid_utf16()
    {
        // A high surrogate with no following low surrogate is malformed UTF-16 — neutralize it.
        var cleaned = WebSocketHandler.CleanLogText("a\uD83Db");

        Assert.Equal("a b", cleaned);
        Assert.DoesNotContain(cleaned, char.IsSurrogate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Null_or_empty_becomes_empty(string? input)
    {
        Assert.Equal(string.Empty, WebSocketHandler.CleanLogText(input));
    }
}
