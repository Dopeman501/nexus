using System;
using System.Collections.Generic;
using System.Linq;

namespace Nexus.Overlay.Capture;

/// <summary>Message-loop owner of active hosts and stable-panel retirement/admission.</summary>
internal sealed partial class StreamHostManager
{
    private readonly string _serviceOrigin;
    private readonly Func<StreamAssignment, string, string, IStreamHost> _create;
    private readonly Dictionary<string, StreamHostAdmission.Reservation> _hosts = new(StringComparer.Ordinal);
    private readonly StreamHostAdmission _admission = new();
    private long? _lastAdmissionNotice;

    internal StreamHostManager(string serviceOrigin, Func<StreamAssignment, string, string, IStreamHost> create)
    {
        _serviceOrigin = serviceOrigin;
        _create = create;
    }
    public int Count => _admission.Count;

    public void Reconcile(IReadOnlyList<StreamAssignment> assignments, string pairedToken)
    {
        _admission.Sweep();
        var byPanel = new Dictionary<string, StreamAssignment>(StringComparer.Ordinal);
        foreach (var assignment in assignments)
        {
            if (!string.IsNullOrEmpty(assignment.SessionId) && assignment.SessionId.Length <= 512
                && !string.IsNullOrEmpty(assignment.PanelDeviceId) && assignment.PanelDeviceId.Length <= 512)
                byPanel[assignment.PanelDeviceId] = assignment;
        }
        var desired = new Dictionary<string, StreamAssignment>(StringComparer.Ordinal);
        foreach (var assignment in byPanel.Values) desired[assignment.SessionId] = assignment;
        foreach (var active in _hosts.ToArray())
            if (desired.TryGetValue(active.Key, out var current) && current.PanelDeviceId != active.Value.PanelId)
                CloseHost(active.Key, "stable panel identity changed");
        var (spawn, close) = StreamHostPlan.Compute(desired.Keys.ToList(), _hosts.Keys.ToList());
        foreach (var sessionId in close) CloseHost(sessionId, "unassigned");
        foreach (var sessionId in spawn) SpawnHost(desired[sessionId], pairedToken);
    }
    public void CloseAll()
    {
        foreach (var sessionId in _hosts.Keys.ToArray()) CloseHost(sessionId, "shutdown");
        _admission.Sweep();
    }
    private void SpawnHost(StreamAssignment desired, string pairedToken)
    {
        var assignment = Snapshot(desired);
        if (!StreamHostStorageBudget.TryMeasure(assignment, out var bytes, out var reason))
        {
            ReportAdmissionFailure(assignment.PanelDeviceId, reason);
            return;
        }
        if (!_admission.TryReserve(assignment.SessionId, assignment.PanelDeviceId, bytes, out var reservation, out reason))
        {
            ReportAdmissionFailure(assignment.PanelDeviceId, reason);
            return;
        }
        try
        {
            var host = _create(assignment, _serviceOrigin, pairedToken);
            reservation!.Host = host;
            host.Faulted = () =>
            {
                if (_hosts.TryGetValue(host.SessionId, out var owner) && ReferenceEquals(owner, reservation))
                    CloseHost(host.SessionId, "faulted");
            };
            _hosts[assignment.SessionId] = reservation;
            host.Start();
            if (!_hosts.TryGetValue(host.SessionId, out var active) || !ReferenceEquals(active, reservation)) return;
            Log.Info($"stream-hosts opened session={assignment.SessionId} device={assignment.PanelDeviceId} reserved={bytes} bytes");
        }
        catch (Exception ex)
        {
            if (reservation!.Host is not null) CloseHost(assignment.SessionId, "initialization failed");
            else _admission.AbandonUnstarted(reservation);
            Log.Error($"stream-hosts spawn {assignment.SessionId}: {ex.Message}");
        }
    }
    private void CloseHost(string sessionId, string reason)
    {
        if (!_hosts.Remove(sessionId, out var reservation)) return;
        _admission.Retire(reservation);
        try { reservation.Host!.Dispose(); }
        catch (Exception ex) { Log.Error($"stream-hosts dispose {sessionId}: {ex.Message}"); }
        Log.Info($"stream-hosts retired session={sessionId} ({reason}); ownership remains until actual completion");
    }
    private void ReportAdmissionFailure(string panelId, string reason)
    {
        var now = Environment.TickCount64;
        if (_lastAdmissionNotice is { } last && now - last < 30000) return;
        _lastAdmissionNotice = now;
        Log.Warn($"stream-hosts blocked device={panelId}: {reason}");
    }
    private static StreamAssignment Snapshot(StreamAssignment value) => new()
    {
        SessionId = value.SessionId, PanelDeviceId = value.PanelDeviceId, CssWidth = value.CssWidth,
        CssHeight = value.CssHeight, Dpr = value.Dpr, Fps = value.Fps, BitrateKbps = value.BitrateKbps,
        Codec = value.Codec, FrameSection = value.FrameSection, FrameSectionBytes = value.FrameSectionBytes,
    };
}
