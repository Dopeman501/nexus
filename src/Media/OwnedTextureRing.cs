using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Overlay.Capture;
using Nexus.Overlay.WebView2;

namespace Nexus.Overlay.Media;

/// <summary>Four owned BGRA surfaces. GPU completion releases WGC; tracked MF release permits reuse.</summary>
internal sealed class OwnedTextureRing : IDisposable
{
    internal const int Capacity = 4;
    private static readonly Guid SampleId = new("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4");
    private readonly Slot[] _slots = new Slot[Capacity];
    private readonly nint _device;
    private readonly nint _context;
    private readonly int _width;
    private readonly int _height;
    private readonly TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;
    private bool _cleanupStarted;
    internal Task Completion => _settled.Task;
    internal long TextureBytes => (long)Capacity * _width * _height * 4;

    internal unsafe OwnedTextureRing(D3DDevice device, int width, int height,
        Func<Action, NativeSignalCallback>? createCallback = null)
    {
        _device = device.Device;
        Wv2.AddRef(_device);
        _width = width; _height = height;
        try
        {
            var get = (delegate* unmanaged[Stdcall]<nint, nint*, void>)Wv2.Slot(_device, 40);
            nint context; get(_device, &context); _context = context;
            if (_context == 0) throw new InvalidOperationException("Capture device returned no immediate context");
            for (int i = 0; i < Capacity; i++) _slots[i] = new Slot(this, createCallback);
        }
        catch
        {
            foreach (var slot in _slots) slot?.Callback.Dispose();
            if (_context != 0) Wv2.Release(_context);
            Wv2.Release(_device);
            throw;
        }
    }

    /// <summary>Transfers the WGC lease even when copying faults. A stuck GPU retains one reserved slot.</summary>
    internal unsafe nint CopyToSample(CapturedFrame source)
    {
        Slot? selected = null;
        foreach (var slot in _slots)
            if (Interlocked.CompareExchange(ref slot.Busy, 1, 0) == 0) { selected = slot; break; }
        if (selected is null || Volatile.Read(ref _disposed) != 0)
        {
            source.Dispose(); selected?.Return(); return 0;
        }
        var entry = selected;
        bool submitted = false;
        bool completed = false;
        nint tracked = 0, sample = 0, buffer = 0;
        try
        {
            EnsureSurface(entry);
            var copy = (delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Wv2.Slot(_context, 47);
            var end = (delegate* unmanaged[Stdcall]<nint, nint, void>)Wv2.Slot(_context, 28);
            var flush = (delegate* unmanaged[Stdcall]<nint, void>)Wv2.Slot(_context, 111);
            copy(_context, entry.Texture, source.Texture); end(_context, entry.Query); submitted = true; flush(_context);
            var getData = (delegate* unmanaged[Stdcall]<nint, nint, nint, uint, uint, int>)Wv2.Slot(_context, 29);
            long started = Stopwatch.GetTimestamp();
            while (true)
            {
                int hr = getData(_context, entry.Query, 0, 0, 0);
                if (hr == 0) { completed = true; break; }
                if (hr < 0 || Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(5))
                    throw new InvalidOperationException($"Capture GPU copy did not complete: 0x{hr:X8}");
                Thread.Sleep(1);
            }
            source.Dispose();
            Check(MfInterop.MFCreateTrackedSample(out tracked));
            sample = Wv2.QueryInterface(tracked, SampleId);
            if (sample == 0) throw new InvalidOperationException("Tracked sample has no IMFSample");
            Check(MfInterop.MFCreateDXGISurfaceBuffer(D3D11Interop.IID_ID3D11Texture2D, entry.Texture, 0, 0, out buffer));
            Check(MfInterop.Sample_AddBuffer(sample, buffer));
            var setAllocator = (delegate* unmanaged[Stdcall]<nint, nint, nint, int>)Wv2.Slot(tracked, 3);
            Check(setAllocator(tracked, entry.Callback.Pointer, 0));
            var result = sample; sample = 0;
            return result;
        }
        catch
        {
            if (submitted && !completed)
            {
                // Keep the original pool frame and native context reachable until actual completion.
                entry.UncertainSource = source;
                _ = ObserveCopyAsync(entry);
            }
            else { source.Dispose(); entry.Return(); }
            throw;
        }
        finally
        {
            if (sample != 0) Wv2.Release(sample);
            if (buffer != 0) Wv2.Release(buffer);
            if (tracked != 0) Wv2.Release(tracked);
        }
    }

    // One observer per reserved slot, never a replacement pipeline or unbounded worker generation.
    private async Task ObserveCopyAsync(Slot entry)
    {
        long started = Stopwatch.GetTimestamp();
        while (!CopyComplete(entry.Query))
        {
            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(5))
            {
                Log.Warn("stream-encoder: GPU copy remains quarantined; host process recovery is required");
                return; // keep lease/context/slot reserved, without an indefinite polling loop
            }
            await Task.Delay(250).ConfigureAwait(false);
        }
        entry.UncertainSource!.Dispose(); entry.UncertainSource = null; entry.Return();
    }

