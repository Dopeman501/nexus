using System.Net;
using Nexus.Overlay.Media;

namespace Nexus.Overlay.Tests.NativeOwners;

public sealed class IngestOwnershipTests
{
    [Fact]
    public async Task Fault_before_subscription_is_delivered_once_and_rejected_payload_is_returned()
    {
        using var client = new IngestClient("http://127.0.0.1", "synthetic", "test-only", handler: new FailedHandler());
        await client.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        int notified = 0, returned = 0;
        client.Faulted = () => notified++;
        client.Faulted = () => notified++;
        client.Send([1], true, _ => returned++);
        Assert.Equal(1, notified); Assert.Equal(1, returned);
        client.Dispose(); client.Dispose();
    }

    [Fact]
    public async Task Capture_offers_do_not_extend_the_blocked_write_deadline()
    {
        var clock = new BoundedIngestTests.ManualClock();
        using var handler = new PausedHandler();
        using var client = new IngestClient("http://127.0.0.1", "synthetic", "test-only",
            handler: handler, rawFrames: true, clock: clock);
        var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int returned = 0;
        client.Faulted = () => faulted.TrySetResult();
        try
        {
            client.Send([1], true, _ => returned++);
            await handler.Stream.Entered.Task;
            await clock.TwoTimers.WaitAsync(TimeSpan.FromSeconds(5)); // keepalive and actual write deadline are installed
            clock.Advance(TimeSpan.FromSeconds(4));
            client.Send([2], true, _ => returned++);
            clock.Advance(TimeSpan.FromSeconds(1));
            await faulted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, returned); // pending returned; in-flight still belongs to native writer
            Assert.False(client.Completion.IsCompleted);
        }
        finally
        {
            handler.Stream.Release.TrySetResult(); client.Dispose();
            await client.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(2, returned);
    }

    [Fact]
    public async Task Idle_keepalive_writes_one_framed_control_packet()
    {
        var clock = new BoundedIngestTests.ManualClock();
        using var handler = new RecordingHandler();
        using var client = new IngestClient("http://127.0.0.1", "synthetic", "test-only", handler: handler, clock: clock);
        clock.Advance(TimeSpan.FromSeconds(5));
        await handler.Stream.Written.Task.WaitAsync(TimeSpan.FromSeconds(5));
        client.Dispose(); await client.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var bytes = handler.Stream.ToArray();
        Assert.Equal(FrameFraming.HeaderSize, bytes.Length);
        var expected = new byte[FrameFraming.HeaderSize]; FrameFraming.WriteHeader(expected, 0, FrameFraming.FlagControl);
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public async Task A_stalled_raw_writer_replaces_unsent_frames_and_recycles_them()
    {
        using var handler = new PausedHandler();
        using var client = new IngestClient("http://127.0.0.1", "synthetic", "test-only",
            handler: handler, rawFrames: true);
        var returned = 0;
        try
        {
            client.Send(new byte[4], true, _ => Interlocked.Increment(ref returned));
            await handler.Stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var i = 0; i < 3; i++)
                client.Send(new byte[4], true, _ => Interlocked.Increment(ref returned));
            Assert.Equal(2, Volatile.Read(ref returned));
        }
        finally
        {
            client.Dispose();
            handler.Stream.Release.TrySetResult();
            await client.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    internal sealed class PausedHandler : HttpMessageHandler
    {
        internal readonly PausedStream Stream = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            await request.Content!.CopyToAsync(Stream, token);
            return new(HttpStatusCode.OK);
        }
    }

    private sealed class FailedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromException<HttpResponseMessage>(new HttpRequestException("Synthetic refusal"));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal readonly RecordingStream Stream = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { await request.Content!.CopyToAsync(Stream, token); return new(HttpStatusCode.OK); }
    }
    private sealed class RecordingStream : MemoryStream
    {
        internal readonly TaskCompletionSource Written = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
        { await base.WriteAsync(bytes, token); Written.TrySetResult(); }
    }

    internal sealed class PausedStream : MemoryStream
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
        {
            Entered.TrySetResult();
            await Release.Task; // deliberately ignores cancellation
            await base.WriteAsync(bytes, token);
        }
    }
}
