using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Overlay.Media;

internal static class IngestFrameWriter
{
    internal static readonly TimeSpan WriteDeadline = TimeSpan.FromSeconds(5);

    internal static async Task WriteAsync(Stream output, IngestPacket packet, byte[] header,
        TimeProvider clock, CancellationToken token, Action fault)
    {
        var writing = WritePacketAsync(output, packet, header, token);
        try
        {
            try { await writing.WaitAsync(WriteDeadline, clock, token).ConfigureAwait(false); }
            catch
            {
                // A timeout is not I/O completion: do not recycle this packet
                // while a cancellation-ignoring stream still references it.
                try { fault(); }
                finally { await writing.ConfigureAwait(false); }
                throw;
            }
        }
        finally { packet.Dispose(); }
    }

    private static async Task WritePacketAsync(Stream output, IngestPacket packet, byte[] header, CancellationToken token)
    {
        FrameFraming.WriteHeader(header, packet.Payload.Length, packet.Flags);
        await output.WriteAsync(header, token).ConfigureAwait(false);
        if (packet.Payload.Length != 0) await output.WriteAsync(packet.Payload, token).ConfigureAwait(false);
        await output.FlushAsync(token).ConfigureAwait(false);
    }
}
