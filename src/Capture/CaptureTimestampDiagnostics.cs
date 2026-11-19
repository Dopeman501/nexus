using System;
using System.Globalization;
using System.Text;

namespace Nexus.Overlay.Capture;

internal enum CaptureTimestampRejection { GetterFailed = 1, NonPositive = 2, FutureAge = 3 }

internal readonly record struct CaptureTimestampAnomaly(CaptureTimestampRejection Reason,
    int HResult, long QpcTicks, long FrameTime100ns, bool AgeKnown, double SignedAgeMilliseconds);

internal readonly record struct CaptureTimestampSnapshot(long GetterFailures, long NonPositive,
    long FutureAge, long Accepted, long QpcFrequency, long Overflow, CaptureTimestampAnomaly[] Anomalies)
{
    public override string ToString()
    {
        var text = new StringBuilder(512);
        text.Append(CultureInfo.InvariantCulture,
            $"timestampGetterFailures={GetterFailures} timestampNonPositive={NonPositive} timestampFutureAge={FutureAge} timestampAccepted={Accepted} timestampQpcFrequency={QpcFrequency} timestampAnomalies={Anomalies.Length} timestampOverflow={Overflow}");
        for (var i = 0; i < Anomalies.Length; i++)
        {
            var sample = Anomalies[i];
            text.Append(CultureInfo.InvariantCulture,
                $" timestampReject{i}[reason={(int)sample.Reason} hresult={sample.HResult} qpcTicks={sample.QpcTicks} frame100ns={sample.FrameTime100ns} ageKnown={(sample.AgeKnown ? 1 : 0)} signedAgeMs={sample.SignedAgeMilliseconds:F3}]");
        }
        return text.ToString();
    }
}

/// <summary>First eight rejected numeric operands per diagnostic owner; synchronized by CaptureDiagnostics.</summary>
internal sealed class CaptureTimestampDiagnostics(long frequency)
{
    internal const int Capacity = 8;
    private readonly CaptureTimestampAnomaly[] _anomalies = new CaptureTimestampAnomaly[Capacity];
    private int _count;
    private long _getterFailures, _nonPositive, _futureAge, _accepted, _overflow;

    internal bool Observe(long now, long timestamp, int hresult, out double age)
    {
        // Preserve the existing conversion and validation, without normalizing a clock or changing playback.
        age = now * (1000d / frequency) - timestamp / 10_000d;
        CaptureTimestampRejection reason;
        if (hresult < 0) { _getterFailures++; reason = CaptureTimestampRejection.GetterFailed; }
        else if (timestamp <= 0) { _nonPositive++; reason = CaptureTimestampRejection.NonPositive; }
        else if (age < 0) { _futureAge++; reason = CaptureTimestampRejection.FutureAge; }
        else { _accepted++; return true; }

        if (_count == Capacity) { _overflow++; return false; }
        var ageKnown = hresult >= 0 && timestamp > 0;
        _anomalies[_count++] = new(reason, hresult, now, timestamp, ageKnown, ageKnown ? age : 0);
        return false;
    }

    internal CaptureTimestampSnapshot Snapshot() => new(_getterFailures, _nonPositive, _futureAge,
        _accepted, frequency, _overflow, _anomalies.AsSpan(0, _count).ToArray());
}
