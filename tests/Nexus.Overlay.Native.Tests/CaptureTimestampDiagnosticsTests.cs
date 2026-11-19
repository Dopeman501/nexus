using Nexus.Overlay.Capture;

namespace Nexus.Overlay.Tests.NativeOwners;

public sealed class CaptureTimestampDiagnosticsTests
{
    [Fact]
    public void Report_distinguishes_nonpositive_future_and_valid_timestamp_outcomes()
    {
        var metrics = new CaptureDiagnostics(1_000);
        metrics.FrameReturned(2_000, 0);
        metrics.FrameReturned(2_000, -1);
        metrics.FrameReturned(2_000, 30_000_000);
        metrics.FrameReturned(2_000, 19_500_000);
        var report = metrics.Report(30_000, 2, 2, 25, true)!;
        Assert.Contains("timestampGetterFailures=0", report);
        Assert.Contains("timestampNonPositive=2", report);
        Assert.Contains("timestampFutureAge=1", report);
        Assert.Contains("timestampAccepted=1", report);
        Assert.Contains("timestampQpcFrequency=1000", report);
        Assert.Contains("timestampAnomalies=3", report);
        Assert.Equal(3, metrics.Snapshot().InvalidFrameTimestamps);
        Assert.Equal(1, metrics.Snapshot().FrameAge.Count);
    }

    [Fact]
    public void Rejected_timestamp_operands_have_fixed_capacity_and_remain_redacted()
    {
        var metrics = new CaptureDiagnostics(1_000, sessionId: "private-session-url-token");
        for (var i = 0; i < 32; i++) metrics.FrameReturned(2_000 + i, 30_000_000 + i);
        var report = metrics.Report(30_000, 2, 2, 25, true)!;
        Assert.Contains("timestampAnomalies=8", report);
        Assert.Contains("timestampOverflow=24", report);
        Assert.Contains("timestampReject0[reason=3 hresult=0 qpcTicks=2000 frame100ns=30000000 ageKnown=1 signedAgeMs=-1000.000]", report);
        Assert.Contains("timestampReject7[", report);
        Assert.DoesNotContain("timestampReject8[", report);
        Assert.DoesNotContain("private-session-url-token", report);
        Assert.True(report.Length < 7000);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(unchecked((int)0x80004005))]
    public void Failed_getter_precedes_timestamp_validation_and_preserves_numeric_hresult(int hresult)
    {
        var metrics = new CaptureDiagnostics(1_000);
        metrics.FrameReturned(2_000, 30_000_000, hresult);
        var snapshot = metrics.Snapshot();
        Assert.Equal(1, snapshot.Timestamp.GetterFailures);
        Assert.Equal(0, snapshot.Timestamp.NonPositive);
        Assert.Equal(0, snapshot.Timestamp.FutureAge);
        Assert.Equal(1, snapshot.InvalidFrameTimestamps);
        Assert.Equal(0, snapshot.FrameAge.Count);
        var rejected = Assert.Single(snapshot.Timestamp.Anomalies);
        Assert.Equal(CaptureTimestampRejection.GetterFailed, rejected.Reason);
        Assert.Equal(hresult, rejected.HResult);
        Assert.False(rejected.AgeKnown);
        Assert.Equal(0, rejected.SignedAgeMilliseconds);
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(10_000_000)]
    [InlineData(24_000_000)]
    public void Equal_clock_boundary_is_valid_but_future_sample_does_not_seed_an_interval(long frequency)
    {
        var metrics = new CaptureDiagnostics(frequency);
        metrics.FrameReturned(2 * frequency, 20_000_000);
        metrics.FrameReturned(2 * frequency, 20_000_001);
        metrics.FrameReturned(3 * frequency, 30_000_000, 1);
        var snapshot = metrics.Snapshot();
        Assert.Equal(2, snapshot.Timestamp.Accepted);
        Assert.Equal(1, snapshot.Timestamp.FutureAge);
        Assert.Equal(0, snapshot.Timestamp.GetterFailures);
        Assert.Equal(2, snapshot.FrameAge.Count);
        Assert.Equal(0, snapshot.FrameInterval.Count);
        Assert.True(Assert.Single(snapshot.Timestamp.Anomalies).SignedAgeMilliseconds < 0);
    }

    [Fact]
    public void Timestamp_snapshot_owns_its_storage_and_reports_without_consuming_evidence()
    {
        var metrics = new CaptureDiagnostics(1_000);
        metrics.FrameReturned(2_000, -7);
        var first = metrics.Snapshot().Timestamp;
        Assert.Equal(CaptureTimestampRejection.NonPositive, first.Anomalies[0].Reason);
        first.Anomalies[0] = default;
        Assert.Equal(-7, metrics.Snapshot().Timestamp.Anomalies[0].FrameTime100ns);
        metrics.CaptureStarted();
        metrics.FrameReturned(3_000, 29_000_000);
        Assert.Equal(0, metrics.Snapshot().FrameInterval.Count);
        Assert.Contains("timestampAnomalies=1", metrics.Report(30_000, 2, 2, 25, true));
        Assert.Contains("timestampAnomalies=1", metrics.Report(60_000, 2, 2, 25, true));
    }

    [Fact]
    public void Per_frame_timestamp_accounting_allocates_no_managed_storage_after_owner_creation()
    {
        var metrics = new CaptureDiagnostics(1_000);
        for (var i = 0; i < 100; i++) metrics.FrameReturned(2_000, 30_000_000);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            metrics.FrameReturned(2_000, 19_000_000);
            metrics.FrameReturned(2_000, 30_000_000);
            metrics.FrameReturned(2_000, 0, -1);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(8, metrics.Snapshot().Timestamp.Anomalies.Length);
        Assert.Equal(20_092, metrics.Snapshot().Timestamp.Overflow);
    }
}
