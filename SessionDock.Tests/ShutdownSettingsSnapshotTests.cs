using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class ShutdownSettingsSnapshotTests
{
    [Fact]
    public void Create_OverlaysDraftCapturedBeforeShutdownWaits()
    {
        var settings = new AppSettings
        {
            Accounts =
            [
                new AccountProfile
                {
                    Key = "one",
                    Destination = "confirmed"
                }
            ],
            ActiveAccountKey = "one"
        };
        var capturedDraft = new DestinationPersistenceRequest(
            "one",
            OwnerEpoch: 3,
            Revision: 7,
            Destination: "captured-edit");

        settings.Accounts[0].Destination = "queued-mutation";
        var snapshot = ShutdownSettingsSnapshot.Create(
            settings,
            capturedDraft,
            capturedDraft);
        settings.Accounts[0].Destination = "later-live-edit";

        Assert.Equal(
            "captured-edit",
            Assert.Single(snapshot.Accounts).Destination);
    }

    [Fact]
    public void Create_RemovedDraftOwnerIsNotResurrected()
    {
        var settings = new AppSettings();
        var capturedDraft = new DestinationPersistenceRequest(
            "removed",
            OwnerEpoch: 3,
            Revision: 7,
            Destination: "captured-edit");

        var snapshot = ShutdownSettingsSnapshot.Create(
            settings,
            capturedDraft,
            capturedDraft);

        Assert.Empty(snapshot.Accounts);
    }

    [Fact]
    public void Create_StaleCapturedDraftDoesNotOverwriteNewerDestination()
    {
        var settings = new AppSettings
        {
            Accounts =
            [
                new AccountProfile
                {
                    Key = "one",
                    Destination = "newer-committed"
                }
            ],
            ActiveAccountKey = "one"
        };
        var capturedDraft = new DestinationPersistenceRequest(
            "one",
            OwnerEpoch: 3,
            Revision: 7,
            Destination: "captured-edit");
        var currentDraft = capturedDraft with
        {
            Revision = 8,
            Destination = "newer-committed"
        };

        var snapshot = ShutdownSettingsSnapshot.Create(
            settings,
            capturedDraft,
            currentDraft);

        Assert.Equal(
            "newer-committed",
            Assert.Single(snapshot.Accounts).Destination);
    }

    [Fact]
    public void Create_OverlaysCapturedWindowPlacementWithoutSharingIt()
    {
        var settings = new AppSettings
        {
            MainWindowPlacement = new WindowPlacementSettings
            {
                Width = 900,
                Height = 600
            }
        };
        var capturedPlacement = new WindowPlacementSettings
        {
            MonitorDeviceName = @"\\.\DISPLAY2",
            OffsetX = 120,
            OffsetY = 80,
            Width = 1080,
            Height = 720,
            IsMaximized = true
        };

        var snapshot = ShutdownSettingsSnapshot.Create(
            settings,
            capturedDestinationRequest: null,
            currentDestinationRequest: null,
            capturedPlacement);
        capturedPlacement.Width = 1400;

        Assert.NotNull(snapshot.MainWindowPlacement);
        Assert.NotSame(capturedPlacement, snapshot.MainWindowPlacement);
        Assert.Equal(1080, snapshot.MainWindowPlacement.Width);
        Assert.True(snapshot.MainWindowPlacement.IsMaximized);
    }
}
