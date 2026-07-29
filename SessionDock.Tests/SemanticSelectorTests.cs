using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Xml.Linq;

namespace SessionDock.Tests;

public sealed class SemanticSelectorTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void MainWindow_SegmentedSelectorsAreExclusiveRadioGroups()
    {
        var document = LoadMainWindow();
        var expected = new[]
        {
            new SelectorExpectation(
                "LaunchTabButton",
                "MainWorkspaceTabs",
                IsChecked: true),
            new SelectorExpectation(
                "RecentTabButton",
                "MainWorkspaceTabs",
                IsChecked: false),
            new SelectorExpectation(
                "ExperienceDestinationModeButton",
                "DestinationMode",
                IsChecked: true),
            new SelectorExpectation(
                "UserDestinationModeButton",
                "DestinationMode",
                IsChecked: false),
            new SelectorExpectation(
                "AllTypeFilterButton",
                "RecentTypeFilter",
                IsChecked: true),
            new SelectorExpectation(
                "PublicFilterButton",
                "RecentTypeFilter",
                IsChecked: false),
            new SelectorExpectation(
                "PrivateFilterButton",
                "RecentTypeFilter",
                IsChecked: false)
        };

        foreach (var expectation in expected)
        {
            var selector = FindNamedElement(document, expectation.Name);
            Assert.Equal(Presentation + "RadioButton", selector.Name);
            Assert.Equal(
                expectation.GroupName,
                (string?)selector.Attribute("GroupName"));
            Assert.Equal(
                expectation.IsChecked.ToString(),
                (string?)selector.Attribute("IsChecked"));
            Assert.Equal(
                "{StaticResource SegmentRadioButton}",
                (string?)selector.Attribute("Style"));
            Assert.Null(selector.Attribute("Click"));
            Assert.Null(selector.Attribute("AutomationProperties.ItemStatus"));

            var group = Assert.IsType<XElement>(selector.Parent);
            Assert.Equal(
                "Cycle",
                (string?)group.Attribute(
                    "KeyboardNavigation.DirectionalNavigation"));
            Assert.Equal(
                "Once",
                (string?)group.Attribute("KeyboardNavigation.TabNavigation"));
        }

        Assert.Equal(
            1,
            expected.Count(item =>
                item.GroupName == "MainWorkspaceTabs" && item.IsChecked));
        Assert.Equal(
            1,
            expected.Count(item =>
                item.GroupName == "DestinationMode" && item.IsChecked));
        Assert.Equal(
            1,
            expected.Count(item =>
                item.GroupName == "RecentTypeFilter" && item.IsChecked));
    }

    [Fact]
    public void SegmentStyle_ShowsCheckedHoverAndKeyboardFocusStates()
    {
        var document = LoadMainWindow();
        var style = document
            .Descendants(Presentation + "Style")
            .Single(element =>
                (string?)element.Attribute(Xaml + "Key") ==
                "SegmentRadioButton");

        Assert.Equal("RadioButton", (string?)style.Attribute("TargetType"));
        var source = style.ToString(SaveOptions.DisableFormatting);
        Assert.Contains("Property=\"IsChecked\"", source, StringComparison.Ordinal);
        Assert.Contains("Property=\"IsMouseOver\"", source, StringComparison.Ordinal);
        Assert.Contains("Property=\"IsKeyboardFocused\"", source, StringComparison.Ordinal);
        Assert.Contains("SelectedControlSurfaceBrush", source, StringComparison.Ordinal);
        Assert.Contains("FocusBrush", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RadioButtonPeer_ProvidesExclusiveSelectionItemSemantics()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Window? window = null;
            try
            {
                var group = new StackPanel();
                var first = new RadioButton { GroupName = "TestGroup" };
                var second = new RadioButton { GroupName = "TestGroup" };
                group.Children.Add(first);
                group.Children.Add(second);

                second.IsChecked = true;
                var checkedEvents = 0;
                var clickEvents = 0;
                first.Checked += (_, _) => checkedEvents++;
                first.Click += (_, _) => clickEvents++;
                window = new Window
                {
                    Content = group,
                    Width = 100,
                    Height = 100,
                    Left = -10_000,
                    Top = -10_000,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None
                };
                window.Show();
                Dispatcher.CurrentDispatcher.Invoke(
                    DispatcherPriority.ApplicationIdle,
                    static () => { });

                var peer = new RadioButtonAutomationPeer(first);
                var provider = Assert.IsAssignableFrom<ISelectionItemProvider>(
                    peer.GetPattern(PatternInterface.SelectionItem));
                provider.Select();

                Assert.True(first.IsChecked == true);
                Assert.False(second.IsChecked == true);
                Assert.True(provider.IsSelected);
                Assert.Equal(1, checkedEvents);
                Assert.Equal(0, clickEvents);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                window?.Close();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(
            thread.Join(TimeSpan.FromSeconds(10)),
            "The STA automation check did not complete.");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void MainWindow_CheckedEventsRouteEverySemanticSelection()
    {
        var root = FindRepositoryRoot();
        var mainSource = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.xaml.cs"));
        var recentSource = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.Recent.cs"));
        var appSource = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "App.xaml.cs"));

        Assert.Contains(
            "AttachSemanticSelectorHandlers();",
            mainSource,
            StringComparison.Ordinal);
        foreach (var selector in new[]
                 {
                     "LaunchTabButton",
                     "RecentTabButton",
                     "ExperienceDestinationModeButton",
                     "UserDestinationModeButton",
                     "AllTypeFilterButton",
                     "PublicFilterButton",
                     "PrivateFilterButton"
                 })
        {
            Assert.Contains(
                $"{selector}.Checked +=",
                mainSource,
                StringComparison.Ordinal);
        }

        Assert.DoesNotContain(
            "LaunchTabButton_Click",
            recentSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PublicFilterButton_Click",
            recentSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "VerifySemanticSelectorsForRuntimeSmoke();",
            appSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_SmallTextAndAccountActionsMeetPolishFloor()
    {
        var document = LoadMainWindow();
        var undersizedText = document
            .Descendants()
            .Select(element => new
            {
                Element = element,
                FontSize = (string?)element.Attribute("FontSize")
            })
            .Where(item =>
                double.TryParse(
                    item.FontSize,
                    System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var fontSize) &&
                fontSize < 11)
            .ToList();
        Assert.Empty(undersizedText);

        foreach (var name in new[]
                 {
                     "AddAccountButton",
                     "EditAccountButton",
                     "ResetButton"
                 })
        {
            var button = FindNamedElement(document, name);
            Assert.Equal("40", (string?)button.Attribute("Width"));
            Assert.Equal("40", (string?)button.Attribute("Height"));
        }

        foreach (var fileName in new[]
                 {
                     "MainWindow.xaml.cs",
                     "MainWindow.Recent.cs"
                 })
        {
            var source = File.ReadAllText(Path.Combine(
                FindRepositoryRoot(),
                "SessionDock",
                fileName));
            var undersizedGeneratedText = Regex.Matches(
                    source,
                    @"FontSize\s*=\s*(?<size>\d+(?:\.\d+)?)",
                    RegexOptions.CultureInvariant)
                .Select(match => double.Parse(
                    match.Groups["size"].Value,
                    System.Globalization.CultureInfo.InvariantCulture))
                .Where(fontSize => fontSize < 11)
                .ToList();
            Assert.Empty(undersizedGeneratedText);
        }
    }

    [Fact]
    public void LocalBadge_DescribesLocalDataInEveryLanguage()
    {
        var root = FindRepositoryRoot();

        Assert.Equal(
            "LOCAL DATA",
            ReadLocalizedString(root, "Strings.en-US.xaml", "Main.Local"));
        Assert.Equal(
            "LOKALE DATA",
            ReadLocalizedString(root, "Strings.nl-NL.xaml", "Main.Local"));
    }

    private static XDocument LoadMainWindow() => XDocument.Load(Path.Combine(
        FindRepositoryRoot(),
        "SessionDock",
        "MainWindow.xaml"));

    private static XElement FindNamedElement(
        XContainer document,
        string name) => document
        .Descendants()
        .Single(element => (string?)element.Attribute(Xaml + "Name") == name);

    private static string ReadLocalizedString(
        string root,
        string fileName,
        string key)
    {
        var document = XDocument.Load(Path.Combine(
            root,
            "SessionDock",
            "Localization",
            fileName));
        return document
            .Descendants()
            .Single(element => (string?)element.Attribute(Xaml + "Key") == key)
            .Value;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "SessionDock.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }

    private sealed record SelectorExpectation(
        string Name,
        string GroupName,
        bool IsChecked);
}
