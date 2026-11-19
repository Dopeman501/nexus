using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Overlay.WebView2;

namespace Nexus.Overlay;

/// <summary>
/// Sparse memory observations for actual Desktop/Stream/compatibility groups.
/// Native membership is read on the environments' UI thread; OS metrics off
/// that thread. GetProcessInfos excludes crashpad. Neither private commit nor
/// working set by itself identifies JS or GPU allocation ownership.
/// </summary>
internal sealed class WebView2MemorySampler : IDisposable
{
    private const int SampleMs = 60_000;
    private const long HeartbeatMs = 60 * 60_000;
    private const long StepBytes = 100 * 1024 * 1024;
    private static readonly BrowserProcessRegistry Environments = new();
    private readonly BrowserProcessRegistry _registry;
    private readonly Action<ClientMemSample> _post;
    private readonly Action<string> _log;
    private readonly Func<int, BrowserProcessMetrics?> _read;
    private readonly Func<long> _now;
    private readonly Func<long> _revision;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _retirement;
    private readonly Timer? _timer;
    private long _lastTotal = -1, _lastLargest = -1, _lastEmitTicks;
    private string? _lastAvailability;
    private int _busy, _disposed;

    internal static IDisposable? RegisterEnvironment(BrowserProfile profile, IntPtr environment)
    {
        var context = SynchronizationContext.Current;
        if (context is null)
        {
            Log.Info("[client-mem-membership] environmentUnavailable=1 reason=1");
            // Preserve an explicit unknown profile in the inventory; absence
            // of a dispatcher cannot make a partial host sum look complete.
            try { return Environments.Register(profile, new UnavailableSource(), new SynchronizationContext()); }
            catch { return null; }
        }
        Wv2ProcessInfo? source = null;
        try
        {
            source = new(environment);
            return Environments.Register(profile, source, context);
        }
        catch
        {
            source?.Dispose();
            Log.Info("[client-mem-membership] environmentUnavailable=1 reason=2");
            return null;
        }
    }

    public WebView2MemorySampler(NexusApi api)
        : this(api.PostClientMem, Log.Info, Environments, BrowserProcessMemoryReader.Read, () => Environment.TickCount64) { }

    internal WebView2MemorySampler(Action<ClientMemSample> post, Action<string> log, BrowserProcessRegistry registry,
        Func<int, BrowserProcessMetrics?> read, Func<long> now, bool startTimer = true, Func<long>? currentRevision = null)
    {
        (_post, _log, _registry, _read, _now) = (post, log, registry, read, now);
        _revision = currentRevision ?? (() => EfficiencyDiagnosticOptions.SessionRevision);
        _retirement = _lifetime.Token;
        if (startTimer) _timer = new Timer(_ => _ = TickAsync(), null, 0, SampleMs);
    }

    internal async Task TickAsync()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_retirement);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var diagnosticRevision = _revision();
            var generation = _registry.Generation;
            var groups = await _registry.CollectAsync(timeout.Token).ConfigureAwait(false);
            if (_retirement.IsCancellationRequested || _registry.Generation != generation) return;
            var sample = BrowserProcessMemory.Sample(groups, _read);
            if (_retirement.IsCancellationRequested || _registry.Generation != generation) return;
            Emit(sample, groups.Length, diagnosticRevision);
        }
        catch (OperationCanceledException)
        {
            if (!_retirement.IsCancellationRequested) Availability("timeout=1");
        }
        catch { if (!_retirement.IsCancellationRequested) Availability("unavailable=1"); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private void Emit(BrowserProcessMemoryResult sample, int profiles, long diagnosticRevision)
    {
        var pendingReleases = _registry.PendingReleaseCount;
        Availability($"profiles={profiles} unavailableProfiles={sample.UnavailableProfiles} unreadableProcesses={sample.UnreadableProcesses} pendingReleases={pendingReleases} children={sample.Nodes.Length} crashpadIncluded=0");
        if (_retirement.IsCancellationRequested) return;
        if (diagnosticRevision != 0 && _revision() == diagnosticRevision)
        {
            var count = Math.Min(sample.Nodes.Length, 128);
            for (var i = 0; i < count; i++)
            {
                if (_retirement.IsCancellationRequested || _revision() != diagnosticRevision) return;
                var n = sample.Nodes[i];
                _log($"[efficiency-webview] sessionRevision={diagnosticRevision} profile={(int)n.Profile} pid={n.ProcessId} kind={n.Kind} birthUtcTicks={n.Metrics.CreatedUtc.Ticks} imageVerified=1 privateBytes={n.Metrics.PrivateBytes} workingSetBytes={n.Metrics.WorkingSetBytes}");
            }
            if (sample.Nodes.Length > count && _revision() == diagnosticRevision) _log($"[efficiency-webview] omitted={sample.Nodes.Length - count}");
        }
        if (!sample.Complete || pendingReleases != 0) return;
        var now = _now();
        if (_lastTotal >= 0 && now - _lastEmitTicks < HeartbeatMs &&
            Math.Abs(sample.WorkingSetBytes - _lastTotal) < StepBytes && Math.Abs(sample.LargestWorkingSetBytes - _lastLargest) < StepBytes) return;
        _lastTotal = sample.WorkingSetBytes;
        _lastLargest = sample.LargestWorkingSetBytes;
        _lastEmitTicks = now;
        var totalMb = (int)(sample.WorkingSetBytes / 1_048_576);
        var largestMb = (int)(sample.LargestWorkingSetBytes / 1_048_576);
        if (_retirement.IsCancellationRequested) return;
        _post(new ClientMemSample { Source = "host", TotalWsMB = totalMb, LargestWsMB = largestMb, LargestPid = sample.LargestPid, Children = sample.Nodes.Length });
        _log($"[client-mem] host total={totalMb}MB largest={largestMb}MB(pid {sample.LargestPid}) children={sample.Nodes.Length}");
    }

    private void Availability(string state)
    {
        if (_retirement.IsCancellationRequested || state == _lastAvailability) return;
        _lastAvailability = state;
        _log("[client-mem-membership] " + state);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _timer?.Dispose();
        _lifetime.Dispose();
    }

    private sealed class UnavailableSource : IBrowserProcessSource
    {
        public BrowserProcessInfo[]? Read() => null;
        public void Dispose() { }
    }
}
