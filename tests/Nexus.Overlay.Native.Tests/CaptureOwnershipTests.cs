using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nexus.Overlay.Capture;
using Nexus.Overlay.WebView2;

namespace Nexus.Overlay.Tests.NativeOwners;

public sealed unsafe class CaptureOwnershipTests
{
    [Fact]
    public void Legacy_capture_poll_keeps_empty_pool_behavior_without_timestamp_storage()
    {
        var capture = (WgcCapture)RuntimeHelpers.GetUninitializedObject(typeof(WgcCapture));
        Assert.False(capture.TryGetNextFrame(out var frame));
        Assert.Null(frame);
    }

    [Fact]
    public void Retired_callback_generation_blocks_restart_and_disposal_blocks_late_start()
    {
        var device = (D3DDevice)RuntimeHelpers.GetUninitializedObject(typeof(D3DDevice));
        using var capture = new WgcCapture(0, device, 2, 2);
        var pending = new TaskCompletionSource();
        var callbacks = (List<Task>)typeof(WgcCapture).GetField("_retiredCallbacks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(capture)!;
        callbacks.Add(pending.Task);
        Assert.Throws<InvalidOperationException>(() => capture.Start());
        Assert.False(capture.Completion.IsCompleted);
        capture.Dispose();
        Assert.Throws<ObjectDisposedException>(() => capture.Start());
        Assert.Throws<ObjectDisposedException>(() => capture.Restart());
        pending.SetResult(); Assert.True(capture.Completion.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData("valid", true)] [InlineData("time-unavailable", true)]
    [InlineData("empty", false)] [InlineData("pool-failure", false)] [InlineData("surface-failure", false)]
    [InlineData("surface-missing", false)] [InlineData("access-missing", false)] [InlineData("texture-failure", false)]
    public void Dequeued_capture_is_not_closed_before_the_consumer_reads_it(string mode, bool accepted)
    {
        using var pool = new FakeCom();
        using var frame = new FakeCom();
        using var close = new FakeCom();
        using var surface = new FakeCom();
        using var access = new FakeCom();
        using var texture = new FakeCom();
        var state = new FrameState(frame.Pointer, surface.Pointer, texture.Pointer, mode);
        pool.State = frame.State = close.State = access.State = state;
        frame.Query = iid => iid == WgcVtable.IID_IClosable ? close.Pointer : 0;
        surface.Query = iid => mode != "access-missing" && iid == WgcVtable.IID_IDirect3DDxgiInterfaceAccess ? access.Pointer : 0;
        pool.Set(WgcVtable.Pool_TryGetNextFrame, (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&Next);
        frame.Set(WgcVtable.Frame_get_Surface, (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&Surface);
        frame.Set(WgcVtable.Frame_get_SystemRelativeTime, (nint)(delegate* unmanaged[Stdcall]<nint, long*, int>)&Timestamp);
        access.Set(3, (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&Texture);
        close.Set(WgcVtable.Closable_Close, (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Close);
        var capture = (WgcCapture)RuntimeHelpers.GetUninitializedObject(typeof(WgcCapture));
        var metrics = new CaptureDiagnostics(System.Diagnostics.Stopwatch.Frequency);
        typeof(WgcCapture).GetField("_diagnosticProvider", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(capture, (Func<CaptureDiagnostics?>)(() => metrics));
        typeof(WgcCapture).GetField("_pool", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(capture, pool.Pointer);
        Assert.Equal(accepted, capture.TryGetNextFrame(out var acquired, out var timestampHResult));
        if (!accepted)
        {
            Assert.Null(acquired);
            Assert.Equal(mode is "empty" or "pool-failure" ? 0 : 1, state.Closed);
            var snapshot = metrics.Snapshot();
            Assert.Equal(mode == "empty" ? 1 : 0, snapshot.PoolEmpty);
            Assert.Equal(mode == "pool-failure" ? 1 : 0, snapshot.PoolFailures);
            Assert.Equal(mode is "empty" or "pool-failure" ? 0 : 1, snapshot.ExtractionFailures);
            return;
        }
        try
        {
            Assert.Equal(mode == "time-unavailable" ? 0 : 1234, acquired!.Timestamp);
            Assert.Equal(mode == "time-unavailable" ? -1 : 0, timestampHResult);
            metrics.FrameReturned(System.Diagnostics.Stopwatch.GetTimestamp(), acquired.Timestamp, timestampHResult);
            Assert.Equal(mode == "time-unavailable" ? 1 : 0, metrics.Snapshot().Timestamp.GetterFailures);
            Assert.Equal(0, metrics.Snapshot().Timestamp.NonPositive);
            Assert.Equal(0, state.Closed);
        }
        finally { acquired!.Dispose(); acquired.Dispose(); }
        Assert.Equal(1, state.Closed);
    }

    private sealed record FrameState(nint Frame, nint Surface, nint Texture, string Mode)
    {
        internal int Closed;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Next(nint self, nint* output)
    { var s = (FrameState)FakeCom.Find(self).State!; *output = s.Mode is "empty" or "pool-failure" ? 0 : s.Frame; return s.Mode == "pool-failure" ? -1 : 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Surface(nint self, nint* output)
    {
        var s = (FrameState)FakeCom.Find(self).State!;
        *output = s.Mode is "surface-failure" or "surface-missing" ? 0 : s.Surface;
        if (*output != 0) FakeCom.Find(*output).References++;
        return s.Mode == "surface-failure" ? -1 : 0;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Timestamp(nint self, long* output)
    { *output = 1234; return ((FrameState)FakeCom.Find(self).State!).Mode == "time-unavailable" ? -1 : 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Texture(nint self, Guid* iid, nint* output)
    {
        var s = (FrameState)FakeCom.Find(self).State!;
        *output = s.Mode == "texture-failure" ? 0 : s.Texture;
        if (*output != 0) FakeCom.Find(*output).References++;
        return s.Mode == "texture-failure" ? -1 : 0;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Close(nint self) { ((FrameState)FakeCom.Find(self).State!).Closed++; return 0; }
}
