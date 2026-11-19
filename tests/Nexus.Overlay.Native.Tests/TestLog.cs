using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Nexus.Overlay;

internal static class Log
{
    internal static Action<string>? WarnObserver;
    internal static void Info(string message) { }
    internal static void Warn(string message) => WarnObserver?.Invoke(message);
    internal static void Error(string message) { }
    internal static void Rotate() { }
}

internal static class Program
{
    internal static void OnDashboardClosed() { }
    internal static void OnWebView2RuntimeInstalled(bool reopenDashboard) { }
    internal static void OnWebView2RuntimePromptClosed() { }
}
