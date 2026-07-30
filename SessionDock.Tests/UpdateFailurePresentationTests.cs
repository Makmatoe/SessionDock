using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using SessionDock.ReleaseTrust;
using SessionDock.Services;
using Velopack.Exceptions;

namespace SessionDock.Tests;

public sealed class UpdateFailurePresentationTests
{
    [Fact]
    public void Create_LockedUpdateFiles_ExplainsHowToRecover()
    {
        var result = UpdateFailurePresentation.Create(
            CreateWithoutConstructor<AcquireLockFailedException>());

        Assert.Equal("UpdateFailure.Busy.Title", result.TitleKey);
        Assert.Equal("Update files are busy", Localize(result.TitleKey));
        Assert.Contains(
            "close",
            Localize(result.DetailKey),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "reopen",
            Localize(result.DetailKey),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("UPDATE BUSY", Localize(result.BadgeKey));
        Assert.Equal(StatusTone.Warning, result.Tone);
    }

    [Theory]
    [MemberData(nameof(ExpectedFailures))]
    public void TryCreate_ExpectedOperationalFailure_IsClassified(
        Exception exception,
        string expectedTitle,
        string expectedBadge)
    {
        var classified = UpdateFailurePresentation.TryCreate(
            exception,
            out var result);

        Assert.True(classified);
        Assert.Equal(expectedTitle, Localize(result.TitleKey));
        Assert.Equal(expectedBadge, Localize(result.BadgeKey));
        Assert.NotEmpty(Localize(result.DetailKey));
        Assert.Equal(StatusTone.Error, result.Tone);
    }

    [Fact]
    public void Create_ReleaseTrustFailure_UsesLocalizedSafeExplanation()
    {
        const string policyMessage = "The signed descriptor was rejected.";

        var result = UpdateFailurePresentation.Create(
            new ReleaseTrustException(policyMessage));

        Assert.Equal("UpdateFailure.Trust.Title", result.TitleKey);
        Assert.Equal("Update was rejected", Localize(result.TitleKey));
        Assert.Equal("UpdateFailure.Trust.Detail", result.DetailKey);
        Assert.NotEqual(policyMessage, Localize(result.DetailKey));
        Assert.Contains(
            "trust checks",
            Localize(result.DetailKey),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("UPDATE REJECTED", Localize(result.BadgeKey));
        Assert.Equal(StatusTone.Error, result.Tone);
    }

    [Fact]
    public void TryCreate_ProgrammerFault_IsNotClassified()
    {
        var classified = UpdateFailurePresentation.TryCreate(
            new InvalidOperationException("programmer fault"),
            out _);

        Assert.False(classified);
    }

    [Theory]
    [InlineData(typeof(InvalidDataException))]
    [InlineData(typeof(NotInstalledException))]
    [InlineData(typeof(Win32Exception))]
    public void SetupRecoveryGuidance_PreservesLocalDataAndDoesNotRecommendOverwrite(
        Type exceptionType)
    {
        Exception exception;
        if (exceptionType == typeof(InvalidDataException))
            exception = new InvalidDataException("invalid package");
        else if (exceptionType == typeof(Win32Exception))
            exception = new Win32Exception(2, "updater missing");
        else
            exception = CreateWithoutConstructor<NotInstalledException>();

        var result = UpdateFailurePresentation.Create(exception);

        var detail = Localize(result.DetailKey);
        Assert.Contains("data", detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "over the existing installation",
            detail,
            StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<Exception, string, string> ExpectedFailures => new()
    {
        {
            new HttpRequestException("offline"),
            "GitHub could not be reached",
            "NETWORK ERROR"
        },
        {
            new TaskCanceledException("HTTP timeout"),
            "GitHub did not respond in time",
            "NETWORK TIMEOUT"
        },
        {
            new OperationCanceledException("HTTP operation stopped"),
            "GitHub did not respond in time",
            "NETWORK TIMEOUT"
        },
        {
            new TimeoutException("timeout"),
            "GitHub did not respond in time",
            "NETWORK TIMEOUT"
        },
        {
            new HttpIOException(
                HttpRequestError.ConnectionError,
                "connection interrupted",
                null),
            "GitHub connection was interrupted",
            "NETWORK ERROR"
        },
        {
            new UnauthorizedAccessException("denied"),
            "Update access was denied",
            "ACCESS DENIED"
        },
        {
            new IOException("locked"),
            "Update files are unavailable",
            "UPDATE FILE ERROR"
        },
        {
            new InvalidDataException("invalid package"),
            "Downloaded update was rejected",
            "UPDATE REJECTED"
        },
        {
            new ChecksumFailedException("package.nupkg"),
            "Downloaded update was rejected",
            "UPDATE REJECTED"
        },
        {
            CreateWithoutConstructor<NotInstalledException>(),
            "Setup is required",
            "SETUP REQUIRED"
        },
        {
            new Win32Exception(2, "updater missing"),
            "Updater could not start",
            "UPDATER ERROR"
        }
    };

    private static T CreateWithoutConstructor<T>() where T : Exception =>
        (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private static string Localize(string key) =>
        EnglishResourceText.Get(key);
}
