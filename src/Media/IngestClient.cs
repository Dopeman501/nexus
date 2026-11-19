using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Overlay.Media;

/// <summary>One framed HTTP stream with codec-specific bounded ownership.</summary>
internal sealed class IngestClient : IDisposable
{
    private static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(5);
    private readonly IngestPacketQueue _queue;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _abort = new();
    private readonly TimeProvider _clock;
    private readonly string _sessionId;
    private readonly object _faultGate = new();
    private readonly TaskCompletionSource _serializationSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _completion;
    private int _serializationState;
    private int _faulted;
    private int _disposed;
    private int _stopped;
    private bool _notified;
    private Action? _onFault;
    private long _lastOffer;
    internal Task Completion => _completion;

    public Action? Faulted
    {
        get { lock (_faultGate) return _onFault; }
        set { lock (_faultGate) _onFault = value; NotifyFault(); }
    }

    internal IngestClient(string serviceOrigin, string sessionId, string token, string? writerNonce = null,
        HttpMessageHandler? handler = null, bool rawFrames = false, TimeProvider? clock = null)
    {
        _sessionId = sessionId;
        _clock = clock ?? TimeProvider.System;
        _queue = new(rawFrames, _clock);
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"{serviceOrigin}/panel/streams/{Uri.EscapeDataString(sessionId)}/ingest") { Content = new FrameStreamContent(this) };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        if (!string.IsNullOrEmpty(writerNonce)) request.Headers.TryAddWithoutValidation("X-Nexus-Frame-Writer", writerNonce);
        _lastOffer = _clock.GetTimestamp();
        _completion = Task.WhenAll(RunAsync(request), KeepaliveAsync(), _serializationSettled.Task);
        _ = _completion.ContinueWith(_ => _abort.Dispose(), TaskScheduler.Default);
    }

    /// <summary>Transfers ownership, including rejection and replacement.</summary>
    public void Send(byte[] payload, bool idr, Action<byte[]>? release = null)
    {
        Volatile.Write(ref _lastOffer, _clock.GetTimestamp());
        var packet = new IngestPacket(payload, idr ? FrameFraming.FlagIdr : (byte)0, release, _clock.GetTimestamp());
        if (_queue.Offer(packet)) return;
        packet.Dispose();
        Fault();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
    }

    private void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _queue.Complete();
        try { _abort.Cancel(); }
        finally { _http.Dispose(); }
    }

    private void Fault()
    {
        if (Interlocked.Exchange(ref _faulted, 1) != 0) return;
        Stop();
        NotifyFault();
    }

    private void NotifyFault()
    {
        Action? notify;
        lock (_faultGate)
        {
            if (_notified || _faulted == 0 || _disposed != 0 || _onFault is null) return;
            _notified = true;
            notify = _onFault;
        }
        try { notify(); }
        catch (Exception error) { Log.Error($"ingest {_sessionId}: fault notification failed: {error.Message}"); }
    }

    private async Task RunAsync(HttpRequestMessage request)
    {
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _abort.Token).ConfigureAwait(false);
            Log.Info($"ingest {_sessionId}: connection ended {(int)response.StatusCode}");
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { }
        catch (Exception error) { Log.Warn($"ingest {_sessionId}: {error.GetType().Name}: {error.Message}"); }
        finally
        {
            Fault();
            if (Interlocked.CompareExchange(ref _serializationState, -1, 0) == 0) _serializationSettled.TrySetResult();
            request.Dispose();
        }
    }

    private async Task KeepaliveAsync()
    {
        try
        {
            while (!_abort.IsCancellationRequested)
            {
                await Task.Delay(KeepaliveInterval, _clock, _abort.Token).ConfigureAwait(false);
                if (_clock.GetElapsedTime(Volatile.Read(ref _lastOffer)) < KeepaliveInterval) continue;
                if (!_queue.Offer(new IngestPacket([], FrameFraming.FlagControl, null, _clock.GetTimestamp()))) Fault();
            }
        }
        catch (OperationCanceledException) { }
    }

    private sealed class FrameStreamContent(IngestClient owner) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream output, TransportContext? context)
        {
            if (Interlocked.CompareExchange(ref owner._serializationState, 1, 0) != 0)
                throw new OperationCanceledException("Ingest stream already retired.");
            try
            {
                var header = new byte[FrameFraming.HeaderSize];
                while (await owner._queue.TakeAsync(owner._abort.Token).ConfigureAwait(false) is { } packet)
                    await IngestFrameWriter.WriteAsync(output, packet, header, owner._clock, owner._abort.Token, owner.Fault).ConfigureAwait(false);
            }
            finally { owner._serializationSettled.TrySetResult(); }
        }

        protected override bool TryComputeLength(out long length) { length = -1; return false; }
    }
}
