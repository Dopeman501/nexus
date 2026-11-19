using System;
using System.Threading;
using Nexus.Overlay.WebView2;

namespace Nexus.Overlay.Capture;

/// <summary>Owns the checked-out WGC frame, not only its texture COM reference.</summary>
internal unsafe class CapturedFrame(nint frame, nint texture, long timestamp) : IDisposable
{
    private nint _frame = frame;
    private nint _texture = texture;
    internal nint Texture => _texture;
    internal long Timestamp { get; } = timestamp;

    public void Dispose()
    {
        var ownedFrame = Interlocked.Exchange(ref _frame, 0);
        if (ownedFrame == 0) return;
        var ownedTexture = Interlocked.Exchange(ref _texture, 0);
        if (ownedTexture != 0) Wv2.Release(ownedTexture);
        Close(ownedFrame);
        OnClosed();
    }

    protected virtual void OnClosed() { }

    internal static CapturedFrame Create(nint frame, nint texture, long timestamp,
        Func<long>? readAllocatedBytes = null)
    {
        var owner = frame == 0 ? null : CapturedFrameDiagnostics.ActiveOwner;
        if (owner is null) return new CapturedFrame(frame, texture, timestamp);
        // First 128 factory attempts per epoch only. This is gross managed thread
        // allocation around the actual wrapper, never native or retained bytes.
        var sample = owner.TryReserveFactoryAllocationSample();
        long before = 0;
        var beforeKnown = sample && TryReadAllocation(readAllocatedBytes, out before);
        var captured = new TrackedFrame(frame, texture, timestamp, owner);
        if (sample)
        {
            var afterKnown = TryReadAllocation(readAllocatedBytes, out var after);
            owner.RecordFactoryAllocation(beforeKnown && afterKnown && after >= before ? after - before : null);
        }
        return captured;
    }

    private static bool TryReadAllocation(Func<long>? read, out long bytes)
    {
        try { bytes = read is null ? GC.GetAllocatedBytesForCurrentThread() : read(); return bytes >= 0; }
        catch (Exception) { bytes = 0; return false; } // Optional telemetry cannot change capture ownership.
    }

    private sealed class TrackedFrame : CapturedFrame
    {
        private readonly CapturedFrameCounter _owner;
        internal TrackedFrame(nint frame, nint texture, long timestamp, CapturedFrameCounter owner) : base(frame, texture, timestamp)
        {
            _owner = owner; owner.Created();
        }
        protected override void OnClosed() => _owner.Closed();
    }

    internal static void Close(nint value)
    {
        var closable = Wv2.QueryInterface(value, WgcVtable.IID_IClosable);
        if (closable != 0)
        {
            var close = (delegate* unmanaged[Stdcall]<nint, int>)Wv2.Slot(closable, WgcVtable.Closable_Close);
            close(closable);
            Wv2.Release(closable);
        }
        Wv2.Release(value);
    }
}
