using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Overlay.WebView2;

namespace Nexus.Overlay.Tests.NativeOwners;

public sealed unsafe class WebViewProcessInfoAbiTests
{
    [Fact]
    public void Pinned_sdk_collection_slots_read_pid_and_kind_and_release_each_owned_reference()
    {
        using var fake = new Fake();
        using (var source = new Wv2ProcessInfo(fake.Environment))
        {
            var row = Assert.Single(source.Read()!);
            Assert.Equal(40012, row.ProcessId); Assert.Equal(4, row.Kind);
            Assert.Equal(1, fake.CollectionsReleased); Assert.Equal(1, fake.ItemsReleased);
        }
        Assert.Equal(1, fake.EnvironmentsReleased);
        Assert.Equal(Wv2ProcessInfo.Environment8Iid, fake.QueriedIid);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void Unsupported_or_failed_native_outcomes_are_not_a_valid_empty_or_partial_snapshot(int fault)
    {
        using var fake = new Fake { Fault = fault };
        using var source = new Wv2ProcessInfo(fake.Environment);
        Assert.Null(source.Read());
        Assert.Equal(fault is 0 ? 0 : 1, fake.CollectionsReleased);
        Assert.Equal(fault >= 4 ? 1 : 0, fake.ItemsReleased);
    }

    [Fact]
    public void Empty_collection_and_disposal_are_explicit_and_idempotent()
    {
        using var fake = new Fake { Count = 0 };
        var source = new Wv2ProcessInfo(fake.Environment);
        Assert.Empty(source.Read()!); source.Dispose(); source.Dispose();
        Assert.Null(source.Read()); Assert.Equal(1, fake.EnvironmentsReleased);
    }

    [Fact]
    public void Wrong_thread_cannot_call_the_environment()
    {
        using var fake = new Fake(); using var source = new Wv2ProcessInfo(fake.Environment);
        BrowserProcessInfo[]? read = [new(1, 0)];
        var thread = new Thread(() => read = source.Read()); thread.Start(); thread.Join();
        Assert.Null(read); Assert.Equal(0, fake.CollectionsReleased);
    }

    [Fact]
    public void Registration_requires_owner_context_and_handles_registration_bound_without_leaking_query_refs()
    {
        var previous = SynchronizationContext.Current;
        var registrations = new List<IDisposable>();
        using var fake = new Fake();
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            using (var unavailable = WebView2MemorySampler.RegisterEnvironment(BrowserProfile.Desktop, IntPtr.Zero))
                Assert.NotNull(unavailable);
            SynchronizationContext.SetSynchronizationContext(new InlineContext());
            for (var i = 0; i < 128; i++) registrations.Add(WebView2MemorySampler.RegisterEnvironment(BrowserProfile.Desktop, fake.Environment)!);
            Assert.Null(WebView2MemorySampler.RegisterEnvironment(BrowserProfile.Desktop, fake.Environment));
        }
        finally
        {
            foreach (var registration in registrations) registration.Dispose();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        Assert.Equal(129, fake.EnvironmentsReleased);
    }

    private sealed class InlineContext : SynchronizationContext { public override void Post(SendOrPostCallback d, object? state) => d(state); }

    private sealed class Fake : IDisposable
    {
        private readonly GCHandle _handle;
        private readonly List<IntPtr> _storage = [];
        internal readonly IntPtr Environment, Collection, Item;
        internal int Fault = -1, Count = 1;
        internal int EnvironmentsReleased, CollectionsReleased, ItemsReleased;
        internal Guid QueriedIid;
        internal Fake()
        {
            _handle = GCHandle.Alloc(this);
            Environment = Object(19); Collection = Object(5); Item = Object(5);
            Set(Environment, 0, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&Query);
            Set(Environment, 18, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)&GetCollection);
            Set(Collection, 3, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)&GetCount);
            Set(Collection, 4, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)&GetItem);
            Set(Item, 3, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int*, int>)&GetPid);
            Set(Item, 4, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int*, int>)&GetKind);
        }
        private IntPtr Object(int slots)
        {
            var table = (IntPtr)NativeMemory.AllocZeroed((nuint)(slots * IntPtr.Size));
            var obj = (IntPtr*)NativeMemory.AllocZeroed((nuint)(2 * IntPtr.Size));
            obj[0] = table; obj[1] = GCHandle.ToIntPtr(_handle);
            _storage.Add(table); _storage.Add((IntPtr)obj);
            Set((IntPtr)obj, 2, (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&Release);
            return (IntPtr)obj;
        }
        private static void Set(IntPtr obj, int index, IntPtr method) => ((IntPtr*)(*(IntPtr*)obj))[index] = method;
        private static Fake Owner(IntPtr obj) => (Fake)GCHandle.FromIntPtr(((IntPtr*)obj)[1]).Target!;
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Query(IntPtr self, Guid* iid, IntPtr* value)
        {
            var f = Owner(self); f.QueriedIid = *iid; *value = f.Fault == 0 ? IntPtr.Zero : self;
            return f.Fault == 0 ? unchecked((int)0x80004002) : 0;
        }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint Release(IntPtr self)
        {
            var f = Owner(self);
            if (self == f.Environment) f.EnvironmentsReleased++;
            else if (self == f.Collection) f.CollectionsReleased++;
            else f.ItemsReleased++;
            return 0;
        }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetCollection(IntPtr self, IntPtr* value) { var f = Owner(self); *value = f.Collection; return f.Fault == 1 ? -1 : 0; }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetCount(IntPtr self, uint* value) { var f = Owner(self); *value = f.Fault == 3 ? 257u : (uint)f.Count; return f.Fault == 2 ? -1 : 0; }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetItem(IntPtr self, uint index, IntPtr* value) { var f = Owner(self); *value = f.Item; return f.Fault == 4 ? -1 : 0; }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetPid(IntPtr self, int* value) { var f = Owner(self); *value = 40012; return f.Fault == 5 ? -1 : 0; }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetKind(IntPtr self, int* value) { *value = 4; return 0; }
        public void Dispose() { foreach (var p in _storage) NativeMemory.Free((void*)p); _handle.Free(); }
    }
}
