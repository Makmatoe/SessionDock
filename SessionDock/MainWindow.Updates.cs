using System.Diagnostics;
using System.Windows;
using SessionDock.ReleaseTrust;
using SessionDock.Services;

namespace SessionDock;

public partial class MainWindow
{
    private readonly SessionDockUpdateService _updateService = new();

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e) =>
        await RunWindowOperationAsync(InstallUpdateButtonClickAsync);

    private async Task InstallUpdateButtonClickAsync(
        CancellationToken cancellationToken)
    {
        if (_operationBusy)
            return;

        var applyingUpdate = false;
        SetOperationBusy(true);
        try
        {
            if (!_updateService.CanSelfUpdate)
            {
                SetStatus(
                    Localize("Main.UpdateInstalledAppRequiredTitle"),
                    Localize("Main.UpdateInstalledAppRequiredDetail"),
                    Localize("Main.UpdatesUnavailableBadge"),
                    StatusTone.Warning);
                return;
            }

            var pending = _updateService.PendingUpdate;
            if (pending is not null)
            {
                SetStatus(
                    Localize("Main.UpdateReadyToRestartTitle"),
                    Localize("Main.UpdateVerifyingDetail"),
                    Localize("Main.UpdateVerifyingBadge"),
                    StatusTone.Neutral);
                var verifiedPending = await _updateService.VerifyPendingAsync(
                    pending,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!ConfirmUpdate(verifiedPending, alreadyDownloaded: true))
                {
                    SetStatus(
                        Localize("Main.UpdateRestartPostponedTitle"),
                        Localize("Main.UpdateRestartPostponedDetail"),
                        Localize("Main.UpdateReadyBadge"),
                        StatusTone.Neutral);
                    return;
                }

                _updateService.ApplyAfterExit(pending);
                applyingUpdate = true;
                _ = Dispatcher.BeginInvoke(() => Close());
                return;
            }

            SetStatus(
                Localize("Main.UpdateCheckingTitle"),
                Localize("Main.UpdateCheckingDetail"),
                Localize("Main.UpdateCheckingBadge"),
                StatusTone.Neutral);
            var available = await _updateService.CheckAsync(
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (available is null)
            {
                SetStatus(
                    Localize("Main.UpdateCurrentTitle"),
                    Localize(
                        "Main.UpdateCurrentDetail",
                        _updateService.CurrentVersion),
                    Localize("Main.UpdateCurrentBadge"),
                    StatusTone.Success);
                return;
            }

            if (!ConfirmUpdate(available.Release, alreadyDownloaded: false))
            {
                SetStatus(
                    Localize("Main.UpdateNotInstalledTitle"),
                    Localize("Main.UpdateNotInstalledDetail"),
                    Localize("Main.UpdateCancelledBadge"),
                    StatusTone.Warning);
                return;
            }

            SetStatus(
                Localize(
                    "Main.UpdateDownloadingTitle",
                    available.Release.Descriptor.Version),
                Localize("Main.UpdateDownloadingDetail", 0),
                Localize("Main.UpdateDownloadingBadge"),
                StatusTone.Neutral);
            await _updateService.DownloadAsync(
                available,
                progress => Dispatcher.BeginInvoke(() => SetStatus(
                    Localize(
                        "Main.UpdateDownloadingTitle",
                        available.Release.Descriptor.Version),
                    Localize("Main.UpdateDownloadingDetail", progress),
                    Localize("Main.UpdateDownloadingBadge"),
                    StatusTone.Neutral)),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            SetStatus(
                Localize("Main.UpdateDownloadedTitle"),
                Localize("Main.UpdateDownloadedDetail"),
                Localize("Main.UpdateRestartingBadge"),
                StatusTone.Success);
            _updateService.ApplyAfterExit(available.UpdateInfo.TargetFullRelease);
            applyingUpdate = true;
            _ = Dispatcher.BeginInvoke(() => Close());
        }
        catch (OperationCanceledException) when (
            _operationLifetime.IsShuttingDown)
        {
            // Window shutdown owns this cancellation.
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            SetStatus(
                Localize("Main.UpdateCancelledTitle"),
                Localize("Main.UpdateCancelledDetail"),
                Localize("Main.UpdateCancelledBadge"),
                StatusTone.Warning);
        }
        catch (Exception ex) when (
            UpdateFailurePresentation.TryCreate(ex, out _))
        {
            var failure = UpdateFailurePresentation.Create(ex);
            Trace.WriteLine(
                $"Expected update failure: {ex.GetType().FullName}.");
            SetStatus(
                Localize(failure.TitleKey),
                Localize(failure.DetailKey),
                Localize(failure.BadgeKey),
                failure.Tone);
        }
        finally
        {
            if (!applyingUpdate && !_operationLifetime.IsShuttingDown)
                SetOperationBusy(false);
        }
    }

    private bool ConfirmUpdate(
        VerifiedReleaseDescriptor update,
        bool alreadyDownloaded)
    {
        var confirmation = new UpdateConfirmationDialog(update, alreadyDownloaded)
        {
            Owner = this
        };
        return confirmation.ShowDialog() == true;
    }
}
