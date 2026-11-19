using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nexus.Overlay.WebView2;

namespace Nexus.Overlay.Capture;

/// <summary>
/// Windows.Graphics.SizeInt32 ABI layout (windows.graphics.h: two INT32
/// fields, passed by value).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SizeInt32
{
    public int Width;
    public int Height;
}

/// <summary>
/// Windows.Graphics.Capture over one HWND; frame arrival only wakes its owner.
/// WGC on an off-screen window silently stops delivering frames after
/// minutes; <see cref="Restart"/> rebuilds item+pool+session on the same
/// HWND, which re-hooks in under a second (bench-proven; the shared
/// D3D device and the WebView2 rendering into the window stay untouched).
/// Not thread-safe: poll, Restart, and Dispose must come from one owner at a
/// time (the host serializes pump-stop before Restart/Dispose).
/// </summary>
internal sealed unsafe class WgcCapture : IDisposable
{
    // windows.graphics.directx.h DirectXPixelFormat_B8G8R8A8UIntNormalized
    // (equals DXGI_FORMAT_B8G8R8A8_UNORM; header not in the local
    // transcription set, well-known published value).
    private const int DirectXPixelFormatB8G8R8A8UIntNormalized = 87;
    private const int PoolBufferCount = 2;

    private readonly IntPtr _hwnd;
    private readonly D3DDevice _device;
    private readonly int _width;
    private readonly int _height;

    private IntPtr _item;
    private IntPtr _pool;
    private IntPtr _session;
    private bool _disposed;
    private readonly CaptureWakeSignal _wake;
    private readonly Func<CaptureDiagnostics?> _diagnosticProvider;
    private CaptureDiagnostics? _diagnostics => _diagnosticProvider();
    private NativeSignalCallback? _arrival;
    private long _arrivalToken;
    private bool _arrivalSubscribed;
    private readonly List<Task> _retiredCallbacks = new();
    internal Task Completion => Task.WhenAll(_retiredCallbacks);
    internal void Wake() => _wake.Signal();
    internal bool WaitForFrame() => _wake.Wait();

