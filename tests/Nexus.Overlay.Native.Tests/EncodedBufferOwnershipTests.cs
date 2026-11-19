using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nexus.Overlay.Media;

namespace Nexus.Overlay.Tests.NativeOwners;

public sealed unsafe class EncodedBufferOwnershipTests
{
    [Theory]
    [InlineData("valid")] [InlineData("conversion-failure")] [InlineData("null-buffer")]
    [InlineData("lock-failure")] [InlineData("oversize")] [InlineData("invalid-length")] [InlineData("null-data")]
    public void Encoded_native_buffers_release_even_when_rejected(string mode)
    {
        using var sample = new FakeCom(); using var buffer = new FakeCom();
        byte* data = (byte*)NativeMemory.Alloc(4);
        try
        {
            data[0] = 1; data[1] = 2; data[2] = 3; data[3] = 4;
            var state = new State(mode, buffer.Pointer, (nint)data);
            sample.State = buffer.State = state;
            sample.Set(41, (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&Convert);
            buffer.Set(3, (nint)(delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)&Lock);
            buffer.Set(4, (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Unlock);
            if (mode is "oversize" or "invalid-length" or "null-data")
                Assert.Throws<InvalidOperationException>(() => MfEncoder.ReadAccessUnit(sample.Pointer));
            else if (mode == "valid") Assert.Equal(new byte[] { 1, 2, 3, 4 }, MfEncoder.ReadAccessUnit(sample.Pointer));
            else Assert.Null(MfEncoder.ReadAccessUnit(sample.Pointer));
            Assert.Equal(1, buffer.References);
            Assert.Equal(mode is "conversion-failure" or "null-buffer" or "lock-failure" ? 0 : 1, state.Unlocks);
        }
        finally { NativeMemory.Free(data); }
    }
    private sealed class State(string mode, nint buffer, nint data)
    { internal readonly string Mode = mode; internal readonly nint Buffer = buffer, Data = data; internal int Unlocks; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Convert(nint self, nint* result)
    {
        var s = (State)FakeCom.Find(self).State!;
        *result = s.Mode is "conversion-failure" or "null-buffer" ? 0 : s.Buffer;
        if (*result != 0) FakeCom.Find(*result).References++;
        return s.Mode == "conversion-failure" ? -1 : 0;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Lock(nint self, byte** data, uint* maximum, uint* length)
    {
        var s = (State)FakeCom.Find(self).State!;
        *data = s.Mode == "null-data" ? null : (byte*)s.Data;
        *maximum = 4;
        *length = s.Mode == "oversize" ? 16 * 1024 * 1024 + 1u : s.Mode == "invalid-length" ? 5u : 4u;
        return s.Mode == "lock-failure" ? -1 : 0;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Unlock(nint self) { ((State)FakeCom.Find(self).State!).Unlocks++; return 0; }
}
