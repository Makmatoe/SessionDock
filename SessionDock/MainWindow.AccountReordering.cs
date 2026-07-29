using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SessionDock.Services;

namespace SessionDock;

public partial class MainWindow
{
    private const string AccountReorderDragFormat =
        "SessionDock.Internal.AccountReorder";
    private const double AccountDragEdgeWidth = 32;
    private const double AccountDragScrollStep = 24;
    private Point? _accountDragStart;
    private string? _accountDragCandidateKey;
    private string? _accountFocusRestoreKey;
    private string? _draggedAccountKey;
    private string? _suppressedAccountClickKey;
    private bool _accountDragInProgress;
    private bool _accountReorderInProgress;

    private bool CanReorderAccounts => IsAccountReorderingAvailable(
        _operationBusy,
        _accountReorderInProgress,
        _operationLifetime.IsShuttingDown,
        _pendingProfile is not null,
        _accountSearch.IsActive,
        _settings.Accounts.Count);

    private void ConfigureAccountReordering(
        Button button,
        int positionInSet,
        int sizeOfSet)
    {
        AutomationProperties.SetPositionInSet(button, positionInSet);
        AutomationProperties.SetSizeOfSet(button, sizeOfSet);
        if (_accountSearch.IsActive)
        {
            button.ToolTip =
                "Clear the account search to reorder saved accounts.";
            AutomationProperties.SetHelpText(
                button,
                "Account reordering is paused while search is active. Clear the account search to reorder saved accounts.");
            return;
        }

        button.PreviewMouseLeftButtonDown +=
            AccountButton_PreviewMouseLeftButtonDown;
        button.PreviewMouseLeftButtonUp +=
            AccountButton_PreviewMouseLeftButtonUp;
        button.PreviewMouseMove += AccountButton_PreviewMouseMove;
        button.LostMouseCapture += AccountButton_LostMouseCapture;
        button.PreviewKeyDown += AccountButton_PreviewKeyDown;
        button.ToolTip =
            "Drag left or right to reorder. Ctrl+Shift+Left/Right also moves this account.";
        AutomationProperties.SetHelpText(
            button,
            "Drag left or right to reorder. Press Control Shift Left or Control Shift Right to move this account.");
    }

    private void AccountButton_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        _accountDragCandidateKey = null;
        _accountDragStart = null;
        if (e.StylusDevice is not null ||
            !CanReorderAccounts ||
            sender is not Button { Tag: string key } ||
            !_settings.Accounts.Any(account => account.Key.Equals(
                key,
                StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _accountDragCandidateKey = key;
        _accountDragStart = e.GetPosition(AccountsList);
    }

    private void AccountButton_PreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e) =>
        ClearAccountDragCandidate(sender);

    private void AccountButton_LostMouseCapture(
        object sender,
        MouseEventArgs e) =>
        ClearAccountDragCandidate(sender);

    private void ClearAccountDragCandidate(object sender)
    {
        if (sender is not Button { Tag: string key } ||
            _accountDragCandidateKey?.Equals(
                key,
                StringComparison.OrdinalIgnoreCase) != true)
        {
            return;
        }

        _accountDragCandidateKey = null;
        _accountDragStart = null;
    }

    private void AccountButton_PreviewMouseMove(
        object sender,
        MouseEventArgs e)
    {
        if (e.StylusDevice is not null ||
            e.LeftButton != MouseButtonState.Pressed ||
            _accountDragInProgress ||
            !CanReorderAccounts ||
            sender is not Button { Tag: string key } button ||
            _accountDragStart is not Point start ||
            _accountDragCandidateKey?.Equals(
                key,
                StringComparison.OrdinalIgnoreCase) != true)
        {
            return;
        }

        var current = e.GetPosition(AccountsList);
        if (!ShouldStartHorizontalAccountDrag(
                current.X - start.X,
                current.Y - start.Y,
                SystemParameters.MinimumHorizontalDragDistance))
        {
            return;
        }

        e.Handled = true;
        _accountDragCandidateKey = null;
        _accountDragStart = null;
        _draggedAccountKey = key;
        _suppressedAccountClickKey = key;
        _accountDragInProgress = true;
        var originalOpacity = button.Opacity;
        try
        {
            button.Opacity = 0.58;
            Mouse.Capture(null);
            var data = new DataObject();
            data.SetData(AccountReorderDragFormat, true);
            _ = DragDrop.DoDragDrop(button, data, DragDropEffects.Move);
        }
        finally
        {
            button.Opacity = originalOpacity;
            HideAccountDropIndicator();
            _draggedAccountKey = null;
            _accountDragInProgress = false;
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(() =>
                {
                    if (!_accountDragInProgress &&
                        _suppressedAccountClickKey?.Equals(
                            key,
                            StringComparison.OrdinalIgnoreCase) == true)
                    {
                        _suppressedAccountClickKey = null;
                    }
                }));
        }
    }

