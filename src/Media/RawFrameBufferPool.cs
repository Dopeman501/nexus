using System;
using System.Threading.Tasks;

namespace Nexus.Overlay.Media;

/// <summary>Three total arrays, including producer, pending and native/HTTP use.</summary>
internal sealed class RawFrameBufferPool : IDisposable
{
    internal const int MaximumPayloadBytes = 16 * 1024 * 1024;
    internal const int Capacity = 3;
    private readonly object _gate = new();
    private readonly byte[]?[] _buffers = new byte[Capacity][];
    private readonly bool[] _rented = new bool[Capacity];
    private readonly int _frameBytes;
    private readonly TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;
    internal Task Completion => _settled.Task;
    internal int AllocatedCount { get { lock (_gate) return Array.FindAll(_buffers, item => item is not null).Length; } }

    internal RawFrameBufferPool(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        _frameBytes = checked(width * height * 4);
        if (_frameBytes > MaximumPayloadBytes) throw new ArgumentOutOfRangeException(nameof(width));
    }

    internal byte[]? TryRent()
    {
        lock (_gate)
        {
            if (_disposed) return null;
            for (var i = 0; i < Capacity; i++)
            {
                if (_rented[i]) continue;
                _rented[i] = true;
                return _buffers[i] ??= new byte[_frameBytes];
            }
            return null;
        }
    }

    internal void Return(byte[] frame)
    {
        lock (_gate)
        {
            for (var i = 0; i < Capacity; i++)
            {
                if (!ReferenceEquals(_buffers[i], frame) || !_rented[i]) continue;
                _rented[i] = false;
                if (_disposed) _buffers[i] = null;
                CompleteIfRetired();
                return;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            for (var i = 0; i < Capacity; i++) if (!_rented[i]) _buffers[i] = null;
            CompleteIfRetired();
        }
    }

    private void CompleteIfRetired()
    {
        if (_disposed && Array.IndexOf(_rented, true) < 0) _settled.TrySetResult();
    }
}
