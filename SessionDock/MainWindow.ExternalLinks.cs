using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock;

public partial class MainWindow
{
    private readonly LatestOnlyRequestQueue<string> _externalLinkQueue = new();

    internal void QueueExternalRobloxLink(string externalLink)
    {
        if (!_externalLinkQueue.Enqueue(externalLink, out var firstRequest))
            return;
        var task = ProcessExternalRobloxLinksAsync(firstRequest!);
        _ = task.ContinueWith(
            completed => System.Diagnostics.Trace.WriteLine(
                $"An external-link request failed safely: {completed.Exception?.GetBaseException().GetType().Name}."),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously |
                TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private async Task ProcessExternalRobloxLinksAsync(string firstRequest)
    {
        var current = firstRequest;
        while (true)
        {
            try
            {
                await HandleExternalRobloxLinkAsync(current);
            }
            catch (OperationCanceledException) when (
                _operationLifetime.IsShuttingDown)
            {
                _externalLinkQueue.Clear();
                return;
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"An external-link request failed safely: {exception.GetType().Name}.");
            }

            if (!_externalLinkQueue.CompleteCurrent(out var nextRequest))
                return;
            current = nextRequest!;
        }
    }

    private async Task HandleExternalRobloxLinkAsync(string externalLink)
    {
        var startupFailure = await StartupCompletion;
        if (startupFailure is not null || _operationLifetime.IsShuttingDown)
            return;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (IsAutoJoinWatchActive)
        {
            AnnounceAutoJoinState(
                "external-link-blocked",
                "External Roblox link was not opened",
                "Stop the active auto-join watch before opening another Roblox destination.",
                "WATCHING");
            return;
        }
        if (!ExternalRobloxLinkPolicy.TryParse(
                externalLink,
                out var link,
                out var error))
        {
            MessageBox.Show(
                this,
                LocalizeExternalLinkError(error),
                Localize("ExternalLink.RefusedTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        if (_operationBusy || _launchInProgress ||
            _accountReorderInProgress || _pendingProfile is not null)
        {
            MessageBox.Show(
                this,
                Localize("ExternalLink.BusyDetail"),
                Localize("ExternalLink.BusyTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        if (_settings.Accounts.Count == 0)
        {
            MessageBox.Show(
                this,
                Localize("ExternalLink.NoAccountDetail"),
                Localize("ExternalLink.NoAccountTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var chooser = new ExternalRobloxLinkDialog(
            link!,
            _settings.Accounts.Select(AppSettingsSnapshot.Clone).ToArray(),
            _settings.ActiveAccountKey)
        {
            Owner = this
        };
        if (chooser.ShowDialog() != true ||
            chooser.SelectedAccountKey is null)
        {
            return;
        }

        var selectedAccount = _settings.Accounts.FirstOrDefault(account =>
            account.Key.Equals(
                chooser.SelectedAccountKey,
                StringComparison.OrdinalIgnoreCase));
        if (selectedAccount is null)
        {
            MessageBox.Show(
                this,
                Localize("ExternalLink.AccountUnavailableDetail"),
                Localize("ExternalLink.AccountUnavailableTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var displayAccount = string.IsNullOrWhiteSpace(selectedAccount.Label)
            ? $"@{selectedAccount.Username}"
            : $"{selectedAccount.Label} (@{selectedAccount.Username})";
        var preview = CreateLocalizedExternalLinkPreview(link!);
        var confirmation = MessageBox.Show(
            this,
            Localize(
                "ExternalLink.ConfirmPrompt",
                preview.Title,
                displayAccount,
                preview.Detail),
            Localize("ExternalLink.ConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
            return;

        if (!string.Equals(
                _activeProfile?.Key,
                selectedAccount.Key,
                StringComparison.OrdinalIgnoreCase))
        {
            await AccountButtonClickAsync(
                new Button { Tag = selectedAccount.Key },
                _operationLifetime.Token);
        }
        if (!string.Equals(
                _activeProfile?.Key,
                selectedAccount.Key,
                StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                this,
                Localize("ExternalLink.SwitchIncompleteDetail"),
                Localize("ExternalLink.SwitchIncompleteTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        await LaunchExternalRobloxLinkAsync(
            link!,
            _operationLifetime.Token);
    }

    private async Task LaunchExternalRobloxLinkAsync(
        ExternalRobloxLink link,
        CancellationToken cancellationToken)
    {
        if (_operationBusy || _launchInProgress || IsAutoJoinWatchActive)
            return;
        SetOperationBusy(true);
        try
        {
            await LaunchAsync(cancellationToken, link);
        }
        finally
        {
            _launchInProgress = false;
            if (!_operationLifetime.IsShuttingDown)
            {
                SetOperationBusy(false);
                UpdateAutoJoinActionPresentation();
            }
        }
    }

    private (string Title, string Detail) CreateLocalizedExternalLinkPreview(
        ExternalRobloxLink link)
    {
        var title = Localize(
            link.IsPrivateServer
                ? "ExternalLink.PrivateTitle"
                : "ExternalLink.PublicTitle");
        var placeDetail = link.Target.PlaceId > 0
            ? Localize(
                "ExternalLink.PlaceDetail",
                link.Target.PlaceId.ToString(CultureInfo.InvariantCulture))
            : Localize("ExternalLink.ResolveDetail");
        var detail = link.IsPrivateServer
            ? Localize("ExternalLink.PrivateDetail", placeDetail)
            : placeDetail;
        return (title, detail);
    }

    private string LocalizeExternalLinkError(string error) =>
        error switch
        {
            "The external link is empty, too long, or contains unsafe characters." =>
                Localize("ExternalLink.ErrorEmptyUnsafe"),
            "Only complete official Roblox links can be opened externally." =>
                Localize("ExternalLink.ErrorCompleteOfficial"),
            "Only unambiguous official roblox.com HTTPS links and safe roblox: links are accepted." =>
                Localize("ExternalLink.ErrorOfficialOnly"),
            "The external link contains invalid escaping." =>
                Localize("ExternalLink.ErrorInvalidEscape"),
            "A private share code is accepted only in an official Roblox share link." =>
                Localize("ExternalLink.ErrorPrivateShareOnly"),
            "An external experience link must use the official /games/PlaceId path." =>
                Localize("ExternalLink.ErrorGamesPath"),
            "The external link contains conflicting destination parameters." =>
                Localize("ExternalLink.ErrorConflicting"),
            "The SessionDock link wrapper is invalid." =>
                Localize("ExternalLink.ErrorWrapper"),
            "Authentication tickets, cookies, tokens, and server JobIds are never accepted from external links." =>
                Localize("ExternalLink.ErrorSensitive"),
            "The external link contains unsupported or ambiguous launch parameters." =>
                Localize("ExternalLink.ErrorUnsupported"),
            "The external link repeats a launch parameter and was refused." =>
                Localize("ExternalLink.ErrorRepeated"),
            "Only Roblox server share links can be opened externally." =>
                Localize("ExternalLink.ErrorServerOnly"),
            "The external link contains an empty launch parameter." =>
                Localize("ExternalLink.ErrorEmptyParameter"),
            _ => Localize("ExternalLink.ErrorDefault")
        };
}
