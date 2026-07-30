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
        SupportDiagnosticsSnapshot snapshot,
        LocalizedTextSnapshot localization) =>
        new(SupportDiagnosticsService.BuildReportText(
            snapshot,
            localization));
}

internal static partial class SupportDiagnosticsService
{
    internal const int MaximumReportLength = 8 * 1024;
    internal const int MaximumDisplayedCount = 10_000;
    private static readonly string[] ReportLocalizationKeys =
    [
        "Diagnostics.Report.Title",
        "Diagnostics.Report.Summary",
        "Diagnostics.Report.ApplicationHeading",
        "Diagnostics.Report.ApplicationVersion",
        "Diagnostics.Report.InstallMode",
        "Diagnostics.Report.InstallModeInstalled",
        "Diagnostics.Report.InstallModePortable",
        "Diagnostics.Report.SystemHeading",
        "Diagnostics.Report.WindowsVersion",
        "Diagnostics.Report.DotNetRuntime",
        "Diagnostics.Report.OsArchitecture",
        "Diagnostics.Report.ProcessArchitecture",
        "Diagnostics.Report.ComponentsHeading",
        "Diagnostics.Report.WebView2",
        "Diagnostics.Report.RobloxPlayer",
        "Diagnostics.Report.LocalStateHeading",
        "Diagnostics.Report.SavedAccounts",
        "Diagnostics.Report.RecentEntries",
        "Diagnostics.Report.Favorites",
        "Diagnostics.Report.TrackedClients",
        "Diagnostics.Report.PreferencesHeading",
        "Diagnostics.Report.Theme",
        "Diagnostics.Report.InterfaceSounds",
        "Diagnostics.Report.ExcludedHeading",
        "Diagnostics.Report.ExcludedIdentity",
        "Diagnostics.Report.ExcludedDestinations",
        "Diagnostics.Report.ExcludedBrowserData",
        "Diagnostics.Value.AvailableVersion",
        "Diagnostics.Value.Available",
        "Diagnostics.Value.NotFound",
        "Diagnostics.Value.InspectionUnavailable",
        "Diagnostics.Value.RobloxVerified",
        "Diagnostics.Value.RobloxNotFound",
        "Diagnostics.Value.ThemeLight",
        "Diagnostics.Value.ThemeHighContrast",
        "Diagnostics.Value.ThemeDark",
        "Diagnostics.Value.Unknown",
        "Diagnostics.Value.CountOrMore",
        "Diagnostics.Value.On",
        "Diagnostics.Value.Off"
    ];
    private static readonly Lazy<LocalizedTextSnapshot> EnglishLocalization =
        new(() => CreateLocalizationSnapshot(
            CultureInfo.GetCultureInfo(LocalizationPreference.English)));

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

