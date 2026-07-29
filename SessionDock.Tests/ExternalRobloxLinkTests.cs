using System.IO;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class ExternalRobloxLinkTests
{
    [Theory]
    [InlineData(
        "https://www.roblox.com/games/123456/My-Game",
        "123456",
        false)]
    [InlineData(
        "roblox://www.roblox.com/games/987654",
        "987654",
        false)]
    [InlineData(
        "https://www.roblox.com/share?code=Abc_12-Z&type=Server",
        "code=Abc_12-Z",
        true)]
    [InlineData(
        "https://roblox.com/games/123456?privateServerLinkCode=Private_Code",
        "https://www.roblox.com/games/123456?privateServerLinkCode=Private_Code",
        true)]
    public void TryParse_SafeOfficialLink_ReturnsMinimalCanonicalDestination(
        string input,
        string expectedDestination,
        bool expectedPrivate)
    {
        var success = ExternalRobloxLinkPolicy.TryParse(
            input,
            out var link,
            out var error);

        Assert.True(success, error);
        Assert.Equal(expectedDestination, link!.Destination);
        Assert.Equal(expectedPrivate, link.IsPrivateServer);
        Assert.DoesNotContain("Abc_12-Z", link.PreviewDetail);
        Assert.DoesNotContain("Private_Code", link.PreviewDetail);
    }

    [Theory]
    [InlineData("123456")]
    [InlineData("code=Abc_12-Z")]
    [InlineData("http://www.roblox.com/games/123456")]
    [InlineData("https://example.test/games/123456")]
    [InlineData("https://roblox.com.example.test/games/123456")]
    [InlineData("roblox://example.test/games/123456")]
    [InlineData("roblox-player:1+launchmode:play+gameinfo:secret")]
    [InlineData("https://www.roblox.com/games/123456#private")]
    [InlineData("https://user@www.roblox.com/games/123456")]
    [InlineData("https://www.roblox.com:444/games/123456")]
    [InlineData("https://www.roblox.com/games/123456?unknown=value")]
    [InlineData("https://www.roblox.com/catalog/games/123456")]
    [InlineData("https://www.roblox.com/games/123456/slug/extra")]
    [InlineData("https://www.roblox.com/games/123456?code=Abc_12-Z&code=Def_45-X")]
    [InlineData("https://www.roblox.com/games/123456?code=Abc_12-Z&type=Server")]
    [InlineData("https://www.roblox.com/games/123456?privateServerLinkCode=Private_Code&linkCode=Other_Code")]
    [InlineData("https://www.roblox.com/games/123456?type=Server")]
    [InlineData("https://www.roblox.com/games/123456?authenticationTicket=secret")]
    [InlineData("https://www.roblox.com/games/123456?browserTrackerId=123")]
    [InlineData("https://www.roblox.com/games/123456?gameInstanceId=00000000-0000-0000-0000-000000000000")]
    [InlineData("https://www.roblox.com/share?code=Abc_12-Z&type=Experience")]
    public void TryParse_UntrustedOrAmbiguousExternalInput_IsRejected(
        string input)
    {
        var success = ExternalRobloxLinkPolicy.TryParse(
            input,
            out var link,
            out _);

        Assert.False(success);
        Assert.Null(link);
    }

    [Fact]
    public void TryParse_BoundedInput_RejectsControlCharactersAndOversize()
    {
        Assert.False(ExternalRobloxLinkPolicy.TryParse(
            "https://www.roblox.com/games/123\r\n--unexpected",
            out _,
            out _));
        Assert.False(ExternalRobloxLinkPolicy.TryParse(
            "https://www.roblox.com/games/" +
            new string('1', ExternalRobloxLinkPolicy.MaximumInputLength),
            out _,
            out _));
    }

    [Fact]
    public void HandlerWrapper_DecodesOnceThenRevalidatesOfficialLink()
    {
        const string original =
            "https://www.roblox.com/games/123456?privateServerLinkCode=Private_Code";
        var wrapper = ExternalRobloxLinkPolicy.WrapForHandler(original);

        Assert.True(ExternalRobloxLinkPolicy.TryParse(
            wrapper,
            out var link,
            out var error), error);
        Assert.True(link!.IsPrivateServer);
        Assert.False(ExternalRobloxLinkPolicy.TryParse(
            ExternalRobloxLinkPolicy.WrapForHandler(
                "https://example.test/games/123456"),
            out _,
            out _));
        Assert.False(ExternalRobloxLinkPolicy.TryParse(
            ExternalRobloxLinkPolicy.HandlerScheme +
            ":https://www.roblox.com/games/123456",
            out _,
            out _));
    }

    [Fact]
    public void PersistencePolicy_NeverSavesExternalPrivateLink()
    {
        Assert.True(ExternalRobloxLinkPolicy.TryParse(
            "https://www.roblox.com/share?code=Abc_12-Z&type=Server",
            out var privateLink,
            out var privateError), privateError);
        Assert.True(ExternalRobloxLinkPolicy.TryParse(
            "https://www.roblox.com/games/123456",
            out var publicLink,
            out var publicError), publicError);

        Assert.False(ExternalRobloxLinkPolicy.ShouldSaveToHistory(privateLink));
        Assert.True(ExternalRobloxLinkPolicy.ShouldSaveToHistory(publicLink));
        Assert.True(ExternalRobloxLinkPolicy.ShouldSaveToHistory(null));
    }

    [Theory]
    [InlineData("--open-roblox-link")]
    [InlineData("--open-roblox-link", "https://www.roblox.com/games/123", "extra")]
    [InlineData("extra", "--open-roblox-link")]
    [InlineData("--open-roblox-link", "https://example.test/games/123")]
    public void CommandLine_MalformedHandlerInvocation_IsRejected(
        params string[] arguments)
    {
        Assert.False(ExternalLaunchCommandLine.TryParse(
            arguments,
            out _,
            out _));
    }

    [Fact]
    public void CommandLine_VelopackArgumentsRemainUnclaimed()
    {
        string[] arguments = ["--squirrel-install", "2.6.2"];

        Assert.True(ExternalLaunchCommandLine.TryParse(
            arguments,
            out var externalLink,
            out var error), error);
        Assert.Null(externalLink);
    }

    [Fact]
    public void CommandLine_ValidHandlerInvocation_ReturnsOriginalBoundedLink()
    {
        const string input = "https://www.roblox.com/games/123456";

        Assert.True(ExternalLaunchCommandLine.TryParse(
            [ExternalLaunchCommandLine.OpenLinkOption, input],
            out var externalLink,
            out var error), error);
        Assert.Equal(input, externalLink);
    }

    [Fact]
    public async Task PipeFrame_RoundTripsBoundedUtf8()
    {
        const string input = "https://www.roblox.com/games/123456";
        await using var stream = new MemoryStream();

        await ExternalLinkPipeProtocol.WriteAsync(
            stream,
            input,
            TestContext.Current.CancellationToken);
        stream.Position = 0;

        Assert.Equal(
            input,
            await ExternalLinkPipeProtocol.ReadAsync(
                stream,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PipeFrame_RejectsOversizedDeclaredPayload()
    {
        var length = BitConverter.GetBytes(
            ExternalLinkPipeProtocol.MaximumPayloadBytes + 1);
        await using var stream = new MemoryStream(length);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ExternalLinkPipeProtocol.ReadAsync(
                stream,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SingleInstancePipe_ForwardsWithinCurrentUserAndSession()
    {
        var applicationId = "SessionDockTest" + Guid.NewGuid().ToString("N");
        using var primary = new SingleInstanceService(applicationId);
        using var secondary = new SingleInstanceService(applicationId);
        Assert.True(primary.IsPrimaryInstance);
        Assert.False(secondary.IsPrimaryInstance);
        var received = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        primary.ListenForExternalLinkRequests(message =>
            received.TrySetResult(message));
        const string input = "https://www.roblox.com/games/123456";

        var forwarded = await secondary.ForwardExternalLinkAsync(
            input,
            TimeSpan.FromSeconds(3),
            TestContext.Current.CancellationToken);

        Assert.True(forwarded);
        Assert.Equal(
            input,
            await received.Task.WaitAsync(
                TimeSpan.FromSeconds(3),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void SingleInstancePipe_NameIsIsolatedByInteractiveSession()
    {
        var first = SingleInstanceService.BuildExternalLinkPipeName(
            "SessionDockTest",
            1);
        var second = SingleInstanceService.BuildExternalLinkPipeName(
            "SessionDockTest",
            2);

        Assert.NotEqual(first, second);
        Assert.EndsWith(".1.ExternalLinks", first, StringComparison.Ordinal);
        Assert.EndsWith(".2.ExternalLinks", second, StringComparison.Ordinal);
    }

    [Fact]
    public void RegistryCommand_QuotesExecutableAndPlaceholderExactly()
    {
        const string executable = @"C:\Program Files\SessionDock\SessionDock.exe";

        var command = RobloxLinkRegistrationService.BuildOpenCommand(executable);

        Assert.Equal(
            "\"C:\\Program Files\\SessionDock\\SessionDock.exe\" " +
            "--open-roblox-link \"%1\"",
            command);
        Assert.Throws<ArgumentException>(() =>
            RobloxLinkRegistrationService.BuildOpenCommand(
                @"C:\Bad\""Path\SessionDock.exe"));
        Assert.Throws<ArgumentException>(() =>
            RobloxLinkRegistrationService.BuildOpenCommand(
                @"relative\SessionDock.exe"));
    }

    [Fact]
    public void RegistryOwnershipMarker_IsExactAndReservedPathsArePerUser()
    {
        Assert.True(RobloxLinkRegistrationService.HasOwnerMarker(
            RobloxLinkRegistrationService.OwnerValue));
        Assert.False(RobloxLinkRegistrationService.HasOwnerMarker(null));
        Assert.False(RobloxLinkRegistrationService.HasOwnerMarker(
            RobloxLinkRegistrationService.OwnerValue + ".foreign"));
        Assert.StartsWith(
            @"Software\Classes\",
            RobloxLinkRegistrationService.ProgIdPath,
            StringComparison.Ordinal);
        Assert.StartsWith(
            @"Software\Classes\",
            RobloxLinkRegistrationService.ProtocolPath,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, null, false, null, false,
        (int)RobloxLinkRegistrationOwnership.Empty)]
    [InlineData(false, null, false, null, true,
        (int)RobloxLinkRegistrationOwnership.Conflict)]
    [InlineData(true, "foreign", false, null, false,
        (int)RobloxLinkRegistrationOwnership.Conflict)]
    [InlineData(false, null, true, "foreign", false,
        (int)RobloxLinkRegistrationOwnership.Conflict)]
    [InlineData(true, RobloxLinkRegistrationService.OwnerValue,
        false, null, true, (int)RobloxLinkRegistrationOwnership.Owned)]
    [InlineData(true, RobloxLinkRegistrationService.OwnerValue,
        true, RobloxLinkRegistrationService.OwnerValue,
        true, (int)RobloxLinkRegistrationOwnership.Owned)]
    public void RegistryOwnershipPolicy_NeverAdoptsOrDeletesForeignEntries(
        bool progIdExists,
        string? progIdOwner,
        bool protocolExists,
        string? protocolOwner,
        bool openWithValueExists,
        int expected)
    {
        Assert.Equal(
            (RobloxLinkRegistrationOwnership)expected,
            RobloxLinkRegistrationService.ClassifyOwnership(
                progIdExists,
                progIdOwner,
                protocolExists,
                protocolOwner,
                openWithValueExists));
    }

    [Fact]
    public void LatestOnlyQueue_BoundsActiveAndPendingWorkAndKeepsNewest()
    {
        var queue = new LatestOnlyRequestQueue<string>();

        Assert.True(queue.Enqueue("first", out var first));
        Assert.Equal("first", first);
        Assert.False(queue.Enqueue("second", out _));
        Assert.False(queue.Enqueue("newest", out _));
        Assert.Equal(2, queue.Count);
        Assert.True(queue.CompleteCurrent(out var pending));
        Assert.Equal("newest", pending);
        Assert.Equal(1, queue.Count);
        Assert.False(queue.CompleteCurrent(out _));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void LatestOnlyQueue_ClearDropsBoundedPendingWork()
    {
        var queue = new LatestOnlyRequestQueue<string>();
        Assert.True(queue.Enqueue("first", out _));
        Assert.False(queue.Enqueue("pending", out _));

        queue.Clear();

        Assert.Equal(0, queue.Count);
        Assert.True(queue.Enqueue("after-clear", out var request));
        Assert.Equal("after-clear", request);
    }

    [Fact]
    public void ProductionStartup_ShowsMainWindowOutsideSmokeConditional()
    {
        var source = File.ReadAllText(Path.Combine(
                FindRepositoryRoot(),
                "SessionDock",
                "App.xaml.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains(
            "#endif\n                    mainWindow.Show();\n#if SESSIONDOCK_SMOKE_HARNESS",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StartupContract_PreservesVelopackAndRoutesExternalLinkByInstance()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "Program.cs"));
        var app = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "App.xaml.cs"));

        Assert.Contains(".SetArgs(velopackArguments)", program, StringComparison.Ordinal);
        Assert.Contains(
            "var velopackArguments = externalLink is null ? args : [];",
            program,
            StringComparison.Ordinal);
        Assert.Contains("ForwardExternalLinkAsync", app, StringComparison.Ordinal);
        Assert.Contains("ListenForExternalLinkRequests", app, StringComparison.Ordinal);
        Assert.Contains("QueueExternalLinkForDispatch", app, StringComparison.Ordinal);
    }

    [Fact]
    public void UiContract_RequiresPreviewAccountChoiceAndTwoConfirmations()
    {
        var root = FindRepositoryRoot();
        var chooser = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "ExternalRobloxLinkDialog.xaml"));
        var chooserCode = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "ExternalRobloxLinkDialog.xaml.cs"));
        var mainCode = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.ExternalLinks.cs"));
        var settings = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "RobloxLinkIntegrationDialog.xaml"));
        var registration = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "Services",
            "RobloxLinkRegistrationService.cs"));
        var english = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "Localization",
            "Strings.en-US.xaml"));

        Assert.Contains(
            "AutomationProperties.Name=\"{DynamicResource ExternalLink.PreviewName}\"",
            chooser,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"{DynamicResource ExternalLink.AccountName}\"",
            chooser,
            StringComparison.Ordinal);
        Assert.Contains(
            "Content=\"{DynamicResource ExternalLink.Review}\"",
            chooser,
            StringComparison.Ordinal);
        Assert.Contains(
            "Localize(\"ExternalLink.ConfirmTitle\")",
            mainCode,
            StringComparison.Ordinal);
        Assert.Contains("link.Target.PlaceId", chooserCode, StringComparison.Ordinal);
        Assert.Contains("link.Target.PlaceId", mainCode, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "link.PreviewDetail",
            chooserCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "link.PreviewDetail",
            mainCode,
            StringComparison.Ordinal);
        Assert.Contains("MessageBoxButton.YesNo", mainCode, StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"{DynamicResource LinkIntegration.Detail}\"",
            settings,
            StringComparison.Ordinal);
        Assert.Contains(
            "never launches Roblox by itself",
            english,
            StringComparison.Ordinal);
        Assert.Contains("Registry.CurrentUser", registration, StringComparison.Ordinal);
        Assert.DoesNotContain("Registry.ClassesRoot", registration, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", registration, StringComparison.Ordinal);
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
}
