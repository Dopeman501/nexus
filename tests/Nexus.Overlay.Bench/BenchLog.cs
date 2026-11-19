namespace Nexus.Overlay;
internal static class Log
{
    public static void Info(string text) => Console.Error.WriteLine(text);
    public static void Warn(string text) => Console.Error.WriteLine(text);
    public static void Error(string text) => Console.Error.WriteLine(text);
    public static void Rotate() { }
}
internal static class Program
{
    internal static void OnDashboardClosed() { }
    internal static void OnWebView2RuntimeInstalled(bool value) { }
    internal static void OnWebView2RuntimePromptClosed() { }
}
