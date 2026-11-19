using Nexus.Overlay.Win32;

namespace Nexus.Overlay.Tests.NativeOwners;

public sealed class WindowClassIconTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public void Reopened_windows_reuse_the_one_class_icon(int windows)
    {
        int loads = 0;
        var icon = new WindowClassIcon(() => (nint)(++loads));
        for (int window = 0; window < windows; window++) Assert.Equal((nint)1, icon.Get());
        Assert.Equal(1, loads);
    }

    [Fact]
    public void Concurrent_acquisition_loads_only_one_owned_handle()
    {
        int loads = 0;
        var icon = new WindowClassIcon(() => (nint)Interlocked.Increment(ref loads));
        var handles = new nint[128];
        Parallel.For(0, handles.Length, index => handles[index] = icon.Get());
        Assert.All(handles, handle => Assert.Equal((nint)1, handle));
        Assert.Equal(1, loads);
    }

    [Fact]
    public void Concurrent_missing_results_retry_only_until_one_successful_load()
    {
        int loads = 0;
        var icon = new WindowClassIcon(() => ++loads <= 2 ? 0 : (nint)42);
        var handles = new nint[128];
        Parallel.For(0, handles.Length, index => handles[index] = icon.Get());
        Assert.Equal(2, handles.Count(handle => handle == 0));
        Assert.Equal(126, handles.Count(handle => handle == 42));
        Assert.Equal(3, loads);
        Assert.Equal((nint)42, icon.Get());
        Assert.Equal(3, loads);
    }

    [Fact]
    public void A_concurrent_failed_load_releases_waiters_and_allows_one_successor()
    {
        int loads = 0;
        var failure = new InvalidOperationException("Synthetic first icon failure");
        var icon = new WindowClassIcon(() => ++loads == 1 ? throw failure : (nint)42);
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var handles = new System.Collections.Concurrent.ConcurrentBag<nint>();
        Parallel.For(0, 128, _ =>
        {
            try { handles.Add(icon.Get()); }
            catch (Exception error) { errors.Add(error); }
        });
        Assert.Same(failure, Assert.Single(errors));
        Assert.Equal(127, handles.Count);
        Assert.All(handles, handle => Assert.Equal((nint)42, handle));
        Assert.Equal(2, loads);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Missing_icon_retries_until_success_then_retains_the_result(int failures)
    {
        int loads = 0;
        var icon = new WindowClassIcon(() => ++loads <= failures ? 0 : (nint)42);
        for (int attempt = 0; attempt < failures; attempt++) Assert.Equal(0, icon.Get());
        Assert.Equal((nint)42, icon.Get());
        Assert.Equal((nint)42, icon.Get());
        Assert.Equal(failures + 1, loads);
    }

    [Fact]
    public void Failed_load_does_not_poison_the_next_attempt()
    {
        int loads = 0;
        var failure = new InvalidOperationException("Synthetic icon load failure");
        var icon = new WindowClassIcon(() => ++loads == 1 ? throw failure : (nint)42);
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => icon.Get()));
        Assert.Equal((nint)42, icon.Get());
        Assert.Equal((nint)42, icon.Get());
        Assert.Equal(2, loads);
    }

    [Fact]
    public void An_absent_icon_keeps_the_existing_default_and_retry_behavior()
    {
        int loads = 0;
        var icon = new WindowClassIcon(() => { loads++; return 0; });
        for (int attempt = 0; attempt < 3; attempt++) Assert.Equal(0, icon.Get());
        Assert.Equal(3, loads);
    }

    [Fact]
    public void A_loader_is_required() => Assert.Throws<ArgumentNullException>(() => new WindowClassIcon(null!));
}
