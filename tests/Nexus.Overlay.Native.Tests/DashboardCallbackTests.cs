using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nexus.Overlay.WebView2;

namespace Nexus.Overlay.Tests.NativeOwners;

public sealed unsafe class DashboardCallbackTests
{
    [Theory]
    [InlineData(1)] [InlineData(1000)]
    public void Dispose_unregisters_then_releases_its_local_navigation_callback_once(int cycles)
    {
        for (int cycle = 0; cycle < cycles; cycle++)
        {
        using var handler = new FakeCom();
        using var core = new FakeCom();
        handler.References = 2; // caller + event subscription
        core.State = handler;
        core.Set(WebView2Vtable.Wv2_remove_NavigationCompleted,
            (nint)(delegate* unmanaged[Stdcall]<nint, long, int>)&Remove);
        var dashboard = (DashboardWindow)RuntimeHelpers.GetUninitializedObject(typeof(DashboardWindow));
        Set(dashboard, "_coreWebView2", core.Pointer);
        Set(dashboard, "_navCompletedHandler", handler.Pointer);
        Set(dashboard, "_navCompletedToken", 1L);
        dashboard.Dispose();
        dashboard.Dispose();
        Assert.Equal(0, handler.References);
        Assert.Equal(0, core.References);
        }
    }

    private static void Set(object owner, string field, object value) =>
        owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Remove(nint self, long token)
    {
        var handler = (FakeCom)FakeCom.Find(self).State!;
        handler.References--;
        return 0;
    }
}
