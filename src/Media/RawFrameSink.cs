using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Nexus.Overlay.Capture;
using Nexus.Overlay.WebView2;

namespace Nexus.Overlay.Media;

/// <summary>
/// Uncompressed frame sink for panels that take a framebuffer rather than a video
/// stream: the captured BGRA texture is copied into a CPU-readable staging texture and
/// handed to the callback as tightly packed rows.
///
/// It stands in for <see cref="MfEncoder"/> and keeps the same Submit/callback shape, so
/// the host swaps one for the other on the assignment's codec and nothing else changes.
/// Only viable for a small, slow panel: a 640x640 frame is 1.6 MB, where H.264 would be
/// a few tens of KB.
///
/// Submit runs on the capture pump thread. The consumer (IngestClient) queues what it is
/// handed without copying, so a frame's array is only reused after the consumer passes it
/// back through <see cref="Return"/> once its bytes are sent.
/// </summary>
internal sealed unsafe class RawFrameSink : IFrameSink
{
    private readonly int _width;
    private readonly int _height;
    private readonly string _logTag;
    private readonly Action<byte[], bool> _onFrame;
    private readonly IntPtr _context;

    // All producer/pending/in-flight arrays share these three leases.
    private readonly RawFrameBufferPool _buffers;
    private readonly TaskCompletionSource _readbackSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CapturedFrame? _quarantinedFrame;
    public Task Completion => Task.WhenAll(_buffers.Completion, _readbackSettled.Task);

    private readonly SharedFrameLink? _shared;
    private readonly Func<CaptureDiagnostics?> _diagnosticProvider;
    private CaptureDiagnostics? _diagnostics => _diagnosticProvider();
    private IntPtr _staging;
    private bool _disposed;

    /// <param name="shared">When set, a mapped frame is written straight into the shared section and
    /// <paramref name="onFrame"/> only sees frames the section could not take.</param>
    public RawFrameSink(D3DDevice device, int width, int height, Action<byte[], bool> onFrame, string logTag,
        SharedFrameLink? shared = null, CaptureDiagnostics? diagnostics = null, Func<CaptureDiagnostics?>? diagnosticsProvider = null)
    {
        _shared = shared;
        _diagnosticProvider = diagnosticsProvider ?? (() => diagnostics);
        _buffers = new RawFrameBufferPool(width, height);
        _width = width;
        _height = height;
        _onFrame = onFrame;
        _logTag = logTag;

        var getContext = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)
            Wv2.Slot(device.Device, D3D11Vtable.Device_GetImmediateContext);
        IntPtr ctx;
        getContext(device.Device, &ctx);
        if (ctx == IntPtr.Zero)
        {
            throw new InvalidOperationException("ID3D11Device::GetImmediateContext returned null");
        }
        _context = ctx;

