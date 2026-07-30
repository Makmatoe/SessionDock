using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class ApplicationStartupTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"SessionDock-startup-tests-{Guid.NewGuid():N}");

    [Theory]
    [MemberData(nameof(ExpectedLocalDataFailures))]
    public void TryStart_ExpectedLocalDataFailureIsReportedAndContained(
        Exception failure)
    {
        string? reportedKey = null;

        var started = ApplicationStartup.TryStart(
            () => throw failure,
            key => reportedKey = key);

        Assert.False(started);
        Assert.Equal(ApplicationStartup.LocalDataFailureKey, reportedKey);
        Assert.Contains(
            "%LOCALAPPDATA%\\SessionDock",
            EnglishResourceText.Get(reportedKey!),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TryStart_UnexpectedFailureRemainsObservable()
    {
        var reported = false;

        Assert.Throws<InvalidOperationException>(() =>
            ApplicationStartup.TryStart(
                () => throw new InvalidOperationException("programmer fault"),
                _ => reported = true));
        Assert.False(reported);
    }

    [Fact]
    public void TryStart_SuccessStartsWithoutReportingFailure()
    {
        var invoked = false;
        var reported = false;

        var started = ApplicationStartup.TryStart(
            () => invoked = true,
            _ => reported = true);

        Assert.True(started);
        Assert.True(invoked);
        Assert.False(reported);
    }

    [Fact]
    public void TryStart_RegularFileAtSettingsRootIsReportedAndContained()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var conflictingRoot = Path.Combine(
            _temporaryDirectory,
            "SessionDock");
        File.WriteAllText(conflictingRoot, "not a directory");
        string? reportedMessage = null;

        var started = ApplicationStartup.TryStart(
            () => _ = new SettingsService(conflictingRoot),
            message => reportedMessage = message);

        Assert.False(started);
        Assert.NotNull(reportedMessage);
    }

    public static TheoryData<Exception> ExpectedLocalDataFailures() => new()
    {
        new IOException("local data unavailable"),
        new UnauthorizedAccessException("local data denied")
    };

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
            Directory.Delete(_temporaryDirectory, recursive: true);
    }
}
