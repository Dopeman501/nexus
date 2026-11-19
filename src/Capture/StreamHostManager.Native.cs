namespace Nexus.Overlay.Capture;

internal sealed partial class StreamHostManager
{
    public StreamHostManager(string serviceOrigin) : this(serviceOrigin,
        (assignment, origin, token) => new StreamPanelHost(assignment, origin, token)) { }
}
