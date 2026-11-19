using System;
using System.Diagnostics;

namespace Nexus.Overlay;

internal static class BrowserProcessMemoryReader
{
    internal static BrowserProcessMetrics? Read(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var birth = process.StartTime.ToUniversalTime();
            var image = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(image)) return null;
            var privateBytes = process.PrivateMemorySize64;
            var workingSet = process.WorkingSet64;
            // Process metadata uses several OS queries. A new independent
            // handle rejects an exit/PID reuse between those queries.
            using var check = Process.GetProcessById(processId);
            if (check.StartTime.ToUniversalTime() != birth || process.HasExited ||
                !string.Equals(check.MainModule?.FileName, image, StringComparison.OrdinalIgnoreCase)) return null;
            return new(birth, image, privateBytes, workingSet);
        }
        catch { return null; } // Counted as unavailable, never as zero memory.
    }
}
