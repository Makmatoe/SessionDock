using Microsoft.Web.WebView2.Wpf;

namespace SessionDock.Services;

internal static class WebSessionException
{
    internal const string OfficialWebView2DownloadUrl =
        "https://developer.microsoft.com/en-us/microsoft-edge/webview2/consumer/";

    internal static bool IsExpectedLifecycleFailure(Exception exception) =>
        exception is WebSessionUnavailableException;

    internal static bool HasActionableRuntimeRecovery(
        WebSessionUnavailableReason reason) =>
        reason is WebSessionUnavailableReason.MissingRuntime or
            WebSessionUnavailableReason.RuntimeStartFailed;

    internal static string GetLocalizationKey(
        WebSessionUnavailableReason reason) =>
        reason switch
        {
            WebSessionUnavailableReason.MissingRuntime =>
                "WebSession.Error.MissingRuntime",
            WebSessionUnavailableReason.RuntimeStartFailed =>
                "WebSession.Error.RuntimeStartFailed",
            WebSessionUnavailableReason.ProcessExited =>
                "WebSession.Error.ProcessExited",
            WebSessionUnavailableReason.Superseded =>
                "WebSession.Error.Superseded",
            WebSessionUnavailableReason.Closed =>
                "WebSession.Error.Closed",
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        };
}

internal enum WebSessionUnavailableReason
{
    MissingRuntime,
    RuntimeStartFailed,
    ProcessExited,
    Superseded,
    Closed
}

internal sealed class WebSessionUnavailableException : Exception
{
    internal WebSessionUnavailableException(
        WebSessionUnavailableReason reason,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
    }

    internal WebSessionUnavailableReason Reason { get; }
}

internal readonly record struct WebSessionToken(
    int Generation,
    string AccountKey);

internal sealed record WebSessionBrowser(
    WebView2 Browser,
    WebSessionToken Token);

internal sealed class WebSessionEventArgs(WebSessionToken token) : EventArgs
{
    internal WebSessionToken Token { get; } = token;
}

internal sealed class WebSessionUnavailableEventArgs(
    WebSessionToken token,
    WebSessionUnavailableReason reason) : EventArgs
{
    internal WebSessionToken Token { get; } = token;
    internal WebSessionUnavailableReason Reason { get; } = reason;
}
