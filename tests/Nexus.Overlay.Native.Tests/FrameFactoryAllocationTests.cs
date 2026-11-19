using Nexus.Overlay.Capture;

namespace Nexus.Overlay.Tests.NativeOwners;

[Collection("Native runtime diagnostic lease")]
public sealed class FrameFactoryAllocationTests
{
    [Fact]
    public void Memory_report_attributes_bounded_actual_frame_factory_allocations()
    {
        var authority = Guid.NewGuid().ToString("N");
        EfficiencyDiagnosticOptions.Apply(authority, 1, "11111111111111111111111111111111", 30_000);
        try
        {
            using var native = new FakeCom();
            native.Query = _ => 0; // no closable interface; release remains entirely fake
            for (var i = 0; i < 200; i++)
            {
                native.References++; // each factory input transfers one caller-owned reference
                CapturedFrame.Create(native.Pointer, 0, 123).Dispose();
            }
            var line = EfficiencyMemorySampler.ReadCurrentProcess().Format(1, 0);
            Assert.Contains("frameFactoryAllocationAttempts=128", line);
            Assert.Contains("frameFactoryAllocationSamples=128", line);
            Assert.Contains("frameFactoryAllocationUnavailable=0", line);
            Assert.Contains("frameFactoryAllocationSampleLimit=128", line);
            Assert.Contains("frameFactoryAllocationKnown=1", line);
            Assert.Equal(1, native.References);
        }
        finally { EfficiencyDiagnosticOptions.Apply(authority, 2, null, 0); }
    }

    [Fact]
    public void Actual_factory_sampling_measures_only_the_tracked_wrapper_allocation()
    {
        InRun(() =>
        {
            using var native = Frame();
            var owner = Assert.IsType<CapturedFrameCounter>(CapturedFrameDiagnostics.ActiveOwner);
            var captured = CapturedFrame.Create(native.Pointer, 0, 123);
            var sample = owner.Snapshot;
            Assert.Equal(1, sample.FactoryAllocationSamples);
            Assert.Equal(48, sample.FactoryAllocationBytes); // .NET10 Windows x64: 40-byte base + pinned epoch owner
            Assert.Equal(0, sample.FactoryAllocationUnavailable);
            captured.Dispose(); captured.Dispose();
            Assert.Equal(1, owner.Snapshot.Created);
            Assert.Equal(1, owner.Snapshot.Closed);
            Assert.Equal(0, native.References);
        });
    }

    [Fact]
    public void Sampling_stops_after_128_attempts_without_reading_for_later_frames()
    {
        InRun(() =>
        {
            using var native = Frame();
            var reads = 0;
            long Read() => ++reads * 48L;
            for (var i = 0; i < 1_000; i++)
            {
                native.References++;
                CapturedFrame.Create(native.Pointer, 0, i, Read).Dispose();
            }
            var s = CapturedFrameDiagnostics.Snapshot;
            Assert.Equal(256, reads);
            Assert.Equal(128, s.FactoryAllocationAttempts);
            Assert.Equal(128, s.FactoryAllocationSamples);
            Assert.Equal(6_144, s.FactoryAllocationBytes);
            Assert.Equal(1_000, s.Created);
            Assert.Equal(s.Created, s.Closed);
            Assert.Equal(1, native.References);
        });
    }

    [Theory]
    [InlineData(0)] // first read throws
    [InlineData(1)] // terminal read throws
    [InlineData(2)] // negative reading
    [InlineData(3)] // a descending thread allocation counter
    public void Unavailable_allocation_reads_do_not_change_frame_ownership(int mode)
    {
        InRun(() =>
        {
            using var native = Frame();
            var reads = 0;
            long Read()
            {
                var read = reads++;
                if ((mode == 0 && read == 0) || (mode == 1 && read == 1)) throw new InvalidOperationException("private text");
                return mode == 2 ? -1 : mode == 3 ? 100 - read : 100 + read;
            }
            var captured = CapturedFrame.Create(native.Pointer, 0, 123, Read);
            Assert.Equal(123, captured.Timestamp);
            captured.Dispose(); captured.Dispose();
            var s = CapturedFrameDiagnostics.Snapshot;
            Assert.Equal(1, s.FactoryAllocationAttempts);
            Assert.Equal(0, s.FactoryAllocationSamples);
            Assert.Equal(0, s.FactoryAllocationBytes);
            Assert.Equal(1, s.FactoryAllocationUnavailable);
            Assert.Equal(1, s.Created);
            Assert.Equal(1, s.Closed);
            Assert.Equal(0, native.References);
            var line = EfficiencyMemorySampler.ReadCurrentProcess().Format(1, 0);
            Assert.Contains("frameFactoryAllocationKnown=0", line);
            Assert.DoesNotContain("private text", line);
        });
    }

    [Fact]
    public void Disabled_diagnostics_and_empty_native_owners_never_read_allocation_counters()
    {
        var authority = Guid.NewGuid().ToString("N");
        EfficiencyDiagnosticOptions.Apply(authority, 1, null, 0);
        using var native = Frame();
        var reads = 0;
        long Read() { reads++; throw new InvalidOperationException(); }
        CapturedFrame.Create(native.Pointer, 0, 123, Read).Dispose();
        Assert.Equal(0, native.References);
        Assert.Equal(0, reads);
        InRun(() =>
        {
            CapturedFrame.Create(0, 0, 123, Read).Dispose();
            Assert.Equal(0, reads);
            Assert.Equal(default, CapturedFrameDiagnostics.Snapshot);
        });
    }

