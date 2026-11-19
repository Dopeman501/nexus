using Nexus.Overlay.Capture;
using Nexus.Overlay.WebView2;

namespace Nexus.Overlay.Tests.NativeOwners;

public sealed unsafe class NativeSignalTests
{
    [Fact]
    public void Arrival_before_wait_is_latched_and_bursts_coalesce()
    {
        using var wake = new CaptureWakeSignal();
        wake.Signal(); wake.Signal();
        Assert.True(wake.Wait(0)); Assert.False(wake.Wait(0));
        wake.Dispose(); wake.Signal();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Native_refs_keep_retired_callback_alive_without_touching_disposed_owner(bool mf)
    {
        int signals = 0;
        using var callback = new NativeSignalCallback(() => signals++, mf);
        nint host = Wv2.QueryInterface(callback.Pointer, mf ? NativeSignalCallback.AsyncCallbackId : NativeSignalCallback.FrameArrivedId);
        Assert.NotEqual(0, host);
        Assert.Equal(0, Wv2.QueryInterface(host, Guid.NewGuid()));
        Invoke(host, mf); Assert.Equal(1, signals);
        callback.Dispose(); Assert.False(callback.Completion.IsCompleted);
        Invoke(host, mf); Assert.Equal(1, signals);
        Wv2.Release(host); Assert.True(callback.Completion.IsCompletedSuccessfully);
    }

    private static void Invoke(nint pointer, bool mf)
    {
        if (mf) ((delegate* unmanaged[Stdcall]<nint, nint, int>)Wv2.Slot(pointer, 4))(pointer, 0);
        else ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)Wv2.Slot(pointer, 3))(pointer, 0, 0);
    }
}