        try { _staging = CreateStaging(device.Device, width, height); }
        catch { Wv2.Release(ctx); _buffers.Dispose(); throw; }
    }

    private static IntPtr CreateStaging(IntPtr device, int width, int height)
    {
        var desc = new D3D11Vtable.D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = D3D11Vtable.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDescCount = 1,
            SampleDescQuality = 0,
            Usage = D3D11Vtable.D3D11_USAGE_STAGING,
            BindFlags = 0,
            CPUAccessFlags = D3D11Vtable.D3D11_CPU_ACCESS_READ,
            MiscFlags = 0,
        };
        var create = (delegate* unmanaged[Stdcall]<IntPtr, D3D11Vtable.D3D11_TEXTURE2D_DESC*, IntPtr, IntPtr*, int>)
            Wv2.Slot(device, D3D11Vtable.Device_CreateTexture2D);
        IntPtr tex;
        var hr = create(device, &desc, IntPtr.Zero, &tex);
        if (hr < 0 || tex == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateTexture2D (staging) failed: 0x{hr:X8}");
        }
        return tex;
    }

    /// <summary>Hands back a frame whose bytes have been sent, for a later Submit to fill.</summary>
    public void Return(byte[] frame)
    {
        _buffers.Return(frame);
    }

    /// <summary>
    /// Copies one captured texture out and emits it. Releases the caller's texture
    /// reference, as the encoder's Submit does.
    /// </summary>
    public void Submit(CapturedFrame frame)
    {
        try
        {
            SubmitCore(frame.Texture);
        }
        catch (ReadbackUnavailableException)
        {
            // Failed Map is not proof that the queued GPU copy stopped reading WGC.
            // Keep the entire native graph in the reserved host until process recovery.
            _quarantinedFrame = frame;
            throw;
        }
        finally
        {
            if (!ReferenceEquals(_quarantinedFrame, frame)) frame.Dispose();
        }
    }

    private void SubmitCore(IntPtr texture)
    {
        var diagnostics = _diagnostics;
        if (_disposed || _staging == IntPtr.Zero)
        {
            return;
        }

        var copy = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)
            Wv2.Slot(_context, D3D11Vtable.Context_CopyResource);
        copy(_context, _staging, texture);

        var map = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, D3D11Vtable.D3D11_MAPPED_SUBRESOURCE*, int>)
            Wv2.Slot(_context, D3D11Vtable.Context_Map);
        D3D11Vtable.D3D11_MAPPED_SUBRESOURCE mapped;
        var mapStarted = diagnostics is not null ? Stopwatch.GetTimestamp() : 0;
        var hr = map(_context, _staging, 0, D3D11Vtable.D3D11_MAP_READ, 0, &mapped);
        diagnostics?.MapCompleted(mapStarted, Stopwatch.GetTimestamp(), hr >= 0 && mapped.pData != IntPtr.Zero);
        if (hr < 0 || mapped.pData == IntPtr.Zero)
        {
            throw new ReadbackUnavailableException($"{_logTag}: staging Map failed: 0x{hr:X8}; source is quarantined");
        }
        int rowBytes = _width * 4;
        byte[]? frame = null;
        var copyStarted = diagnostics is not null ? Stopwatch.GetTimestamp() : 0;
        try
        {
            if (_shared is not null && _shared.TryWrite((byte*)mapped.pData, mapped.RowPitch, _width, _height))
            {
                diagnostics?.CopyCompleted(copyStarted, Stopwatch.GetTimestamp(), shared: true);
                return;
            }
            frame = _buffers.TryRent();
            if (frame is null)
            {
                diagnostics?.CopyCompleted(copyStarted, Stopwatch.GetTimestamp(), shared: false, bufferExhausted: true);
                return; // all three leases are still owned downstream
            }
            // RowPitch is the driver's stride and is >= width*4; copy row by row so the
            // callback always receives tightly packed rows.
            var src = (byte*)mapped.pData;
            fixed (byte* dst = frame)
            {
                if (mapped.RowPitch == (uint)rowBytes)
                {
                    Buffer.MemoryCopy(src, dst, frame.Length, frame.Length);
                }
                else
                {
                    for (int y = 0; y < _height; y++)
                    {
                        Buffer.MemoryCopy(src + ((long)y * mapped.RowPitch), dst + ((long)y * rowBytes), rowBytes, rowBytes);
                    }
                }
            }
            diagnostics?.CopyCompleted(copyStarted, Stopwatch.GetTimestamp(), shared: false);
        }
        catch
        {
            if (frame is not null) Return(frame);
            throw;
        }
        finally
        {
            var unmap = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)
                Wv2.Slot(_context, D3D11Vtable.Context_Unmap);
            unmap(_context, _staging, 0);
        }

        // Every raw frame stands alone, so each one is a keyframe.
        if (frame is not null)
        {
            try { _onFrame(frame, true); }
            catch { Return(frame); throw; }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _buffers.Dispose();
        if (_quarantinedFrame is not null)
        {
            Log.Warn($"{_logTag}: GPU readback remains quarantined; host process recovery is required");
            return;
        }
        if (_staging != IntPtr.Zero)
        {
            Marshal.Release(_staging);
            _staging = IntPtr.Zero;
        }
        if (_context != IntPtr.Zero)
        {
            Marshal.Release(_context);
        }
        _readbackSettled.TrySetResult();
    }

    private sealed class ReadbackUnavailableException(string message) : InvalidOperationException(message);
}
