using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Nexus.Overlay.Win32;
using Xunit.Abstractions;

namespace Nexus.Overlay.Tests.NativeOwners;

public sealed class WindowClassIconResourceTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "IconResources")]
    public void Legacy_file_loads_allocate_distinct_icons_that_the_caller_must_release() => CheckResources(cached: false);

    [Fact]
    [Trait("Category", "IconResources")]
    public void Repeated_class_acquisition_keeps_native_icon_resources_constant() => CheckResources(cached: true);

    private void CheckResources(bool cached)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "icon.ico");
        uint flags = Native.LR_LOADFROMFILE | Native.LR_DEFAULTSIZE | (cached ? 0 : Native.LR_SHARED);
        using var process = Process.GetCurrentProcess();
        nint Load()
        {
            nint icon = Native.LoadImageW(0, path, Native.IMAGE_ICON, 0, 0, flags);
            Assert.NotEqual(0, icon);
            return icon;
        }
        // Warm only this private test process's Windows icon decoder/cache.
        Assert.True(DestroyIcon(Load()));
        var handles = new HashSet<nint>();
        var samples = new List<object>();
        var baseline = Counts(process);
        var owner = new WindowClassIcon(Load);
        (uint Gdi, uint User) first = default;
        try
        {
            for (int acquisition = 0; acquisition < 8; acquisition++)
            {
                nint handle = cached ? owner.Get() : Load();
                handles.Add(handle);
                var counts = Counts(process);
                if (acquisition == 0) first = counts;
                samples.Add(new { Acquisition = acquisition, Handle = handle.ToInt64(), counts.Gdi, counts.User });
            }
            var final = Counts(process);
            if (cached)
            {
                Assert.Single(handles);
                Assert.Equal(first, final);
            }
            else
            {
                Assert.Equal(8, handles.Count);
                Assert.True(final.Gdi > first.Gdi);
                Assert.True(final.User > first.User);
            }
        }
        finally
        {
            // No window class/window references these private test handles.
            // Product class icons intentionally have no per-window teardown.
            foreach (nint handle in handles) Assert.True(DestroyIcon(handle));
            var retired = Counts(process);
            string json = JsonSerializer.Serialize(new
            {
                Cached = cached, IconSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
                Flags = flags, LoadsObserved = handles.Count, BaselineGdi = baseline.Gdi, BaselineUser = baseline.User,
                Samples = samples, RetiredGdi = retired.Gdi, RetiredUser = retired.User,
                WindowsCreated = 0, InstalledFilesOrProcessesTouched = false,
            }, new JsonSerializerOptions { WriteIndented = true });
            output.WriteLine(json);
            if (Environment.GetEnvironmentVariable("NEXUS_ICON_EVIDENCE") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                string file = Path.Combine(directory, cached ? "class-icon.json" : "legacy-file-icons.json");
                using var evidence = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(evidence);
                writer.WriteLine(json);
            }
            Assert.Equal(baseline, retired);
        }
    }

    private static (uint Gdi, uint User) Counts(Process process) =>
        (GetGuiResources(process.Handle, 0), GetGuiResources(process.Handle, 1));

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(nint process, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
}
