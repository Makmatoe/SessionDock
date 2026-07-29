using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;

namespace SessionDock.Services;

internal enum DiagnosticDependencyState
{
    Available,
    MissingOrUnverified,
    InspectionUnavailable
}

internal enum DiagnosticTheme
{
    Dark,
    Light,
    WindowsHighContrast
}

internal sealed record SupportDiagnosticsContext(
    Version? SessionDockVersion,
    bool CanSelfUpdate,
    int AccountCount,
    int RecentCount,
    int FavoriteCount,
    int TrackedRunningClientCount,
    DiagnosticTheme Theme,
    bool UiSoundsEnabled);

internal sealed record SupportDiagnosticsSnapshot(
    Version? SessionDockVersion,
    Version OperatingSystemVersion,
    Version RuntimeVersion,
    Architecture OperatingSystemArchitecture,
    Architecture ProcessArchitecture,
    DiagnosticDependencyState WebView2State,
    Version? WebView2Version,
    DiagnosticDependencyState RobloxPlayerState,
    bool CanSelfUpdate,
    int AccountCount,
    int RecentCount,
    int FavoriteCount,
    int TrackedRunningClientCount,
    DiagnosticTheme Theme,
    bool UiSoundsEnabled);

internal sealed class SupportDiagnosticsDocument
{
    private SupportDiagnosticsDocument(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > SupportDiagnosticsService.MaximumReportLength ||
            text.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The support diagnostics document is invalid.",
                nameof(text));
        }

        Text = text;
    }

    public string Text { get; }

    internal static SupportDiagnosticsDocument Create(
        SupportDiagnosticsSnapshot snapshot) =>
        new(SupportDiagnosticsService.BuildReportText(snapshot));
}

internal static partial class SupportDiagnosticsService
{
    internal const int MaximumReportLength = 8 * 1024;
    internal const int MaximumDisplayedCount = 10_000;

    public static SupportDiagnosticsSnapshot Capture(
        SupportDiagnosticsContext context,
        Func<string?> trustedRobloxPlayerPathProbe) =>
        Capture(
            context,
            trustedRobloxPlayerPathProbe,
            GetWebView2Version);

    internal static SupportDiagnosticsSnapshot Capture(
        SupportDiagnosticsContext context,
        Func<string?> trustedRobloxPlayerPathProbe,
        Func<string?> webView2VersionProbe)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(trustedRobloxPlayerPathProbe);
        ArgumentNullException.ThrowIfNull(webView2VersionProbe);

        var robloxState = InspectRobloxPlayer(trustedRobloxPlayerPathProbe);
        var (webView2State, webView2Version) = InspectWebView2(
            webView2VersionProbe);

