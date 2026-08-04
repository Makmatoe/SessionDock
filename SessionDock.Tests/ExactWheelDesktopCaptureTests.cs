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

    [Fact]
    public void ForegroundSnapshot_ReadsForegroundOnceAndCanonicalizesTheRoot()
    {
        var source = File.ReadAllText(RepoFile(
            "SessionDock.ExactWheel",
            "ExactWheelDesktopCapture.cs"));
        var start = source.IndexOf(
            "public static nint GetForegroundRootWindow()",
            StringComparison.Ordinal);
        var end = source.IndexOf(
            "public static bool IsForeground(",
            start,
            StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var snapshot = source[start..end];

        Assert.Contains("GetRootWindow", snapshot);
        Assert.Equal(
            1,
            CountOccurrences(snapshot, "GetForegroundWindow()"));

        var isForegroundEnd = source.IndexOf(
            "internal static nint GetRootWindow(",
            end,
            StringComparison.Ordinal);
        Assert.True(isForegroundEnd > end);
        var isForeground = source[end..isForegroundEnd];
        Assert.Contains("GetForegroundRootWindow()", isForeground);
        Assert.Equal(
            0,
            CountOccurrences(isForeground, "GetForegroundWindow()"));
    }

    private static ExactWheelDisplayTopology Display() => new(
        0,
        0,
        1920,
        1080,
        [new ExactWheelMonitorSnapshot(
            new ExactWheelRect(0, 0, 1920, 1080))]);

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(
                   value,
                   offset,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
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
