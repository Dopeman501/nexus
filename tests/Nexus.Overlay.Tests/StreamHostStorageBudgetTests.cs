using Nexus.Overlay.Capture;
using Xunit;

namespace Nexus.Overlay.Tests;

public sealed class StreamHostStorageBudgetTests
{
    [Theory]
    [InlineData("rawBgra", 1680, 756, 30481920L)]
    [InlineData("h264", 1024, 600, 87384064L)]
    [InlineData("h264", 800, 800, 88228864L)]
    [InlineData("h264", 3840, 2160, 340824064L)]
    public void Supported_inventory_and_encoded_headroom_have_checked_explicit_storage_costs(string codec, int width, int height, long expected)
    {
        var a = StreamHostRetirementTests.Assignment("one", "panel");
        a.Codec = codec; a.CssWidth = width; a.CssHeight = height;
        Assert.True(StreamHostStorageBudget.TryMeasure(a, out var bytes, out _));
        Assert.Equal(expected, bytes);
    }

    [Theory]
    [InlineData(0, 100, 1, "rawBgra")]
    [InlineData(100, -1, 1, "rawBgra")]
    [InlineData(100, 100, -1, "rawBgra")]
    [InlineData(100, 100, double.NaN, "rawBgra")]
    [InlineData(100, 100, double.PositiveInfinity, "rawBgra")]
    [InlineData(int.MaxValue, int.MaxValue, double.MaxValue, "h264")]
    [InlineData(4096, 4096, 1, "h264")]
    [InlineData(4096, 4096, 1, "rawBgra")]
    [InlineData(100, 100, 1, "unknown")]
    public void Invalid_or_excessive_geometry_is_rejected_before_native_construction(int width, int height, double dpr, string codec)
    {
        var a = StreamHostRetirementTests.Assignment("one", "panel");
        a.CssWidth = width; a.CssHeight = height; a.Dpr = dpr; a.Codec = codec;
        Assert.False(StreamHostStorageBudget.TryMeasure(a, out _, out _));
    }

    [Fact]
    public void Shared_mapping_is_budgeted_and_cannot_claim_an_undersized_or_unbounded_section()
    {
        var a = StreamHostRetirementTests.Assignment("one", "panel");
        a.FrameSection = "synthetic-section";
        a.FrameSectionBytes = 64 * 64 * 4;
        Assert.True(StreamHostStorageBudget.TryMeasure(a, out var bytes, out _));
        Assert.Equal(64 * 64 * 4 * 7 + 64, bytes);
        a.FrameSectionBytes--;
        Assert.False(StreamHostStorageBudget.TryMeasure(a, out _, out _));
        a.FrameSectionBytes = int.MaxValue;
        Assert.False(StreamHostStorageBudget.TryMeasure(a, out _, out _));
    }
}
