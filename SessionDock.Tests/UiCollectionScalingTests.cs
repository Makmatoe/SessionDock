using System.Xml.Linq;

namespace SessionDock.Tests;

public sealed class UiCollectionScalingTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(32)]
    [InlineData(100)]
    [InlineData(128)]
    public void TemplateEditor_StandardRowsReuseOneOptionCatalog(
        int clientCount)
    {
        IReadOnlyList<string> shared = Enumerable.Range(0, 256)
            .Select(index => $"option-{index}")
            .ToArray();

        var rowOptions = Enumerable.Range(0, clientCount)
            .Select(_ => TemplateEditorDialog.ReuseSharedOptionsOrAppend(
                shared,
                additionalOption: null))
            .ToArray();

        Assert.All(rowOptions, options => Assert.Same(shared, options));
        Assert.Single(rowOptions.Distinct(ReferenceEqualityComparer.Instance));

        var withUnavailable =
            TemplateEditorDialog.ReuseSharedOptionsOrAppend(
                shared,
                "unavailable");
        Assert.NotSame(shared, withUnavailable);
        Assert.Equal(shared.Count + 1, withUnavailable.Count);
        Assert.Equal(256, shared.Count);
    }

    [Fact]
    public void TemplateEditor_RowConstructionUsesSharedOptionCatalogs()
    {
        var source = File.ReadAllText(RepoFile(
            "SessionDock",
            "TemplateEditorDialog.xaml.cs"));
        var constructor = Slice(
            source,
            "internal TemplateEditorDialog(",
            "internal SessionTemplate? SavedTemplate");

        Assert.DoesNotContain("clientMacros.ToList()", constructor);
        Assert.Contains("sharedDestinationChoices", constructor);
        Assert.Contains("out var choices", constructor);
        Assert.Contains("out var destinationChoices", constructor);
        Assert.True(
            constructor.IndexOf(
                "var sharedDestinationChoices",
                StringComparison.Ordinal) <
            constructor.IndexOf("_clientRows = _clients", StringComparison.Ordinal));
    }

    [Fact]
    public void SharedListStyle_EnablesRecyclingVirtualization()
    {
        var document = XDocument.Load(RepoFile("SessionDock", "App.xaml"));
        var style = document.Descendants(Presentation + "Style")
            .Single(element =>
                (string?)element.Attribute("TargetType") == "ListBox" &&
                element.Attribute(Xaml + "Key") is null);

        AssertSetter(style, "ScrollViewer.CanContentScroll", "True");
        AssertSetter(style, "VirtualizingPanel.IsVirtualizing", "True");
        AssertSetter(
            style,
            "VirtualizingPanel.VirtualizationMode",
            "Recycling");
    }

    [Fact]
    public void PortableSelectionLists_UseBoundedVirtualizingHosts()
    {
        var document = XDocument.Load(RepoFile(
            "SessionDock",
            "PortableDataDialog.xaml"));
        var style = document.Descendants(Presentation + "Style")
            .Single(element =>
                (string?)element.Attribute(Xaml + "Key") ==
                "PortableSelectionItems");
        AssertSetter(style, "MaxHeight", "260");
        AssertSetter(style, "VirtualizingPanel.IsVirtualizing", "True");
        AssertSetter(
            style,
            "VirtualizingPanel.VirtualizationMode",
            "Recycling");
        Assert.Contains(
            style.Descendants(Presentation + "VirtualizingStackPanel"),
            _ => true);

        var expectedNames = new[]
        {
            "TemplateItemsControl",
            "MacroItemsControl",
            "DestinationItemsControl",
            "PresetItemsControl"
        };
        foreach (var name in expectedNames)
        {
            var items = document.Descendants(Presentation + "ItemsControl")
                .Single(element =>
                    (string?)element.Attribute(Xaml + "Name") == name);
            Assert.Equal(
                "{StaticResource PortableSelectionItems}",
                (string?)items.Attribute("Style"));
        }
    }

    [Fact]
    public void HiddenAdvancedCollections_DeferVisualTreeRebuilds()
    {
        var accountSource = File.ReadAllText(RepoFile(
            "SessionDock",
            "MainWindow.xaml.cs"));
        var accountRender = Slice(
            accountSource,
            "private void RenderAccountList()",
            "private static void RestoreKeyboardFocus");
        var accountGuard = accountRender.IndexOf(
            "AdvancedWorkspace.Visibility != Visibility.Visible",
            StringComparison.Ordinal);
        var accountClear = accountRender.IndexOf(
            "AccountsList.Children.Clear()",
            StringComparison.Ordinal);
        Assert.True(accountGuard >= 0);
        Assert.True(accountClear > accountGuard);

        var recentSource = File.ReadAllText(RepoFile(
            "SessionDock",
            "MainWindow.Recent.cs"));
        var recentRender = Slice(
            recentSource,
            "private void RenderRecentExperiences()",
            "private void RestoreRecentKeyboardFocus");
        var recentGuard = recentRender.IndexOf(
            "RecentTabPanel.Visibility != Visibility.Visible",
            StringComparison.Ordinal);
        var recentClear = recentRender.IndexOf(
            "RecentExperiencesList.Children.Clear()",
            StringComparison.Ordinal);
        Assert.True(recentGuard >= 0);
        Assert.True(recentClear > recentGuard);

        var navigation = File.ReadAllText(RepoFile(
            "SessionDock",
            "MainWindow.Home.cs"));
        var focusRoutes = Slice(
            navigation,
            "WindowLayoutService.FitToWorkArea(this);",
            "#if SESSIONDOCK_SMOKE_HARNESS");
        Assert.Contains("case MainWorkspacePage.Advanced:", focusRoutes);
        Assert.Contains("RenderAccountList();", focusRoutes);
        Assert.Contains("RenderRecentExperiences();", focusRoutes);
    }

    [Fact]
    public void GuidedTour_TracksLayoutOnlyWhileRunning()
    {
        var source = File.ReadAllText(RepoFile(
            "SessionDock",
            "GuidedTourOverlay.xaml.cs"));
        var constructor = Slice(
            source,
            "public GuidedTourOverlay()",
            "public event EventHandler? Completed");
        Assert.DoesNotContain("LayoutUpdated +=", constructor);
        Assert.Contains("StartLayoutTracking();", source);
        Assert.Contains("StopLayoutTracking();", source);
        Assert.Contains("LayoutUpdated -= Overlay_LayoutUpdated", source);
    }

    [Fact]
    public void PortableSelection_DependencyChecksUseIndexes()
    {
        var source = File.ReadAllText(RepoFile(
            "SessionDock",
            "PortableDataDialog.xaml.cs"));
        Assert.Contains("_normalizedCatalog", source);
        Assert.Contains("_macroRowsById.TryGetValue(", source);
        Assert.Contains(
            "row.MacroDependencies.Any(keyboardMacroIds.Contains)",
            source);
        Assert.DoesNotContain(
            "row.MacroDependencies.Any(dependencyId =>\r\n" +
            "                        _macroRows.Any(",
            source.ReplaceLineEndings("\r\n"));
    }

    private static void AssertSetter(
        XElement style,
        string property,
        string value) =>
        Assert.Contains(
            style.Elements(Presentation + "Setter"),
            setter =>
                (string?)setter.Attribute("Property") == property &&
                (string?)setter.Attribute("Value") == value);

    private static string Slice(
        string source,
        string startMarker,
        string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing source boundary: {endMarker}");
        return source[start..end];
    }

    private static string RepoFile(params string[] components)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null &&
               !File.Exists(Path.Combine(current.FullName, "SessionDock.slnx")))
        {
            current = current.Parent;
        }

        if (current is null)
            throw new DirectoryNotFoundException("Repository root not found.");
        return Path.Combine([current.FullName, .. components]);
    }
}
