using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Xunit;

namespace Nexus.Overlay.Tests;

public sealed class BrowserProcessOwnershipTests
{
    [Fact]
    public async Task Actual_environment_membership_does_not_require_native_overlay_ancestry()
    {
        var registry = new BrowserProcessRegistry();
        using var registration = registry.Register(BrowserProfile.Desktop, new Source([new(40012, 0), new(40808, 4)]), new InlineContext());
        var groups = await registry.CollectAsync(CancellationToken.None);
        var desktop = Assert.Single(groups);
        Assert.True(desktop.Available);
        Assert.Equal(new[] { 40012, 40808 }, Array.ConvertAll(desktop.Processes, p => p.ProcessId));
    }

    [Fact]
    public async Task Shared_profile_uses_one_environment_and_a_live_sibling_survives_retirement()
    {
        var registry = new BrowserProcessRegistry();
        var first = new Source([new(1, 0)]); var second = new Source([new(2, 0)]);
        var a = registry.Register(BrowserProfile.Desktop, first, new InlineContext());
        using var b = registry.Register(BrowserProfile.Desktop, second, new InlineContext());
        Assert.Single(await registry.CollectAsync(default));
        Assert.Equal(1, first.Reads); Assert.Equal(0, second.Reads);
        a.Dispose(); a.Dispose();
        Assert.Equal(2, Assert.Single((await registry.CollectAsync(default))[0].Processes).ProcessId);
        Assert.Equal(1, first.Disposals);
    }

    [Fact]
    public async Task Unsupported_environment_falls_back_to_same_profile_sibling_and_keeps_stream_separate()
    {
        var registry = new BrowserProcessRegistry();
        using var a = registry.Register(BrowserProfile.Desktop, new Source(null), new InlineContext());
        using var b = registry.Register(BrowserProfile.Desktop, new Source([new(1, 0)]), new InlineContext());
        using var c = registry.Register(BrowserProfile.Stream, new Source([new(2, 0)]), new InlineContext());
        Assert.Equal(2, (await registry.CollectAsync(default)).Length);
    }

    [Fact]
    public async Task Compatibility_kiosk_and_desktop_udfs_are_both_retained_as_distinct_groups()
    {
        var registry = new BrowserProcessRegistry();
        using var desktop = registry.Register(BrowserProfile.Desktop, new Source([new(40012, 0)]), new InlineContext());
        using var stream = registry.Register(BrowserProfile.Stream, new Source([new(27488, 0)]), new InlineContext());
        var compatibilityProfile = BrowserProcessRegistry.KioskProfile(true);
        using var compatibility = compatibilityProfile.HasValue
            ? registry.Register(compatibilityProfile.Value, new Source([new(60012, 0)]), new InlineContext()) : null;
        var groups = await registry.CollectAsync(default);
        Assert.Equal(3, groups.Length);
        Assert.Contains(groups, group => group.Processes[0].ProcessId == 40012);
        Assert.Contains(groups, group => group.Processes[0].ProcessId == 60012);
        Assert.Equal(BrowserProfile.Desktop, BrowserProcessRegistry.KioskProfile(false));
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(1, -1)] [InlineData(1, 7)]
    public async Task Invalid_descriptors_are_explicitly_unavailable(int id, int kind)
    {
        var registry = new BrowserProcessRegistry();
        using var a = registry.Register(BrowserProfile.Desktop, new Source([new(id, kind)]), new InlineContext());
        Assert.False(Assert.Single(await registry.CollectAsync(default)).Available);
    }

    [Fact]
    public async Task Oversized_duplicate_and_failed_collections_are_unavailable()
    {
        foreach (var source in new[] { new Source(new BrowserProcessInfo[257]), new Source([new(1, 0), new(1, 1)]), new Source(null) { Throws = true } })
        {
            var registry = new BrowserProcessRegistry();
            using var a = registry.Register(BrowserProfile.Desktop, source, new InlineContext());
            Assert.False(Assert.Single(await registry.CollectAsync(default)).Available);
        }
    }

