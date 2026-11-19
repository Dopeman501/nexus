using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nexus.Overlay.Capture;
using Nexus.Overlay.Media;
using Nexus.Overlay.WebView2;

namespace Nexus.Overlay.Tests.NativeOwners;

public sealed unsafe class RawReadbackFailureTests
{
    [Theory]
    [InlineData("no-context")] [InlineData("texture-error")] [InlineData("texture-missing")]
    public void Failed_native_construction_releases_every_acquired_context(string mode)
    {
        using var nativeDevice = new FakeCom(); using var context = new FakeCom();
        nativeDevice.State = new State(context.Pointer, 0, mode);
        nativeDevice.Set(40, (nint)(delegate* unmanaged[Stdcall]<nint, nint*, void>)&Context);
        nativeDevice.Set(5, (nint)(delegate* unmanaged[Stdcall]<nint, void*, nint, nint*, int>)&CreateTexture);
        var device = (D3DDevice)RuntimeHelpers.GetUninitializedObject(typeof(D3DDevice)); device.Device = nativeDevice.Pointer;
        Assert.Throws<InvalidOperationException>(() => new RawFrameSink(device, 2, 2, (_, _) => { }, "synthetic"));
        Assert.Equal(1, nativeDevice.References); Assert.Equal(1, context.References);
    }

    [Fact]
    public void Failed_map_keeps_the_capture_lease_and_native_graph_quarantined()
    {
        using var deviceCom = new FakeCom(); using var context = new FakeCom(); using var staging = new FakeCom();
        using var frame = new FakeCom(); using var close = new FakeCom(); using var texture = new FakeCom();
        var state = new State(context.Pointer, staging.Pointer);
        deviceCom.State = context.State = close.State = state;
        frame.Query = iid => iid == WgcVtable.IID_IClosable ? close.Pointer : 0;
        deviceCom.Set(40, (nint)(delegate* unmanaged[Stdcall]<nint, nint*, void>)&Context);
        deviceCom.Set(5, (nint)(delegate* unmanaged[Stdcall]<nint, void*, nint, nint*, int>)&CreateTexture);
        context.Set(47, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, void>)&Copy);
        context.Set(14, (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, D3D11Vtable.D3D11_MAPPED_SUBRESOURCE*, int>)&Map);
        close.Set(6, (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Close);
        var device = (D3DDevice)RuntimeHelpers.GetUninitializedObject(typeof(D3DDevice)); device.Device = deviceCom.Pointer;
        var metrics = new CaptureDiagnostics(System.Diagnostics.Stopwatch.Frequency);
        using var sink = new RawFrameSink(device, 2, 2, (_, _) => Assert.Fail("Failed readback cannot publish a frame"), "synthetic", diagnostics: metrics);
        Assert.ThrowsAny<InvalidOperationException>(() => sink.Submit(new(frame.Pointer, texture.Pointer, 0)));
        sink.Dispose();
        Assert.Equal(1, state.Copies); Assert.Equal(0, state.Closed);
        Assert.Equal(2, context.References); Assert.Equal(1, staging.References);
        Assert.Equal(1, frame.References); Assert.Equal(1, texture.References);
        Assert.False(sink.Completion.IsCompleted); // explicit process recovery, never a replacement generation
        Assert.Equal(1, metrics.Snapshot().MapFailures);
        Assert.Equal(1, metrics.Snapshot().Map.Count);
        Assert.Equal(0, metrics.Snapshot().Copy.Count);
    }
    private sealed class State(nint context, nint staging, string mode = "map-failure")
    { internal readonly nint Context = context, Staging = staging; internal readonly string Mode = mode; internal int Copies, Closed; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Context(nint self, nint* output)
    { var s = (State)FakeCom.Find(self).State!; *output = s.Mode == "no-context" ? 0 : s.Context; if (*output != 0) Wv2.AddRef(*output); }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateTexture(nint self, void* descriptor, nint data, nint* output)
    { var s = (State)FakeCom.Find(self).State!; *output = s.Staging; return s.Mode == "texture-error" ? -1 : 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Copy(nint self, nint target, nint source) => ((State)FakeCom.Find(self).State!).Copies++;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Map(nint self, nint texture, uint subresource, uint mode, uint flags, D3D11Vtable.D3D11_MAPPED_SUBRESOURCE* output)
    { *output = default; return unchecked((int)0x80004005); }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Close(nint self) { ((State)FakeCom.Find(self).State!).Closed++; return 0; }
}
