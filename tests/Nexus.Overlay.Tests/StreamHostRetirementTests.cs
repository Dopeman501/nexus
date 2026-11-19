using Nexus.Overlay;
using Nexus.Overlay.Capture;
using Xunit;

namespace Nexus.Overlay.Tests;

public sealed class StreamHostRetirementTests : IDisposable
{
    public StreamHostRetirementTests() => StreamPanelHost.Created.Clear();
    public void Dispose() => StreamPanelHost.Created.Clear();

    [Fact]
    public void Reminted_session_cannot_replace_a_still_retiring_stable_panel()
    {
        var manager = new StreamHostManager("http://synthetic.invalid");
        manager.Reconcile([Assignment("one", "panel")], "test-token");
        var old = Assert.Single(StreamPanelHost.Created);
        manager.Reconcile([Assignment("two", "panel")], "test-token");
        manager.Reconcile([Assignment("three", "panel")], "test-token");
        Assert.True(old.Disposed);
        Assert.Single(StreamPanelHost.Created);
        Assert.Equal(1, manager.Count); // Retired owners keep the overlay alive too.
        old.Finish.TrySetResult();
        manager.Reconcile([Assignment("three", "panel")], "test-token");
        Assert.Equal(2, StreamPanelHost.Created.Count);
        Assert.Equal("three", StreamPanelHost.Created[1].SessionId);
    }

    [Fact]
    public void Global_capacity_is_reserved_before_construction_and_includes_retired_panels()
    {
        var manager = new StreamHostManager("http://synthetic.invalid");
        var desired = Enumerable.Range(0, 33).Select(i => Assignment("session" + i, "panel" + i)).ToArray();
        manager.Reconcile(desired, "test-token");
        Assert.Equal(32, StreamPanelHost.Created.Count);
        manager.CloseAll();
        manager.Reconcile([Assignment("new", "other-panel")], "test-token");
        Assert.Equal(32, StreamPanelHost.Created.Count);
        foreach (var host in StreamPanelHost.Created) host.Finish.TrySetResult();
        manager.Reconcile([Assignment("new", "other-panel")], "test-token");
        Assert.Equal(33, StreamPanelHost.Created.Count);
    }

    [Fact]
    public void Frame_byte_budget_blocks_construction_even_when_owner_slots_are_available()
    {
        var manager = new StreamHostManager("http://synthetic.invalid");
        var desired = Enumerable.Range(0, 6).Select(i =>
        {
            var a = Assignment("session" + i, "panel" + i);
            a.CssWidth = a.CssHeight = 2048;
            return a;
        }).ToArray();
        manager.Reconcile(desired, "test-token");
        Assert.Equal(5, StreamPanelHost.Created.Count); // 5 × 96 MiB <= 512 MiB; six does not fit.
        Assert.Equal(5, manager.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Initialization_and_disposal_failures_keep_the_bound_owner_quarantined(bool faultInstead)
    {
        var manager = new StreamHostManager("synthetic", (a, o, t) => new StreamPanelHost(a, o, t)
        {
            ThrowOnStart = !faultInstead, FaultOnStart = faultInstead, ThrowOnDispose = true,
        });
        manager.Reconcile([Assignment("one", "panel")], "test-token");
        var old = Assert.Single(StreamPanelHost.Created);
        Assert.True(old.Disposed);
        manager.Reconcile([Assignment("two", "panel")], "test-token");
        Assert.Single(StreamPanelHost.Created);
        Assert.Equal(1, manager.Count);
        old.Finish.TrySetException(new IOException("unconfirmed native teardown"));
        manager.Reconcile([Assignment("three", "panel")], "test-token");
        Assert.Single(StreamPanelHost.Created);
        Assert.Equal(1, manager.Count);
    }

    [Fact]
    public void Inert_constructor_failure_releases_admission_and_successful_count_reaps_without_reconcile()
    {
        var manager = new StreamHostManager("synthetic", (a, o, t) => a.PanelDeviceId == "bad"
            ? throw new InvalidOperationException("inert constructor failed") : new StreamPanelHost(a, o, t));
        manager.Reconcile([Assignment("bad", "bad"), Assignment("one", "panel")], "test-token");
        Assert.Single(StreamPanelHost.Created);
        Assert.Equal(1, manager.Count);
        manager.CloseAll();
        StreamPanelHost.Created[0].Finish.TrySetResult();
        Assert.Equal(0, manager.Count); // No subsequent prefs push is necessary to become idle.
    }

    [Fact]
    public void Late_old_fault_cannot_close_a_new_host_that_reused_its_session_id()
    {
        var manager = new StreamHostManager("synthetic");
        manager.Reconcile([Assignment("same", "old-panel")], "test-token");
        var old = StreamPanelHost.Created[0];
        manager.Reconcile([Assignment("same", "new-panel")], "test-token");
        var current = StreamPanelHost.Created[1];
        old.Faulted?.Invoke();
        Assert.False(current.Disposed);
        Assert.Equal(2, manager.Count);
    }

    [Fact]
    public void Invalid_assignments_and_duplicate_stable_panels_cannot_allocate_extra_hosts()
    {
        var manager = new StreamHostManager("synthetic");
        var invalid = Assignment("invalid", "invalid");
        invalid.Dpr = double.NaN;
        manager.Reconcile([invalid, Assignment("older", "panel"), Assignment("newer", "panel"), Assignment("", "missing")], "test-token");
        Assert.Equal("newer", Assert.Single(StreamPanelHost.Created).SessionId);
    }

    internal static StreamAssignment Assignment(string session, string panel) => new()
    {
        SessionId = session, PanelDeviceId = panel, CssWidth = 64, CssHeight = 64, Dpr = 1, Codec = "rawBgra",
    };
}
