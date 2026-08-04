namespace SessionDock.Tests;

public sealed class HomeSetupRoutingStructureTests
{
    [Fact]
    public void ManagedAccountBrowserFlows_ReturnToTheFocusedAccountsPage()
    {
        var root = FindRepositoryRoot();
        var setupSource = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.Setup.cs"));
        var mainSource = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.xaml.cs"));

        Assert.Contains(
            "_returnToAccountsAfterBrowser = true;",
            setupSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReturnToAccountsAfterBrowserIfRequested",
            setupSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "NavigateToWorkspace(MainWorkspacePage.Accounts, resizeWindow: true);",
            setupSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReturnToAccountsAfterBrowserIfRequested(_activeProfile.Key);",
            mainSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReturnToAccountsAfterBrowserIfRequested(nextProfile?.Key);",
            mainSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Closing_DirtyDestinationEditorResolvesBeforeShutdownStarts()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "MainWindow.xaml.cs"));
        var handlerStart = source.IndexOf(
            "private async void MainWindow_Closing",
            StringComparison.Ordinal);
        var handlerEnd = source.IndexOf(
            "private static void DisarmWatchdogOnApplicationExit",
            handlerStart,
            StringComparison.Ordinal);
        Assert.True(handlerStart >= 0 && handlerEnd > handlerStart);
        var handler = source[handlerStart..handlerEnd];

        var dirtyGuard = handler.IndexOf(
            "HasDestinationEditorChanges()",
            StringComparison.Ordinal);
        var resolution = handler.IndexOf(
            "await TryResolveDestinationEditorChangesAsync()",
            StringComparison.Ordinal);
        var shutdown = handler.IndexOf(
            "_operationLifetime.BeginShutdown()",
            StringComparison.Ordinal);
        Assert.True(dirtyGuard >= 0);
        Assert.True(resolution > dirtyGuard);
        Assert.True(shutdown > resolution);
        Assert.Contains("e.Cancel = true;", handler, StringComparison.Ordinal);
        Assert.Contains(
            "_destinationCloseRequested = true;",
            handler,
            StringComparison.Ordinal);

        var setupSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "MainWindow.Setup.cs"));
        Assert.Contains(
            "_destinationEditorResolutionTask ??=",
            setupSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "_destinationCloseRequested",
            setupSource,
            StringComparison.Ordinal);
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
