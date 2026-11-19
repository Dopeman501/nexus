using System;
using System.Threading;

namespace Nexus.Overlay.WebView2;

/// <summary>
/// Optional SDK 1.0.2792.45 Environment8 snapshot. All calls and release run
/// on the environment's owning UI thread. No event handler or browser action.
/// Native GetProcessInfos excludes crashpad and exposes neither frame ownership
/// nor allocation/JS/GPU metrics.
/// </summary>
internal sealed unsafe class Wv2ProcessInfo : IBrowserProcessSource
{
    internal static readonly Guid Environment8Iid = new("d6eb91dd-c3d2-45e5-bd29-6dc2bc4de9cf");
    internal const int Environment8GetProcessInfosSlot = 18;
    private IntPtr _environment8;
    private readonly int _thread = Environment.CurrentManagedThreadId;

    internal Wv2ProcessInfo(IntPtr environment) => _environment8 = Wv2.QueryInterface(environment, Environment8Iid);

    public BrowserProcessInfo[]? Read()
        => Environment.CurrentManagedThreadId != _thread || _environment8 == IntPtr.Zero ? null : ReadCollection(_environment8);

    internal static BrowserProcessInfo[]? ReadCollection(IntPtr environment8)
    {
        IntPtr collection = IntPtr.Zero;
        try
        {
            var get = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Wv2.Slot(environment8, Environment8GetProcessInfosSlot);
            if (get(environment8, &collection) < 0 || collection == IntPtr.Zero) return null;
            uint count = 0;
            var getCount = (delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Wv2.Slot(collection, 3);
            if (getCount(collection, &count) < 0 || count > BrowserProcessRegistry.MaximumProcesses) return null;
            var rows = new BrowserProcessInfo[count];
            for (uint i = 0; i < count; i++)
            {
                var row = ReadItem(collection, i);
                if (!row.HasValue) return null;
                rows[i] = row.Value;
            }
            return rows;
        }
        finally { if (collection != IntPtr.Zero) Wv2.Release(collection); }
    }

    private static BrowserProcessInfo? ReadItem(IntPtr collection, uint index)
    {
        IntPtr item = IntPtr.Zero;
        try
        {
            var get = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Wv2.Slot(collection, 4);
            if (get(collection, index, &item) < 0 || item == IntPtr.Zero) return null;
            int processId = 0, kind = 0;
            var getId = (delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Wv2.Slot(item, 3);
            var getKind = (delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Wv2.Slot(item, 4);
            if (getId(item, &processId) < 0 || getKind(item, &kind) < 0) return null;
            return new(processId, kind);
        }
        finally { if (item != IntPtr.Zero) Wv2.Release(item); }
    }

    public void Dispose()
    {
        // Registration marshals release back to the owning context.
        if (Environment.CurrentManagedThreadId != _thread) return;
        var environment = Interlocked.Exchange(ref _environment8, IntPtr.Zero);
        if (environment != IntPtr.Zero) Wv2.Release(environment);
    }
}
