using System.Windows;
using System.Windows.Controls;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock;

public partial class MainWindow
{
    private bool _returnToAccountsAfterBrowser;
    private Task<bool>? _destinationEditorResolutionTask;
    private bool _destinationCloseRequested;

    private bool _refreshingSetupWorkspace;
    private bool _suppressDestinationSelectionChange;
    private string? _editingDestinationId;
    private string? _managedAccountKey;
    private DestinationEditorBaseline? _destinationEditorBaseline;
    private AccessibilityLiveRegion? _namedDestinationValidationLiveRegion;
    private TaskCompletionSource<DestinationEditorDecision>?
        _destinationEditorDecision;
    private IReadOnlyList<DestinationAccountAssignmentRow>
        _destinationAccountAssignmentRows = [];

    private void RefreshDestinationsWorkspace(string? selectId = null)
    {
        if (_refreshingSetupWorkspace)
            return;
        _refreshingSetupWorkspace = true;
        try
        {
            var requestedId = selectId ?? _editingDestinationId;
            var rows = (_settings.NamedDestinations ?? [])
                .Where(destination => destination is not null)
                .Select(destination => new NamedDestinationPageRow(
                    destination.Id,
                    destination.Name,
                    destination.Value))
                .OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            NamedDestinationsList.ItemsSource = rows;
            DestinationsEmptyText.Visibility = rows.Length == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            var selected = rows.FirstOrDefault(row => string.Equals(
                row.Id,
                requestedId,
                StringComparison.OrdinalIgnoreCase));
            NamedDestinationsList.SelectedItem = selected;
            if (selected is null)
                PrepareNewDestinationEditor();
            else
                LoadDestinationEditor(selected.Id);
        }
        finally
        {
            _refreshingSetupWorkspace = false;
        }
    }

