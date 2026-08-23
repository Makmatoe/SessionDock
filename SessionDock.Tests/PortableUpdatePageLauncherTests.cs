using System.ComponentModel;
using System.Diagnostics;
using System.Xml.Linq;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class PortableUpdatePageLauncherTests
{
    [Fact]
    public void CreateStartInfo_UsesOnlyCanonicalLatestReleasePage()
    {
        var startInfo = PortableUpdatePageLauncher.CreateStartInfo();
        var uri = new Uri(startInfo.FileName);

        Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
        Assert.Equal("github.com", uri.Host);
        Assert.Equal(
            "/Makmatoe/SessionDock/releases/latest",
            uri.AbsolutePath);
        Assert.True(uri.IsDefaultPort);
        Assert.Empty(uri.Query);
        Assert.Empty(uri.Fragment);
        Assert.True(startInfo.UseShellExecute);
        Assert.Empty(startInfo.Arguments);
        Assert.Empty(startInfo.Verb);
    }

    [Fact]
    public void TryOpen_UsesConfiguredShellStart()
    {
        ProcessStartInfo? observed = null;
        var process = new Process();

        var opened = PortableUpdatePageLauncher.TryOpen(startInfo =>
        {
            observed = startInfo;
            return process;
        });

        Assert.True(opened);
        Assert.NotNull(observed);
        Assert.Equal(
            PortableUpdatePageLauncher.LatestReleaseUrl,
            observed.FileName);
        Assert.True(observed.UseShellExecute);
    }

    [Theory]
    [InlineData(typeof(Win32Exception))]
    [InlineData(typeof(InvalidOperationException))]
    public void TryOpen_ExpectedBrowserFailureReturnsFalse(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        var opened = PortableUpdatePageLauncher.TryOpen(_ => throw exception);

        Assert.False(opened);
    }

    [Fact]
    public void TryOpen_UnexpectedProgrammerFailureIsNotHidden()
    {
        Assert.Throws<ArgumentException>(() =>
            PortableUpdatePageLauncher.TryOpen(
                _ => throw new ArgumentException("programmer fault")));
    }

    [Fact]
    public void MainWindow_KeepsInstalledFlowAndPresentsPortableActionHonestly()
    {
        var root = FindRepositoryRoot();
        var updateSource = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.Updates.cs"));
        var localizationSource = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.Localization.cs"));
        var mainWindow = XDocument.Load(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.xaml"));
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml =
            "http://schemas.microsoft.com/winfx/2006/xaml";

        Assert.Contains(
            "if (!_updateService.CanSelfUpdate)",
            updateSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "PortableUpdatePageLauncher.TryOpen()",
            updateSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "_updateService.CheckAsync(",
            updateSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "_updateService.ApplyAfterExitAsync(",
            updateSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "Main.PortableUpdateName",
            localizationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "SettingsHub.PortableUpdatesAction",
            localizationSource,
            StringComparison.Ordinal);
        Assert.Contains(
            mainWindow.Descendants(presentation + "TextBlock"),
            element =>
                (string?)element.Attribute(xaml + "Name") ==
                "SettingsUpdatesDetailText");
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[]
                 {
                     Environment.CurrentDirectory,
                     AppContext.BaseDirectory
                 })
        {
            for (var directory = new DirectoryInfo(start);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(
                        directory.FullName,
                        "SessionDock.slnx")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException(
            "The SessionDock repository root could not be located.");
    }
}
