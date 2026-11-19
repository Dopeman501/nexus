using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nexus.Overlay.Capture;
using Nexus.Overlay.Media;
using Nexus.Overlay.WebView2;

namespace Nexus.Overlay.Tests.NativeOwners;

public sealed unsafe class TextureRingInitializationTests
{
    [Fact]
    public void Failed_third_callback_releases_partial_native_graph_before_factory_returns()
    {
        using var nativeDevice = new FakeCom(); using var context = new FakeCom();
        nativeDevice.State = context;
        nativeDevice.Set(40, (nint)(delegate* unmanaged[Stdcall]<nint, nint*, void>)&GetContext);
        var device = (D3DDevice)RuntimeHelpers.GetUninitializedObject(typeof(D3DDevice));
        device.Device = nativeDevice.Pointer;
        int created = 0;
        var callbacks = new List<NativeSignalCallback>();
        Assert.Throws<InvalidOperationException>(() => new OwnedTextureRing(device, 800, 800, action =>
        {
            if (++created == 3) throw new InvalidOperationException("Synthetic allocation failure");
            var callback = new NativeSignalCallback(action, true); callbacks.Add(callback); return callback;
        }));
        Assert.Equal(1, nativeDevice.References); Assert.Equal(1, context.References);
        Assert.All(callbacks, callback => Assert.True(callback.Completion.IsCompletedSuccessfully));
    }

    [Fact]
    public void Missing_context_releases_device_reference()
    {
        using var nativeDevice = new FakeCom();
        nativeDevice.Set(40, (nint)(delegate* unmanaged[Stdcall]<nint, nint*, void>)&GetContext);
        var device = (D3DDevice)RuntimeHelpers.GetUninitializedObject(typeof(D3DDevice)); device.Device = nativeDevice.Pointer;
        Assert.Throws<InvalidOperationException>(() => new OwnedTextureRing(device, 800, 800));
        Assert.Equal(1, nativeDevice.References);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void GetContext(nint self, nint* result)
    {
        *result = (FakeCom.Find(self).State as FakeCom)?.Pointer ?? 0;
        if (*result != 0) Wv2.AddRef(*result);
    }
}
