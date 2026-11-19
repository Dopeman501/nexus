using System;
using System.Diagnostics;
using System.Threading;

namespace Nexus.Overlay.Capture;

/// <summary>A latched wake; late retired callbacks never touch a disposed handle.</summary>
internal sealed class CaptureWakeSignal(CaptureDiagnostics? diagnostics = null, Func<CaptureDiagnostics?>? diagnosticsProvider = null) : IDisposable
{
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private bool _retired;

    internal void Signal()
    {
        Signal(frameAvailable: false);
    }

    internal void SignalFrame() => Signal(frameAvailable: true);

    private void Signal(bool frameAvailable)
    {
        lock (_gate)
        {
            if (_retired) return;
            var active = diagnosticsProvider?.Invoke() ?? diagnostics;
            if (active is not null)
            {
                if (frameAvailable) active.FrameCallback(Stopwatch.GetTimestamp());
                else active.ControlSignal();
            }
            _wake.Set();
        }
    }

    internal bool Wait(int milliseconds = 250)
    {
        var active = diagnosticsProvider?.Invoke() ?? diagnostics;
        var started = active is not null ? Stopwatch.GetTimestamp() : 0;
        var signaled = _wake.WaitOne(milliseconds);
        active?.WaitCompleted(signaled, started, Stopwatch.GetTimestamp());
        return signaled;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_retired) return;
            _retired = true;
            _wake.Dispose();
        }
    }
}
