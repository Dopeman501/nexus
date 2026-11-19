using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nexus.Overlay.Capture;
using Nexus.Overlay.WebView2;

namespace Nexus.Overlay.Tests.NativeOwners;

public sealed unsafe class ArrivalSubscriptionTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Partial_registration_and_late_native_callbacks_retain_balanced_ownership(bool registered)
    {
        using var pool = new FakeCom();
        var device = (D3DDevice)RuntimeHelpers.GetUninitializedObject(typeof(D3DDevice));
        using var capture = new WgcCapture(0, device, 2, 2);
        int signaled = 0;
        using var callback = new NativeSignalCallback(() => signaled++);
        nint late = Wv2.QueryInterface(callback.Pointer, NativeSignalCallback.FrameArrivedId);
        var state = new State(callback.Pointer, registered);
        pool.State = state;
        pool.Query = _ => 0;
        pool.Set(WgcVtable.Pool_remove_FrameArrived, (nint)(delegate* unmanaged[Stdcall]<nint, long, int>)&Remove);
        pool.Released = () => { if (registered && Interlocked.Exchange(ref state.HostReleased, 1) == 0) Wv2.Release(state.Callback); };
        if (registered) Wv2.AddRef(callback.Pointer); // native subscription ownership
        Set(capture, "_pool", pool.Pointer); Set(capture, "_arrival", callback);
        Set(capture, "_arrivalSubscribed", registered); Set(capture, "_arrivalToken", 123L);
        capture.Dispose(); capture.Dispose();
        Assert.Equal(registered ? 1 : 0, state.Removed);
        Assert.Equal(registered ? 123L : 0, state.Token);
        Assert.False(capture.Completion.IsCompleted);
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)Wv2.Slot(late, 3))(late, 0, 0);
        Assert.Equal(0, signaled); // a retired handler cannot touch its disposed wake owner
        Wv2.Release(late);
        Assert.True(capture.Completion.IsCompletedSuccessfully);
        Assert.Equal(0, pool.References);
    }
    private static void Set(object target, string name, object value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private sealed class State(nint callback, bool registered)
    { internal readonly nint Callback = callback; internal readonly bool Registered = registered; internal int Removed, HostReleased; internal long Token; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Remove(nint self, long token)
    {
        var state = (State)FakeCom.Find(self).State!; state.Removed++; state.Token = token;
        // Simulate a removal error: pool teardown must still retire its native reference.
        return unchecked((int)0x80004005);
    }
}
