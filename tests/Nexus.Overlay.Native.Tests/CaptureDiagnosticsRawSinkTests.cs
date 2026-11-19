using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nexus.Overlay.Capture;
using Nexus.Overlay.Media;
using Nexus.Overlay.WebView2;

namespace Nexus.Overlay.Tests.NativeOwners;

/// <summary>Fake COM and16synthetic pixel bytes only: no GPU, WGC, window or service.</summary>
public sealed unsafe class CaptureDiagnosticsRawSinkTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Shared_output_keeps_exact_pixels_without_http_arrays_for_diagnostics_off_and_on(bool enabled, bool padded)
    {
        using var fixture = new Fixture(padded);
        var metrics = enabled ? new CaptureDiagnostics(Stopwatch.Frequency) : null;
        var name = "Nexus-R12-Synthetic-" + Guid.NewGuid().ToString("N");
        using var mapping = MemoryMappedFile.CreateNew(name, 64 + 16);
        using var view = mapping.CreateViewAccessor();
        using var ready = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-Ready");
        view.Write(0, 0x4653584E);
        using var shared = new SharedFrameLink(name, 16, () => Assert.Fail("Private synthetic section became stale"), "synthetic");
        using var sink = new RawFrameSink(fixture.Device, 2, 2, (_, _) => Assert.Fail("Shared output cannot publish HTTP buffers"),
            "synthetic", shared, metrics);
        sink.Submit(fixture.NextFrame());
        Assert.True(ready.WaitOne(0));
        Assert.Equal(2, view.ReadInt64(8));
        Assert.Equal(2, view.ReadInt32(16)); Assert.Equal(2, view.ReadInt32(20));
        Assert.Equal(16, view.ReadInt32(28));
        var pixels = new byte[16]; view.ReadArray(64, pixels, 0, 16);
        Assert.Equal(Enumerable.Range(1, 16).Select(x => (byte)x), pixels);
        Assert.Equal(1, fixture.State.Unmaps); Assert.Equal(1, fixture.State.Closed);
        if (metrics is not null)
        {
            Assert.Equal(1, metrics.Snapshot().SharedCopies);
            Assert.Equal(0, metrics.Snapshot().HttpCopies);
            Assert.Equal(0, metrics.Snapshot().BufferExhausted);
        }
        sink.Dispose();
        Assert.True(sink.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public void Submit_after_dispose_releases_capture_without_map_or_copy()
    {
        using var fixture = new Fixture(padded: false);
        var metrics = new CaptureDiagnostics(Stopwatch.Frequency);
        using var sink = new RawFrameSink(fixture.Device, 2, 2, (_, _) => Assert.Fail("Retired sink cannot publish"),
            "synthetic", diagnostics: metrics);
        sink.Dispose();
        sink.Submit(fixture.NextFrame());
        Assert.Equal(1, fixture.State.Closed);
        Assert.Equal(0, fixture.State.Unmaps);
        Assert.Equal(0, metrics.Snapshot().Map.Count);
        Assert.Equal(0, metrics.Snapshot().Copy.Count);
        Assert.True(sink.Completion.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Real_raw_sink_keeps_pixel_rows_and_buffer_bound_while_recording_copy_and_map(bool padded)
    {
        using var fixture = new Fixture(padded);
        var metrics = new CaptureDiagnostics(Stopwatch.Frequency);
        var received = new List<byte[]>();
        using var sink = new RawFrameSink(fixture.Device, 2, 2, (bytes, _) => received.Add(bytes), "synthetic", diagnostics: metrics);
        for (var i = 0; i < 4; i++) sink.Submit(fixture.NextFrame());
        Assert.Equal(3, received.Count);
        Assert.All(received, bytes => Assert.Equal(Enumerable.Range(1, 16).Select(x => (byte)x), bytes));
        Assert.Equal(4, fixture.State.Unmaps);
        Assert.Equal(4, fixture.State.Closed);
        var snapshot = metrics.Snapshot();
        Assert.Equal(4, snapshot.Map.Count);
        Assert.Equal(4, snapshot.Copy.Count);
        Assert.Equal(3, snapshot.HttpCopies);
        Assert.Equal(1, snapshot.BufferExhausted);
        Assert.Equal(0, snapshot.MapFailures);
        sink.Dispose();
        Assert.False(sink.Completion.IsCompleted);
        foreach (var bytes in received) sink.Return(bytes);
        Assert.True(sink.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public void Callback_failure_returns_the_existing_array_and_preserves_capture_release_and_retry()
    {
        using var fixture = new Fixture(padded: false);
        var metrics = new CaptureDiagnostics(Stopwatch.Frequency);
        var fail = true;
        byte[]? received = null;
        using var sink = new RawFrameSink(fixture.Device, 2, 2, (bytes, _) =>
        {
            if (fail) throw new IOException("Injected callback failure");
            received = bytes;
        }, "synthetic", diagnostics: metrics);
        Assert.Throws<IOException>(() => sink.Submit(fixture.NextFrame()));
        fail = false;
        sink.Submit(fixture.NextFrame());
        Assert.NotNull(received);
        Assert.Equal(2, metrics.Snapshot().HttpCopies); // completed copies, not callback/delivery success
        Assert.Equal(0, metrics.Snapshot().BufferExhausted);
        Assert.Equal(2, fixture.State.Closed);
        Assert.Equal(2, fixture.State.Unmaps);
        sink.Return(received);
        sink.Dispose();
        Assert.True(sink.Completion.IsCompletedSuccessfully);
    }

    private sealed class State(nint context, nint staging, bool padded) : IDisposable
    {
        internal readonly nint Context = context, Staging = staging;
        internal readonly uint Pitch = padded ? 12U : 8U;
        internal readonly byte* Pixels = (byte*)NativeMemory.AllocZeroed(24);
        internal int Unmaps, Closed;
        internal void Fill()
        {
            for (var row = 0; row < 2; row++)
                for (var column = 0; column < 8; column++) Pixels[row * Pitch + column] = (byte)(1 + row * 8 + column);
        }
        public void Dispose() => NativeMemory.Free(Pixels);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly FakeCom _device = new(), _context = new(), _staging = new(), _frame = new(), _close = new(), _texture = new();
        internal readonly State State;
        internal readonly D3DDevice Device;
        internal Fixture(bool padded)
        {
            State = new(_context.Pointer, _staging.Pointer, padded); State.Fill();
            _device.State = _context.State = _close.State = State;
            _frame.Query = iid => iid == WgcVtable.IID_IClosable ? _close.Pointer : 0;
            _device.Set(40, (nint)(delegate* unmanaged[Stdcall]<nint, nint*, void>)&Context);
            _device.Set(5, (nint)(delegate* unmanaged[Stdcall]<nint, void*, nint, nint*, int>)&CreateTexture);
            _context.Set(47, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, void>)&Copy);
            _context.Set(14, (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, D3D11Vtable.D3D11_MAPPED_SUBRESOURCE*, int>)&Map);
            _context.Set(15, (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, void>)&Unmap);
            _close.Set(6, (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Close);
            Device = (D3DDevice)RuntimeHelpers.GetUninitializedObject(typeof(D3DDevice)); Device.Device = _device.Pointer;
        }
        internal CapturedFrame NextFrame()
        {
            Wv2.AddRef(_frame.Pointer); Wv2.AddRef(_texture.Pointer);
            return new(_frame.Pointer, _texture.Pointer, 0);
        }
        public void Dispose()
        {
            State.Dispose();
            _texture.Dispose(); _close.Dispose(); _frame.Dispose(); _staging.Dispose(); _context.Dispose(); _device.Dispose();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Context(nint self, nint* output)
    { *output = ((State)FakeCom.Find(self).State!).Context; Wv2.AddRef(*output); }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateTexture(nint self, void* descriptor, nint initial, nint* output)
    { *output = ((State)FakeCom.Find(self).State!).Staging; return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Copy(nint self, nint target, nint source) { }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Map(nint self, nint texture, uint subresource, uint mode, uint flags, D3D11Vtable.D3D11_MAPPED_SUBRESOURCE* output)
    {
        var state = (State)FakeCom.Find(self).State!;
        *output = new() { pData = (nint)state.Pixels, RowPitch = state.Pitch };
        return 0;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Unmap(nint self, nint texture, uint subresource) => ((State)FakeCom.Find(self).State!).Unmaps++;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Close(nint self) { ((State)FakeCom.Find(self).State!).Closed++; return 0; }
}
