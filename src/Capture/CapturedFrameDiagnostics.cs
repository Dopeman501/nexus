namespace Nexus.Overlay.Capture;

internal readonly record struct CapturedFrameCounts(long Created, long Closed,
    int FactoryAllocationAttempts = 0, int FactoryAllocationSamples = 0,
    long FactoryAllocationBytes = 0, int FactoryAllocationUnavailable = 0)
{
    internal long OutstandingUpperBound => Created - Closed;
}

internal sealed class CapturedFrameCounter
{
    internal const int FactoryAllocationSampleLimit = 128;
    private readonly object _allocationGate = new();
    private long _created;
    private long _closed;
    private int _allocationAttempts, _allocationSamples, _allocationUnavailable;
    private long _allocationBytes;

    internal void Created() => Interlocked.Increment(ref _created);
    internal void Closed() => Interlocked.Increment(ref _closed);

    internal bool TryReserveFactoryAllocationSample()
    {
        if (Volatile.Read(ref _allocationAttempts) >= FactoryAllocationSampleLimit) return false;
        lock (_allocationGate)
        {
            if (_allocationAttempts >= FactoryAllocationSampleLimit) return false;
            _allocationAttempts++;
            return true;
        }
    }

    internal void RecordFactoryAllocation(long? bytes)
    {
        lock (_allocationGate)
        {
            if (bytes is null) _allocationUnavailable++;
            else { _allocationSamples++; _allocationBytes += bytes.Value; }
        }
    }

    internal CapturedFrameCounts Snapshot
    {
        get
        {
            // Read terminal events first. The difference is an upper bound during concurrent changes,
            // not an atomic census or a GPU/native-allocation measurement.
            var closed = Interlocked.Read(ref _closed);
            lock (_allocationGate) return new(Interlocked.Read(ref _created), closed,
                _allocationAttempts, _allocationSamples, _allocationBytes, _allocationUnavailable);
        }
    }
}

internal static class CapturedFrameDiagnostics
{
    private static readonly DiagnosticLeaseOwner<CapturedFrameCounter> Owners = new();
    internal static CapturedFrameCounter? ActiveOwner => Owners.Get(static () => new CapturedFrameCounter());

    internal static nint Register(nint frame)
    {
        if (frame != 0) ActiveOwner?.Created();
        return frame;
    }

    internal static void Closed() => ActiveOwner?.Closed();
    internal static CapturedFrameCounts Snapshot => ActiveOwner?.Snapshot ?? default;
}
