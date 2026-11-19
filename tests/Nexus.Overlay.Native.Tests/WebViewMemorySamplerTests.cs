using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Overlay.Tests.NativeOwners;

public sealed class WebViewMemorySamplerTests
{
    private static readonly BrowserProcessMetrics Metric = new(DateTime.UtcNow.AddHours(-1), "C:/runtime/msedgewebview2.exe", 12 * 1048576, 20 * 1048576);
    [Fact]
    public async Task Environment_owned_broker_processes_emit_complete_host_sample_and_keep_sparse_policy()
    {
        var registry = new BrowserProcessRegistry();
        using var registration = registry.Register(BrowserProfile.Desktop, new Source(), new InlineContext());
        var posts = new List<ClientMemSample>(); var logs = new List<string>(); long now = 0;
        using var sampler = new WebView2MemorySampler(posts.Add, logs.Add, registry, _ => Metric, () => now, false);
        await sampler.TickAsync(); await sampler.TickAsync();
        Assert.Single(posts); Assert.Equal(2, posts[0].Children); Assert.Equal(40, posts[0].TotalWsMB);
        now = 60 * 60_000; await sampler.TickAsync(); Assert.Equal(2, posts.Count);
        Assert.Contains(logs, l => l.Contains("crashpadIncluded=0"));
        Assert.DoesNotContain(logs, l => l.Contains("C:/runtime"));
    }

    [Fact]
    public async Task Working_set_step_emits_without_claiming_a_private_or_gpu_heap()
    {
        var registry = new BrowserProcessRegistry(); using var registration = registry.Register(BrowserProfile.Stream, new Source(), new InlineContext());
        var posts = new List<ClientMemSample>(); long bytes = 20 * 1048576;
        using var sampler = new WebView2MemorySampler(posts.Add, _ => { }, registry, _ => Metric with { WorkingSetBytes = bytes }, () => 0, false);
        await sampler.TickAsync(); bytes += 100 * 1048576; await sampler.TickAsync();
        Assert.Equal(2, posts.Count); Assert.Equal(240, posts[1].TotalWsMB);
    }

    [Fact]
    public async Task Unreadable_or_unsupported_membership_is_not_posted_as_a_healthy_partial_sum()
    {
        var registry = new BrowserProcessRegistry(); using var registration = registry.Register(BrowserProfile.Stream, new Source(), new InlineContext());
        var posts = new List<ClientMemSample>(); var logs = new List<string>();
        using var sampler = new WebView2MemorySampler(posts.Add, logs.Add, registry, id => id == 1 ? Metric : null, () => 0, false);
        await sampler.TickAsync(); await sampler.TickAsync();
        Assert.Empty(posts); Assert.Single(logs, l => l.StartsWith("[client-mem-membership]")); Assert.Contains("unreadableProcesses=1", logs[0]);
    }

