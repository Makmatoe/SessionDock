using SessionDock.ExactWheel;

namespace SessionDock.Tests;

public sealed class ExactWheelDesktopCaptureTests
{
    [Fact]
    public void SnapshotPlaybackCapture_RejectsInvalidGeometryBeforeNativeAccess()
    {
        var display = Display();
        var validClient = new ExactWheelRect(8, 32, 792, 592);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ExactWheelDesktopCapture.CapturePlaybackTarget(
                nint.Zero,
                display,
                "RobloxPlayerBeta.exe",
                default,
                validClient));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ExactWheelDesktopCapture.CapturePlaybackTarget(
                nint.Zero,
                display,
                "RobloxPlayerBeta.exe",
                new ExactWheelRect(0, 0, 800, 600),
                new ExactWheelRect(20, 20, 10, 10)));
        Assert.Throws<ArgumentException>(() =>
            ExactWheelDesktopCapture.CapturePlaybackTarget(
                nint.Zero,
                display,
                "RobloxPlayerBeta.exe",
                string.Empty,
                new ExactWheelRect(0, 0, 800, 600),
                validClient));
    }

    [Fact]
    public void SnapshotPlaybackCapture_ReusesGeometryButKeepsLiveWindowChecks()
    {
        var source = File.ReadAllText(RepoFile(
            "SessionDock.ExactWheel",
            "ExactWheelDesktopCapture.cs"));
        var marker = source.IndexOf(
            "ExactWheelRect verifiedWindowRect",
            StringComparison.Ordinal);
        Assert.True(marker >= 0);
        var start = source.LastIndexOf(
            "public static ExactWheelRecordingTarget CapturePlaybackTarget(",
            marker,
            StringComparison.Ordinal);
        var end = source.IndexOf(
            "public static ExactWheelDisplayTopology CaptureDisplayTopology(",
            marker,
            StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var overload = source[start..end];

        Assert.Contains("ValidateTargetWindow", overload);
        Assert.Contains("CaptureWindowClass", overload);
        Assert.DoesNotContain("GetWindowRect", overload);
        Assert.DoesNotContain("GetClientRect", overload);
        Assert.DoesNotContain("ClientToScreen", overload);
    }

    [Fact]
    public void CachedClassPlaybackCapture_DoesNotRecaptureImmutableMetadata()
    {
        var source = File.ReadAllText(RepoFile(
            "SessionDock.ExactWheel",
            "ExactWheelDesktopCapture.cs"));
        var marker = source.IndexOf(
            "string verifiedWindowClass",
            StringComparison.Ordinal);
        Assert.True(marker >= 0);
        var start = source.LastIndexOf(
            "public static ExactWheelRecordingTarget CapturePlaybackTarget(",
            marker,
            StringComparison.Ordinal);
        var end = source.IndexOf(
            "public static ExactWheelDisplayTopology CaptureDisplayTopology(",
            marker,
            StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var overload = source[start..end];

        Assert.Contains("ValidateTargetWindow", overload);
        Assert.Contains("ValidateWindowClass", overload);
        Assert.DoesNotContain("CaptureWindowClass", overload);
        Assert.DoesNotContain("GetWindowRect", overload);
        Assert.DoesNotContain("GetClientRect", overload);
        Assert.DoesNotContain("ClientToScreen", overload);
    }

    private static ExactWheelDisplayTopology Display() => new(
        0,
        0,
        1920,
        1080,
        [new ExactWheelMonitorSnapshot(
            new ExactWheelRect(0, 0, 1920, 1080))]);

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
