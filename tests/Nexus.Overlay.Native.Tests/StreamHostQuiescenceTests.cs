using System.Reflection;
using System.Runtime.CompilerServices;
using Nexus.Overlay.Capture;
using Nexus.Overlay.Media;

namespace Nexus.Overlay.Tests.NativeOwners;

public sealed class StreamHostQuiescenceTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Actual_host_retains_pipeline_until_both_pump_and_io_quiesce(bool parkPump)
    {
        using var nativeDevice = new FakeCom();
        var device = (D3DDevice)RuntimeHelpers.GetUninitializedObject(typeof(D3DDevice)); device.Device = nativeDevice.Pointer;
        using var capture = new WgcCapture(0, device, 2, 2); // inert, no native activation
        using var handler = new IngestOwnershipTests.PausedHandler();
        using var ingest = new IngestClient("http://127.0.0.1", "synthetic", "test-only", handler: handler, rawFrames: true);
        ingest.Send([1], true);
        await handler.Stream.Entered.Task;
        using var host = new StreamPanelHost(new() { SessionId = "synthetic", PanelDeviceId = "test-panel", CssWidth = 2, CssHeight = 2, Dpr = 1 }, "http://127.0.0.1", "test-only");
        var sink = new PendingSink();
        Set(host, "_d3d", device); Set(host, "_capture", capture); Set(host, "_encoder", sink); Set(host, "_ingest", ingest);
        using var releasePump = new ManualResetEventSlim(false);
        Thread? pump = null;
        if (parkPump)
        {
            var pumpExited = Get<TaskCompletionSource>(host, "_pumpExited");
            pump = new Thread(() => { releasePump.Wait(); pumpExited.TrySetResult(); }) { IsBackground = true };
            Set(host, "_pumpThread", pump); pump.Start();
        }
        try
        {
            host.Dispose(); host.Dispose();
            Assert.False(host.Completion.IsCompleted);
            Assert.Equal(1, nativeDevice.References);
            if (parkPump) Assert.False(sink.Disposed.Task.IsCompleted);
            releasePump.Set();
            await sink.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            sink.Finished.TrySetResult();
            Assert.False(host.Completion.IsCompleted); // the cancellation-ignoring HTTP write still owns storage
            Assert.Equal(1, nativeDevice.References);
            handler.Stream.Release.TrySetResult();
            await host.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, nativeDevice.References);
        }
        finally
        {
            releasePump.Set(); sink.Finished.TrySetResult(); handler.Stream.Release.TrySetResult(); host.Dispose();
            if (pump is not null) Assert.True(pump.Join(5000));
            await host.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
    private static void Set(object target, string field, object value)
        => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static T Get<T>(object target, string field)
        => (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private sealed class PendingSink : IFrameSink
    {
        internal readonly TaskCompletionSource Disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Completion => Finished.Task;
        public void Submit(CapturedFrame frame) => throw new NotSupportedException();
        public void Dispose() => Disposed.TrySetResult();
    }
}
