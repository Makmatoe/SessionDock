using System.Globalization;
using System.Windows;
using SessionDock.Services;
using Velopack;
using Velopack.Locators;

namespace SessionDock;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
#if SESSIONDOCK_SMOKE_HARNESS
        if (!RuntimeSmokeTestOptions.TryParse(
                args,
                out var runtimeSmokeTest,
                out _))
        {
            Environment.ExitCode = 2;
            return;
        }
#endif
        string? externalLink = null;
        var externalCommandValid = true;
#if SESSIONDOCK_SMOKE_HARNESS
        if (runtimeSmokeTest is null)
        {
#endif
        externalCommandValid = ExternalLaunchCommandLine.TryParse(
            args,
            out externalLink,
            out _);
#if SESSIONDOCK_SMOKE_HARNESS
        }
#endif
        if (!externalCommandValid)
        {
            MessageBox.Show(
                StartupLocalization.GetString("ExternalLink.ErrorDefault"),
                StartupLocalization.GetString("ExternalLink.RefusedTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Environment.ExitCode = 2;
            return;
        }
#if SESSIONDOCK_SMOKE_HARNESS
        var velopackArguments = runtimeSmokeTest is not null ||
                               externalLink is not null
            ? []
            : args;
#else
        var velopackArguments = externalLink is null ? args : [];
#endif
        VelopackApp.Build()
            .SetArgs(velopackArguments)
            .SetAutoApplyOnStartup(false)
            .Run();
        AppDataPaths.ConfigureProtectedInstallRoot(
            VelopackLocator.Current.RootAppDir);

#if SESSIONDOCK_SMOKE_HARNESS
        if (runtimeSmokeTest is not null)
        {
            try
            {
                AppDataPaths.ConfigureIsolatedRuntimeRoot(
                    runtimeSmokeTest.RootDirectory);
            }
            catch (Exception exception) when (
                LocalDataException.IsExpectedPersistenceFailure(exception) ||
                exception is ArgumentException)
            {
                Environment.ExitCode = 2;
                return;
            }
        }
#endif

#if SESSIONDOCK_SMOKE_HARNESS
        var requiresProductionSecurityContext = runtimeSmokeTest is null;
#else
        var requiresProductionSecurityContext =
            ProductionRuntimeAdmissionPolicy.RequiresAdmission(args);
#endif
        if (requiresProductionSecurityContext &&
            !RuntimeSecurityPolicy.IsCurrentProcessSupported(out _))
        {
            MessageBox.Show(
                StartupLocalization.GetString(
                    "Startup.SecurityContextFailureDetail"),
                StartupLocalization.GetString("Startup.CannotStartTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Environment.ExitCode = 1;
            return;
        }

#if SESSIONDOCK_SMOKE_HARNESS
        var application = runtimeSmokeTest is null
            ? new App(externalLink)
            : new App(runtimeSmokeTest);
#else
        var application = new App(externalLink);
#endif
        externalLink = null;
        Array.Clear(args);
        application.InitializeComponent();
        Environment.ExitCode = application.Run();
    }
}

internal static class StartupLocalization
{
    private static readonly string[] ResourceKeys =
    [
        "ExternalLink.ErrorDefault",
        "ExternalLink.RefusedTitle",
        "Startup.CannotStartTitle",
        "Startup.SecurityContextFailureDetail",
        "Startup.ForwardLinkTitle",
        "Startup.ForwardLinkDetail",
        "Startup.LocalDataFailureDetail"
    ];
    private static readonly Lazy<LocalizedTextSnapshot> Strings = new(() =>
        LocalizedTextSnapshot.FromResources(
            CultureInfo.CurrentUICulture,
            ResourceKeys));

    internal static string GetString(string key) =>
        Strings.Value.GetString(key);
}

internal static class ProductionRuntimeAdmissionPolicy
{
    internal static bool RequiresAdmission(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return true;
    }
}
