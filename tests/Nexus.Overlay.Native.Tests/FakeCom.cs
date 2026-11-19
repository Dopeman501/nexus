using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nexus.Overlay.Tests.NativeOwners;

internal sealed unsafe class FakeCom : IDisposable
{
    private static readonly ConcurrentDictionary<nint, FakeCom> Instances = new();
    private readonly nint* _table;
    internal readonly nint Pointer;
    internal int References = 1;
    internal object? State;
    internal Func<Guid, nint>? Query;
    internal Action? Released = null;

    internal FakeCom(int slots = 128)
    {
        _table = (nint*)NativeMemory.AllocZeroed((nuint)(slots * nint.Size));
        Pointer = (nint)NativeMemory.AllocZeroed((nuint)nint.Size);
        *(nint*)Pointer = (nint)_table;
        _table[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&QueryInterface;
        _table[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef;
        _table[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release;
        Instances[Pointer] = this;
    }

    internal void Set(int slot, nint method) => _table[slot] = method;
    internal static FakeCom Find(nint pointer) => Instances[pointer];

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(nint self, Guid* iid, nint* output)
    {
        var value = Find(self).Query?.Invoke(*iid) ?? self;
        *output = value;
        if (value == 0) return unchecked((int)0x80004002);
        Find(value).References++;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(nint self) => (uint)++Find(self).References;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(nint self)
    {
        var value = Find(self);
        value.Released?.Invoke();
        return (uint)--value.References;
    }

    public void Dispose()
    {
        Instances.TryRemove(Pointer, out _);
        NativeMemory.Free((void*)Pointer);
        NativeMemory.Free(_table);
    }
}
