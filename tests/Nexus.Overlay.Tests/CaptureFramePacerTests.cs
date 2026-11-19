using Nexus.Overlay.Capture;
using Xunit;

namespace Nexus.Overlay.Tests;

public sealed class CaptureFramePacerTests
{
    [Theory]
    [InlineData(25)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(1)]
    [InlineData(240)]
    public void Extracted_policy_matches_installed37_for_jitter_stalls_and_bursts(int fps)
    {
        var pacer = new CaptureFramePacer(fps, 10_000_000);
        long origin = 0, submitted = 0, now = 2_000_000_000_000;
        int[] gaps = [333_333, 333_334, 166_666, 1, 1_050_000, 33_333, 1_770_000, 400_000, 0];
        for (int index = 0; index < 10_000; index++)
        {
            now += gaps[index % gaps.Length];
            bool expected = Installed37(now, fps, ref origin, ref submitted);
            Assert.Equal(expected, pacer.ShouldSubmit(now));
        }
    }

    [Theory]
    [InlineData(25, 30)]
    [InlineData(25, 60)]
    [InlineData(30, 30)]
    [InlineData(30, 60)]
    [InlineData(60, 60)]
    public void Smooth_input_preserves_assigned_rate_with_bounded_slack(int fps, int inputFps)
    {
        var pacer = new CaptureFramePacer(fps, 10_000_000);
        int accepted = 0;
        for (int frame = 0; frame < inputFps * 60; frame++)
            if (pacer.ShouldSubmit(2_000_000_000_000 + (long)frame * 10_000_000 / inputFps)) accepted++;
        Assert.InRange(accepted, fps * 60 - 1, fps * 60 + 1);
    }

    [Fact]
    public void Zero_clock_sentinel_remains_identical_to_installed37()
    {
        var pacer = new CaptureFramePacer(25, 10_000_000);
        long origin = 0, submitted = 0;
        long[] times = [0, 0, 1, 1, 400_000, 1_000_000_000];
        foreach (long now in times)
            Assert.Equal(Installed37(now, 25, ref origin, ref submitted), pacer.ShouldSubmit(now));
    }

    [Fact]
    public void Static_gap_does_not_bank_a_catchup_burst()
    {
        var pacer = new CaptureFramePacer(25, 10_000_000);
        Assert.True(pacer.ShouldSubmit(2_000_000_000_000));
        Assert.True(pacer.ShouldSubmit(2_000_100_000_000));
        Assert.True(pacer.ShouldSubmit(2_000_100_000_001)); // existing one-frame slack
        Assert.False(pacer.ShouldSubmit(2_000_100_000_002));
    }

    // Exact policy from baseline37 StreamPanelHost.PumpFrames, with ticks supplied.
    private static bool Installed37(long now, int fps, ref long origin, ref long submitted)
    {
        if (origin == 0) origin = now;
        var allowed = 1 + (now - origin) * fps / 10_000_000;
        if (allowed - submitted > 3) { origin = now; submitted = 0; }
        else if (submitted > allowed) return false;
        submitted++;
        return true;
    }
}
