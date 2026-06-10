using KnockBox.Server.Networking;
using Xunit;

namespace KnockBox.Server.Tests;

public class ConnectionTests
{
    // Comfortably larger than the outbound capacity so overflow is guaranteed regardless of the exact cap.
    private const int Overflow = 5000;

    private static byte[] Frame(int i) => BitConverter.GetBytes(i);
    private static int Decode(byte[] b) => BitConverter.ToInt32(b);

    [Fact]
    public async Task DropOldest_evicts_oldest_frames_and_keeps_the_newest()
    {
        var socket = new FakeWebSocket();
        var conn = new Connection("p1", "Ann", socket, OutboundOverflow.DropOldest);

        // No reader draining yet, so the bounded channel fills and evicts the oldest writes.
        for (var i = 0; i < Overflow; i++) conn.Send(Frame(i));

        conn.CompleteOutbound();
        await conn.SendLoopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(socket.Sent.Count < Overflow, "some frames should have been dropped");
        Assert.Equal(Overflow - 1, Decode(socket.Sent[^1]));         // newest frame survives
        Assert.DoesNotContain(socket.Sent, b => Decode(b) == 0);     // oldest frame was evicted
    }

    [Fact]
    public async Task CloseOnFull_completes_the_send_loop_when_the_queue_overflows()
    {
        var socket = new FakeWebSocket();
        var conn = new Connection("p1", "Ann", socket, OutboundOverflow.CloseOnFull);

        // A stuck control socket: nothing drains, the queue overflows, the writer is auto-completed.
        for (var i = 0; i < Overflow; i++) conn.Send(Frame(i));

        // Crucially we do NOT call CompleteOutbound here — the loop must end on its own, proving the
        // overflow tore the connection down (so the owning handler will clean it up).
        var loop = conn.SendLoopAsync(CancellationToken.None);
        await loop.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(loop.IsCompletedSuccessfully);
        Assert.True(socket.Sent.Count < Overflow, "frames past the cap are lost on a stuck socket");
    }

    [Fact]
    public async Task Healthy_connection_delivers_every_frame_in_order()
    {
        var socket = new FakeWebSocket();
        var conn = new Connection("p1", "Ann", socket, OutboundOverflow.DropOldest);

        var loop = conn.SendLoopAsync(CancellationToken.None); // draining keeps the queue from filling
        for (var i = 0; i < 100; i++) conn.Send(Frame(i));
        conn.CompleteOutbound();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(100, socket.Sent.Count);
        Assert.Equal(Enumerable.Range(0, 100), socket.Sent.Select(Decode));
    }
}