    [Fact]
    public async Task Disposal_cancels_pending_ui_read_and_cannot_emit_late_or_overlap()
    {
        var registry = new BrowserProcessRegistry(); var context = new QueuedContext(); var source = new Source();
        using var registration = registry.Register(BrowserProfile.Desktop, source, context);
        var posts = new List<ClientMemSample>(); var logs = new List<string>();
        var sampler = new WebView2MemorySampler(posts.Add, logs.Add, registry, _ => Metric, () => 0, false);
        var pending = sampler.TickAsync(); await sampler.TickAsync(); Assert.Single(context.Queue);
        sampler.Dispose(); sampler.Dispose(); await pending; context.Drain(); await sampler.TickAsync();
        Assert.Empty(posts); Assert.Empty(logs); Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task Retired_registration_or_disposal_during_os_sample_cannot_publish()
    {
        foreach (var retireSampler in new[] { false, true })
        {
            var registry = new BrowserProcessRegistry(); var registration = registry.Register(BrowserProfile.Desktop, new Source(), new InlineContext());
            var posts = new List<ClientMemSample>(); WebView2MemorySampler? sampler = null;
            using (sampler = new(posts.Add, _ => { }, registry, _ => { if (retireSampler) sampler!.Dispose(); else registration.Dispose(); return Metric; }, () => 0, false))
                await sampler.TickAsync();
            registration.Dispose(); Assert.Empty(posts);
        }
    }

    [Fact]
    public async Task Reader_failure_is_numeric_unavailability_and_does_not_escape()
    {
        var registry = new BrowserProcessRegistry(); using var registration = registry.Register(BrowserProfile.Desktop, new Source(), new InlineContext());
        var logs = new List<string>();
        using var sampler = new WebView2MemorySampler(_ => { }, logs.Add, registry, _ => throw new Exception("private-text"), () => 0, false);
        await sampler.TickAsync(); Assert.Equal("[client-mem-membership] unavailable=1", Assert.Single(logs));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Diagnostic_node_rows_are_bounded_with_explicit_overflow_and_no_image_paths(bool enabled)
    {
        var rows = Enumerable.Range(1, 129).Select(id => new BrowserProcessInfo(id, id == 1 ? 0 : 1)).ToArray();
        var registry = new BrowserProcessRegistry(); using var registration = registry.Register(BrowserProfile.Desktop, new Source { Rows = rows }, new InlineContext());
        var logs = new List<string>();
        using var sampler = new WebView2MemorySampler(_ => { }, logs.Add, registry, _ => Metric, () => 0, false, () => enabled ? 7 : 0);
        await sampler.TickAsync();
        Assert.DoesNotContain(logs, l => l.Contains("C:/runtime"));
        if (enabled)
        {
            Assert.Equal(128, logs.Count(l => l.StartsWith("[efficiency-webview] sessionRevision=")));
            Assert.Contains("[efficiency-webview] omitted=1", logs);
        }
        else Assert.DoesNotContain(logs, l => l.StartsWith("[efficiency-webview]"));
    }

    [Fact]
    public async Task Changed_or_expired_diagnostic_revision_cannot_emit_the_remaining_old_epoch_rows()
    {
        var registry = new BrowserProcessRegistry(); using var registration = registry.Register(BrowserProfile.Desktop, new Source(), new InlineContext());
        long revision = 1; var logs = new List<string>();
        using var sampler = new WebView2MemorySampler(_ => { }, line => { logs.Add(line); if (line.StartsWith("[efficiency-webview]")) revision = 2; }, registry, _ => Metric, () => 0, false, () => revision);
        await sampler.TickAsync();
        Assert.Single(logs, line => line.StartsWith("[efficiency-webview]"));
    }

    [Fact]
    public async Task Retirement_during_availability_callback_does_not_admit_a_host_post()
    {
        var registry = new BrowserProcessRegistry(); using var registration = registry.Register(BrowserProfile.Desktop, new Source(), new InlineContext());
        var posts = new List<ClientMemSample>(); WebView2MemorySampler? sampler = null;
        using (sampler = new(posts.Add, _ => sampler!.Dispose(), registry, _ => Metric, () => 0, false))
            await sampler.TickAsync();
        Assert.Empty(posts);
    }

    private sealed class Source : IBrowserProcessSource
    {
        internal int Reads;
        internal BrowserProcessInfo[] Rows = [new(1, 0), new(2, 4)];
        public BrowserProcessInfo[] Read() { Reads++; return Rows; }
        public void Dispose() { }
    }
    private sealed class InlineContext : SynchronizationContext { public override void Post(SendOrPostCallback d, object? state) => d(state); }
    private sealed class QueuedContext : SynchronizationContext
    {
        internal readonly Queue<Action> Queue = new();
        public override void Post(SendOrPostCallback d, object? state) => Queue.Enqueue(() => d(state));
        internal void Drain() { while (Queue.TryDequeue(out var next)) next(); }
    }
}
