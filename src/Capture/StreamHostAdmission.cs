using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Nexus.Overlay.Capture;

internal interface IStreamHost : IDisposable
{
    string SessionId { get; }
    string PanelDeviceId { get; }
    Task Completion { get; }
    Action? Faulted { get; set; }
    void Start();
}

/// <summary>Numeric admission policy, independent of the raw wire limit for H.264.</summary>
internal static class StreamHostStorageBudget
{
    internal const int MaximumOwners = 32;
    internal const long MaximumOwnedBytes = 512L * 1024 * 1024;
    internal const int MaximumRawFrameBytes = 16 * 1024 * 1024;
    internal const int MaximumSide = 4096;
    internal const int MaximumEncodedPixels = 3840 * 2160;
    // Encoded queue 32 MiB + maximum in-flight and producer payloads 16 MiB each.
    internal const long MaximumEncodedHttpBytes = 64L * 1024 * 1024;
    internal const int Nv12ReferenceSlots = 6; // Queue four + one producer + one encoder-held output reference.
    // WGC two BGRA surfaces + raw staging/three arrays, or four owned MF BGRA slots.
    // Encoder/driver/HTTP internals and shared sections are accounted separately.
    internal const int OwnedFrameUnits = 6;

    internal static bool TryMeasure(StreamAssignment assignment, out long bytes, out string reason)
    {
        bytes = 0;
        reason = "invalid stream geometry or codec";
        if (assignment.CssWidth <= 0 || assignment.CssHeight <= 0 || !double.IsFinite(assignment.Dpr) || assignment.Dpr <= 0)
            return false;
        var raw = string.Equals(assignment.Codec, "rawBgra", StringComparison.OrdinalIgnoreCase);
        if (!raw && !string.Equals(assignment.Codec, "h264", StringComparison.OrdinalIgnoreCase)) return false;
        var width = Math.Round(assignment.CssWidth * assignment.Dpr);
        var height = Math.Round(assignment.CssHeight * assignment.Dpr);
        if (!double.IsFinite(width) || !double.IsFinite(height) || width > MaximumSide || height > MaximumSide) return false;
        var pixels = (long)Math.Max(2, (int)width & ~1) * Math.Max(2, (int)height & ~1);
        var frameBytes = checked(pixels * 4);
        if ((raw && frameBytes > MaximumRawFrameBytes) || (!raw && pixels > MaximumEncodedPixels)) return false;
        var sharedBytes = raw && !string.IsNullOrEmpty(assignment.FrameSection) && assignment.FrameSectionBytes > 0
            ? assignment.FrameSectionBytes : 0;
        if (sharedBytes > MaximumRawFrameBytes || (sharedBytes > 0 && sharedBytes < frameBytes)) return false;
        bytes = checked(frameBytes * OwnedFrameUnits + (raw ? sharedBytes + (sharedBytes > 0 ? 64 : 0)
            : MaximumEncodedHttpBytes + pixels * 3 / 2 * Nv12ReferenceSlots));
        reason = "";
        return true;
    }
}

/// <summary>Message-loop-owned registry; active and unresolved retired generations share one budget.</summary>
internal sealed class StreamHostAdmission
{
    internal sealed class Reservation(string sessionId, string panelId, long bytes)
    {
        internal string SessionId { get; } = sessionId;
        internal string PanelId { get; } = panelId;
        internal long Bytes { get; } = bytes;
        internal IStreamHost? Host;
        internal bool Retiring;
    }
    private readonly Dictionary<string, Reservation> _panels = new(StringComparer.Ordinal);
    internal long OwnedBytes { get; private set; }
    internal int Count { get { Sweep(); return _panels.Count; } }

    internal bool TryReserve(string sessionId, string panelId, long bytes, out Reservation? reservation, out string reason)
    {
        Sweep();
        reservation = null;
        if (_panels.ContainsKey(panelId)) { reason = "previous stable-panel generation has not quiesced; wait or explicitly recover the overlay process"; return false; }
        if (bytes <= 0 || _panels.Count >= StreamHostStorageBudget.MaximumOwners || bytes > StreamHostStorageBudget.MaximumOwnedBytes - OwnedBytes)
        {
            reason = "global active/retired capture budget reached (32 owners / 512 MiB); existing hosts are preserved";
            return false;
        }
        reservation = new(sessionId, panelId, bytes);
        _panels.Add(panelId, reservation);
        OwnedBytes += bytes;
        reason = "";
        return true;
    }

    internal void Retire(Reservation reservation) => reservation.Retiring = true;
    internal void AbandonUnstarted(Reservation reservation)
    {
        // The inert constructor has allocated no native state. Bound hosts release only through Completion.
        if (reservation.Host is null) Remove(reservation);
    }
    internal void Sweep()
    {
        foreach (var reservation in _panels.Values.ToArray())
        {
            if (!reservation.Retiring || reservation.Host is not { } host) continue;
            if (host.Completion.IsCompletedSuccessfully) Remove(reservation);
            else if (host.Completion.IsFaulted) _ = host.Completion.Exception; // Observed but still quarantined.
        }
    }
    private void Remove(Reservation reservation)
    {
        if (_panels.TryGetValue(reservation.PanelId, out var current) && ReferenceEquals(current, reservation))
        {
            _panels.Remove(reservation.PanelId);
            OwnedBytes -= reservation.Bytes;
        }
    }
}
