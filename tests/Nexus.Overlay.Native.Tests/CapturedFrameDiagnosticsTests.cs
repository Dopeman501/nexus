using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nexus.Overlay.Capture;
using Nexus.Overlay.WebView2;

namespace Nexus.Overlay.Tests.NativeOwners;

[CollectionDefinition("Native runtime diagnostic lease", DisableParallelization=true)]
public sealed class NativeRuntimeDiagnosticLeaseCollection { }

[Collection("Native runtime diagnostic lease")]
public sealed class CapturedFrameDiagnosticsTests
{
    [Fact]
    public void Created_and_closed_counts_report_owned_leases()
    {
        var counts = new CapturedFrameCounter();
        counts.Created();
        Assert.Equal(1, counts.Snapshot.OutstandingUpperBound);
        counts.Closed();
        Assert.Equal(1, counts.Snapshot.Created);
        Assert.Equal(1, counts.Snapshot.Closed);
        Assert.Equal(0, counts.Snapshot.OutstandingUpperBound);
    }

    [Fact]
    public void Concurrent_lifetimes_reconcile_without_an_object_registry()
    {
        var counts = new CapturedFrameCounter();
        Parallel.For(0, 10_000, _ =>
        {
            counts.Created();
            Assert.True(counts.Snapshot.OutstandingUpperBound >= 0);
            counts.Closed();
        });
        Assert.Equal(10_000, counts.Snapshot.Created);
        Assert.Equal(counts.Snapshot.Created, counts.Snapshot.Closed);
    }

    [Fact]
    public void Instrumentation_preserves_native_identity_and_ignores_an_empty_owner()
    {
        var before = CapturedFrameDiagnostics.Snapshot;
        Assert.Equal((nint)0, CapturedFrameDiagnostics.Register(0));
        Assert.Equal(before, CapturedFrameDiagnostics.Snapshot);
        Assert.Equal((nint)1234, CapturedFrameDiagnostics.Register(1234));
        CapturedFrameDiagnostics.Closed(); // fake terminal event; never calls COM or a device.
        var after = CapturedFrameDiagnostics.Snapshot;
        var delta = EfficiencyDiagnosticOptions.Enabled ? 1 : 0;
        Assert.Equal(before.Created + delta, after.Created);
        Assert.Equal(before.Closed + delta, after.Closed);
    }

    [Fact]
    public unsafe void A_frame_crossing_expiry_closes_its_original_counter_exactly_once()
    {
        using var frame=new FakeCom();using var close=new FakeCom();var closed=new int[1];close.State=closed;
        frame.Query=iid=>iid==WgcVtable.IID_IClosable?close.Pointer:0;
        close.Set(WgcVtable.Closable_Close,(nint)(delegate* unmanaged[Stdcall]<nint,int>)&CloseCounted);
        var authority=Guid.NewGuid().ToString("N");EfficiencyDiagnosticOptions.Apply(authority,1,"11111111111111111111111111111111",30000);
        try
        {
            var before=Assert.IsType<CapturedFrameCounter>(CapturedFrameDiagnostics.ActiveOwner);
            var lease=CapturedFrame.Create(frame.Pointer,0,123);
            Assert.Equal(1,before.Snapshot.OutstandingUpperBound);
            EfficiencyDiagnosticOptions.Apply(authority,2,null,0);
            EfficiencyDiagnosticOptions.Apply(authority,3,"33333333333333333333333333333333",30000);
            var current=Assert.IsType<CapturedFrameCounter>(CapturedFrameDiagnostics.ActiveOwner);Assert.NotSame(before,current);
            lease.Dispose();lease.Dispose();
            Assert.Equal(1,closed[0]);Assert.Equal(1,before.Snapshot.Closed);Assert.Equal(0,before.Snapshot.OutstandingUpperBound);
            Assert.Equal(0,current.Snapshot.Created);Assert.Equal(0,current.Snapshot.Closed);
        }
        finally{EfficiencyDiagnosticOptions.Apply(authority,4,null,0);}
    }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvStdcall)])]
    private static int CloseCounted(nint self){((int[])FakeCom.Find(self).State!)[0]++;return 0;}

#if WINDOWS
    [Fact]
    public void Diagnostics_do_not_add_per_frame_object_storage_or_allocations()
    {
        using (var warm = EmptyFrame()) { }
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            using var frame = EmptyFrame(); // zero handles: no COM/native release is possible.
            GC.KeepAlive(frame);
        }
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - start, 0, 40_000);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static CapturedFrame EmptyFrame() => new(0, 0, 123);
#endif
}

