using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Nexus.Overlay.Capture;
using Nexus.Overlay.Media;
using Nexus.Overlay.WebView2;
using Nexus.Overlay.Win32;

namespace Nexus.Overlay.Bench;

internal static class Entry
{
    private static int Main(string[] args)
    {
        if (args.Length != 3 || !int.TryParse(args[0], out int warm) || !int.TryParse(args[1], out int measure)
            || warm < 0 || warm > 3600 || measure < 1 || measure > 3600) return 2;
        var output = Path.GetFullPath(args[2]);
        if (File.Exists(output)) throw new IOException("Benchmark evidence already exists");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        if (!Native.SetProcessDpiAwarenessContext(-4)) throw new InvalidOperationException("Benchmark requires physical-pixel DPI awareness");
        int offscreenLeft = Native.GetSystemMetrics(76 /* SM_XVIRTUALSCREEN */) - SyntheticScene.Width * 2 - 100;
        using var firstScene = new SyntheticScene(false, offscreenLeft);
        using var secondScene = new SyntheticScene(true, offscreenLeft + SyntheticScene.Width + 50);
        using var idle = new Pipeline(firstScene.Start(), false);
        using var video = new Pipeline(secondScene.Start(), true);
        idle.Start(); video.Start();
        Console.WriteLine("NEXUS_BENCH_READY"); Console.Out.Flush();
        Thread.Sleep(warm * 1000);
        firstScene.ThrowIfFailed(); secondScene.ThrowIfFailed();
        using var process = Process.GetCurrentProcess();
        var samples = new List<object>();
        var cpuStart = process.TotalProcessorTime.TotalSeconds;
        long authoredStart = secondScene.PresentedFrames, capturedStart = video.Frames, encodedStart = video.Encoded;
        var started = Stopwatch.StartNew();
        samples.Add(new { atSeconds = 0, cpuSeconds = 0, privateBytes = process.PrivateMemorySize64,
            workingSetBytes = process.WorkingSet64, handles = process.HandleCount, threads = process.Threads.Count,
            idle = idle.Read(), animated = video.Read(), authoredFrames = secondScene.PresentedFrames,
            fixture = new { width = SyntheticScene.Width, height = SyntheticScene.Height, fps = SyntheticScene.FramesPerSecond,
                renderer = "opaque-layered-memory-dc", firstX = offscreenLeft, secondX = offscreenLeft + SyntheticScene.Width + 50 } });
        while (started.Elapsed.TotalSeconds < measure)
        {
            Thread.Sleep(Math.Min(10000, Math.Max(1, (measure * 1000) - (int)started.ElapsedMilliseconds)));
            process.Refresh();
            samples.Add(new { atSeconds = started.Elapsed.TotalSeconds, cpuSeconds = process.TotalProcessorTime.TotalSeconds - cpuStart,
                privateBytes = process.PrivateMemorySize64, workingSetBytes = process.WorkingSet64,
                handles = process.HandleCount, threads = process.Threads.Count,
                idle = idle.Read(), animated = video.Read(), authoredFrames = secondScene.PresentedFrames });
            File.WriteAllText(output, JsonSerializer.Serialize(samples));
        }
        firstScene.ThrowIfFailed(); secondScene.ThrowIfFailed();
        var seconds = started.Elapsed.TotalSeconds;
        bool valid = CheckCadence("authored", secondScene.PresentedFrames - authoredStart, seconds)
            & CheckCadence("captured", video.Frames - capturedStart, seconds)
            & CheckCadence("encoded", video.Encoded - encodedStart, seconds);
        return valid ? 0 : 3;
    }

    private static bool CheckCadence(string stage, long frames, double seconds)
    {
        double fps = frames / seconds;
        Console.WriteLine($"NEXUS_BENCH_CADENCE {stage} frames={frames} seconds={seconds:F3} fps={fps:F3} scene=800x800 target=30");
        if (fps is >= 27 and <= 33) return true;
        Console.Error.WriteLine($"Invalid benchmark workload: {stage} cadence is outside the 30fps smoke tolerance");
        return false;
    }

    private sealed class Pipeline(nint window, bool encode) : IDisposable
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Thread? _thread;
        private WgcCapture? _capture;
        private volatile bool _stop;
        private long _frames, _emptyReads, _waits, _encoded, _bytes;
        private Exception? _failure;
        internal long Frames => Interlocked.Read(ref _frames);
        internal long Encoded => Interlocked.Read(ref _encoded);
        internal void Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = encode ? "benchmark-video" : "benchmark-idle" };
            _thread.Start(); _ready.Task.GetAwaiter().GetResult();
        }
        internal object Read() => new { frames = Interlocked.Read(ref _frames), emptyReads = Interlocked.Read(ref _emptyReads),
            waits = Interlocked.Read(ref _waits), encoded = Interlocked.Read(ref _encoded), bytes = Interlocked.Read(ref _bytes), failure = _failure?.ToString() };
        private void Run()
        {
            RoInitialize(1);
#if BASELINE
            TimeBeginPeriod(1);
#endif
            try
            {
                using var device = D3DDevice.Create();
                using var capture = new WgcCapture(window, device, 800, 800);
                _capture = capture;
#if !BASELINE
                capture.Start();
#endif
                using var encoder = encode ? new MfEncoder(device, 800, 800, 30, 8000, (data, _) =>
                { Interlocked.Increment(ref _encoded); Interlocked.Add(ref _bytes, data.Length); }, "benchmark") : null;
                _ready.TrySetResult();
                long submitted = 0, origin = 0;
                while (!_stop)
                {
#if BASELINE
                    bool got = capture.TryGetNextFrame(out var frame, out _);
#else
                    bool got = capture.TryGetNextFrame(out var frame);
#endif
                    if (!got)
                    {
                        Interlocked.Increment(ref _emptyReads); Interlocked.Increment(ref _waits);
#if BASELINE
                        Thread.Sleep(2);
#else
                        capture.WaitForFrame();
#endif
                        continue;
                    }
                    Interlocked.Increment(ref _frames);
                    long now = Stopwatch.GetTimestamp();
                    if (origin == 0) origin = now;
                    long allowed = 1 + (now - origin) * 30 / Stopwatch.Frequency;
                    if (allowed - submitted > 3) { origin = now; submitted = 0; }
                    bool deliver = encoder is not null && submitted <= allowed;
                    if (deliver) { submitted++; encoder!.Submit(frame!); }
                    else
                    {
#if BASELINE
                        Wv2.Release(frame);
#else
                        frame!.Dispose();
#endif
                    }
                }
                encoder?.Dispose();
#if !BASELINE
                encoder?.Completion.GetAwaiter().GetResult();
                capture.Dispose(); capture.Completion.GetAwaiter().GetResult();
#endif
            }
            catch (Exception error) { _failure = error; _ready.TrySetException(error); }
            finally
            {
#if BASELINE
                TimeEndPeriod(1);
#endif
            }
        }
        public void Dispose()
        {
            _stop = true;
#if !BASELINE
            _capture?.Wake();
#endif
            if (_thread is not null && !_thread.Join(10000)) throw new TimeoutException("Benchmark pipeline retirement is pending");
            if (_failure is not null) throw new InvalidOperationException("Benchmark pipeline failed", _failure);
        }
    }
    [DllImport("combase.dll")] private static extern int RoInitialize(uint type);
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] private static extern uint TimeBeginPeriod(uint ms);
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")] private static extern uint TimeEndPeriod(uint ms);
}