    private async void AccountButton_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (!CanReorderAccounts ||
            Keyboard.Modifiers != (ModifierKeys.Control | ModifierKeys.Shift) ||
            e.Key is not (Key.Left or Key.Right) ||
            sender is not Button { Tag: string sourceKey })
        {
            return;
        }

        e.Handled = true;
        var sourceIndex = _settings.Accounts.FindIndex(account =>
            account.Key.Equals(
                sourceKey,
                StringComparison.OrdinalIgnoreCase));
        if (sourceIndex < 0)
            return;

        string? beforeKey;
        if (e.Key == Key.Left)
        {
            if (sourceIndex == 0)
                return;
            beforeKey = _settings.Accounts[sourceIndex - 1].Key;
        }
        else
        {
            if (sourceIndex == _settings.Accounts.Count - 1)
                return;
            beforeKey = sourceIndex + 2 < _settings.Accounts.Count
                ? _settings.Accounts[sourceIndex + 2].Key
                : null;
        }

        await RunWindowOperationAsync(cancellationToken =>
            ReorderAccountAsync(sourceKey, beforeKey, cancellationToken));
    }

    private void AccountsScrollViewer_PreviewDragOver(
        object sender,
        DragEventArgs e)
    {
        if (!IsValidInternalAccountDrag(e))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            HideAccountDropIndicator();
            return;
        }

        var viewerPosition = e.GetPosition(AccountsScrollViewer);
        var targetOffset = CalculateAccountDragScrollOffset(
            viewerPosition.X,
            AccountsScrollViewer.ViewportWidth,
            AccountsScrollViewer.HorizontalOffset,
            AccountsScrollViewer.ScrollableWidth);
        if (targetOffset != AccountsScrollViewer.HorizontalOffset)
            AccountsScrollViewer.ScrollToHorizontalOffset(targetOffset);

        var buttons = GetSavedAccountButtons();
        var insertionIndex = GetAccountDropInsertionIndex(e, buttons);
        ShowAccountDropIndicator(insertionIndex, buttons);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private async void AccountsScrollViewer_PreviewDrop(
        object sender,
        DragEventArgs e)
    {
        if (!IsValidInternalAccountDrag(e) ||
            _draggedAccountKey is not string sourceKey)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            HideAccountDropIndicator();
            return;
        }

        var buttons = GetSavedAccountButtons();
        var insertionIndex = GetAccountDropInsertionIndex(e, buttons);
        var beforeKey = insertionIndex < buttons.Count
            ? buttons[insertionIndex].Tag as string
            : null;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        HideAccountDropIndicator();
        await RunWindowOperationAsync(cancellationToken =>
            ReorderAccountAsync(sourceKey, beforeKey, cancellationToken));
    }

    private void AccountsScrollViewer_DragLeave(
        object sender,
        DragEventArgs e) =>
        HideAccountDropIndicator();

    private bool IsValidInternalAccountDrag(DragEventArgs e) =>
        _accountDragInProgress &&
        _draggedAccountKey is not null &&
        CanReorderAccounts &&
        e.Data.GetDataPresent(AccountReorderDragFormat, autoConvert: false);

    private List<Button> GetSavedAccountButtons()
    {
        var savedKeys = _settings.Accounts
            .Select(account => account.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return AccountsList.Children
            .OfType<Button>()
            .Where(button =>
                button.Tag is string key && savedKeys.Contains(key))
            .ToList();
    }

    private int GetAccountDropInsertionIndex(
        DragEventArgs e,
        IReadOnlyList<Button> buttons)
    {
        var pointerX = e.GetPosition(AccountsList).X;
        var midpoints = buttons
            .Select(button => button.TranslatePoint(
                new Point(button.ActualWidth / 2, 0),
                AccountsList).X)
            .ToArray();
        return CalculateAccountDropInsertionIndex(pointerX, midpoints);
    }

    private void ShowAccountDropIndicator(
        int insertionIndex,
        IReadOnlyList<Button> buttons)
    {
        if (buttons.Count == 0)
        {
            HideAccountDropIndicator();
            return;
        }

        double boundaryX;
        if (insertionIndex <= 0)
        {
            boundaryX = buttons[0]
                .TranslatePoint(new Point(), AccountsList).X;
        }
        else if (insertionIndex >= buttons.Count)
        {
            var last = buttons[^1];
            boundaryX = last.TranslatePoint(new Point(), AccountsList).X +
                last.ActualWidth + last.Margin.Right / 2;
        }
        else
        {
            var previous = buttons[insertionIndex - 1];
            var next = buttons[insertionIndex];
            var previousRight = previous.TranslatePoint(
                new Point(previous.ActualWidth, 0),
                AccountsList).X;
            var nextLeft = next.TranslatePoint(new Point(), AccountsList).X;
            boundaryX = (previousRight + nextLeft) / 2;
        }

        var viewportX = AccountsList.TranslatePoint(
            new Point(boundaryX, 0),
            AccountsViewport).X;
        var maximumX = Math.Max(
            0,
            AccountsViewport.ActualWidth - AccountDropIndicator.Width);
        var transform = (TranslateTransform)AccountDropIndicator.RenderTransform;
        transform.X = Math.Clamp(
            viewportX - AccountDropIndicator.Width / 2,
            0,
            maximumX);
        AccountDropIndicator.Visibility = Visibility.Visible;
    }

    private void HideAccountDropIndicator() =>
        AccountDropIndicator.Visibility = Visibility.Collapsed;

    private async Task ReorderAccountAsync(
        string sourceKey,
        string? beforeKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanReorderAccounts ||
            !AccountOrder.WouldMoveBefore(
                _settings.Accounts,
                sourceKey,
                beforeKey))
        {
            return;
        }

        _accountReorderInProgress = true;
        if (AccountsList.IsKeyboardFocusWithin)
            _accountFocusRestoreKey = sourceKey;
        UpdateAccountControlAvailability();
        RefreshLaunchAvailability();
        try
        {
            await TryCommitSettingsMutationAsync(
                () => _ = AccountOrder.TryMoveBefore(
                    _settings.Accounts,
                    sourceKey,
                    beforeKey),
                "Account order could not be saved",
                "ACCOUNT ORDER ERROR",
                "SessionDock restored the previous order because it could not save the change. Check that %LOCALAPPDATA%\\SessionDock is writable and not locked, then try again.");
        }
        finally
        {
            _accountReorderInProgress = false;
            if (!_operationLifetime.IsShuttingDown)
            {
                RenderAccountList();
                RefreshLaunchAvailability();
            }
        }
    }

    private void UpdateAccountControlAvailability()
    {
        var enabled = !_operationBusy &&
                      !_accountReorderInProgress &&
                      !IsAutoJoinWatchActive;
        var activeProfileVisible =
            _activeProfile is not null &&
            _accountSearch.MatchesAccount(
                _activeProfile,
                _activeProfile.Group);
        AccountsList.IsEnabled = enabled;
        AddAccountButton.IsEnabled =
            enabled && _pendingProfile is null;
        var activeAccountControlsEnabled =
            AreActiveAccountControlsAvailable(
                enabled,
                _pendingProfile is not null,
                _activeProfile is not null,
                activeProfileVisible);
        EditAccountButton.IsEnabled = activeAccountControlsEnabled;
        ResetButton.IsEnabled = activeAccountControlsEnabled;

        var activeProfileFilteredOut =
            _activeProfile is not null && !activeProfileVisible;
        EditAccountButton.ToolTip = activeProfileFilteredOut
            ? "Clear the account search or select a visible account before editing."
            : "Change the selected account label, group, and color";
        ResetButton.ToolTip = activeProfileFilteredOut
            ? "Clear the account search or select a visible account before removing."
            : "Clear and remove the selected local account profile";
        AutomationProperties.SetHelpText(
            EditAccountButton,
            activeProfileFilteredOut
                ? "The selected account is hidden by account search. Clear the search or select a visible account before editing."
                : "Change the selected account label, group, and color.");
        AutomationProperties.SetHelpText(
            ResetButton,
            activeProfileFilteredOut
                ? "The selected account is hidden by account search. Clear the search or select a visible account before removing."
                : "Clear and remove the selected local account profile.");
        RetryFailedBatchButton.IsEnabled =
            enabled && _batchRetryState is not null;
    }

    internal static int CalculateAccountDropInsertionIndex(
        double pointerX,
        IReadOnlyList<double> itemMidpoints)
    {
        ArgumentNullException.ThrowIfNull(itemMidpoints);
        for (var index = 0; index < itemMidpoints.Count; index++)
        {
            if (pointerX < itemMidpoints[index])
                return index;
        }

        return itemMidpoints.Count;
    }

    internal static bool IsAccountReorderingAvailable(
        bool operationBusy,
        bool reorderInProgress,
        bool shuttingDown,
        bool hasPendingProfile,
        bool searchActive,
        int accountCount) =>
        !operationBusy &&
        !reorderInProgress &&
        !shuttingDown &&
        !hasPendingProfile &&
        !searchActive &&
        accountCount >= 2;

    internal static bool AreActiveAccountControlsAvailable(
        bool controlsEnabled,
        bool hasPendingProfile,
        bool hasActiveProfile,
        bool activeProfileVisible) =>
        controlsEnabled &&
        !hasPendingProfile &&
        hasActiveProfile &&
        activeProfileVisible;

    internal static bool ShouldStartHorizontalAccountDrag(
        double deltaX,
        double deltaY,
        double minimumHorizontalDistance)
    {
        var horizontal = Math.Abs(deltaX);
        return horizontal >= Math.Max(0, minimumHorizontalDistance) &&
               horizontal >= Math.Abs(deltaY);
    }

    internal static double CalculateAccountDragScrollOffset(
        double pointerX,
        double viewportWidth,
        double currentOffset,
        double scrollableWidth)
    {
        var maximumOffset = Math.Max(0, scrollableWidth);
        var clampedCurrent = Math.Clamp(currentOffset, 0, maximumOffset);
        if (viewportWidth <= 0)
            return clampedCurrent;

        var edgeWidth = Math.Min(AccountDragEdgeWidth, viewportWidth / 2);
        var target = pointerX < edgeWidth
            ? clampedCurrent - AccountDragScrollStep
            : pointerX > viewportWidth - edgeWidth
                ? clampedCurrent + AccountDragScrollStep
                : clampedCurrent;
        return Math.Clamp(target, 0, maximumOffset);
    }
}
