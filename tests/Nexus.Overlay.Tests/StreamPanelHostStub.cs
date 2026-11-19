using Nexus.Overlay;

namespace Nexus.Overlay.Capture;

// The source-linked manager's native factory sees only this synthetic host in
// pure tests. No HWND, capture API, normal manager or device discovery is loaded.
internal sealed class StreamPanelHost : IStreamHost
{
    internal static List<StreamPanelHost> Created { get; } = [];
    internal TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool Disposed;
    internal bool ThrowOnStart;
    internal bool ThrowOnDispose;
    internal bool FaultOnStart;
    public string SessionId { get; }
    public string PanelDeviceId { get; }
    public Task Completion => Finish.Task;
    public Action? Faulted { get; set; }
    public StreamPanelHost(StreamAssignment assignment, string origin, string token)
    {
        SessionId = assignment.SessionId;
        PanelDeviceId = assignment.PanelDeviceId;
        Created.Add(this);
    }
    public void Start()
    {
        if (ThrowOnStart) throw new InvalidOperationException("synthetic start failure");
        if (FaultOnStart) Faulted?.Invoke();
    }
    public void Dispose()
    {
        Disposed = true;
        if (ThrowOnDispose) throw new InvalidOperationException("synthetic teardown failure");
    }
}
