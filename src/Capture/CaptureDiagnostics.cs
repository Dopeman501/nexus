using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Nexus.Overlay.Capture;

internal readonly record struct CaptureDiagnosticSnapshot(long Callbacks, long ControlSignals,
    long SignaledWaits, long TimedOutWaits, long Returned, long Empty, long PollFailures,
    long BudgetDisposals, long Submitted, long SubmitFailures, long InvalidFrameTimestamps,
    double LastFrameAgeMs, double LastFrameIntervalMs, double LastCallbackResumeMs,
    long CaptureStarts, long PoolEmpty, long PoolFailures, long ExtractionFailures,
    long SharedCopies, long HttpCopies, long BufferExhausted, long MapFailures,
    DiagnosticTimingSnapshot FrameAge, DiagnosticTimingSnapshot FrameInterval,
    DiagnosticTimingSnapshot CallbackInterval, DiagnosticTimingSnapshot CallbackResume,
    DiagnosticTimingSnapshot Wait, DiagnosticTimingSnapshot Poll, DiagnosticTimingSnapshot Submit,
    DiagnosticTimingSnapshot Map, DiagnosticTimingSnapshot Copy, CaptureTimestampSnapshot Timestamp);

/// <summary>Optional bounded capture timeline; a timestamp gap alone never identifies a compositor/pool cause.</summary>
internal sealed class CaptureDiagnostics
{
    private readonly object _gate = new();
    private readonly long _frequency;
    private readonly string _tag;
    private readonly DiagnosticReportGate _report;
    private readonly CaptureTimestampDiagnostics _timestamps;
    private readonly DiagnosticTimingHistogram _age = new(), _frameInterval = new(), _callbackInterval = new(), _callbackResume = new();
    private readonly DiagnosticTimingHistogram _wait = new(), _poll = new(), _submit = new(), _map = new(), _copy = new();
    private long _callbacks, _controlSignals, _signaledWaits, _timeouts, _returned, _empty, _pollFailures;
    private long _budgetDisposals, _submitted, _submitFailures, _invalidFrameTimestamps, _captureStarts;
    private long _poolEmpty, _poolFailures, _extractionFailures, _sharedCopies, _httpCopies, _bufferExhausted, _mapFailures;
    private long _lastCallback, _lastFrameTime;
    private double _lastAge, _lastInterval, _lastResume;

