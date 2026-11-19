using System.Runtime.InteropServices;
using System.Diagnostics;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using Nexus.Overlay.Win32;

namespace Nexus.Overlay.Bench;

internal sealed class SyntheticScene(bool animate, int x) : IWin32WindowOwner, IDisposable
{
    internal const int Width = 800, Height = 800, FramesPerSecond = 30;
    private const uint LayeredStyle = 0x00080000;
    private readonly TaskCompletionSource<nint> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _thread;
    private nint _window;
    private uint _step;
    private nint _surfaceDc, _bitmap, _previousBitmap;
    private long _presented;
    private Exception? _failure;
    private readonly ManualResetEvent _stopAnimation = new(false);
    private Thread? _animation;
    internal long PresentedFrames => Interlocked.Read(ref _presented);
    internal void ThrowIfFailed()
    {
        if (_failure is { } failure) throw new InvalidOperationException("Authored scene failed", failure);
    }
    internal nint Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "benchmark-scene" };
        _thread.SetApartmentState(ApartmentState.STA); _thread.Start();
        return _ready.Task.GetAwaiter().GetResult();
    }
    private void Run()
    {
        try
        {
            Native.CoInitializeEx(0, Native.COINIT_APARTMENTTHREADED);
            _window = Win32Window.Create("Nexus.Bench.Scene." + x, "Synthetic Nexus benchmark",
                Native.WS_POPUP, LayeredStyle | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW,
                x, 50, Width, Height, this);
            CreateSurface();
            Draw();
            if (!Native.GetWindowRect(_window, out var bounds) || bounds.Width != Width || bounds.Height != Height)
                throw new InvalidOperationException("Benchmark scene must be exactly 800x800 physical pixels");
            Native.ShowWindow(_window, Native.SW_SHOWNOACTIVATE);
            if (animate)
            {
                _animation = new Thread(Animate) { IsBackground = true, Name = "benchmark-authored-30fps" };
                _animation.Start();
            }
            _ready.TrySetResult(_window);
            while (Native.GetMessageW(out var message, 0, 0, 0))
            { Native.TranslateMessage(ref message); Native.DispatchMessageW(ref message); }
        }
        catch (Exception error) { _failure = error; _ready.TrySetException(error); }
        finally
        {
            _stopAnimation.Set();
            if (_animation is null || _animation.Join(5000))
            {
                if (_previousBitmap != 0) SelectObject(_surfaceDc, _previousBitmap);
                if (_bitmap != 0) DeleteObject(_bitmap);
                if (_surfaceDc != 0) DeleteDC(_surfaceDc);
            }
            else _failure = new TimeoutException("Scene presentation remains in flight");
            if (_window != 0) { Native.DestroyWindow(_window); Win32Window.Unregister(_window); _window = 0; }
        }
    }
    public nint? HandleMessage(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == Native.WM_CLOSE)
        {
            Native.DestroyWindow(window); Win32Window.Unregister(window); _window = 0;
            Native.PostQuitMessage(0); return 0;
        }
        return null;
    }
    private void Draw()
    {
        // Window GetDC is clipped to the visible desktop region. A memory DC
        // supplies the complete opaque bitmap even when this HWND is offscreen.
        var brush = CreateSolidBrush((_step++ % 8) * 0x202020);
        if (brush == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateSolidBrush failed");
        try
        {
            var rectangle = new Native.RECT { Right = Width, Bottom = Height };
            if (FillRect(_surfaceDc, ref rectangle, brush) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Memory scene FillRect failed");
        }
        finally { DeleteObject(brush); }
        var destination = new Native.POINT { x = x, y = 50 };
        var source = new Native.POINT();
        var size = new Size { Width = Width, Height = Height };
        if (!UpdateLayeredWindow(_window, 0, ref destination, ref size, _surfaceDc, ref source, 0, 0, 4 /* ULW_OPAQUE */))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Opaque offscreen scene presentation failed");
        Interlocked.Increment(ref _presented);
    }

    private void CreateSurface()
    {
        _surfaceDc = CreateCompatibleDC(0);
        if (_surfaceDc == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateCompatibleDC failed");
        var bitmap = new BitmapInfo { Header = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = Width, Height = -Height,
            Planes = 1, BitCount = 32, ImageBytes = Width * Height * 4,
        } };
        _bitmap = CreateDIBSection(_surfaceDc, ref bitmap, 0, out _, 0, 0);
        if (_bitmap == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateDIBSection failed");
        _previousBitmap = SelectObject(_surfaceDc, _bitmap);
        if (_previousBitmap is 0 or -1)
        {
            _previousBitmap = 0;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SelectObject failed");
        }
    }
    private void Animate()
    {
        try { AnimateCore(); }
        catch (Exception error) { _failure = error; _stopAnimation.Set(); }
    }
    private void AnimateCore()
    {
        // Authored input must not speed up when the baseline pump requests timeBeginPeriod(1).
        using var timer = new EventWaitHandle(false, EventResetMode.AutoReset);
        nint handle = CreateWaitableTimerExW(0, 0, 2, 0x1f0003);
        if (handle == 0) throw new InvalidOperationException("High-resolution authored-scene clock unavailable");
        timer.SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: true);
        WaitHandle[] waits = [timer, _stopAnimation];
        long origin = Stopwatch.GetTimestamp(), frame = 1;
        while (!_stopAnimation.WaitOne(0))
        {
            long next = origin + frame * Stopwatch.Frequency / FramesPerSecond;
            long due = -Math.Max(1, (next - Stopwatch.GetTimestamp()) * 10_000_000 / Stopwatch.Frequency);
            if (!SetWaitableTimer(handle, ref due, 0, 0, 0, false)) throw new InvalidOperationException("Authored-scene timer failed");
            if (WaitHandle.WaitAny(waits) != 0) break;
            Draw(); frame++;
        }
    }
    public void Dispose()
    {
        _stopAnimation.Set();
        if (_animation is not null && !_animation.Join(5000)) throw new TimeoutException("Authored scene did not stop");
        Native.PostMessageW(_window, Native.WM_CLOSE, 0, 0);
        if (_thread is not null && !_thread.Join(5000)) throw new TimeoutException("Scene window did not stop");
        _stopAnimation.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)] private struct Size { internal int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    {
        internal uint Size;
        internal int Width, Height;
        internal ushort Planes, BitCount;
        internal uint Compression, ImageBytes;
        internal int XPixelsPerMeter, YPixelsPerMeter;
        internal uint ColoursUsed, ImportantColours;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { internal BitmapInfoHeader Header; internal uint Colour; }
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(nint window, nint destinationDc,
        ref Native.POINT destination, ref Size size, nint sourceDc, ref Native.POINT source, uint colourKey, nint blend, uint flags);
    [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref Native.RECT rectangle, nint brush);
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint colour);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWaitableTimerExW(nint attributes, nint name, uint flags, uint access);
    [DllImport("kernel32.dll")] private static extern bool SetWaitableTimer(nint timer, ref long due, int period, nint completion, nint argument, bool resume);
}
