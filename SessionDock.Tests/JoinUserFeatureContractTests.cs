using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class JoinUserFeatureContractTests : IDisposable
{
    private readonly string _storageDirectory = Path.Combine(
        Path.GetTempPath(),
        $"SessionDock-JoinUser-{Guid.NewGuid():N}");

    [Fact]
    public void SettingsRoundTrip_PreservesValidatedJoinUserDestination()
    {
        var key = Guid.NewGuid().ToString("N");
        var service = new SettingsService(_storageDirectory);
        service.Save(new AppSettings
        {
            Accounts =
            [
                new AccountProfile
                {
                    Key = key,
                    UserId = 42,
                    Username = "Builderman",
                    SessionFolder = $@"Profiles\{key}",
                    Destination = "user:@TargetUser"
                }
            ],
            ActiveAccountKey = key
        });

        var loaded = service.Load();

        Assert.Equal(
            "user:@TargetUser",
            Assert.Single(loaded.Accounts).Destination);
    }

    [Fact]
    public void BatchPlanner_RejectsJoinUserWithoutPartialPlans()
    {
        var account = new AccountProfile
        {
            UserId = 42,
            Username = "Builderman",
            Destination = "user:@TargetUser"
        };

        var success = BatchDestinationPlanner.TryCreate(
            [account],
            [],
            out var plans,
            out var error);

        Assert.False(success);
        Assert.Empty(plans);
        Assert.Contains("single launch", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MainWindow_ExposesExplicitAccessibleDestinationModes()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.xaml"));

        Assert.Contains("x:Name=\"ExperienceDestinationModeButton\"", xaml);
        Assert.Contains("x:Name=\"UserDestinationModeButton\"", xaml);
        Assert.Contains(
            "AutomationProperties.Name=\"{DynamicResource Main.JoinUser}\"",
            xaml);
        Assert.Contains(
            "<sys:String x:Key=\"Main.JoinUser\">Join a user</sys:String>",
            File.ReadAllText(Path.Combine(
                root,
                "SessionDock",
                "Localization",
                "Strings.en-US.xaml")));
    }

    [Fact]
    public void MainWindow_ResolvesJoinabilityBeforeRequestingLaunchTicket()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "SessionDock",
            "MainWindow.xaml.cs"));
        var launchStart = source.IndexOf(
            "private async Task LaunchAsync",
            StringComparison.Ordinal);
        var launchEnd = source.IndexOf(
            "private void ShowJoinUserUnavailable",
            launchStart,
            StringComparison.Ordinal);
        var launchMethod = source[launchStart..launchEnd];

        Assert.True(
            launchMethod.IndexOf("ResolveJoinUserAsync", StringComparison.Ordinal) <
            launchMethod.IndexOf("GetAuthenticationTicketAsync", StringComparison.Ordinal));
        Assert.Contains("IsCurrentWebSessionOwner(sessionToken)", launchMethod);
        Assert.Contains("RobloxLaunchUriBuilder.BuildFollowUser", launchMethod);
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory))
            Directory.Delete(_storageDirectory, recursive: true);
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
