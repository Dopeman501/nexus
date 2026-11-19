using Nexus.Overlay.Media;

namespace Nexus.Overlay.Tests.NativeOwners;

public sealed class BoundedIngestTests
{
    [Fact]
    public void Three_total_arrays_remain_reserved_until_actual_return()
    {
        using var pool = new RawFrameBufferPool(1680, 756);
        var frames = Enumerable.Range(0, 3).Select(_ => pool.TryRent()!).ToArray();
        Assert.Equal(3, pool.AllocatedCount);
        Assert.Null(pool.TryRent());
        pool.Return(new byte[1]);
        pool.Return(frames[1]);
        Assert.Same(frames[1], pool.TryRent());
        pool.Dispose();
        Assert.Null(pool.TryRent());
        Assert.False(pool.Completion.IsCompleted);
        foreach (var frame in frames) { pool.Return(frame); pool.Return(frame); }
        Assert.True(pool.Completion.IsCompletedSuccessfully);
        Assert.Equal(0, pool.AllocatedCount);
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(-1, 1)] [InlineData(4096, 4096)]
    public void Invalid_raw_geometry_is_rejected_before_allocation(int width, int height)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new RawFrameBufferPool(width, height));

    [Fact]
    public async Task Raw_latest_replacement_control_and_expiry_have_exactly_one_release()
    {
        var clock = new ManualClock();
        var queue = new IngestPacketQueue(true, clock);
        int returned = 0;
        IngestPacket Frame() => new([1], 0, _ => returned++, clock.GetTimestamp());
        var first = Frame(); var second = Frame();
        Assert.True(queue.Offer(first)); Assert.True(queue.Offer(second));
        Assert.Equal(1, queue.Count); Assert.Equal(1, returned);
        Assert.True(queue.Offer(new([], FrameFraming.FlagControl, _ => returned++, clock.GetTimestamp())));
        Assert.Equal(2, returned);
        var picked = await queue.TakeAsync(default);
        Assert.Same(second, picked); picked!.Dispose(); picked.Dispose();
        Assert.Equal(3, returned);
        Assert.True(queue.Offer(Frame())); clock.Advance(TimeSpan.FromMilliseconds(251));
        var waiting = queue.TakeAsync(default).AsTask();
        Assert.Equal(4, returned); Assert.False(waiting.IsCompleted);
        queue.Complete(); Assert.Null(await waiting);
        Assert.False(queue.Offer(Frame()));
        Assert.Equal(0, queue.QueuedBytes);
    }

    [Fact]
    public void Thousands_of_raw_submissions_keep_one_pending_and_return_every_packet_once()
    {
        var queue = new IngestPacketQueue(true, TimeProvider.System);
        int returned = 0;
        var payload = new byte[16];
        for (int i = 0; i < 10000; i++)
        {
            Assert.True(queue.Offer(new(payload, 0, _ => returned++, 0)));
            Assert.Equal(1, queue.Count); Assert.Equal(16, queue.QueuedBytes);
        }
        Assert.Equal(9999, returned);
        queue.Complete(); queue.Complete(); Assert.Equal(10000, returned);
    }

    [Fact]
    public async Task Encoded_queue_preserves_order_and_faults_on_count_or_byte_budget()
    {
        var queue = new IngestPacketQueue(false, TimeProvider.System);
        var one = new IngestPacket(new byte[16 * 1024 * 1024], 0, null, 0);
        var two = new IngestPacket(new byte[16 * 1024 * 1024], 0, null, 0);
        Assert.True(queue.Offer(one)); Assert.True(queue.Offer(two));
        Assert.False(queue.Offer(new([1], 0, null, 0)));
        Assert.Same(one, await queue.TakeAsync(default)); Assert.Same(two, await queue.TakeAsync(default));
        for (int i = 0; i < 600; i++) Assert.True(queue.Offer(new([], 0, null, 0)));
        Assert.False(queue.Offer(new([], 0, null, 0)));
        queue.Complete(); Assert.Null(await queue.TakeAsync(default));
    }

    [Fact]
    public async Task Five_second_deadline_faults_but_retains_a_cancellation_ignoring_write()
    {
        var clock = new ManualClock();
        using var stream = new IngestOwnershipTests.PausedStream();
        int faulted = 0, returned = 0;
        var signaled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writing = IngestFrameWriter.WriteAsync(stream, new([1, 2], 0, _ => returned++, 0),
            new byte[FrameFraming.HeaderSize], clock, default, () => { faulted++; signaled.TrySetResult(); });
        await stream.Entered.Task;
        clock.Advance(TimeSpan.FromSeconds(5));
        await signaled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, faulted); Assert.Equal(0, returned); Assert.False(writing.IsCompleted);
        stream.Release.SetResult();
        await Assert.ThrowsAsync<TimeoutException>(() => writing);
        Assert.Equal(1, returned);
    }

    internal sealed class ManualClock : TimeProvider
    {
        private long _now;
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private readonly TaskCompletionSource _twoTimers = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task TwoTimers => _twoTimers.Task;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _now);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state); timer.Change(dueTime, period);
            lock (_gate)
            {
                _timers.Add(timer);
                if (_timers.Count >= 2) _twoTimers.TrySetResult();
            }
            return timer;
        }
        internal void Advance(TimeSpan amount)
        {
            Interlocked.Add(ref _now, amount.Ticks);
            ManualTimer[] timers;
            lock (_gate) timers = _timers.ToArray();
            foreach (var timer in timers) timer.Fire();
        }
        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private long _due = long.MaxValue;
            public bool Change(TimeSpan dueTime, TimeSpan period) { Interlocked.Exchange(ref _due, dueTime < TimeSpan.Zero ? long.MaxValue : clock.GetTimestamp() + dueTime.Ticks); return true; }
            internal void Fire()
            {
                long due = Interlocked.Read(ref _due);
                if (due <= clock.GetTimestamp() && Interlocked.CompareExchange(ref _due, long.MaxValue, due) == due) callback(state);
            }
            public void Dispose() => Interlocked.Exchange(ref _due, long.MaxValue);
            public ValueTask DisposeAsync() { Dispose(); return default; }
        }
    }
}