    [Fact]
    public void A_run_change_during_sampling_keeps_creation_allocation_and_close_with_the_pinned_owner()
    {
        var authority = Guid.NewGuid().ToString("N");
        EfficiencyDiagnosticOptions.Apply(authority, 1, "11111111111111111111111111111111", 30_000);
        try
        {
            using var native = Frame();
            var owner = Assert.IsType<CapturedFrameCounter>(CapturedFrameDiagnostics.ActiveOwner);
            var reads = 0;
            long Read()
            {
                if (reads++ == 0)
                {
                    EfficiencyDiagnosticOptions.Apply(authority, 2, null, 0);
                    EfficiencyDiagnosticOptions.Apply(authority, 3, "33333333333333333333333333333333", 30_000);
                    return 100;
                }
                return 148;
            }
            var frame = CapturedFrame.Create(native.Pointer, 0, 123, Read);
            var current = Assert.IsType<CapturedFrameCounter>(CapturedFrameDiagnostics.ActiveOwner);
            Assert.NotSame(owner, current);
            frame.Dispose(); frame.Dispose();
            Assert.Equal(1, owner.Snapshot.Created);
            Assert.Equal(1, owner.Snapshot.Closed);
            Assert.Equal(48, owner.Snapshot.FactoryAllocationBytes);
            Assert.Equal(default, current.Snapshot);
        }
        finally { EfficiencyDiagnosticOptions.Apply(authority, 4, null, 0); }
    }

    [Fact]
    public void Concurrent_sampling_reservations_share_the_same_fixed_budget()
    {
        var owner = new CapturedFrameCounter();
        Parallel.For(0, 10_000, _ =>
        {
            if (owner.TryReserveFactoryAllocationSample()) owner.RecordFactoryAllocation(48);
        });
        Assert.Equal(128, owner.Snapshot.FactoryAllocationAttempts);
        Assert.Equal(128, owner.Snapshot.FactoryAllocationSamples);
        Assert.Equal(6_144, owner.Snapshot.FactoryAllocationBytes);
    }

    [Fact]
    public void A_valid_zero_allocation_delta_is_known_instead_of_unavailable()
    {
        InRun(() =>
        {
            using var native = Frame();
            CapturedFrame.Create(native.Pointer, 0, 123, () => 0).Dispose();
            var s = CapturedFrameDiagnostics.Snapshot;
            Assert.Equal(1, s.FactoryAllocationSamples);
            Assert.Equal(0, s.FactoryAllocationBytes);
            Assert.Equal(0, s.FactoryAllocationUnavailable);
            Assert.Contains("frameFactoryAllocationKnown=1", EfficiencyMemorySampler.ReadCurrentProcess().Format(1, 0));
        });
    }

    [Fact]
    public void Disabled_actual_factory_preserves_existing_40_byte_wrapper_cost()
    {
        var authority = Guid.NewGuid().ToString("N");
        EfficiencyDiagnosticOptions.Apply(authority, 1, null, 0);
        using var native = Frame();
        for (var i = 0; i < 100; i++)
        {
            native.References++;
            CapturedFrame.Create(native.Pointer, 0, 123).Dispose();
        }
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            native.References++;
            CapturedFrame.Create(native.Pointer, 0, 123).Dispose();
        }
        Assert.Equal(40_000, GC.GetAllocatedBytesForCurrentThread() - start);
        Assert.Equal(1, native.References);
    }

    [Fact]
    public void Memory_sampler_controller_admits_once_per_run_and_retires_without_restarting()
    {
        var authority = Guid.NewGuid().ToString("N");
        EfficiencyDiagnosticOptions.Apply(authority, 1, null, 0);
        var lines = new List<string>();
        using var controller = new EfficiencyMemorySamplerController(lines.Add);
        try
        {
            controller.Update(); controller.Update();
            Assert.Empty(lines);
            EfficiencyDiagnosticOptions.Apply(authority, 2, "22222222222222222222222222222222", 30_000);
            controller.Update(); controller.Update();
            Assert.Single(lines);
            EfficiencyDiagnosticOptions.Apply(authority, 3, null, 0);
            controller.Update();
            EfficiencyDiagnosticOptions.Apply(authority, 4, "44444444444444444444444444444444", 30_000);
            controller.Update();
            Assert.Equal(2, lines.Count);
            controller.Dispose(); controller.Dispose();
            controller.Update();
            Assert.Equal(2, lines.Count);
        }
        finally { EfficiencyDiagnosticOptions.Apply(authority, 5, null, 0); }
    }

    private static FakeCom Frame()
    {
        var frame = new FakeCom(); frame.Query = _ => 0;
        return frame;
    }

    private static void InRun(Action body)
    {
        var authority = Guid.NewGuid().ToString("N");
        EfficiencyDiagnosticOptions.Apply(authority, 1, "11111111111111111111111111111111", 30_000);
        try { body(); }
        finally { EfficiencyDiagnosticOptions.Apply(authority, 2, null, 0); }
    }
}