    internal CaptureDiagnostics(long frequency, long createdAt = 0, string sessionId = "")
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        _frequency = frequency;
        _timestamps = new(frequency);
        _report = new(frequency, createdAt);
        _tag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)))[..12];
    }

    internal void CaptureStarted()
    {
        lock (_gate) { _captureStarts++; _lastFrameTime = _lastCallback = 0; }
    }

    internal void FrameCallback(long now)
    {
        lock (_gate)
        {
            _callbacks++;
            if (_lastCallback != 0) _callbackInterval.AddElapsed(_lastCallback, now, _frequency);
            _lastCallback = now;
        }
    }

    internal void ControlSignal() { lock (_gate) _controlSignals++; }

    internal void WaitCompleted(bool signaled, long started, long completed)
    {
        lock (_gate)
        {
            _wait.AddElapsed(started, completed, _frequency);
            if (!signaled) { _timeouts++; return; }
            _signaledWaits++;
            if (_lastCallback == 0) return;
            // Latest observed callback, not a one-to-one callback/frame association.
            _lastResume = (completed - (double)_lastCallback) * 1000 / _frequency;
            _callbackResume.Add(_lastResume);
        }
    }

    internal void FrameReturned(long now, long frameTime100ns) => FrameReturned(now, frameTime100ns, 0);

    internal void FrameReturned(long now, long frameTime100ns, int timestampHResult)
    {
        lock (_gate)
        {
            _returned++;
            if (!_timestamps.Observe(now, frameTime100ns, timestampHResult, out var age))
            {
                _invalidFrameTimestamps++;
                _lastFrameTime = 0;
                return;
            }
            _lastAge = age;
            _age.Add(age);
            if (_lastFrameTime != 0)
            {
                _lastInterval = (frameTime100ns - (double)_lastFrameTime) / 10_000;
                _frameInterval.Add(_lastInterval);
            }
            _lastFrameTime = frameTime100ns;
        }
    }

    internal void PollCompleted(long started, long completed, bool returned, bool failed = false)
    {
        lock (_gate)
        {
            _poll.AddElapsed(started, completed, _frequency);
            if (failed) _pollFailures++;
            else if (!returned) _empty++;
        }
    }

    internal void PoolRead(bool empty, bool failed = false)
    {
        lock (_gate) { if (failed) _poolFailures++; else if (empty) _poolEmpty++; }
    }
    internal void ExtractionFailed() { lock (_gate) _extractionFailures++; }
    internal void BudgetDisposed() { lock (_gate) _budgetDisposals++; }
    internal void SubmitCompleted(long started, long completed, bool success)
    {
        lock (_gate)
        {
            _submit.AddElapsed(started, completed, _frequency);
            if (success) _submitted++; else _submitFailures++;
        }
    }
    internal void MapCompleted(long started, long completed, bool success)
    {
        lock (_gate) { _map.AddElapsed(started, completed, _frequency); if (!success) _mapFailures++; }
    }
    internal void CopyCompleted(long started, long completed, bool shared, bool bufferExhausted = false)
    {
        lock (_gate)
        {
            _copy.AddElapsed(started, completed, _frequency);
            if (bufferExhausted) _bufferExhausted++;
            else if (shared) _sharedCopies++;
            else _httpCopies++;
        }
    }

    internal CaptureDiagnosticSnapshot Snapshot() { lock (_gate) return SnapshotLocked(); }
    private CaptureDiagnosticSnapshot SnapshotLocked() => new(_callbacks, _controlSignals, _signaledWaits,
        _timeouts, _returned, _empty, _pollFailures, _budgetDisposals, _submitted, _submitFailures,
        _invalidFrameTimestamps, _lastAge, _lastInterval, _lastResume, _captureStarts,
        _poolEmpty, _poolFailures, _extractionFailures, _sharedCopies, _httpCopies, _bufferExhausted, _mapFailures,
        _age.Snapshot(), _frameInterval.Snapshot(), _callbackInterval.Snapshot(), _callbackResume.Snapshot(),
        _wait.Snapshot(), _poll.Snapshot(), _submit.Snapshot(), _map.Snapshot(), _copy.Snapshot(), _timestamps.Snapshot());

    internal string? Report(long now, int width, int height, int fps, bool raw, bool final = false)
    {
        CaptureDiagnosticSnapshot s;
        lock (_gate) { if (!_report.Take(now, final)) return null; s = SnapshotLocked(); }
        return string.Create(CultureInfo.InvariantCulture,
            $"[efficiency-capture] v=1 session={_tag} size={width}x{height} fps={fps} codec={(raw ? "rawBgra" : "h264")} final={final} cumulative starts={s.CaptureStarts} callbacks={s.Callbacks} controlSignals={s.ControlSignals} signaledWaits={s.SignaledWaits} timeouts={s.TimedOutWaits} returned={s.Returned} noFrame={s.Empty} pollFailures={s.PollFailures} poolEmpty={s.PoolEmpty} poolFailures={s.PoolFailures} extractionFailures={s.ExtractionFailures} budgetDisposed={s.BudgetDisposals} submitOk={s.Submitted} submitFailures={s.SubmitFailures} invalidFrameTime={s.InvalidFrameTimestamps} sharedCopies={s.SharedCopies} httpCopies={s.HttpCopies} bufferExhausted={s.BufferExhausted} mapFailures={s.MapFailures} frameAge[{s.FrameAge}] frameInterval[{s.FrameInterval}] callbackInterval[{s.CallbackInterval}] sinceLatestCallbackAtResume[{s.CallbackResume}] wait[{s.Wait}] poll[{s.Poll}] submit[{s.Submit}] map[{s.Map}] copy[{s.Copy}] poolOverwrite=unknown {s.Timestamp}");
    }
}
