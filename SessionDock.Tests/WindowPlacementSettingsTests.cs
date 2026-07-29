using System.Windows;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class WindowPlacementSettingsTests : IDisposable
{
    private readonly string _storageDirectory = Path.Combine(
        Path.GetTempPath(),
        $"SessionDock-window-placement-tests-{Guid.NewGuid():N}");

    [Fact]
    public void Normalize_PreservesValidPlacementAndTrimsMonitorName()
    {
        var normalized = WindowPlacementPolicy.Normalize(new()
        {
            MonitorDeviceName = "  \\\\.\\DISPLAY2  ",
            OffsetX = 120,
            OffsetY = 80,
            Width = 1080,
            Height = 720,
            IsMaximized = true
        });

        Assert.NotNull(normalized);
        Assert.Equal(@"\\.\DISPLAY2", normalized.MonitorDeviceName);
        Assert.Equal(120, normalized.OffsetX);
        Assert.Equal(80, normalized.OffsetY);
        Assert.Equal(1080, normalized.Width);
        Assert.Equal(720, normalized.Height);
        Assert.True(normalized.IsMaximized);
    }

    [Theory]
    [MemberData(nameof(InvalidPlacements))]
    public void Normalize_DiscardsUnsafeOrCorruptGeometry(
        WindowPlacementSettings placement)
    {
        Assert.Null(WindowPlacementPolicy.Normalize(placement));
    }

    [Fact]
    public void Normalize_DiscardsInvalidMonitorNameButKeepsSafeGeometry()
    {
        var normalized = WindowPlacementPolicy.Normalize(new()
        {
            MonitorDeviceName = "DISPLAY1\nforged",
            OffsetX = 10,
            OffsetY = 20,
            Width = 1080,
            Height = 720
        });

        Assert.NotNull(normalized);
        Assert.Null(normalized.MonitorDeviceName);
    }

    [Fact]
    public void CalculateRestoredBounds_UsesMonitorRelativePosition()
    {
        var placement = new WindowPlacementSettings
        {
            MonitorDeviceName = @"\\.\DISPLAY2",
            OffsetX = 120,
            OffsetY = 80,
            Width = 1080,
            Height = 720
        };

        var bounds = WindowPlacementPolicy.CalculateRestoredBounds(
            new Rect(-1920, 0, 1920, 1040),
            placement,
            minimumWidth: 800,
            minimumHeight: 520);

        Assert.NotNull(bounds);
        Assert.Equal(-1800, bounds.Value.Left);
        Assert.Equal(80, bounds.Value.Top);
        Assert.Equal(1080, bounds.Value.Width);
        Assert.Equal(720, bounds.Value.Height);
    }

    [Fact]
    public void CalculateRestoredBounds_ClampsOffscreenAndOversizedGeometry()
    {
        var placement = new WindowPlacementSettings
        {
            OffsetX = 9000,
            OffsetY = -500,
            Width = 5000,
            Height = 4000
        };

        var bounds = WindowPlacementPolicy.CalculateRestoredBounds(
            new Rect(0, 0, 1200, 800),
            placement,
            minimumWidth: 800,
            minimumHeight: 520);

        Assert.NotNull(bounds);
        Assert.Equal(new Rect(16, 16, 1168, 768), bounds.Value);
    }

    [Fact]
    public void SettingsService_SaveAndLoad_PreservesValidPlacement()
    {
        var service = new SettingsService(_storageDirectory);
        service.Save(new AppSettings
        {
            MainWindowPlacement = new WindowPlacementSettings
            {
                MonitorDeviceName = @"\\.\DISPLAY2",
                OffsetX = 120,
                OffsetY = 80,
                Width = 1080,
                Height = 720,
                IsMaximized = true
            }
        });

        var loaded = new SettingsService(_storageDirectory).Load();

        Assert.NotNull(loaded.MainWindowPlacement);
        Assert.Equal(
            @"\\.\DISPLAY2",
            loaded.MainWindowPlacement.MonitorDeviceName);
        Assert.True(loaded.MainWindowPlacement.IsMaximized);
    }

    [Fact]
    public void SettingsService_Load_DiscardsOutOfBoundsPlacement()
    {
        var service = new SettingsService(_storageDirectory);
        service.Save(new AppSettings
        {
            MainWindowPlacement = new WindowPlacementSettings
            {
                OffsetX = 0,
                OffsetY = 0,
                Width = 1_000_000,
                Height = 720
            }
        });

        var loaded = new SettingsService(_storageDirectory).Load();

        Assert.Null(loaded.MainWindowPlacement);
    }

    public static TheoryData<WindowPlacementSettings> InvalidPlacements =>
        new()
        {
            new WindowPlacementSettings
                { Width = double.NaN, Height = 720 },
            new WindowPlacementSettings
                { Width = 1080, Height = double.PositiveInfinity },
            new WindowPlacementSettings { Width = 319, Height = 720 },
            new WindowPlacementSettings { Width = 1080, Height = 239 },
            new WindowPlacementSettings { Width = 32_769, Height = 720 },
            new WindowPlacementSettings { Width = 1080, Height = 32_769 },
            new WindowPlacementSettings
                { OffsetX = 1_000_001, Width = 1080, Height = 720 },
            new WindowPlacementSettings
                { OffsetY = -1_000_001, Width = 1080, Height = 720 }
        };

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory))
            Directory.Delete(_storageDirectory, recursive: true);
    }
}