    private async void NamedDestinationsList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_refreshingSetupWorkspace ||
            _suppressDestinationSelectionChange)
            return;
        var requestedId =
            (NamedDestinationsList.SelectedItem as NamedDestinationPageRow)?.Id;
        if (string.Equals(
                requestedId,
                _editingDestinationId,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SelectDestinationListItem(_editingDestinationId);
        if (!await TryResolveDestinationEditorChangesAsync() ||
            _destinationCloseRequested)
            return;

        SelectDestinationListItem(requestedId);
        if (requestedId is null)
            PrepareNewDestinationEditor();
        else
            LoadDestinationEditor(requestedId);
    }

    private async void NewDestinationButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if ((_editingDestinationId is not null ||
             HasDestinationEditorChanges()) &&
            (!await TryResolveDestinationEditorChangesAsync() ||
             _destinationCloseRequested))
        {
            return;
        }

        SelectDestinationListItem(null);
        PrepareNewDestinationEditor();
        DestinationNameBox.Focus();
    }

    private void PrepareNewDestinationEditor()
    {
        _editingDestinationId = null;
        DestinationNameBox.Text = string.Empty;
        DestinationValueBox.Text = string.Empty;
        DestinationEditorHeadingText.Text = Localize(
            "Destinations.EditorNewHeading");
        NamedDestinationValidationLiveRegion.Update(
            string.Empty,
            announceChanges: false);
        DeleteDestinationButton.IsEnabled = false;
        RefreshDestinationAssignmentRows([]);
        CaptureDestinationEditorBaseline();
    }

    private void LoadDestinationEditor(string destinationId)
    {
        var destination = (_settings.NamedDestinations ?? [])
            .FirstOrDefault(candidate => candidate is not null &&
                string.Equals(
                    candidate.Id,
                    destinationId,
                    StringComparison.OrdinalIgnoreCase));
        if (destination is null)
        {
            PrepareNewDestinationEditor();
            return;
        }

        _editingDestinationId = destination.Id;
        DestinationNameBox.Text = destination.Name;
        DestinationValueBox.Text = destination.Value;
        DestinationEditorHeadingText.Text = Localize(
            "Destinations.EditorEditHeading",
            destination.Name);
        NamedDestinationValidationLiveRegion.Update(
            string.Empty,
            announceChanges: false);
        DeleteDestinationButton.IsEnabled = true;
        RefreshDestinationAssignmentRows(destination.AccountKeys ?? []);
        CaptureDestinationEditorBaseline();
    }

    private void RefreshDestinationAssignmentRows(
        IEnumerable<string> selectedAccountKeys)
    {
        var selected = selectedAccountKeys.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        _destinationAccountAssignmentRows = _settings.Accounts
            .Where(account => account is not null)
            .Select(account => new DestinationAccountAssignmentRow(
                account.Key,
                AccountDisplayName(account),
                AccountDestinationSummary(account),
                selected.Contains(account.Key)))
            .ToArray();
        DestinationAccountAssignmentsList.ItemsSource =
            _destinationAccountAssignmentRows;
    }

    private async void SaveDestinationButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await RunWindowOperationAsync(async _ =>
        {
            await SaveDestinationAsync();
        });
    }

    private async Task<bool> SaveDestinationAsync()
    {
        // Resolve the Advanced workspace's debounced custom draft first.
        // The named assignment below is the newer user intent and must win.
        if (!await FlushDestinationPersistenceAsync())
            return false;

        var name = DestinationNameBox.Text;
        var value = DestinationValueBox.Text;
        var selectedKeys = _destinationAccountAssignmentRows
            .Where(row => row.IsAssigned)
            .Select(row => row.AccountKey)
            .ToArray();
        var validationProbe = AppSettingsSnapshot.Create(_settings);
        if (!NamedDestinationPolicy.TryUpsert(
                validationProbe,
                _editingDestinationId,
                name,
                value,
                selectedKeys,
                out _,
                out var validationError))
        {
            ShowDestinationValidation(validationError);
            return false;
        }

        var savedId = string.Empty;
        var applied = false;
        if (!await TryCommitSettingsMutationAsync(
                () => applied = NamedDestinationPolicy.TryUpsert(
                    _settings,
                    _editingDestinationId,
                    name,
                    value,
                    selectedKeys,
                    out savedId,
                    out _),
                Localize("Destinations.SaveFailureTitle"),
                Localize("Main.SettingsErrorBadge")))
        {
            return false;
        }
        if (!applied)
        {
            ShowDestinationValidation(
                "Validation.NamedDestination.ValueInvalid");
            return false;
        }

        NamedDestinationValidationLiveRegion.Update(
            string.Empty,
            announceChanges: false);
        ShowDestinationForProfile(_activeProfile);
        RefreshLaunchAvailability();
        RefreshDestinationsWorkspace(savedId);
        RefreshAccountsWorkspace();
        return true;
    }

    private async void DeleteDestinationButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_editingDestinationId is null)
            return;
        var destination = (_settings.NamedDestinations ?? [])
            .FirstOrDefault(candidate => candidate is not null &&
                string.Equals(
                    candidate.Id,
                    _editingDestinationId,
                    StringComparison.OrdinalIgnoreCase));
        if (destination is null)
            return;
        var confirmation = MessageBox.Show(
            this,
            Localize("Destinations.DeleteConfirm", destination.Name),
            Localize("Destinations.DeleteTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
            return;

        var destinationId = destination.Id;
        await RunWindowOperationAsync(async _ =>
        {
            var removed = false;
            if (!await TryCommitSettingsMutationAsync(
                    () => removed = NamedDestinationPolicy.Delete(
                        _settings,
                        destinationId),
                    Localize("Destinations.DeleteFailureTitle"),
                    Localize("Main.SettingsErrorBadge")) ||
                !removed)
            {
                return;
            }

            RefreshDestinationsWorkspace();
            RefreshAccountsWorkspace();
        });
    }

    private void ShowDestinationValidation(string key)
    {
        NamedDestinationValidationLiveRegion.Update(
            Localize(key),
            severity: AccessibilityLiveRegionSeverity.Assertive);
        if (string.Equals(
                key,
                "Validation.NamedDestination.ValueInvalid",
                StringComparison.Ordinal))
        {
            DestinationValueBox.Focus();
            return;
        }

        if (key is "Validation.NamedDestination.NameRequired"
            or "Validation.NamedDestination.NameTooLong"
            or "Validation.NamedDestination.NameUnique")
        {
            DestinationNameBox.Focus();
            return;
        }

        SaveDestinationButton.Focus();
    }

    private AccessibilityLiveRegion NamedDestinationValidationLiveRegion =>
        _namedDestinationValidationLiveRegion ??=
            new AccessibilityLiveRegion(DestinationEditorValidationText);

    private void CaptureDestinationEditorBaseline()
    {
        _destinationEditorBaseline = new DestinationEditorBaseline(
            _editingDestinationId,
            DestinationNameBox.Text,
            DestinationValueBox.Text,
            _destinationAccountAssignmentRows
                .Where(row => row.IsAssigned)
                .Select(row => row.AccountKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private bool HasDestinationEditorChanges()
    {
        var baseline = _destinationEditorBaseline;
        if (baseline is null)
            return false;
        if (!string.Equals(
                baseline.DestinationId,
                _editingDestinationId,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                baseline.Name,
                DestinationNameBox.Text,
                StringComparison.Ordinal) ||
            !string.Equals(
                baseline.Value,
                DestinationValueBox.Text,
                StringComparison.Ordinal))
        {
            return true;
        }

        var assignedKeys = _destinationAccountAssignmentRows
            .Where(row => row.IsAssigned)
            .Select(row => row.AccountKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return !baseline.AssignedAccountKeys.SetEquals(assignedKeys);
    }

    private Task<bool> TryResolveDestinationEditorChangesAsync()
    {
        if (!HasDestinationEditorChanges())
            return Task.FromResult(true);

        return _destinationEditorResolutionTask ??=
            ResolveDestinationEditorChangesCoreAsync();
    }

    private async Task<bool> ResolveDestinationEditorChangesCoreAsync()
    {
        try
        {
            var decision = await ShowDestinationEditorDecisionAsync();
            if (decision == DestinationEditorDecision.Cancel)
                return false;
            if (decision == DestinationEditorDecision.Discard)
                return true;

            var saved = false;
            await RunWindowOperationAsync(async _ =>
            {
                saved = await SaveDestinationAsync();
            });
            return saved;
        }
        finally
        {
            _destinationEditorResolutionTask = null;
        }
    }

    private Task<DestinationEditorDecision>
        ShowDestinationEditorDecisionAsync()
    {
        if (_destinationEditorDecision is not null)
            return _destinationEditorDecision.Task;

        _destinationEditorDecision = new TaskCompletionSource<
            DestinationEditorDecision>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        DestinationUnsavedOverlay.Visibility = Visibility.Visible;
        DestinationUnsavedSaveButton.Focus();
        return _destinationEditorDecision.Task;
    }

    private void DestinationUnsavedSaveButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        CompleteDestinationEditorDecision(DestinationEditorDecision.Save);
    }

    private void DestinationUnsavedDiscardButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        CompleteDestinationEditorDecision(DestinationEditorDecision.Discard);
    }

    private void DestinationUnsavedCancelButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        CompleteDestinationEditorDecision(DestinationEditorDecision.Cancel);
    }

    private void CompleteDestinationEditorDecision(
        DestinationEditorDecision decision)
    {
        var completion = _destinationEditorDecision;
        if (completion is null)
            return;
        _destinationEditorDecision = null;
        DestinationUnsavedOverlay.Visibility = Visibility.Collapsed;
        completion.TrySetResult(decision);
    }

    private void SelectDestinationListItem(string? destinationId)
    {
        _suppressDestinationSelectionChange = true;
        try
        {
            NamedDestinationsList.SelectedItem = destinationId is null
                ? null
                : NamedDestinationsList.Items
                    .OfType<NamedDestinationPageRow>()
                    .FirstOrDefault(row => string.Equals(
                        row.Id,
                        destinationId,
                        StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _suppressDestinationSelectionChange = false;
        }
    }

    private void RefreshAccountsWorkspace(string? selectKey = null)
    {
        if (_refreshingSetupWorkspace)
            return;
        _refreshingSetupWorkspace = true;
        try
        {
            var requestedKey = selectKey ?? _managedAccountKey ??
                _settings.ActiveAccountKey;
            var rows = _settings.Accounts
                .Where(account => account is not null)
                .Select(account => new ManagedAccountPageRow(
                    account.Key,
                    AccountDisplayName(account),
                    $"@{account.Username}  \u00B7  {account.UserId}",
                    AccountDestinationSummary(account)))
                .ToArray();
            ManageAccountsList.ItemsSource = rows;
            AccountsEmptyText.Visibility = rows.Length == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            var selected = rows.FirstOrDefault(row => string.Equals(
                row.AccountKey,
                requestedKey,
                StringComparison.OrdinalIgnoreCase)) ?? rows.FirstOrDefault();
            ManageAccountsList.SelectedItem = selected;
            _managedAccountKey = selected?.AccountKey;
            UpdateManageAccountActions();
        }
        finally
        {
            _refreshingSetupWorkspace = false;
        }
    }

    private void ManageAccountsList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_refreshingSetupWorkspace)
            return;
        _managedAccountKey =
            (ManageAccountsList.SelectedItem as ManagedAccountPageRow)?.AccountKey;
        UpdateManageAccountActions();
    }

    private void UpdateManageAccountActions()
    {
        var hasSelection = SelectedManagedAccount() is not null;
        var canMutate = !_operationBusy &&
            !_accountReorderInProgress &&
            _pendingProfile is null;
        ManageAccountsAddButton.IsEnabled = canMutate;
        ManageAccountsEditButton.IsEnabled = canMutate && hasSelection;
        ManageAccountsRemoveButton.IsEnabled = canMutate && hasSelection;
        ManageAccountsSignInButton.IsEnabled = canMutate && hasSelection;
    }

    private AccountProfile? SelectedManagedAccount() =>
        _managedAccountKey is null
            ? null
            : _settings.Accounts.FirstOrDefault(account => account is not null &&
                string.Equals(
                    account.Key,
                    _managedAccountKey,
                    StringComparison.OrdinalIgnoreCase));

    private async void ManageAccountsAddButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _returnToAccountsAfterBrowser = true;
        ShowAdvancedWorkspace();
        await RunWindowOperationAsync(AddAccountButtonClickAsync);
        if (BrowserPanel.Visibility != Visibility.Visible &&
            _pendingProfile is null)
        {
            ReturnToAccountsAfterBrowserIfRequested();
        }
    }

    private async void ManageAccountsEditButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var account = SelectedManagedAccount();
        if (account is null)
            return;
        await RunWindowOperationAsync(_ => EditAccountProfileAsync(account));
    }

    private async void ManageAccountsRemoveButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var account = SelectedManagedAccount();
        if (account is null)
            return;
        await RunWindowOperationAsync(cancellationToken =>
            RemoveAccountAsync(account, cancellationToken));
    }

    private async void ManageAccountsSignInButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var account = SelectedManagedAccount();
        if (account is null)
            return;
        ShowAdvancedWorkspace();
        await RunWindowOperationAsync(async cancellationToken =>
        {
            if (!string.Equals(
                    account.Key,
                    _activeProfile?.Key,
                    StringComparison.OrdinalIgnoreCase))
            {
                await AccountButtonClickAsync(
                    account.Key,
                    cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(
                    account.Key,
                    _activeProfile?.Key,
                    StringComparison.OrdinalIgnoreCase))
            {
                _returnToAccountsAfterBrowser = true;
                await SignInButtonClickAsync(cancellationToken);
            }
        });
        if (BrowserPanel.Visibility != Visibility.Visible)
        {
            if (_returnToAccountsAfterBrowser)
            {
                ReturnToAccountsAfterBrowserIfRequested(account.Key);
            }
            else
            {
                _managedAccountKey = account.Key;
                NavigateToWorkspace(
                    MainWorkspacePage.Accounts,
                    resizeWindow: true);
            }
        }
    }

    private void ReturnToAccountsAfterBrowserIfRequested(
        string? accountKey = null)
    {
        if (!_returnToAccountsAfterBrowser)
            return;

        _returnToAccountsAfterBrowser = false;
        _managedAccountKey = accountKey ?? _activeProfile?.Key;
        NavigateToWorkspace(MainWorkspacePage.Accounts, resizeWindow: true);
        if (ManageAccountsList.Items.Count > 0)
            ManageAccountsList.Focus();
        else
            ManageAccountsAddButton.Focus();
    }

    private string AccountDestinationSummary(AccountProfile account)
    {
        var assignedId = NamedDestinationPolicy.GetAssignedDestinationId(
            _settings,
            account.Key);
        var named = assignedId is null
            ? null
            : (_settings.NamedDestinations ?? []).FirstOrDefault(destination =>
                destination is not null &&
                string.Equals(
                    destination.Id,
                    assignedId,
                    StringComparison.OrdinalIgnoreCase));
        if (named is not null)
            return Localize("Accounts.NamedDestination", named.Name);
        return string.IsNullOrWhiteSpace(account.Destination)
            ? Localize("Accounts.NoDestination")
            : Localize("Accounts.CustomDestination", account.Destination);
    }

    private static string AccountDisplayName(AccountProfile account) =>
        string.IsNullOrWhiteSpace(account.Label)
            ? $"@{account.Username}"
            : $"{account.Label} (@{account.Username})";

    private sealed record NamedDestinationPageRow(
        string Id,
        string Name,
        string Value);

    private sealed record DestinationEditorBaseline(
        string? DestinationId,
        string Name,
        string Value,
        HashSet<string> AssignedAccountKeys);

    private enum DestinationEditorDecision
    {
        Save,
        Discard,
        Cancel
    }

    private sealed class DestinationAccountAssignmentRow(
        string accountKey,
        string displayName,
        string destinationSummary,
        bool isAssigned)
    {
        public string AccountKey { get; } = accountKey;
        public string DisplayName { get; } = displayName;
        public string DestinationSummary { get; } = destinationSummary;
        public bool IsAssigned { get; set; } = isAssigned;
    }

    private sealed record ManagedAccountPageRow(
        string AccountKey,
        string DisplayName,
        string Identity,
        string DestinationSummary);
}