        return new SupportDiagnosticsSnapshot(
            context.SessionDockVersion,
            Environment.OSVersion.Version,
            Environment.Version,
            RuntimeInformation.OSArchitecture,
            RuntimeInformation.ProcessArchitecture,
            webView2State,
            webView2Version,
            robloxState,
            context.CanSelfUpdate,
            context.AccountCount,
            context.RecentCount,
            context.FavoriteCount,
            context.TrackedRunningClientCount,
            context.Theme,
            context.UiSoundsEnabled);
    }

    public static SupportDiagnosticsDocument BuildDocument(
        SupportDiagnosticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return SupportDiagnosticsDocument.Create(snapshot);
    }

    internal static string BuildReportText(
        SupportDiagnosticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var report = string.Join(
            "\n",
            "SessionDock support diagnostics",
            "Privacy-safe summary; review before sharing.",
            string.Empty,
            "Application",
            $"- Version: {FormatApplicationVersion(snapshot.SessionDockVersion)}",
            $"- Install/update mode: {(snapshot.CanSelfUpdate ? "Installed; in-app updates available" : "Portable or development copy; in-app updates unavailable")}",
            string.Empty,
            "System",
            $"- Windows version: {FormatVersion(snapshot.OperatingSystemVersion)}",
            $"- .NET runtime: {FormatVersion(snapshot.RuntimeVersion)}",
            $"- OS architecture: {FormatArchitecture(snapshot.OperatingSystemArchitecture)}",
            $"- Process architecture: {FormatArchitecture(snapshot.ProcessArchitecture)}",
            string.Empty,
            "Required components",
            $"- Microsoft Edge WebView2 Runtime: {FormatWebView2(snapshot)}",
            $"- Roblox Player: {FormatRobloxPlayer(snapshot.RobloxPlayerState)}",
            string.Empty,
            "Local state (counts only)",
            $"- Saved accounts: {FormatCount(snapshot.AccountCount)}",
            $"- Recent entries: {FormatCount(snapshot.RecentCount)}",
            $"- Favorites: {FormatCount(snapshot.FavoriteCount)}",
            $"- Roblox clients tracked this run: {FormatCount(snapshot.TrackedRunningClientCount)}",
            string.Empty,
            "Preferences",
            $"- Theme: {FormatTheme(snapshot.Theme)}",
            $"- Interface sounds: {(snapshot.UiSoundsEnabled ? "On" : "Off")}",
            string.Empty,
            "Excluded by design",
            "- User and computer names; account names, labels, IDs, and keys",
            "- Local paths; destinations; place, server, and private-server details",
            "- Browser profiles, cookies, tokens, URLs, configuration files, logs, and exception details",
            string.Empty);

        if (report.Length > MaximumReportLength)
        {
            throw new InvalidOperationException(
                "The bounded diagnostics report exceeded its maximum size.");
        }

        return report;
    }

    private static DiagnosticDependencyState InspectRobloxPlayer(
        Func<string?> trustedRobloxPlayerPathProbe)
    {
        try
        {
            // The path is intentionally reduced to a boolean inside this method.
            // It is never retained in the diagnostics model.
            return string.IsNullOrWhiteSpace(trustedRobloxPlayerPathProbe())
                ? DiagnosticDependencyState.MissingOrUnverified
                : DiagnosticDependencyState.Available;
        }
        catch
        {
            return DiagnosticDependencyState.InspectionUnavailable;
        }
    }

    private static (DiagnosticDependencyState State, Version? Version)
        InspectWebView2(Func<string?> webView2VersionProbe)
    {
        try
        {
            var rawVersion = webView2VersionProbe();
            if (string.IsNullOrWhiteSpace(rawVersion))
            {
                return (
                    DiagnosticDependencyState.MissingOrUnverified,
                    null);
            }

            var match = SafeWebView2VersionPattern().Match(rawVersion.Trim());
            return match.Success && Version.TryParse(
                    match.Groups["version"].Value,
                    out var version)
                ? (DiagnosticDependencyState.Available, version)
                : (DiagnosticDependencyState.InspectionUnavailable, null);
        }
        catch
        {
            return (
                DiagnosticDependencyState.InspectionUnavailable,
                null);
        }
    }

    private static string? GetWebView2Version()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return null;
        }
    }

    private static string FormatWebView2(SupportDiagnosticsSnapshot snapshot)
    {
        return snapshot.WebView2State switch
        {
            DiagnosticDependencyState.Available
                when snapshot.WebView2Version is not null =>
                $"Available (version {FormatVersion(snapshot.WebView2Version)})",
            DiagnosticDependencyState.Available => "Available",
            DiagnosticDependencyState.MissingOrUnverified =>
                "Not found",
            _ => "Could not be inspected"
        };
    }

    private static string FormatRobloxPlayer(
        DiagnosticDependencyState state) =>
        state switch
        {
            DiagnosticDependencyState.Available =>
                "Available and Windows-verified",
            DiagnosticDependencyState.MissingOrUnverified =>
                "Not found or could not be verified",
            _ => "Could not be inspected"
        };

    private static string FormatTheme(DiagnosticTheme theme) =>
        theme switch
        {
            DiagnosticTheme.Light => "Light",
            DiagnosticTheme.WindowsHighContrast => "Windows high contrast",
            _ => "Dark"
        };

    private static string FormatArchitecture(Architecture architecture) =>
        architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "Arm64",
            Architecture.Arm => "Arm",
            _ => "Unknown"
        };

    private static string FormatVersion(Version? version)
    {
        if (version is null)
            return "Unknown";

        var fieldCount = version.Build >= 0
            ? version.Revision >= 0 ? 4 : 3
            : 2;
        return version.ToString(fieldCount);
    }

    private static string FormatApplicationVersion(Version? version)
    {
        if (version is null)
            return "Unknown";
        if (version.Build >= 0)
            return version.ToString(3);
        return version.ToString(2);
    }

    private static string FormatCount(int count)
    {
        if (count <= 0)
            return "0";
        if (count >= MaximumDisplayedCount)
        {
            return MaximumDisplayedCount.ToString(
                    "N0",
                    CultureInfo.InvariantCulture) +
                " or more";
        }

        return count.ToString(CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(
        "^(?<version>[0-9]{1,6}(?:\\.[0-9]{1,6}){1,4})(?: (?:stable|beta|dev|canary))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SafeWebView2VersionPattern();
}

internal static class SupportDiagnosticsExporter
{
    public const string SuggestedFileName =
        "SessionDock-support-diagnostics.txt";
    private static readonly UTF8Encoding Utf8WithoutByteOrderMark =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static async Task ExportAsync(
        string destinationPath,
        SupportDiagnosticsDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(document);
        if (!Path.GetExtension(destinationPath).Equals(
                ".txt",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Support diagnostics must be exported as a text file.",
                nameof(destinationPath));
        }

        var encodedLength = Utf8WithoutByteOrderMark.GetByteCount(document.Text);
        if (encodedLength > SupportDiagnosticsService.MaximumReportLength * 2)
        {
            throw new ArgumentException(
                "The support diagnostics document is too large.",
                nameof(document));
        }

        await File.WriteAllTextAsync(
            destinationPath,
            document.Text,
            Utf8WithoutByteOrderMark,
            cancellationToken);
    }
}