    private unsafe bool CopyComplete(nint query)
    {
        var get = (delegate* unmanaged[Stdcall]<nint, nint, nint, uint, uint, int>)Wv2.Slot(_context, 29);
        return get(_context, query, 0, 0, 0) == 0;
    }

    private unsafe void EnsureSurface(Slot entry)
    {
        if (entry.Texture != 0 && entry.Query != 0) return;
        var desc = new D3D11Vtable.D3D11_TEXTURE2D_DESC
        {
            Width = (uint)_width, Height = (uint)_height, MipLevels = 1, ArraySize = 1,
            Format = 87, SampleDescCount = 1, BindFlags = 0x28,
        };
        nint texture;
        var create = (delegate* unmanaged[Stdcall]<nint, D3D11Vtable.D3D11_TEXTURE2D_DESC*, nint, nint*, int>)Wv2.Slot(_device, 5);
        if (entry.Texture == 0) { Check(create(_device, &desc, 0, &texture)); entry.Texture = texture; }
        // D3D11_QUERY_DESC { D3D11_QUERY_EVENT, MiscFlags=0 }.
        ulong queryDesc = 0; nint query;
        var createQuery = (delegate* unmanaged[Stdcall]<nint, void*, nint*, int>)Wv2.Slot(_device, 24);
        Check(createQuery(_device, &queryDesc, &query)); entry.Query = query;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        TrySettle();
    }

    private void TrySettle()
    {
        lock (_slots)
        {
            if (_disposed == 0 || _cleanupStarted) return;
            foreach (var slot in _slots) if (Volatile.Read(ref slot.Busy) != 0) return;
            _cleanupStarted = true;
            foreach (var slot in _slots)
            {
                slot.Callback.Dispose();
                if (slot.Texture != 0) Wv2.Release(slot.Texture);
                if (slot.Query != 0) Wv2.Release(slot.Query);
            }
            Wv2.Release(_context); Wv2.Release(_device);
            _ = FinishCallbacksAsync();
        }
    }

    private async Task FinishCallbacksAsync()
    {
        foreach (var slot in _slots) await slot.Callback.Completion.ConfigureAwait(false);
        _settled.TrySetResult();
    }

    private static void Check(int hr) { if (hr < 0) throw new InvalidOperationException($"Owned capture surface failed: 0x{hr:X8}"); }

    private sealed class Slot
    {
        internal int Busy;
        internal nint Texture, Query;
        internal CapturedFrame? UncertainSource;
        internal readonly NativeSignalCallback Callback;
        private readonly OwnedTextureRing _owner;
        internal Slot(OwnedTextureRing owner, Func<Action, NativeSignalCallback>? create)
        { _owner = owner; Callback = create is null ? new(Return, mediaFoundation: true) : create(Return); }
        internal void Return() { Interlocked.Exchange(ref Busy, 0); _owner.TrySettle(); }
    }
}
