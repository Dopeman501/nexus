using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nexus.Overlay.Capture;
using Nexus.Overlay.Media;
using Nexus.Overlay.WebView2;

namespace Nexus.Overlay.Tests.NativeOwners;

public sealed class GpuCopyFailureTests
{
    [Theory]
    [InlineData("create-failure")] [InlineData("late-completion")] [InlineData("wait-then-late")] [InlineData("quarantine")]
    public async Task Copy_failures_never_release_source_storage_before_completion(string mode)
    {
        using var fakes = new Graph(mode);
        using var ring = new OwnedTextureRing(fakes.Device, 2, 2);
        var warned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Log.WarnObserver = message => { if (message.Contains("GPU copy remains quarantined", StringComparison.Ordinal)) warned.TrySetResult(); };
        try
        {
            Assert.Throws<InvalidOperationException>(() => ring.CopyToSample(new(fakes.Frame.Pointer, fakes.Source.Pointer, 0)));
            ring.Dispose();
            if (mode == "quarantine")
            {
                Assert.False(ring.Completion.IsCompleted); Assert.Equal(0, fakes.State.Closed);
                await warned.Task.WaitAsync(TimeSpan.FromSeconds(10));
                int calls = fakes.State.Queries;
                await Task.Delay(300); Assert.Equal(calls, fakes.State.Queries); // bounded observation, no indefinite poll
                Assert.False(ring.Completion.IsCompleted); Assert.Equal(0, fakes.State.Closed);
                // Test-only explicit recovery: actual completion is now known. Do not leave
                // native callback allocations behind in the test runner's process.
                fakes.State.Complete = true;
                var slots = (Array)typeof(OwnedTextureRing).GetField("_slots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ring)!;
                var recovery = (Task)typeof(OwnedTextureRing).GetMethod("ObserveCopyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(ring, [slots.GetValue(0)])!;
                await recovery;
            }
            await ring.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, fakes.State.Closed);
            Assert.Equal(1, fakes.NativeDevice.References); Assert.Equal(1, fakes.Context.References);
            Assert.Equal(0, fakes.Frame.References); Assert.Equal(0, fakes.Source.References);
            Assert.Equal(mode == "create-failure" ? 0 : 1, fakes.State.Copies);
            Assert.Equal(0, fakes.State.ClosedWhenCopyQueued);
        }
        finally { Log.WarnObserver = null; }
    }

    private sealed unsafe class Graph : IDisposable
    {
        internal readonly FakeCom NativeDevice = new(), Context = new(), Target = new(), Query = new(), Frame = new(), Source = new(), Closable = new();
        internal readonly State State;
        internal readonly D3DDevice Device;
        internal Graph(string mode)
        {
            State = new(mode, Context.Pointer, Target.Pointer, Query.Pointer);
            NativeDevice.State = Context.State = Closable.State = State;
            Frame.Query = iid => iid == WgcVtable.IID_IClosable ? Closable.Pointer : 0;
            NativeDevice.Set(40, (nint)(delegate* unmanaged[Stdcall]<nint, nint*, void>)&GetContext);
            NativeDevice.Set(5, (nint)(delegate* unmanaged[Stdcall]<nint, void*, nint, nint*, int>)&CreateTexture);
            NativeDevice.Set(24, (nint)(delegate* unmanaged[Stdcall]<nint, void*, nint*, int>)&CreateQuery);
            Context.Set(47, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, void>)&Copy);
            Context.Set(28, (nint)(delegate* unmanaged[Stdcall]<nint, nint, void>)&End);
            Context.Set(111, (nint)(delegate* unmanaged[Stdcall]<nint, void>)&Flush);
            Context.Set(29, (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, uint, uint, int>)&Data);
            Closable.Set(6, (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Close);
            Device = (D3DDevice)RuntimeHelpers.GetUninitializedObject(typeof(D3DDevice)); Device.Device = NativeDevice.Pointer;
        }
        public void Dispose()
        { NativeDevice.Dispose(); Context.Dispose(); Target.Dispose(); Query.Dispose(); Frame.Dispose(); Source.Dispose(); Closable.Dispose(); }
    }
    private sealed class State(string mode, nint context, nint target, nint query)
    {
        internal readonly string Mode = mode;
        internal readonly nint Context = context, Target = target, Query = query;
        internal int Closed, Copies, Queries, ClosedWhenCopyQueued;
        internal volatile bool Complete;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void GetContext(nint self, nint* output) { *output = ((State)FakeCom.Find(self).State!).Context; Wv2.AddRef(*output); }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe int CreateTexture(nint self, void* desc, nint data, nint* output)
    { var s = (State)FakeCom.Find(self).State!; *output = s.Mode == "create-failure" ? 0 : s.Target; return *output == 0 ? -1 : 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe int CreateQuery(nint self, void* desc, nint* output) { *output = ((State)FakeCom.Find(self).State!).Query; return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Copy(nint self, nint target, nint source)
    { var s = (State)FakeCom.Find(self).State!; s.Copies++; s.ClosedWhenCopyQueued = s.Closed; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void End(nint self, nint query) { }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Flush(nint self) { }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Data(nint self, nint query, nint data, uint size, uint flags)
    {
        var s = (State)FakeCom.Find(self).State!; int calls = ++s.Queries;
        if (s.Mode == "wait-then-late" && calls == 1) return 1;
        return s.Complete || (s.Mode == "late-completion" && calls > 1) || (s.Mode == "wait-then-late" && calls > 2) ? 0 : -1;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Close(nint self) { ((State)FakeCom.Find(self).State!).Closed++; return 0; }
}
