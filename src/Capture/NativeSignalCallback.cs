using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Overlay.WebView2;

namespace Nexus.Overlay.Capture;

/// <summary>AOT-safe, agile WGC/MF signal callback. Native references own its state.</summary>
internal sealed unsafe class NativeSignalCallback : IDisposable
{
    internal static readonly Guid FrameArrivedId = new("51a947f7-79cf-5a3e-a3a5-1289cfa6dfe8");
    internal static readonly Guid AsyncCallbackId = new("a27003cf-2354-4f2a-8d6a-ab7cff15437e");
    private static readonly Guid AgileId = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");
    private readonly State _state;
    private nint _pointer;
    internal nint Pointer => _pointer;
    internal Task Completion => _state.Released.Task;

    internal NativeSignalCallback(Action signal, bool mediaFoundation = false)
    {
        _state = new(signal, mediaFoundation ? AsyncCallbackId : FrameArrivedId);
        var table = (nint*)NativeMemory.Alloc((nuint)(5 * nint.Size));
        nint* value = null;
        GCHandle handle = default;
        try
        {
            table[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&Query;
            table[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef;
            table[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release;
            table[3] = mediaFoundation
                ? (nint)(delegate* unmanaged[Stdcall]<nint, uint*, uint*, int>)&Parameters
                : (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&FrameInvoke;
            table[4] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&MediaInvoke;
            value = (nint*)NativeMemory.AllocZeroed((nuint)(3 * nint.Size));
            value[0] = (nint)table;
            value[1] = 1;
            handle = GCHandle.Alloc(_state);
            value[2] = GCHandle.ToIntPtr(handle);
            _pointer = (nint)value;
        }
        catch
        {
            if (handle.IsAllocated) handle.Free();
            NativeMemory.Free(value); NativeMemory.Free(table);
            throw;
        }
    }

    public void Dispose()
    {
        Volatile.Write(ref _state.Retired, true);
        var pointer = Interlocked.Exchange(ref _pointer, 0);
        if (pointer != 0) Wv2.Release(pointer);
    }

    private sealed class State(Action signal, Guid iid)
    {
        internal readonly Action Signal = signal;
        internal readonly Guid Id = iid;
        internal readonly TaskCompletionSource Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Retired;
    }

    private static State Get(nint self) => (State)GCHandle.FromIntPtr(((nint*)self)[2]).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Query(nint self, Guid* iid, nint* output)
    {
        if (output == null || iid == null) return unchecked((int)0x80004003);
        *output = 0;
        if (*iid != WebView2Native.IID_IUnknown && *iid != AgileId && *iid != Get(self).Id)
            return unchecked((int)0x80004002);
        *output = self;
        Interlocked.Increment(ref ((long*)self)[1]);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(nint self) => (uint)Interlocked.Increment(ref ((long*)self)[1]);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(nint self)
    {
        var remaining = (uint)Interlocked.Decrement(ref ((long*)self)[1]);
        if (remaining != 0) return remaining;
        var state = Get(self);
        GCHandle.FromIntPtr(((nint*)self)[2]).Free();
        NativeMemory.Free((void*)((nint*)self)[0]);
        NativeMemory.Free((void*)self);
        state.Released.TrySetResult();
        return 0;
    }

    private static int Invoke(nint self)
    {
        var state = Get(self);
        if (Volatile.Read(ref state.Retired)) return 0;
        try { state.Signal(); return 0; }
        catch { return unchecked((int)0x80004005); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int FrameInvoke(nint self, nint sender, nint args) => Invoke(self);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int MediaInvoke(nint self, nint result) => Invoke(self);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Parameters(nint self, uint* flags, uint* queue) => unchecked((int)0x80004001);
}