        return BuildDocument(
            snapshot,
            EnglishLocalization.Value);
    }

    internal static SupportDiagnosticsDocument BuildDocument(
        SupportDiagnosticsSnapshot snapshot,
        AppLocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(localization);

        return BuildDocument(
            snapshot,
            LocalizedTextSnapshot.Capture(
                localization,
                ReportLocalizationKeys));
    }

    internal static LocalizedTextSnapshot CreateLocalizationSnapshot(
        CultureInfo culture) =>
        LocalizedTextSnapshot.FromResources(
            culture,
            ReportLocalizationKeys);

    internal static SupportDiagnosticsDocument BuildDocument(
        SupportDiagnosticsSnapshot snapshot,
        LocalizedTextSnapshot localization)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(localization);

        return SupportDiagnosticsDocument.Create(snapshot, localization);
    }

    internal static string BuildReportText(
        SupportDiagnosticsSnapshot snapshot,
        LocalizedTextSnapshot localization)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(localization);

        var report = string.Join(
            "\n",
            localization.GetString("Diagnostics.Report.Title"),
            localization.GetString("Diagnostics.Report.Summary"),
            string.Empty,
            localization.GetString("Diagnostics.Report.ApplicationHeading"),
            localization.Format(
                "Diagnostics.Report.ApplicationVersion",
                FormatApplicationVersion(
                    snapshot.SessionDockVersion,
                    localization)),
            localization.Format(
                "Diagnostics.Report.InstallMode",
                localization.GetString(
                    snapshot.CanSelfUpdate
                        ? "Diagnostics.Report.InstallModeInstalled"
                        : "Diagnostics.Report.InstallModePortable")),
            string.Empty,
            localization.GetString("Diagnostics.Report.SystemHeading"),
            localization.Format(
                "Diagnostics.Report.WindowsVersion",
                FormatVersion(snapshot.OperatingSystemVersion, localization)),
            localization.Format(
                "Diagnostics.Report.DotNetRuntime",
                FormatVersion(snapshot.RuntimeVersion, localization)),
            localization.Format(
                "Diagnostics.Report.OsArchitecture",
                FormatArchitecture(
                    snapshot.OperatingSystemArchitecture,
                    localization)),
            localization.Format(
                "Diagnostics.Report.ProcessArchitecture",
                FormatArchitecture(
                    snapshot.ProcessArchitecture,
                    localization)),
            string.Empty,
            localization.GetString("Diagnostics.Report.ComponentsHeading"),
            localization.Format(
                "Diagnostics.Report.WebView2",
                FormatWebView2(snapshot, localization)),
            localization.Format(
                "Diagnostics.Report.RobloxPlayer",
                FormatRobloxPlayer(
                    snapshot.RobloxPlayerState,
                    localization)),
            string.Empty,
            localization.GetString("Diagnostics.Report.LocalStateHeading"),
            localization.Format(
                "Diagnostics.Report.SavedAccounts",
                FormatCount(snapshot.AccountCount, localization)),
            localization.Format(
                "Diagnostics.Report.RecentEntries",
                FormatCount(snapshot.RecentCount, localization)),
            localization.Format(
                "Diagnostics.Report.Favorites",
                FormatCount(snapshot.FavoriteCount, localization)),
            localization.Format(
                "Diagnostics.Report.TrackedClients",
                FormatCount(
                    snapshot.TrackedRunningClientCount,
                    localization)),
            string.Empty,
            localization.GetString("Diagnostics.Report.PreferencesHeading"),
            localization.Format(
                "Diagnostics.Report.Theme",
                FormatTheme(snapshot.Theme, localization)),
            localization.Format(
                "Diagnostics.Report.InterfaceSounds",
                localization.GetString(
                    snapshot.UiSoundsEnabled
                        ? "Diagnostics.Value.On"
                        : "Diagnostics.Value.Off")),
            string.Empty,
            localization.GetString("Diagnostics.Report.ExcludedHeading"),
            localization.GetString("Diagnostics.Report.ExcludedIdentity"),
            localization.GetString("Diagnostics.Report.ExcludedDestinations"),
            localization.GetString("Diagnostics.Report.ExcludedBrowserData"),
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

    private static string FormatWebView2(
        SupportDiagnosticsSnapshot snapshot,
        LocalizedTextSnapshot localization)
    {
        return snapshot.WebView2State switch
        {
            DiagnosticDependencyState.Available
                when snapshot.WebView2Version is not null =>
                localization.Format(
                    "Diagnostics.Value.AvailableVersion",
                    FormatVersion(snapshot.WebView2Version, localization)),
            DiagnosticDependencyState.Available =>
                localization.GetString("Diagnostics.Value.Available"),
            DiagnosticDependencyState.MissingOrUnverified =>
                localization.GetString("Diagnostics.Value.NotFound"),
            _ => localization.GetString(
                "Diagnostics.Value.InspectionUnavailable")
        };
    }

    private static string FormatRobloxPlayer(
        DiagnosticDependencyState state,
        LocalizedTextSnapshot localization) =>
        state switch
        {
            DiagnosticDependencyState.Available =>
                localization.GetString("Diagnostics.Value.RobloxVerified"),
            DiagnosticDependencyState.MissingOrUnverified =>
                localization.GetString("Diagnostics.Value.RobloxNotFound"),
            _ => localization.GetString(
                "Diagnostics.Value.InspectionUnavailable")
        };

    private static string FormatTheme(
        DiagnosticTheme theme,
        LocalizedTextSnapshot localization) =>
        theme switch
        {
            DiagnosticTheme.Light =>
                localization.GetString("Diagnostics.Value.ThemeLight"),
            DiagnosticTheme.WindowsHighContrast =>
                localization.GetString(
                    "Diagnostics.Value.ThemeHighContrast"),
            _ => localization.GetString("Diagnostics.Value.ThemeDark")
        };

    private static string FormatArchitecture(
        Architecture architecture,
        LocalizedTextSnapshot localization) =>
        architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "Arm64",
            Architecture.Arm => "Arm",
            _ => localization.GetString("Diagnostics.Value.Unknown")
        };

    private static string FormatVersion(
        Version? version,
        LocalizedTextSnapshot localization)
    {
        if (version is null)
            return localization.GetString("Diagnostics.Value.Unknown");

        var fieldCount = version.Build >= 0
            ? version.Revision >= 0 ? 4 : 3
            : 2;
        return version.ToString(fieldCount);
    }

    private static string FormatApplicationVersion(
        Version? version,
        LocalizedTextSnapshot localization)
    {
        if (version is null)
            return localization.GetString("Diagnostics.Value.Unknown");
        if (version.Build >= 0)
            return version.ToString(3);
        return version.ToString(2);
    }

    private static string FormatCount(
        int count,
        LocalizedTextSnapshot localization)
    {
        if (count <= 0)
            return "0";
        if (count >= MaximumDisplayedCount)
        {
            return localization.Format(
                "Diagnostics.Value.CountOrMore",
                MaximumDisplayedCount.ToString(
                    "N0",
                    localization.Culture));
        }

        return count.ToString(localization.Culture);
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
