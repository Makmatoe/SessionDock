using System.Reflection;
using System.Windows.Controls;
using System.Xml.Linq;
using SessionDock;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class ListSearchTests
{
    private const string ServerJobId =
        "A18C877E-4070-4A84-A5F7-36668B46A77D";

    private static readonly AccountProfile Account = new()
    {
        Label = "Main Builder",
        Username = "BrickMaster",
        UserId = 123456789,
        Destination = "https://www.roblox.com/games/987654321/Build-Island"
    };

    private static readonly RecentExperience Recent = new()
    {
        CustomName = "Friday Hangout",
        Name = "Build Island",
        PlaceId = 987654321,
        AccountUsername = "BrickMaster",
        Destination = "code=Private_ABC-123",
        ServerJobId = ServerJobId
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \t")]
    public void EmptyQuery_MatchesEveryListItem(string? query)
    {
        Assert.True(ListSearchMatcher.MatchesAccount(Account, query));
        Assert.True(ListSearchMatcher.MatchesRecent(Recent, query));
    }

    [Theory]
    [InlineData("main builder")]
    [InlineData("BRICKmaster")]
    [InlineData("123456789")]
    [InlineData("987654321")]
    [InlineData("build-island")]
    public void AccountQuery_MatchesEverySupportedFieldIgnoringCase(
        string query)
    {
        Assert.True(ListSearchMatcher.MatchesAccount(Account, query));
    }

    [Fact]
    public void AccountQuery_CanMatchOptionalGroup()
    {
        Assert.True(ListSearchMatcher.MatchesAccount(
            Account,
            "weekend squad",
            group: "Weekend Squad"));
        Assert.False(ListSearchMatcher.MatchesAccount(Account, "weekend squad"));
    }

    [Theory]
    [InlineData("friday hangout")]
    [InlineData("BUILD island")]
    [InlineData("987654321")]
    [InlineData("brickMASTER")]
    [InlineData("private_abc")]
    [InlineData("a18c877e")]
    public void RecentQuery_MatchesEverySupportedFieldIgnoringCase(string query)
    {
        Assert.True(ListSearchMatcher.MatchesRecent(Recent, query));
    }

    [Theory]
    [InlineData("builder 123456789")]
    [InlineData("brickmaster island")]
    public void AccountQuery_AllowsTermsToMatchAcrossFields(string query)
    {
        Assert.True(ListSearchMatcher.MatchesAccount(Account, query));
    }

    [Theory]
    [InlineData("friday brickmaster")]
    [InlineData("island a18c877e")]
    [InlineData("private_abc 987654321")]
    public void RecentQuery_AllowsTermsToMatchAcrossFields(string query)
    {
        Assert.True(ListSearchMatcher.MatchesRecent(Recent, query));
    }

    [Theory]
    [InlineData("different account")]
    [InlineData("builder missing")]
    public void AccountQuery_RejectsMissingTerms(string query)
    {
        Assert.False(ListSearchMatcher.MatchesAccount(Account, query));
    }

    [Theory]
    [InlineData("unknown experience")]
    [InlineData("friday missing")]
    public void RecentQuery_RejectsMissingTerms(string query)
    {
        Assert.False(ListSearchMatcher.MatchesRecent(Recent, query));
    }

    [Fact]
    public void Matchers_RejectNullItems()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ListSearchMatcher.MatchesAccount(null!, "query"));
        Assert.Throws<ArgumentNullException>(() =>
            ListSearchMatcher.MatchesRecent(null!, "query"));
    }

    [Fact]
    public void SearchState_TracksChangesAndClears()
    {
        var state = new SearchQueryState();

        Assert.False(state.IsActive);
        Assert.False(state.Update(null));
        Assert.True(state.Update("builder"));
        Assert.True(state.IsActive);
        Assert.Equal("builder", state.Query);
        Assert.False(state.Update("builder"));
        Assert.True(state.MatchesAccount(Account));
        Assert.True(state.Clear());
        Assert.False(state.IsActive);
        Assert.Equal(string.Empty, state.Query);
        Assert.False(state.Clear());
    }

    [Fact]
    public void SearchState_LimitsUnexpectedlyLongInput()
    {
        var state = new SearchQueryState();

        Assert.True(state.Update(new string('x', SearchQueryState.MaximumLength + 1)));

        Assert.Equal(SearchQueryState.MaximumLength, state.Query.Length);
    }

    [Fact]
    public void MainWindow_ExposesBoundedAccessibleSearchFieldsAndShortcuts()
    {
        var document = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "MainWindow.xaml"));
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml =
            "http://schemas.microsoft.com/winfx/2006/xaml";

        Assert.Equal(
            "Window_PreviewKeyDown",
            (string?)document.Root?.Attribute("PreviewKeyDown"));

        foreach (var (name, changedHandler, accessibleName, helpText) in new[]
                 {
                     (
                         "AccountSearchBox",
                         "AccountSearchBox_TextChanged",
                         "{DynamicResource Main.AccountSearchName}",
                         "{DynamicResource Main.AccountSearchHelp}"),
                     (
                         "RecentSearchBox",
                         "RecentSearchBox_TextChanged",
                         "{DynamicResource Main.RecentSearchName}",
                         "{DynamicResource Main.RecentSearchHelp}")
                 })
        {
            var searchBox = document
                .Descendants(presentation + "TextBox")
                .Single(element =>
                    (string?)element.Attribute(xaml + "Name") == name);

            Assert.Equal("256", (string?)searchBox.Attribute("MaxLength"));
            Assert.Equal(
                changedHandler,
                (string?)searchBox.Attribute("TextChanged"));
            Assert.Equal(
                accessibleName,
                (string?)searchBox.Attribute("AutomationProperties.Name"));
            Assert.Equal(
                helpText,
                (string?)searchBox.Attribute("AutomationProperties.HelpText"));
        }

        var englishResources = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "Localization",
            "Strings.en-US.xaml"));
        Assert.Contains("Escape", englishResources, StringComparison.Ordinal);
        Assert.Contains("Control F", englishResources, StringComparison.Ordinal);

        var shortcutSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "MainWindow.Search.cs"));
        Assert.Contains("e.Key != Key.F", shortcutSource, StringComparison.Ordinal);
        Assert.Contains(
            "Keyboard.Modifiers != ModifierKeys.Control",
            shortcutSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "e.Key == Key.Escape",
            shortcutSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "contextSearchBox.Focus();",
            shortcutSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "clearSearchBox.Clear();",
            shortcutSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "searchBox.Focus();",
            shortcutSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_SearchClearButtonsAreAccessibleAndTrackTheirQueries()
    {
        var document = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "MainWindow.xaml"));
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml =
            "http://schemas.microsoft.com/winfx/2006/xaml";

        foreach (var contract in new[]
                 {
                     new
                     {
                         SearchBox = "AccountSearchBox",
                         ClearButton = "AccountSearchClearButton",
                         Click = "AccountSearchClearButton_Click",
                         AutomationName =
                             "{DynamicResource Main.ClearAccountSearchName}",
                         ToolTip =
                             "{DynamicResource Main.ClearAccountSearchTooltip}"
                     },
                     new
                     {
                         SearchBox = "RecentSearchBox",
                         ClearButton = "RecentSearchClearButton",
                         Click = "RecentSearchClearButton_Click",
                         AutomationName =
                             "{DynamicResource Main.ClearRecentSearchName}",
                         ToolTip =
                             "{DynamicResource Main.ClearRecentSearchTooltip}"
                     }
                 })
        {
            var searchBox = document
                .Descendants(presentation + "TextBox")
                .Single(element =>
                    (string?)element.Attribute(xaml + "Name") ==
                    contract.SearchBox);
            var clearButton = document
                .Descendants(presentation + "Button")
                .Single(element =>
                    (string?)element.Attribute(xaml + "Name") ==
                    contract.ClearButton);

            Assert.Same(searchBox.Parent, clearButton.Parent);
            Assert.Equal(
                contract.Click,
                (string?)clearButton.Attribute("Click"));
            Assert.Equal(
                contract.AutomationName,
                (string?)clearButton.Attribute("AutomationProperties.Name"));
            Assert.Equal(
                contract.ToolTip,
                (string?)clearButton.Attribute("ToolTip"));
            Assert.Equal(
                contract.ToolTip,
                (string?)clearButton.Attribute(
                    "AutomationProperties.HelpText"));

            var buttonStyle = clearButton
                .Elements(presentation + "Button.Style")
                .Single()
                .Elements(presentation + "Style")
                .Single();
            Assert.Equal(
                "{StaticResource SearchClearButton}",
                (string?)buttonStyle.Attribute("BasedOn"));
            Assert.Equal("Button", (string?)buttonStyle.Attribute("TargetType"));
            AssertSetter(buttonStyle, presentation, "Visibility", "Visible");
            AssertSetter(buttonStyle, presentation, "IsTabStop", "True");

            var emptyQueryTrigger = buttonStyle
                .Descendants(presentation + "DataTrigger")
                .Single(trigger =>
                    (string?)trigger.Attribute("Binding") ==
                        $"{{Binding Text, ElementName={contract.SearchBox}}}" &&
                    (string?)trigger.Attribute("Value") == string.Empty);
            AssertSetter(
                emptyQueryTrigger,
                presentation,
                "Visibility",
                "Collapsed");
            AssertSetter(
                emptyQueryTrigger,
                presentation,
                "IsTabStop",
                "False");

            var icon = clearButton
                .Descendants(presentation + "Path")
                .Single();
            Assert.Equal(
                "{StaticResource IconClear}",
                (string?)icon.Attribute("Data"));
        }

        var application = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "App.xaml"));
        var clearButtonStyle = application
            .Descendants(presentation + "Style")
            .Single(style =>
                (string?)style.Attribute(xaml + "Key") ==
                "SearchClearButton");
        AssertSetter(
            clearButtonStyle,
            presentation,
            "Foreground",
            "{DynamicResource TextBrush}");
    }

    [Fact]
    public void SearchClearHelper_ClearsTheProvidedTextBox()
    {
        var helper = typeof(MainWindow).GetMethod(
            "ClearSearchBox",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(helper);
        Assert.Equal(typeof(void), helper.ReturnType);
        Assert.Equal(
            new[] { typeof(TextBox) },
            helper.GetParameters()
                .Select(parameter => parameter.ParameterType)
                .ToArray());

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var searchBox = new TextBox { Text = "builder" };

                helper.Invoke(null, new object[] { searchBox });

                Assert.Equal(string.Empty, searchBox.Text);
            }
            catch (TargetInvocationException exception)
            {
                failure = exception.InnerException ?? exception;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.Null(failure);
    }

    [Fact]
    public void MainWindow_IncludesPersistedGroupsInAccountSearchAndVisibility()
    {
        var root = FindRepositoryRoot();
        var mainWindowSource = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.xaml.cs"));
        var renderStart = mainWindowSource.IndexOf(
            "private void RenderAccountList()",
            StringComparison.Ordinal);
        var renderEnd = mainWindowSource.IndexOf(
            "private static void RestoreKeyboardFocus",
            renderStart,
            StringComparison.Ordinal);
        Assert.True(renderStart >= 0 && renderEnd > renderStart);
        Assert.Contains(
            "account.Group",
            mainWindowSource[renderStart..renderEnd],
            StringComparison.Ordinal);

        var reorderingSource = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.AccountReordering.cs"));
        var availabilityStart = reorderingSource.IndexOf(
            "private void UpdateAccountControlAvailability()",
            StringComparison.Ordinal);
        var availabilityEnd = reorderingSource.IndexOf(
            "internal static int CalculateAccountDropInsertionIndex",
            availabilityStart,
            StringComparison.Ordinal);
        Assert.True(
            availabilityStart >= 0 && availabilityEnd > availabilityStart);
        Assert.Contains(
            "_activeProfile.Group",
            reorderingSource[availabilityStart..availabilityEnd],
            StringComparison.Ordinal);
    }

    private static void AssertSetter(
        XElement owner,
        XNamespace presentation,
        string property,
        string value)
    {
        var setters = owner
            .Elements(presentation + "Setter")
            .Where(element =>
                (string?)element.Attribute("Property") == property)
            .ToArray();

        var setter = Assert.Single(setters);
        Assert.Equal(value, (string?)setter.Attribute("Value"));
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
