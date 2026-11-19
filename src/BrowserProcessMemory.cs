using System;
using System.Collections.Generic;
using System.IO;

namespace Nexus.Overlay;

internal readonly record struct BrowserProcessMetrics(DateTime CreatedUtc, string Image, long PrivateBytes, long WorkingSetBytes);
internal readonly record struct BrowserProcessMemoryNode(BrowserProfile Profile, int ProcessId, int Kind, BrowserProcessMetrics Metrics);
internal sealed record BrowserProcessMemoryResult(BrowserProcessMemoryNode[] Nodes, int UnavailableProfiles, int UnreadableProcesses)
{
    internal bool Complete => Nodes.Length > 0 && UnavailableProfiles == 0 && UnreadableProcesses == 0;
    internal long WorkingSetBytes { get { long total = 0; foreach (var n in Nodes) total += n.Metrics.WorkingSetBytes; return total; } }
    internal int LargestPid { get { long max = -1; int id = 0; foreach (var n in Nodes) if (n.Metrics.WorkingSetBytes > max) { max = n.Metrics.WorkingSetBytes; id = n.ProcessId; } return id; } }
    internal long LargestWorkingSetBytes { get { long max = 0; foreach (var n in Nodes) max = Math.Max(max, n.Metrics.WorkingSetBytes); return max; } }
}

/// <summary>OS metrics are correlated to the native snapshot, not to ancestry.
/// A creation newer than the snapshot or a different binary is not admitted.
/// Working sets may share pages; private bytes are commit, not JS/GPU bytes.</summary>
internal static class BrowserProcessMemory
{
    internal static BrowserProcessMemoryResult Sample(BrowserProcessGroup[] groups, Func<int, BrowserProcessMetrics?> read)
    {
        var nodes = new Dictionary<int, BrowserProcessMemoryNode>();
        int unavailable = 0, unreadable = 0;
        foreach (var group in groups)
        {
            if (!group.Available) { unavailable++; continue; }
            BrowserProcessInfo? browser = null;
            foreach (var row in group.Processes)
                if (row.Kind == 0) { if (browser.HasValue) { browser = null; break; } browser = row; }
            if (!browser.HasValue) { unavailable++; continue; }
            var root = read(browser.Value.ProcessId);
            if (!Valid(root, group.CapturedUtc) || !IsWebView(root!.Value.Image)) { unavailable++; continue; }
            foreach (var row in group.Processes)
            {
                var metric = row.ProcessId == browser.Value.ProcessId ? root : read(row.ProcessId);
                if (!Valid(metric, group.CapturedUtc) || !string.Equals(metric!.Value.Image, root!.Value.Image, StringComparison.OrdinalIgnoreCase)) { unreadable++; continue; }
                var node = new BrowserProcessMemoryNode(group.Profile, row.ProcessId, row.Kind, metric.Value);
                if (nodes.TryGetValue(row.ProcessId, out var prior))
                {
                    if (prior.Kind != node.Kind || prior.Metrics.CreatedUtc != node.Metrics.CreatedUtc || !string.Equals(prior.Metrics.Image, node.Metrics.Image, StringComparison.OrdinalIgnoreCase)) unreadable++;
                }
                else nodes.Add(row.ProcessId, node);
            }
        }
        var result = new BrowserProcessMemoryNode[nodes.Count];
        nodes.Values.CopyTo(result, 0);
        return new(result, unavailable, unreadable);
    }

    private static bool Valid(BrowserProcessMetrics? metrics, DateTime at)
        => metrics.HasValue && metrics.Value.CreatedUtc <= at && metrics.Value.CreatedUtc > DateTime.UnixEpoch
            && metrics.Value.PrivateBytes >= 0 && metrics.Value.WorkingSetBytes >= 0;

    private static bool IsWebView(string image)
        => string.Equals(Path.GetFileName(image), "msedgewebview2.exe", StringComparison.OrdinalIgnoreCase);
}
