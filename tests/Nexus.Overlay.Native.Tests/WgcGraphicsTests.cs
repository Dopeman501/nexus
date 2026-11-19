using System.Diagnostics;
using System.Runtime.InteropServices;
using Nexus.Overlay.Capture;
using Nexus.Overlay.Win32;

namespace Nexus.Overlay.Tests.NativeOwners;

public sealed class WgcGraphicsTests
{
    [Fact]
    [Trait("Category", "Graphics")]
    public async Task Real_frame_arrivals_wake_empty_pool_and_restart_on_the_same_window()
    {
        RoInitialize(1);
        using var scene = new SyntheticWindow();
        nint window = await scene.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var device = D3DDevice.Create();
        var capture = new WgcCapture(window, device, 800, 800);
        capture.Start();
        try
        {
            for (int generation = 0; generation < 3; generation++)
            {
                int count = 0, timeouts = 0;
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed < TimeSpan.FromSeconds(2))
                {
                    if (capture.TryGetNextFrame(out var frame)) { frame!.Dispose(); count++; }
                    else if (!capture.WaitForFrame()) timeouts++;
                }
                Assert.InRange(count, 40, 150);
                Assert.InRange(timeouts, 0, 1);
                if (generation < 2) capture.Restart();
            }
        }
        finally { capture.Dispose(); }
        await capture.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [DllImport("combase.dll")] private static extern int RoInitialize(uint type);

    private sealed class SyntheticWindow : IWin32WindowOwner, IDisposable
    {
        internal readonly TaskCompletionSource<nint> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread _thread;
        private nint _window;
        private uint _step;
        internal SyntheticWindow()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "synthetic-wgc-scene" };
            _thread.SetApartmentState(ApartmentState.STA); _thread.Start();
        }
        private void Run()
        {
            try
            {
                Native.CoInitializeEx(0, Native.COINIT_APARTMENTTHREADED);
                _window = Win32Window.Create("Nexus.Test.Capture.Scene", "Synthetic capture ownership test",
                    Native.WS_POPUP, 0, 50, 50, 800, 800, this);
                Native.ShowWindow(_window, Native.SW_SHOWNOACTIVATE);
                Native.SetTimer(_window, 1, 16, 0); Ready.TrySetResult(_window);
                while (Native.GetMessageW(out var message, 0, 0, 0))
                { Native.TranslateMessage(ref message); Native.DispatchMessageW(ref message); }
            }
            catch (Exception error) { Ready.TrySetException(error); }
        }
        public nint? HandleMessage(nint window, uint message, nint wParam, nint lParam)
        {
            if (message == Native.WM_TIMER)
            {
                var dc = GetDC(window); var brush = CreateSolidBrush((_step++ % 8) * 0x202020);
                Native.GetClientRect(window, out var rectangle); FillRect(dc, ref rectangle, brush);
                DeleteObject(brush); ReleaseDC(window, dc); return 0;
            }
            if (message == Native.WM_CLOSE) { Native.DestroyWindow(window); Native.PostQuitMessage(0); return 0; }
            return null;
        }
        public void Dispose() { Native.PostMessageW(_window, Native.WM_CLOSE, 0, 0); _thread.Join(5000); }
        [DllImport("user32.dll")] private static extern nint GetDC(nint window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
        [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref Native.RECT rect, nint brush);
        [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint colour);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    }
}
