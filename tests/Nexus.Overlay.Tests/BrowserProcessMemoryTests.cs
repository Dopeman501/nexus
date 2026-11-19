using System;
using Xunit;

namespace Nexus.Overlay.Tests;

public sealed class BrowserProcessMemoryTests
{
    private static readonly DateTime At = DateTime.UnixEpoch.AddYears(50);
    private static readonly BrowserProcessMetrics Valid = new(At.AddMinutes(-1), "C:/Edge/msedgewebview2.exe", 100, 200);
    private static BrowserProcessGroup Group(params BrowserProcessInfo[] rows) => new(BrowserProfile.Desktop, At, rows, BrowserProcessStatus.Available);

    [Fact]
    public void Broker_ancestry_is_irrelevant_and_kinds_birth_image_commit_and_residency_remain_separate()
    {
        var sample = BrowserProcessMemory.Sample([Group(new(40012, 0), new(40808, 4))], id => Valid with { WorkingSetBytes = id == 40012 ? 200 : 400 });
        Assert.True(sample.Complete); Assert.Equal(600, sample.WorkingSetBytes);
        Assert.Equal(40808, sample.LargestPid); Assert.Equal(400, sample.LargestWorkingSetBytes);
        Assert.Equal(4, sample.Nodes[1].Kind); Assert.Equal(100, sample.Nodes[1].Metrics.PrivateBytes);
    }

    [Fact]
    public void Shared_process_is_not_double_counted_and_conflicting_identity_is_unavailable()
    {
        var group = Group(new BrowserProcessInfo(1, 0));
        Assert.Single(BrowserProcessMemory.Sample([group, group with { Profile = BrowserProfile.Stream }], _ => Valid).Nodes);
        var calls = 0;
        var conflicting = BrowserProcessMemory.Sample([group, group], _ => ++calls == 1 ? Valid : Valid with { CreatedUtc = Valid.CreatedUtc.AddSeconds(-1) });
        Assert.False(conflicting.Complete); Assert.Equal(1, conflicting.UnreadableProcesses);
    }

    [Fact]
    public void Missing_duplicate_browser_or_unavailable_group_is_not_zero_memory()
    {
        foreach (var group in new[] { Group(), Group(new(1, 0), new(2, 0)), Group(new BrowserProcessInfo(1, 0)) with { Status = BrowserProcessStatus.Unavailable } })
        {
            var sample = BrowserProcessMemory.Sample([group], _ => Valid);
            Assert.False(sample.Complete); Assert.Equal(1, sample.UnavailableProfiles);
        }
        Assert.False(BrowserProcessMemory.Sample([], _ => Valid).Complete);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void Root_must_be_readable_older_than_snapshot_and_the_browser_binary(int scenario)
    {
        BrowserProcessMetrics? bad = scenario switch
        {
            0 => null, 1 => Valid with { CreatedUtc = At.AddTicks(1) }, 2 => Valid with { CreatedUtc = DateTime.UnixEpoch },
            3 => Valid with { PrivateBytes = -1 }, 4 => Valid with { WorkingSetBytes = -1 }, _ => Valid with { Image = "C:/other.exe" }
        };
        var sample = BrowserProcessMemory.Sample([Group(new BrowserProcessInfo(1, 0))], _ => bad);
        Assert.False(sample.Complete); Assert.Equal(1, sample.UnavailableProfiles);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Child_pid_reuse_or_different_image_is_excluded_and_reported(bool reused)
    {
        var bad = reused ? Valid with { CreatedUtc = At.AddTicks(1) } : Valid with { Image = "C:/other/msedgewebview2.exe" };
        var sample = BrowserProcessMemory.Sample([Group(new(1, 0), new(2, 1))], id => id == 1 ? Valid : bad);
        Assert.False(sample.Complete); Assert.Single(sample.Nodes); Assert.Equal(1, sample.UnreadableProcesses);
    }
}
