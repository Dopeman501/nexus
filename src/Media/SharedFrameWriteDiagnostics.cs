using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Nexus.Overlay.Media;

internal readonly record struct SharedFrameWriteSnapshot(long Writes, long PublishedSequence,
    long ObservedConsumedSequence, long PendingAtObservation, long Invalid);

/// <summary>Scalar metadata only; observed pending frames are not a physical-frame loss measurement.</summary>
internal sealed class SharedFrameWriteDiagnostics
{
    private readonly string _tag;
    private readonly long _revision, _frequency, _created;
    private long _writes, _published, _consumed, _pending, _invalid, _lastReport;
    private int _reports;

    internal SharedFrameWriteDiagnostics(string section, long revision, long frequency, long created)
    {
        ArgumentException.ThrowIfNullOrEmpty(section);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        _tag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(section)))[..12];
        _revision = revision; _frequency = frequency; _created = created;
    }

    internal void Observe(long published, long consumed)
    {
        if (published <= _published || (published & 1) != 0)
        {
            _invalid++; _pending = -1;
            return;
        }
        _writes++; _published = published; _consumed = consumed;
        if (consumed < 0 || (consumed & 1) != 0 || consumed > published)
        {
            _invalid++; _pending = -1;
            return;
        }
        _pending = (published - consumed) / 2;
    }

    internal SharedFrameWriteSnapshot Snapshot() => new(_writes, _published, _consumed, _pending, _invalid);

    internal string? Report(long now)
    {
        if (now < _created || _reports >= 72 || (_reports > 0 && (double)(now - _lastReport) / _frequency < 30)) return null;
        _reports++; _lastReport = now;
        return string.Create(CultureInfo.InvariantCulture,
            $"[efficiency-shared] v=1 role=writer section={_tag} epoch={_revision} sample={_reports} elapsedMs={(long)((double)(now - _created) * 1000 / _frequency)} writes={_writes} publishedSequence={_published} observedConsumedSequence={_consumed} pendingAtObservation={_pending} invalid={_invalid} snapshotIsNotAtomic=1");
    }
}
