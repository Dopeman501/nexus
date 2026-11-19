using System;

namespace Nexus.Overlay.Win32;

/// <summary>An icon retained for the lifetime of its process-registered window class.</summary>
internal sealed class WindowClassIcon
{
    private readonly object _gate = new();
    private readonly Func<IntPtr> _load;
    private IntPtr _icon;

    internal WindowClassIcon(Func<IntPtr> load)
    {
        ArgumentNullException.ThrowIfNull(load);
        _load = load;
    }

    internal IntPtr Get()
    {
        lock (_gate)
        {
            // An absent/failed load may be retried. A successful icon must
            // outlive every window because the registered class retains it.
            if (_icon == IntPtr.Zero) _icon = _load();
            return _icon;
        }
    }
}