    [Fact]
    public async Task Cancellation_prevents_late_native_read_and_only_one_ui_item_can_wait()
    {
        var registry = new BrowserProcessRegistry(); var context = new QueuedContext(); var source = new Source([new(1, 0)]);
        using var a = registry.Register(BrowserProfile.Desktop, source, context);
        using var canceled = new CancellationTokenSource();
        var pending = registry.CollectAsync(canceled.Token);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(Assert.Single(await registry.CollectAsync(default)).Available);
        Assert.Single(context.Queue);
        context.Drain();
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task Retired_owner_cannot_publish_a_queued_collection()
    {
        var registry = new BrowserProcessRegistry(); var context = new QueuedContext(); var source = new Source([new(1, 0)]);
        var a = registry.Register(BrowserProfile.Desktop, source, context);
        var before = registry.Generation;
        var pending = registry.CollectAsync(default);
        a.Dispose(); context.Drain();
        Assert.Equal(BrowserProcessStatus.Retired, Assert.Single(await pending).Status);
        Assert.Empty(await registry.CollectAsync(default));
        Assert.True(registry.Generation > before); Assert.Equal(1, source.Disposals);
    }

    [Fact]
    public async Task Collection_copies_source_storage_and_retirement_during_read_wins()
    {
        var rows = new[] { new BrowserProcessInfo(1, 0) }; var registry = new BrowserProcessRegistry();
        var source = new Source(rows); IDisposable? registration = null;
        registration = registry.Register(BrowserProfile.Desktop, source, new InlineContext());
        var group = Assert.Single(await registry.CollectAsync(default));
        rows[0] = new(2, 0); Assert.Equal(1, group.Processes[0].ProcessId);
        source.OnRead = () => registration.Dispose();
        Assert.False(Assert.Single(await registry.CollectAsync(default)).Available);
    }

    [Fact]
    public async Task Empty_and_pre_canceled_registry_are_distinct_and_registration_is_bounded()
    {
        var registry = new BrowserProcessRegistry();
        Assert.Empty(await registry.CollectAsync(default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registry.CollectAsync(new CancellationToken(true)));
        Assert.Throws<ArgumentOutOfRangeException>(() => registry.Register((BrowserProfile)99, new Source([]), new InlineContext()));
        var registrations = new List<IDisposable>();
        try
        {
            for (var i = 0; i < 128; i++) registrations.Add(registry.Register(BrowserProfile.Desktop, new Source([]), new InlineContext()));
            Assert.Throws<InvalidOperationException>(() => registry.Register(BrowserProfile.Desktop, new Source([]), new InlineContext()));
        }
        finally { foreach (var a in registrations) a.Dispose(); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Off_thread_retirement_is_bounded_and_released_on_owner_even_if_post_fails(bool throwPost)
    {
        var registry = new BrowserProcessRegistry(); var context = new QueuedContext { ThrowPost = throwPost }; var source = new Source([]);
        var a = registry.Register(BrowserProfile.Desktop, source, context);
        Exception? failure = null;
        var worker = new Thread(() => { try { a.Dispose(); } catch (Exception e) { failure = e; } });
        worker.Start(); worker.Join();
        Assert.Null(failure); Assert.Equal(1, registry.PendingReleaseCount); Assert.Equal(0, source.Disposals);
        using var next = registry.Register(BrowserProfile.Stream, new Source([]), new InlineContext());
        Assert.Equal(0, registry.PendingReleaseCount); Assert.Equal(1, source.Disposals);
        context.ThrowPost = false; context.Drain(); Assert.Equal(1, source.Disposals);
    }

    [Fact]
    public void Failed_release_stays_owned_until_retry_and_wrong_thread_context_cannot_release_com()
    {
        var registry = new BrowserProcessRegistry(); var source = new Source([]) { DisposeThrows = true };
        var a = registry.Register(BrowserProfile.Desktop, source, new InlineContext());
        a.Dispose(); Assert.Equal(1, registry.PendingReleaseCount);
        source.DisposeThrows = false;
        using var next = registry.Register(BrowserProfile.Stream, new Source([]), new InlineContext());
        Assert.Equal(0, registry.PendingReleaseCount);
        var other = new Source([]); var b = registry.Register(BrowserProfile.Desktop, other, new InlineContext());
        var worker = new Thread(b.Dispose); worker.Start(); worker.Join();
        Assert.Equal(1, registry.PendingReleaseCount); Assert.Equal(0, other.Disposals);
        using var final = registry.Register(BrowserProfile.Desktop, new Source([]), new InlineContext());
        Assert.Equal(0, registry.PendingReleaseCount); Assert.Equal(1, other.Disposals);
    }

    [Fact]
    public async Task Process_born_during_native_enumeration_is_rejected_against_read_start()
    {
        var started = DateTime.UnixEpoch.AddYears(50); var now = started;
        var registry = new BrowserProcessRegistry(() => now);
        var source = new Source([new(1, 0)]) { OnRead = () => now = now.AddMinutes(1) };
        using var registration = registry.Register(BrowserProfile.Desktop, source, new InlineContext());
        var group = Assert.Single(await registry.CollectAsync(default));
        Assert.Equal(started, group.CapturedUtc);
        var sample = BrowserProcessMemory.Sample([group], _ => new(started.AddSeconds(30), "C:/runtime/msedgewebview2.exe", 1, 1));
        Assert.False(sample.Complete);
    }

    private sealed class Source(BrowserProcessInfo[]? rows) : IBrowserProcessSource
    {
        internal int Reads, Disposals;
        internal bool Throws;
        internal bool DisposeThrows;
        internal Action? OnRead;
        public BrowserProcessInfo[]? Read() { Reads++; OnRead?.Invoke(); if (Throws) throw new InvalidOperationException(); return rows; }
        public void Dispose() { if (DisposeThrows) throw new InvalidOperationException(); Disposals++; }
    }
    private sealed class InlineContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }
    private sealed class QueuedContext : SynchronizationContext
    {
        internal readonly Queue<Action> Queue = new();
        internal bool ThrowPost;
        public override void Post(SendOrPostCallback d, object? state) { if (ThrowPost) throw new InvalidOperationException(); Queue.Enqueue(() => d(state)); }
        internal void Drain() { while (Queue.TryDequeue(out var next)) next(); }
    }
}
