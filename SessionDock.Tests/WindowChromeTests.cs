using System.Xml.Linq;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class WindowChromeTests
{
    [Fact]
    public void MainWindow_ConnectsAccountDragDropSurfaceAndIndicator()
    {
        var document = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "MainWindow.xaml"));
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml =
            "http://schemas.microsoft.com/winfx/2006/xaml";

        var scrollViewer = document.Descendants(presentation + "ScrollViewer")
            .Single(element =>
                (string?)element.Attribute(xaml + "Name") ==
                "AccountsScrollViewer");
        Assert.Equal("True", (string?)scrollViewer.Attribute("AllowDrop"));
        Assert.Equal(
            "AccountsScrollViewer_PreviewDragOver",
            (string?)scrollViewer.Attribute("PreviewDragOver"));
        Assert.Equal(
            "AccountsScrollViewer_PreviewDrop",
            (string?)scrollViewer.Attribute("PreviewDrop"));
        Assert.Equal(
            "AccountsScrollViewer_DragLeave",
            (string?)scrollViewer.Attribute("DragLeave"));

        var indicator = document.Descendants(presentation + "Border")
            .Single(element =>
                (string?)element.Attribute(xaml + "Name") ==
                "AccountDropIndicator");
        Assert.Equal(
            "False",
            (string?)indicator.Attribute("IsHitTestVisible"));
    }

    [Fact]
    public void MainWindow_IntegratesNativeCaptionIntoApplicationHeader()
    {
        var document = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "MainWindow.xaml"));
        var root = document.Root!;
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace shell =
            "clr-namespace:System.Windows.Shell;assembly=PresentationFramework";
        XNamespace xaml =
            "http://schemas.microsoft.com/winfx/2006/xaml";

        Assert.Equal("None", (string?)root.Attribute("WindowStyle"));
        Assert.Equal("False", (string?)root.Attribute("AllowsTransparency"));

        var chrome = root
            .Element(shell + "WindowChrome.WindowChrome")?
            .Element(shell + "WindowChrome");
        Assert.NotNull(chrome);
        Assert.Equal("64", (string?)chrome.Attribute("CaptionHeight"));
        Assert.Equal("6", (string?)chrome.Attribute("ResizeBorderThickness"));
        Assert.Equal("0", (string?)chrome.Attribute("GlassFrameThickness"));
        Assert.Equal("False", (string?)chrome.Attribute("UseAeroCaptionButtons"));

        var rootGrid = root.Element(presentation + "Grid");
        var advancedWorkspace = rootGrid?
            .Elements(presentation + "Grid")
            .Single(element =>
                (string?)element.Attribute(xaml + "Name") ==
                "AdvancedWorkspace");
        var workspace = advancedWorkspace?
            .Elements(presentation + "Grid")
            .Single(element => (string?)element.Attribute("Grid.Column") == "1");
        var headerRow = workspace?
            .Element(presentation + "Grid.RowDefinitions")?
            .Elements(presentation + "RowDefinition")
            .First();
        Assert.Equal("64", (string?)headerRow?.Attribute("Height"));

        var captionControls = root.Descendants()
            .Single(element =>
                element.Name.LocalName == "WindowCaptionControls" &&
                (string?)element.Attribute(xaml + "Name") ==
                "CaptionControls");
        Assert.Equal(
            "CaptionControls",
            (string?)captionControls.Attribute(xaml + "Name"));

        foreach (var buttonName in new[]
                 {
                     "ReleaseNotesButton",
                     "InstallUpdateButton"
                 })
        {
            var button = root.Descendants(presentation + "Button")
                .Single(element =>
                    (string?)element.Attribute(xaml + "Name") == buttonName);
            Assert.Equal(
                "True",
                (string?)button.Attribute(
                    shell + "WindowChrome.IsHitTestVisibleInChrome"));
        }
    }

    [Fact]
    public void ProductionWindows_DoNotUseLayeredTransparency()
    {
        var applicationDirectory = Path.Combine(
            FindRepositoryRoot(),
            "SessionDock");
        var violations = Directory.EnumerateFiles(
                applicationDirectory,
                "*.xaml",
                SearchOption.TopDirectoryOnly)
            .Select(XDocument.Load)
            .Where(document => document.Root?.Name.LocalName == "Window")
            .Where(document => string.Equals(
                (string?)document.Root!.Attribute("AllowsTransparency"),
                "True",
                StringComparison.OrdinalIgnoreCase))
            .Select(document =>
                (string?)document.Root!.Attribute(
                    XName.Get(
                        "Class",
                        "http://schemas.microsoft.com/winfx/2006/xaml")) ??
                "unknown window")
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void RuntimeSmoke_DoesNotOverrideProductionWindowChrome()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "App.xaml.cs"));

        Assert.DoesNotContain(
            "mainWindow.WindowStyle = WindowStyle.None",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CaptionButtons_PreserveNativeWindowCommandsAndSnapHitTesting()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "WindowCaptionControls.xaml.cs"));

        Assert.Contains("SystemCommands.MinimizeWindow", source);
        Assert.Contains("SystemCommands.MaximizeWindow", source);
        Assert.Contains("SystemCommands.RestoreWindow", source);
        Assert.Contains("SystemCommands.CloseWindow", source);
        Assert.Contains("HitMaximizeButton = 9", source);
        Assert.Contains("message != NonClientHitTestMessage", source);
        Assert.Contains("!MaximizeButton.IsVisible", source);
        Assert.Contains(
            "PresentationSource.FromVisual(MaximizeButton) is null",
            source);
        Assert.Contains("catch (InvalidOperationException)", source);
        Assert.DoesNotContain("Application.Current.Shutdown", source);
        Assert.DoesNotContain("Environment.Exit", source);

        var appSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "App.xaml.cs"));
        Assert.Contains(
            "VerifyWorkspaceCaptionControlsForRuntimeSmoke",
            appSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "HomeCaptionControls.CloseForRuntimeSmoke()",
            appSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_PreservesPerMonitorV2Awareness()
    {
        var manifest = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "app.manifest"));

        Assert.Contains("PerMonitorV2", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeColorRef_UsesWindowsBgrByteOrdering()
    {
        var color = System.Windows.Media.Color.FromRgb(0x12, 0x34, 0x56);

        Assert.Equal(0x563412, NativeWindowFrameService.ToColorRef(color));
    }

    [Theory]
    [InlineData(-1920, 0, 1920, 1040, -1700, 100, 1080, 720,
        -1700, 100, 1080, 720)]
    [InlineData(-1920, -200, 1920, 1080, -2200, -500, 2400, 1400,
        -1904, -184, 1888, 1048)]
    [InlineData(0, 0, 120, 90, 80, 70, 500, 400,
        16, 16, 88, 58)]
    public void CalculateFittedBounds_UsesTheSuppliedMonitorWorkArea(
        double workLeft,
        double workTop,
        double workWidth,
        double workHeight,
        double windowLeft,
        double windowTop,
        double windowWidth,
        double windowHeight,
        double expectedLeft,
        double expectedTop,
        double expectedWidth,
        double expectedHeight)
    {
        var result = WindowLayoutService.CalculateFittedBounds(
            new System.Windows.Rect(
                workLeft,
                workTop,
                workWidth,
                workHeight),
            new System.Windows.Rect(
                windowLeft,
                windowTop,
                windowWidth,
                windowHeight));

        Assert.Equal(expectedLeft, result.Left);
        Assert.Equal(expectedTop, result.Top);
        Assert.Equal(expectedWidth, result.Width);
        Assert.Equal(expectedHeight, result.Height);
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
