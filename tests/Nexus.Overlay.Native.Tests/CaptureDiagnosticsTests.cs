using Nexus.Overlay.Capture;
using System.Diagnostics;

namespace Nexus.Overlay.Tests.NativeOwners;

public sealed class CaptureDiagnosticsTests
{
    [Theory]
    [InlineData(1_000)]
    [InlineData(10_000_000)]
    [InlineData(24_000_000)]
    public void Frame_age_and_interarrival_convert_qpc_frequency_and_wgc_100ns_units(long frequency)
    {
        var metrics = new CaptureDiagnostics(frequency);
        metrics.FrameReturned(2 * frequency, 19_500_000);
        Assert.Equal(50, metrics.Snapshot().LastFrameAgeMs, 6);
        metrics.FrameReturned(2 * frequency + frequency / 10, 20_500_000);
        Assert.Equal(100, metrics.Snapshot().LastFrameIntervalMs, 6);
        Assert.Equal(50, metrics.Snapshot().LastFrameAgeMs, 6);
        Assert.Equal(2, metrics.Snapshot().Returned);
    }

    [Fact]
    public void Missing_and_future_frame_timestamps_are_unknown_rather_than_negative_age()
    {
        var metrics = new CaptureDiagnostics(1_000);
        metrics.FrameReturned(2_000, 0);
        metrics.FrameReturned(2_000, 30_000_000);
        Assert.Equal(2, metrics.Snapshot().InvalidFrameTimestamps);
        Assert.Equal(2, metrics.Snapshot().Returned);
    }

    [Fact]
    public void Callback_and_control_signals_are_distinct_and_wait_latency_uses_latest_observed_callback()
    {
        var metrics = new CaptureDiagnostics(1_000);
        metrics.FrameCallback(1_000);
        metrics.FrameCallback(1_002);
        metrics.ControlSignal();
        metrics.WaitCompleted(true, 1_003, 1_007);
        metrics.WaitCompleted(false, 1_010, 1_260);
        var snapshot = metrics.Snapshot();
        Assert.Equal(2, snapshot.Callbacks);
        Assert.Equal(1, snapshot.ControlSignals);
        Assert.Equal(1, snapshot.SignaledWaits);
        Assert.Equal(1, snapshot.TimedOutWaits);
        Assert.Equal(5, snapshot.LastCallbackResumeMs);
    }

    [Fact]
    public void Actual_latched_wake_counts_callbacks_separately_from_coalesced_signals_and_ignores_retired_calls()
    {
        var metrics = new CaptureDiagnostics(Stopwatch.Frequency);
        using var wake = new CaptureWakeSignal(metrics);
        wake.SignalFrame(); wake.SignalFrame();
        Assert.True(wake.Wait(0));
        Assert.False(wake.Wait(0));
        wake.Signal();
        Assert.True(wake.Wait(0));
        wake.Dispose(); wake.Signal(); wake.SignalFrame();
        var snapshot = metrics.Snapshot();
        Assert.Equal(2, snapshot.Callbacks);
        Assert.Equal(1, snapshot.ControlSignals);
        Assert.Equal(2, snapshot.SignaledWaits);
        Assert.Equal(1, snapshot.TimedOutWaits);
        Assert.Equal(3, snapshot.Wait.Count);
    }

    [Fact]
    public void Poll_budget_submit_and_raw_sink_outcomes_remain_distinct()
    {
        var metrics = new CaptureDiagnostics(1_000);
        metrics.CaptureStarted();
        metrics.PoolRead(empty: true);
        metrics.PoolRead(empty: false, failed: true);
        metrics.PoolRead(empty: false);
        metrics.ExtractionFailed();
        metrics.PollCompleted(0, 2, returned: true);
        metrics.PollCompleted(2, 4, returned: false);
        metrics.PollCompleted(4, 7, returned: false, failed: true);
        metrics.BudgetDisposed();
        metrics.SubmitCompleted(10, 20, success: true);
        metrics.SubmitCompleted(20, 50, success: false);
        metrics.MapCompleted(60, 80, success: true);
        metrics.MapCompleted(80, 90, success: false);
        metrics.CopyCompleted(100, 104, shared: true);
        metrics.CopyCompleted(104, 109, shared: false);
        metrics.CopyCompleted(109, 110, shared: false, bufferExhausted: true);
        var s = metrics.Snapshot();
        Assert.Equal((1L, 1L, 1L), (s.PoolEmpty, s.PoolFailures, s.ExtractionFailures));
        Assert.Equal((1L, 1L, 1L), (s.Empty, s.PollFailures, s.BudgetDisposals));
        Assert.Equal((1L, 1L), (s.Submitted, s.SubmitFailures));
        Assert.Equal((1L, 1L, 1L, 1L), (s.SharedCopies, s.HttpCopies, s.BufferExhausted, s.MapFailures));
        Assert.Equal(20, s.Submit.MeanMilliseconds);
        Assert.Equal(15, s.Map.MeanMilliseconds);
        Assert.Equal(3, s.Copy.Count);
    }

