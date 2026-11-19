using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Nexus.Overlay.Media;

internal sealed class IngestPacket(byte[] payload, byte flags, Action<byte[]>? release, long queuedAt) : IDisposable
{
    private Action<byte[]>? _release = release;
    internal byte[] Payload { get; } = payload;
    internal byte Flags { get; } = flags;
    internal long QueuedAt { get; } = queuedAt;
    internal bool IsControl => (Flags & FrameFraming.FlagControl) != 0;
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke(Payload);
}

/// <summary>Raw is latest-pending; encoded packets preserve order or fault.</summary>
internal sealed class IngestPacketQueue(bool raw, TimeProvider clock)
{
    internal const long MaximumEncodedQueuedBytes = 32 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Queue<IngestPacket> _pending = new();
    private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private long _bytes;
    private bool _completed;
    internal int Count { get { lock (_gate) return _pending.Count; } }
    internal long QueuedBytes { get { lock (_gate) return _bytes; } }

    internal bool Offer(IngestPacket packet)
    {
        IngestPacket? replaced = null;
        lock (_gate)
        {
            if (_completed || packet.Payload.Length > RawFrameBufferPool.MaximumPayloadBytes) return false;
            if (raw && _pending.Count != 0)
            {
                if (packet.IsControl) replaced = packet;
                else { replaced = _pending.Dequeue(); _bytes -= replaced.Payload.Length; }
            }
            if (!ReferenceEquals(replaced, packet))
            {
                if (!raw && (_pending.Count >= 600 || _bytes + packet.Payload.Length > MaximumEncodedQueuedBytes)) return false;
                _pending.Enqueue(packet);
                _bytes += packet.Payload.Length;
                _wake.Writer.TryWrite(0);
            }
        }
        replaced?.Dispose();
        return true;
    }

    internal async ValueTask<IngestPacket?> TakeAsync(CancellationToken token)
    {
        while (true)
        {
            IngestPacket? packet = null;
            lock (_gate)
            {
                if (_pending.Count != 0) { packet = _pending.Dequeue(); _bytes -= packet.Payload.Length; }
                else if (_completed) return null;
            }
            if (packet is not null)
            {
                if (!raw || packet.IsControl || clock.GetElapsedTime(packet.QueuedAt) <= TimeSpan.FromMilliseconds(250)) return packet;
                packet.Dispose();
                continue;
            }
            try { await _wake.Reader.ReadAsync(token).ConfigureAwait(false); }
            catch (ChannelClosedException) { return null; }
        }
    }

    internal void Complete()
    {
        IngestPacket[] retired;
        lock (_gate)
        {
            if (_completed) return;
            _completed = true;
            retired = _pending.ToArray();
            _pending.Clear();
            _bytes = 0;
            _wake.Writer.TryComplete();
        }
        foreach (var packet in retired) packet.Dispose();
    }
}
