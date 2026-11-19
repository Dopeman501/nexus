using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Overlay;

internal enum BrowserProfile { Desktop, Stream, PanelCompatibility }
internal readonly record struct BrowserProcessInfo(int ProcessId, int Kind);
internal enum BrowserProcessStatus { Available, Unavailable, Retired }
internal readonly record struct BrowserProcessGroup(BrowserProfile Profile, DateTime CapturedUtc, BrowserProcessInfo[] Processes, BrowserProcessStatus Status)
{
    internal bool Available => Status == BrowserProcessStatus.Available;
}
internal interface IBrowserProcessSource : IDisposable
{
    BrowserProcessInfo[]? Read();
}

internal sealed class BrowserProcessRegistry
{
    // Kiosk profile selection stays explicit because compatibility rendering
    // creates a different UDF from the ordinary Desktop environment.
    internal static BrowserProfile? KioskProfile(bool compatibility)
        => compatibility ? BrowserProfile.PanelCompatibility : BrowserProfile.Desktop;
    internal const int MaximumProcesses = 256;
    private const int MaximumRegistrations = 128;
    private readonly object _gate = new();
    private readonly List<Registration> _owners = [];
    private readonly List<Registration> _pendingReleases = [];
    private readonly Func<DateTime> _utcNow;
    internal BrowserProcessRegistry(Func<DateTime>? utcNow = null) => _utcNow = utcNow ?? (() => DateTime.UtcNow);
    private long _generation;
    internal long Generation { get { lock (_gate) return _generation; } }
    internal int PendingReleaseCount { get { lock (_gate) return _pendingReleases.Count; } }

    internal IDisposable Register(BrowserProfile profile, IBrowserProcessSource source, SynchronizationContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);
        if (!Enum.IsDefined(profile)) throw new ArgumentOutOfRangeException(nameof(profile));
        DrainPendingReleases();
        lock (_gate)
        {
            if (_owners.Count + _pendingReleases.Count >= MaximumRegistrations) throw new InvalidOperationException("Browser diagnostic registration bound reached.");
            var owner = new Registration(this, profile, source, context);
            _owners.Add(owner);
            _generation++;
            return owner;
        }
    }

    internal async Task<BrowserProcessGroup[]> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Registration[] owners;
        lock (_gate) owners = _owners.ToArray();
        var groups = new List<BrowserProcessGroup>(3);
        foreach (var profile in owners.GroupBy(o => o.Profile))
        {
            BrowserProcessGroup? last = null;
            // Same UDF registrations share a native process collection. Query
            // one working environment; a retired/unsupported owner cannot hide
            // a still-live sibling. Never merge an unrelated Trust environment.
            foreach (var owner in profile)
            {
                last = await owner.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (last.Value.Available) break;
            }
            if (last.HasValue) groups.Add(last.Value);
        }
        return groups.ToArray();
    }

    private void DrainPendingReleases()
    {
        Registration[] pending;
        lock (_gate) pending = _pendingReleases.ToArray();
        foreach (var owner in pending) owner.ReleaseOnOwnerThread();
    }

    private sealed class Registration : IDisposable
    {
        private readonly BrowserProcessRegistry _registry;
        private readonly IBrowserProcessSource _source;
        private readonly SynchronizationContext _context;
        private readonly int _ownerThread = Environment.CurrentManagedThreadId;
        private int _retired;
        private int _queued;
        private int _released;
        internal BrowserProfile Profile { get; }

        internal Registration(BrowserProcessRegistry registry, BrowserProfile profile, IBrowserProcessSource source, SynchronizationContext context)
            => (_registry, Profile, _source, _context) = (registry, profile, source, context);

        internal async Task<BrowserProcessGroup> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.CompareExchange(ref _queued, 1, 0) != 0)
                return new(Profile, _registry._utcNow(), [], BrowserProcessStatus.Unavailable);
            var pending = new TaskCompletionSource<BrowserProcessGroup>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var canceled = cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken));
            try { _context.Post(_ =>
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    _registry.DrainPendingReleases();
                    // Lower bound: a PID born during native enumeration is
                    // ambiguous even if it exists before enumeration ends.
                    var capturedUtc = _registry._utcNow();
                    var status = BrowserProcessStatus.Retired;
                    BrowserProcessInfo[] rows = [];
                    if (Volatile.Read(ref _retired) == 0)
                    {
                        status = BrowserProcessStatus.Unavailable;
                        try
                        {
                            var read = _source.Read();
                            if (read is not null && read.Length <= MaximumProcesses && Valid(read))
                            {
                                rows = (BrowserProcessInfo[])read.Clone();
                                status = BrowserProcessStatus.Available;
                            }
                        }
                        catch { /* Explicit unavailable result; no product failure. */ }
                    }
                    if (Volatile.Read(ref _retired) != 0) { rows = []; status = BrowserProcessStatus.Retired; }
                    pending.TrySetResult(new(Profile, capturedUtc, rows, status));
                }
                finally { Volatile.Write(ref _queued, 0); }
            }, null); }
            catch { Volatile.Write(ref _queued, 0); throw; }
            return await pending.Task.ConfigureAwait(false);
        }

        private static bool Valid(BrowserProcessInfo[] rows)
        {
            var seen = new HashSet<int>();
            foreach (var row in rows)
                if (row.ProcessId <= 0 || row.Kind is < 0 or > 6 || !seen.Add(row.ProcessId)) return false;
            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _retired, 1) != 0) return;
            lock (_registry._gate)
            {
                _registry._owners.Remove(this);
                _registry._pendingReleases.Add(this);
                _registry._generation++;
            }
            if (Environment.CurrentManagedThreadId == _ownerThread) ReleaseOnOwnerThread();
            else try { _context.Post(_ => ReleaseOnOwnerThread(), null); } catch { /* Retained inside the registration bound. */ }
        }

        internal void ReleaseOnOwnerThread()
        {
            if (Environment.CurrentManagedThreadId != _ownerThread || Interlocked.CompareExchange(ref _released, 1, 0) != 0) return;
            try { _source.Dispose(); }
            catch { Volatile.Write(ref _released, 0); return; }
            lock (_registry._gate) _registry._pendingReleases.Remove(this);
        }
    }
}
