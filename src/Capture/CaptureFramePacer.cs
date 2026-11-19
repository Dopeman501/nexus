namespace Nexus.Overlay.Capture;

/// <summary>One pump's assignment budget. Allows the existing one-frame clock slack
/// and retires banked budget after a capture gap instead of bursting downstream.</summary>
internal sealed class CaptureFramePacer(int targetFps, long frequency)
{
    private long _origin;
    private long _submitted;

    internal bool ShouldSubmit(long now)
    {
        if (_origin == 0) _origin = now;
        var allowed = 1 + (now - _origin) * targetFps / frequency;
        if (allowed - _submitted > 3)
        {
            _origin = now;
            _submitted = 0;
        }
        else if (_submitted > allowed) return false;
        _submitted++;
        return true;
    }
}