    [Fact]
    public void Reports_are_bounded_rate_limited_redacted_and_final_is_once()
    {
        var metrics = new CaptureDiagnostics(1_000, sessionId: "private-device-and-token");
        Assert.Null(metrics.Report(29_999, 400, 400, 25, raw: true));
        var line = metrics.Report(30_000, 400, 400, 25, raw: true);
        Assert.NotNull(line);
        Assert.Contains("400x400 fps=25 codec=rawBgra", line);
        Assert.DoesNotContain("private-device-and-token", line);
        Assert.Contains("poolOverwrite=unknown", line);
        Assert.True(line.Length < 3500);
        Assert.Null(metrics.Report(30_000, 400, 400, 25, raw: true));
        Assert.Null(metrics.Report(1, 400, 400, 25, raw: true));
        Assert.NotNull(metrics.Report(60_000, 400, 400, 25, raw: false));
        Assert.NotNull(metrics.Report(60_001, 400, 400, 25, raw: true, final: true));
        Assert.Null(metrics.Report(100_000, 400, 400, 25, raw: true, final: true));
    }

    [Fact]
    public void Restart_does_not_measure_a_timestamp_interval_across_generations()
    {
        var metrics = new CaptureDiagnostics(1_000);
        metrics.CaptureStarted(); metrics.FrameCallback(2_000); metrics.FrameReturned(2_000, 19_500_000);
        metrics.CaptureStarted(); metrics.WaitCompleted(true, 2_000, 2_001);
        metrics.FrameReturned(3_000, 29_500_000);
        Assert.Equal(2, metrics.Snapshot().CaptureStarts);
        Assert.Equal(0, metrics.Snapshot().FrameInterval.Count);
        Assert.Equal(0, metrics.Snapshot().CallbackResume.Count);
    }

    [Fact]
    public void Impossible_clock_order_is_counted_without_negative_histogram_samples()
    {
        var metrics = new CaptureDiagnostics(1_000);
        metrics.FrameCallback(2_000);
        metrics.WaitCompleted(true, 1_000, 1_001);
        metrics.FrameReturned(3_000, 29_000_000);
        metrics.FrameReturned(3_001, 28_000_000);
        var snapshot = metrics.Snapshot();
        Assert.Equal(1, snapshot.CallbackResume.InvalidSamples);
        Assert.Equal(1, snapshot.FrameInterval.InvalidSamples);
        Assert.Equal(0, snapshot.CallbackResume.Count);
    }

    [Fact]
    public void Timing_buckets_have_fixed_boundaries_and_snapshots_do_not_share_storage()
    {
        var histogram = new DiagnosticTimingHistogram();
        foreach (var value in new double[] { 0, 1, 4, 8, 16, 33, 66, 100, 101 }) histogram.Add(value);
        histogram.Add(-1); histogram.Add(double.NaN); histogram.Add(double.PositiveInfinity);
        histogram.AddElapsed(10, 9, 1_000); histogram.AddElapsed(0, 1, 0);
        var snapshot = histogram.Snapshot();
        Assert.Equal(new long[] { 2, 1, 1, 1, 1, 1, 1, 1 }, snapshot.Buckets);
        Assert.Equal(9, snapshot.Count); Assert.Equal(5, snapshot.InvalidSamples);
        Assert.Equal(101, snapshot.MaximumMilliseconds);
        snapshot.Buckets[0] = -99;
        Assert.Equal(2, histogram.Snapshot().Buckets[0]);
        Assert.Contains("buckets=2/1/1/1/1/1/1/1", histogram.Snapshot().ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureDiagnostics(0));
    }
}
