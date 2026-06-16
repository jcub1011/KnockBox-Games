using KnockBox.Contracts;
using KnockBox.Server.Serialization;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// LogMessage travels the SAME source-generated path the live server uses
/// (<see cref="KnockBoxProtocolContext"/>) — distinct from the reflection-based Contracts tests —
/// so this pins that the string-enum converter on <c>Level</c> is honoured by the generator (and
/// stays AOT-safe), not just by runtime reflection.
/// </summary>
public class GameLogSerializationTests
{
    [Fact]
    public void Log_message_round_trips_through_the_source_generated_context()
    {
        IMessage original = new LogMessage(LogLevel.Error, "boom");

        var bytes = JsonSerializer.SerializeToUtf8Bytes(original, KnockBoxProtocolContext.Default.IMessage);
        var json = Encoding.UTF8.GetString(bytes);

        // Level is its readable NAME on the wire so JS clients send "Error", not 4.
        Assert.Contains("\"type\":\"Log\"", json);
        Assert.Contains("\"level\":\"Error\"", json);

        var back = Assert.IsType<LogMessage>(
            JsonSerializer.Deserialize(bytes, KnockBoxProtocolContext.Default.IMessage));
        Assert.Equal(LogLevel.Error, back.Level);
        Assert.Equal("boom", back.Message);
    }

    [Fact]
    public void Incoming_log_frame_with_a_level_name_deserializes_via_the_context()
    {
        // What the JS clients actually put on the wire (camelCase fields, level by name).
        var utf8 = Encoding.UTF8.GetBytes("""{ "type": "Log", "level": "Warning", "message": "careful" }""");

        var back = Assert.IsType<LogMessage>(
            JsonSerializer.Deserialize(utf8, KnockBoxProtocolContext.Default.IMessage));
        Assert.Equal(LogLevel.Warning, back.Level);
        Assert.Equal("careful", back.Message);
    }
}
