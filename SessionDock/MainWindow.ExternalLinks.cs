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
        if (!ExternalRobloxLinkPolicy.TryParse(
                externalLink,
                out var link,
                out var error))
        {
            MessageBox.Show(
                this,
                error,
                "SessionDock refused the external link",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        if (_operationBusy || _launchInProgress ||
            _accountReorderInProgress || _pendingProfile is not null)
        {
            MessageBox.Show(
                this,
                "SessionDock is busy with another account operation. No account was launched. Try the link again when the current operation finishes.",
                "SessionDock is busy",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        if (_settings.Accounts.Count == 0)
        {
            MessageBox.Show(
                this,
                "Add and sign in to a SessionDock account before opening a Roblox link.",
                "No saved account",
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
                "That saved account is no longer available. No account was launched.",
                "Account unavailable",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var displayAccount = string.IsNullOrWhiteSpace(selectedAccount.Label)
            ? $"@{selectedAccount.Username}"
            : $"{selectedAccount.Label} (@{selectedAccount.Username})";
        var confirmation = MessageBox.Show(
            this,
            $"Open {link!.PreviewTitle.ToLowerInvariant()} as {displayAccount}?\n\n{link.PreviewDetail}\n\nSessionDock will request a fresh launch ticket from the selected account only after you confirm.",
            "Confirm Roblox launch",
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
                "SessionDock could not switch to the selected account. No account was launched.",
                "Account switch incomplete",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        await LaunchExternalRobloxLinkAsync(
            link,
            _operationLifetime.Token);
    }

    private async Task LaunchExternalRobloxLinkAsync(
        ExternalRobloxLink link,
        CancellationToken cancellationToken)
    {
        if (_operationBusy || _launchInProgress)
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
                LaunchButtonLabel.Text = _joinUserMode ? "Join user" : "Launch";
                SetOperationBusy(false);
            }
        }
    }
}
