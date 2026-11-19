using System.Reflection;
using System.Runtime.CompilerServices;
using Nexus.Overlay.Capture;
using Nexus.Overlay.Media;

namespace Nexus.Overlay.Tests.NativeOwners;

public sealed class StreamHostPumpStartTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_start_retires_only_a_provably_unstarted_worker(bool throwAfterStarting)
    {
        await CheckStart(throwAfterStarting, fail: true);
    }

    [Fact]
    public async Task Healthy_start_waits_for_the_actual_pump_before_retiring_its_pipeline()
    {
        await CheckStart(startBeforeFailure: false, fail: false);
    }

    private static async Task CheckStart(bool startBeforeFailure, bool fail)
    {
        using var nativeDevice = new FakeCom();
        var device = (D3DDevice)RuntimeHelpers.GetUninitializedObject(typeof(D3DDevice));
        device.Device = nativeDevice.Pointer;
        using var capture = new WgcCapture(0, device, 2, 2); // inert: no native capture activation
        using var host = new StreamPanelHost(new()
        {
            SessionId = "synthetic-thread-start", PanelDeviceId = "test-panel", CssWidth = 2, CssHeight = 2, Dpr = 1,
        }, "http://127.0.0.1", "test-only");
        var sink = new PendingSink();
        Set(host, "_d3d", device); Set(host, "_capture", capture); Set(host, "_encoder", sink);
        var exited = Get<TaskCompletionSource>(host, "_pumpExited");
        Thread? attempted = null;
        try
        {
            Action<Thread>? start = fail ? thread =>
            {
                attempted = thread;
                if (startBeforeFailure) thread.Start();
                throw new OutOfMemoryException("Synthetic Thread.Start failure");
            } : null;
            if (fail)
            {
                var error = Assert.Throws<TargetInvocationException>(() => Start(host, start));
                Assert.IsType<OutOfMemoryException>(error.InnerException);
                if (startBeforeFailure)
                {
                    Assert.Same(attempted, Get<Thread>(host, "_pumpThread"));
                    Assert.False(exited.Task.IsCompleted); // an actual worker remains live
                }
            }
            else Start(host, start);
            attempted ??= Get<Thread>(host, "_pumpThread");
            host.Dispose();
            // An unstarted worker must not park actual owner retirement forever.
            await sink.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(exited.Task.IsCompletedSuccessfully);
            Assert.False(host.Completion.IsCompleted); // downstream ownership is independent
            Assert.Equal(1, nativeDevice.References);
            sink.Finished.TrySetResult();
            await host.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, nativeDevice.References);
        }
        finally
        {
            host.Dispose();
            if (attempted is not null && (attempted.ThreadState & System.Threading.ThreadState.Unstarted) == 0)
                Assert.True(attempted.Join(5000));
            else exited.TrySetResult(); // test-only cleanup: this synthetic thread provably never started
            sink.Finished.TrySetResult();
            await host.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static void Start(StreamPanelHost host, Action<Thread>? start) =>
        typeof(StreamPanelHost).GetMethod("StartPump", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, [start]);
    private static void Set(object target, string field, object value) =>
        target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static T Get<T>(object target, string field) =>
        (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private sealed class PendingSink : IFrameSink
    {
        internal readonly TaskCompletionSource Disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Completion => Finished.Task;
        public void Submit(CapturedFrame frame) => throw new NotSupportedException();
        public void Dispose() => Disposed.TrySetResult();
    }
}