    public WgcCapture(IntPtr hwnd, D3DDevice device, int pixelWidth, int pixelHeight, CaptureDiagnostics? diagnostics = null,
        Func<CaptureDiagnostics?>? diagnosticsProvider = null)
    {
        _hwnd = hwnd;
        _device = device;
        _width = pixelWidth;
        _height = pixelHeight;
        _diagnosticProvider = diagnosticsProvider ?? (() => diagnostics);
        _wake = new(diagnostics, _diagnosticProvider);
    }

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try { CreateSession(); }
        catch { ReleaseSession(); throw; }
    }

    /// <summary>
    /// Returns a pool-frame lease, or false when empty. The consumer closes it
    /// after synchronous CPU reading or a completed copy into owned GPU storage.
    /// Its timestamp is SystemRelativeTime in QPC-based 100ns ticks.
    /// </summary>
    public bool TryGetNextFrame(out CapturedFrame? captured) => TryGetNextFrame(out captured, out _);

    internal bool TryGetNextFrame(out CapturedFrame? captured, out int timestampHResult)
    {
        captured = null;
        timestampHResult = 0;
        long frameTimeTicks = 0;
        if (_pool == IntPtr.Zero) return false;

        IntPtr frame = IntPtr.Zero;
        var tryGet = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Wv2.Slot(_pool, WgcVtable.Pool_TryGetNextFrame);
        var getHr = tryGet(_pool, &frame);
        if (getHr < 0 || frame == IntPtr.Zero)
        {
            _diagnostics?.PoolRead(empty: frame == IntPtr.Zero, failed: getHr < 0);
            return false;
        }

        try
        {
            long frameTime;
            var getTime = (delegate* unmanaged[Stdcall]<IntPtr, long*, int>)Wv2.Slot(frame, WgcVtable.Frame_get_SystemRelativeTime);
            timestampHResult = getTime(frame, &frameTime);
            if (timestampHResult >= 0) frameTimeTicks = frameTime;

            IntPtr surface;
            var getSurface = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Wv2.Slot(frame, WgcVtable.Frame_get_Surface);
            if (getSurface(frame, &surface) < 0 || surface == IntPtr.Zero)
            {
                _diagnostics?.ExtractionFailed();
                return false;
            }

            try
            {
                var access = Wv2.QueryInterface(surface, WgcVtable.IID_IDirect3DDxgiInterfaceAccess);
                if (access == IntPtr.Zero) { _diagnostics?.ExtractionFailed(); return false; }
                try
                {
                    IntPtr tex;
                    var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)
                        Wv2.Slot(access, WgcVtable.InterfaceAccess_GetInterface);
                    fixed (Guid* iid = &D3D11Interop.IID_ID3D11Texture2D)
                    {
                        if (getInterface(access, iid, &tex) < 0 || tex == IntPtr.Zero)
                        {
                            _diagnostics?.ExtractionFailed();
                            return false;
                        }
                    }
                    captured = CapturedFrame.Create(frame, tex, frameTimeTicks);
                    frame = IntPtr.Zero; // ownership transfers to the consumer
                    return true;
                }
                finally { Wv2.Release(access); }
            }
            finally { Wv2.Release(surface); }
        }
        finally { if (frame != IntPtr.Zero) CapturedFrame.Close(frame); }
    }

    /// <summary>Tears down and recreates item+pool+session on the same HWND.</summary>
    public void Restart()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ReleaseSession();
        CreateSession();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseSession();
        _wake.Dispose();
    }

    private void CreateSession()
    {
        _retiredCallbacks.RemoveAll(task => task.IsCompletedSuccessfully);
        if (_retiredCallbacks.Count != 0)
            throw new InvalidOperationException("Capture callback retirement is still pending.");
        // GraphicsCaptureItem via the interop factory (no WinRT projection
        // for CreateForWindow exists).
        var interop = WinRtInterop.GetActivationFactory(
            "Windows.Graphics.Capture.GraphicsCaptureItem",
            WgcVtable.IID_IGraphicsCaptureItemInterop);
        try
        {
            IntPtr item;
            var createForWindow = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)
                Wv2.Slot(interop, WgcVtable.ItemInterop_CreateForWindow);
            int hr;
            fixed (Guid* iid = &WgcVtable.IID_IGraphicsCaptureItem)
            {
                hr = createForWindow(interop, _hwnd, iid, &item);
            }
            if (hr < 0 || item == IntPtr.Zero)
                throw new CaptureItemException(hr);
            _item = item;
        }
        finally { Wv2.Release(interop); }

        var statics = WinRtInterop.GetActivationFactory(
            "Windows.Graphics.Capture.Direct3D11CaptureFramePool",
            WgcVtable.IID_IDirect3D11CaptureFramePoolStatics2);
        try
        {
            IntPtr pool;
            var createFreeThreaded = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int, SizeInt32, IntPtr*, int>)
                Wv2.Slot(statics, WgcVtable.PoolStatics2_CreateFreeThreaded);
            var hr = createFreeThreaded(
                statics, _device.WinRtDevice,
                DirectXPixelFormatB8G8R8A8UIntNormalized, PoolBufferCount,
                new SizeInt32 { Width = _width, Height = _height }, &pool);
            if (hr < 0 || pool == IntPtr.Zero)
            {
                ReleaseSession();
                throw new InvalidOperationException($"CreateFreeThreaded failed 0x{hr:X8}");
            }
            _pool = pool;
        }
        finally { Wv2.Release(statics); }

        _arrival = new NativeSignalCallback(_wake.SignalFrame);
        var subscribe = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, long*, int>)
            Wv2.Slot(_pool, WgcVtable.Pool_add_FrameArrived);
        long arrivalToken;
        var arrivalHr = subscribe(_pool, _arrival.Pointer, &arrivalToken);
        if (arrivalHr < 0)
        {
            ReleaseSession();
            throw new InvalidOperationException($"Capture FrameArrived registration failed 0x{arrivalHr:X8}");
        }
        _arrivalToken = arrivalToken;
        _arrivalSubscribed = true;

        IntPtr session;
        var createSession = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)
            Wv2.Slot(_pool, WgcVtable.Pool_CreateCaptureSession);
        var sessionHr = createSession(_pool, _item, &session);
        if (sessionHr < 0 || session == IntPtr.Zero)
        {
            ReleaseSession();
            throw new InvalidOperationException($"CreateCaptureSession failed 0x{sessionHr:X8}");
        }
        _session = session;

        // Best effort: Session2 predates all supported OS builds but a
        // missing QI must not kill the stream (the cursor is off-screen
        // anyway).
        var session2 = Wv2.QueryInterface(_session, WgcVtable.IID_IGraphicsCaptureSession2);
        if (session2 != IntPtr.Zero)
        {
            var putCursor = (delegate* unmanaged[Stdcall]<IntPtr, byte, int>)
                Wv2.Slot(session2, WgcVtable.Session2_put_IsCursorCaptureEnabled);
            putCursor(session2, 0);
            Wv2.Release(session2);
        }

        // Best effort: absent before Windows 11 24H2, where the compositor default interval applies.
        var session5 = Wv2.QueryInterface(_session, WgcVtable.IID_IGraphicsCaptureSession5);
        if (session5 != IntPtr.Zero)
        {
            var putMinUpdate = (delegate* unmanaged[Stdcall]<IntPtr, long, int>)
                Wv2.Slot(session5, WgcVtable.Session5_put_MinUpdateInterval);
            var minHr = putMinUpdate(session5, WgcVtable.CaptureMinUpdateIntervalTicks);
            Wv2.Release(session5);
            if (minHr < 0) Log.Warn($"stream-capture: MinUpdateInterval refused 0x{minHr:X8}");
        }

        var start = (delegate* unmanaged[Stdcall]<IntPtr, int>)Wv2.Slot(_session, WgcVtable.Session_StartCapture);
        var startHr = start(_session);
        if (startHr < 0)
        {
            ReleaseSession();
            throw new InvalidOperationException($"StartCapture failed 0x{startHr:X8}");
        }
        Log.Info($"stream-capture: session started {_width}x{_height} hwnd=0x{_hwnd:X}");
        _diagnostics?.CaptureStarted();
    }

    private void ReleaseSession()
    {
        if (_arrival is { } arrival)
        {
            // Mark retired before removing the native subscription. A late
            // invocation is a no-op; native refs retain the callback state.
            arrival.Dispose();
            if (_pool != IntPtr.Zero && _arrivalSubscribed)
            {
                var remove = (delegate* unmanaged[Stdcall]<IntPtr, long, int>)
                    Wv2.Slot(_pool, WgcVtable.Pool_remove_FrameArrived);
                remove(_pool, _arrivalToken);
                _arrivalSubscribed = false;
            }
            _retiredCallbacks.Add(arrival.Completion);
            _arrival = null;
        }
        if (_session != IntPtr.Zero)
        {
            CloseAndRelease(_session);
            _session = IntPtr.Zero;
        }
        if (_pool != IntPtr.Zero)
        {
            CloseAndRelease(_pool);
            _pool = IntPtr.Zero;
        }
        if (_item != IntPtr.Zero)
        {
            // GraphicsCaptureItem is not IClosable; a plain release suffices.
            Wv2.Release(_item);
            _item = IntPtr.Zero;
        }
    }

    /// <summary>IClosable.Close (when implemented) then Release. Frames,
    /// pools, and sessions all need the explicit Close to free their D3D
    /// resources deterministically.</summary>
    private static void CloseAndRelease(IntPtr winRtObject)
    {
        var closable = Wv2.QueryInterface(winRtObject, WgcVtable.IID_IClosable);
        if (closable != IntPtr.Zero)
        {
            var close = (delegate* unmanaged[Stdcall]<IntPtr, int>)Wv2.Slot(closable, WgcVtable.Closable_Close);
            close(closable);
            Wv2.Release(closable);
        }
        Wv2.Release(winRtObject);
    }
}

/// <summary>CreateForWindow refused the HWND; HResult carries its HRESULT.</summary>
internal sealed class CaptureItemException : InvalidOperationException
{
    public CaptureItemException(int hr) : base($"CreateForWindow failed 0x{hr:X8}") => HResult = hr;
}
