using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nexus.Overlay.Capture;
using Nexus.Overlay.Media;
using Nexus.Overlay.WebView2;

namespace Nexus.Overlay.Tests.NativeOwners;

/// <summary>Real Windows D3D/MF, synthetic solid frames only. No Nexus APIs, windows or devices.</summary>
public sealed class GraphicsOwnershipTests
{
    [Fact]
    [Trait("Category", "Graphics")]
    public void Raw_shared_section_preserves_complete_pixels_without_http_arrays()
    {
        const int width = 1680, height = 756, bytes = width * height * 4;
        string name = "Nexus-Synthetic-" + Guid.NewGuid().ToString("N");
        using var mapping = MemoryMappedFile.CreateNew(name, bytes + 64);
        using var view = mapping.CreateViewAccessor();
        using var ready = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-Ready");
        view.Write(0, 0x4653584e);
        using var device = D3DDevice.Create();
        using var frame = new FakeCom(); using var close = new FakeCom();
        ConfigureFrame(frame, close);
        using var shared = new SharedFrameLink(name, bytes, () => Assert.Fail("Synthetic reader reported stale"), "synthetic");
        using var sink = new RawFrameSink(device, width, height, (_, _) => Assert.Fail("Shared output unexpectedly used HTTP"), "synthetic", shared);
        for (int i = 1; i <= 3; i++)
        {
            Wv2.AddRef(frame.Pointer);
            sink.Submit(new(frame.Pointer, CreateSolid(device, width, height, (uint)(0xff000000 | (i * 0x202020))), i));
            Assert.True(ready.WaitOne(1000)); Assert.Equal(i * 2, view.ReadInt64(8));
            Assert.Equal(width, view.ReadInt32(16)); Assert.Equal(height, view.ReadInt32(20));
            Assert.Equal(bytes, view.ReadInt32(28));
            Assert.Equal(i * 32, view.ReadByte(64)); Assert.Equal(i * 32, view.ReadByte(bytes + 60));
            Assert.Equal(255, view.ReadByte(bytes + 63));
            view.Write(32, i * 2L);
        }
        sink.Dispose(); Assert.True(sink.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    [Trait("Category", "Graphics")]
    public void Raw_readback_preserves_pixels_and_never_allocates_a_fourth_array()
    {
        using var device = D3DDevice.Create();
        using var frame = new FakeCom(); using var close = new FakeCom();
        ConfigureFrame(frame, close);
        var output = new List<byte[]>();
        using var sink = new RawFrameSink(device, 1680, 756, (data, _) => output.Add(data), "synthetic");
        for (int i = 0; i < 5; i++)
        {
            Wv2.AddRef(frame.Pointer);
            sink.Submit(new(frame.Pointer, CreateSolid(device, 1680, 756, (uint)(0xff000000 | (i * 0x202020))), i));
        }
        Assert.Equal(3, output.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(5_080_320, output[i].Length);
            foreach (int offset in new[] { 0, output[i].Length / 2, output[i].Length - 4 })
            {
                Assert.Equal(i * 32, output[i][offset]); Assert.Equal(255, output[i][offset + 3]);
            }
        }
        sink.Return(output[0]);
        Wv2.AddRef(frame.Pointer);
        sink.Submit(new(frame.Pointer, CreateSolid(device, 1680, 756, 0xffa0a0a0), 6));
        Assert.Equal(4, output.Count); Assert.Same(output[0], output[3]); Assert.Equal(160, output[3][0]);
        sink.Dispose(); Assert.False(sink.Completion.IsCompleted);
        foreach (var data in output) sink.Return(data);
        Assert.True(sink.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    [Trait("Category", "Graphics")]
    public async Task Tracked_sample_release_controls_four_surface_ring_reuse()
    {
        Assert.True(MfInterop.MFStartup(MfVtable.MF_VERSION, MfVtable.MFSTARTUP_NOSOCKET) >= 0);
        using var device = D3DDevice.Create();
        using var ring = new OwnedTextureRing(device, 800, 800);
        using var frame = new FakeCom(); using var close = new FakeCom();
        ConfigureFrame(frame, close);
        var samples = new List<nint>();
        try
        {
            for (int i = 0; i < 4; i++)
            {
                Wv2.AddRef(frame.Pointer);
                var source = new CapturedFrame(frame.Pointer, CreateSolid(device, 800, 800, (uint)(0xff000000 | (i * 0x303030))), i);
                var sample = ring.CopyToSample(source);
                Assert.NotEqual(0, sample); samples.Add(sample);
            }
            Assert.Equal(10_240_000, ring.TextureBytes);
            Wv2.AddRef(frame.Pointer);
            Assert.Equal(0, ring.CopyToSample(new(frame.Pointer, CreateSolid(device, 800, 800, 0xff000000), 5)));
            ring.Dispose(); Assert.False(ring.Completion.IsCompleted);
            foreach (var sample in samples) Wv2.Release(sample);
            samples.Clear();
            await ring.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { foreach (var sample in samples) Wv2.Release(sample); MfInterop.MFShutdown(); }
    }

    [Theory]
    [InlineData(800, 800, 30)] [InlineData(1024, 600, 30)]
    [InlineData(800, 800, 60)] [InlineData(1024, 600, 60)]
    [Trait("Category", "Graphics")]
    public async Task Selected_video_processor_and_encoder_sustain_authored_cadence(int width, int height, int fps)
    {
        using var device = D3DDevice.Create();
        using var frame = new FakeCom(); using var close = new FakeCom();
        ConfigureFrame(frame, close);
        int encoded = 0;
        var bytes = new List<byte[]>();
        using var encoder = new MfEncoder(device, width, height, fps, 8000, (data, _) =>
        { lock (bytes) bytes.Add(data); Interlocked.Increment(ref encoded); }, "synthetic");
        var started = Stopwatch.StartNew();
        int frames = fps * 6;
        for (int i = 0; i < frames; i++)
        {
            Wv2.AddRef(frame.Pointer);
            encoder.Submit(new(frame.Pointer, CreateSolid(device, width, height, (uint)(0xff000000 | ((i % 8) * 0x202020))), i));
            var delay = (i + 1) * 1000 / fps - started.ElapsedMilliseconds;
            if (delay > 0) Thread.Sleep((int)delay);
        }
        Thread.Sleep(500);
        encoder.Dispose();
        await encoder.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.InRange(encoded, frames - Math.Max(2, frames / 50), frames);
        var evidence = Environment.GetEnvironmentVariable("NEXUS_GRAPHICS_EVIDENCE");
        if (!string.IsNullOrEmpty(evidence))
        {
            Directory.CreateDirectory(evidence);
            using var output = File.Create(Path.Combine(evidence, $"synthetic-{width}x{height}-{fps}fps.h264"));
            lock (bytes) foreach (var data in bytes) output.Write(data);
        }
    }

    internal static unsafe nint CreateSolid(D3DDevice device, int width, int height, uint colour)
    {
        var desc = new D3D11Vtable.D3D11_TEXTURE2D_DESC
        { Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = 87, SampleDescCount = 1, BindFlags = 0x28 };
        var pixels = new uint[width * height]; Array.Fill(pixels, colour);
        nint texture;
        fixed (uint* data = pixels)
        {
            var initial = new InitialData { Data = (nint)data, Pitch = (uint)(width * 4) };
            var create = (delegate* unmanaged[Stdcall]<nint, D3D11Vtable.D3D11_TEXTURE2D_DESC*, InitialData*, nint*, int>)Wv2.Slot(device.Device, 5);
            Assert.True(create(device.Device, &desc, &initial, &texture) >= 0);
        }
        return texture;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InitialData { internal nint Data; internal uint Pitch, SlicePitch; }
    private static unsafe void ConfigureFrame(FakeCom frame, FakeCom close)
    {
        frame.Query = iid => iid == WgcVtable.IID_IClosable ? close.Pointer : 0;
        close.Set(6, (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Close);
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Close(nint self) => 0;
}
