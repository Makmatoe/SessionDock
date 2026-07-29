using System.Windows;
using System.Windows.Controls;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock;

public partial class BatchLaunchDialog : Window
{
    private readonly IReadOnlyList<BatchLaunchAccountOption> _accounts;
    private readonly List<BatchLaunchPreset> _presets;
    private readonly bool _retryMode;

    public IReadOnlyList<AccountProfile> SelectedAccounts { get; private set; } = [];
    public TimeSpan Delay { get; private set; } =
        TimeSpan.FromSeconds(BatchLaunchPreferences.DefaultDelaySeconds);
    public int DelaySeconds => (int)Delay.TotalSeconds;
    public bool PresetsChanged { get; private set; }
    public IReadOnlyList<BatchLaunchPreset> UpdatedPresets =>
        _presets.Select(AppSettingsSnapshot.Clone).ToArray();

    public BatchLaunchDialog(IEnumerable<AccountProfile> accounts)
        : this(
            accounts,
            [],
            BatchLaunchPreferences.DefaultDelaySeconds)
    {
    }

    internal BatchLaunchDialog(
        IEnumerable<AccountProfile> accounts,
        IEnumerable<BatchLaunchPreset> presets,
        int delaySeconds,
        IReadOnlyCollection<string>? initiallySelectedAccountKeys = null,
        bool retryMode = false)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(presets);
        InitializeComponent();
        WindowLayoutService.FitToWorkArea(this);
        _retryMode = retryMode;
        var accountArray = accounts.ToArray();
        var selectedKeys = initiallySelectedAccountKeys is null
            ? null
            : new HashSet<string>(
                initiallySelectedAccountKeys,
                StringComparer.OrdinalIgnoreCase);
        _accounts = accountArray
            .Select(account => new BatchLaunchAccountOption(
                account,
                selectedKeys is null || selectedKeys.Contains(account.Key)))
            .ToArray();
        _presets = BatchLaunchPreferences.NormalizePresets(
                presets,
                accountArray)
            .Select(AppSettingsSnapshot.Clone)
            .ToList();
        AccountsList.ItemsSource = _accounts;
        SetDelay(delaySeconds);
        PopulateGroups();
        RefreshPresetList();

        if (_retryMode)
        {
            Title = "Retry failed launches";
            DialogTitleText.Text = "Retry failed only";
            DialogIntroText.Text = _accounts.Count == 1
                ? "Review the failed account below before retrying it. No account from the completed part of the batch can be added here."
                : "Review the failed accounts below before retrying them. Accounts that already started are not included.";
            CloseClientsWarningText.Text =
                "Retrying closes every currently running verified Roblox Player instance, including clients started by the previous batch.";
            PresetsPanel.Visibility = Visibility.Collapsed;
            StartButton.Content = _accounts.Count == 1
                ? "Retry account"
                : "Retry selected";
            System.Windows.Automation.AutomationProperties.SetName(
                StartButton,
                _accounts.Count == 1
                    ? "Retry failed account"
                    : "Retry selected failed accounts");
        }
    }

    private void PopulateGroups()
    {
        var groups = _accounts
            .Select(account => account.GroupName)
            .Where(group => group is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new BatchGroupOption(group!))
            .ToArray();
        GroupComboBox.ItemsSource = groups;
        GroupComboBox.SelectedIndex = groups.Length == 0 ? -1 : 0;
        GroupComboBox.IsEnabled = groups.Length > 0;
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e) =>
        SetAllSelected(true);

    private void SelectNoneButton_Click(object sender, RoutedEventArgs e) =>
        SetAllSelected(false);

    private void SelectGroupButton_Click(object sender, RoutedEventArgs e)
    {
        if (GroupComboBox.SelectedItem is not BatchGroupOption group)
        {
            SetValidation("No account group is available.");
            return;
        }

        foreach (var account in _accounts)
        {
            account.IsSelected = string.Equals(
                account.GroupName,
                group.Name,
                StringComparison.OrdinalIgnoreCase);
        }
        AccountsList.Items.Refresh();
        SetStatus($"Selected {SelectedOptionCount()} account(s) in {group.Name}.");
    }

    private void SetAllSelected(bool selected)
    {
        foreach (var account in _accounts)
            account.IsSelected = selected;
        AccountsList.Items.Refresh();
        SetStatus(selected
            ? $"Selected all {_accounts.Count} account(s)."
            : "Cleared the account selection.");
    }

    private void LoadPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (PresetComboBox.SelectedItem is not BatchLaunchPreset preset)
        {
            SetValidation("Choose a saved preset to load.");
            return;
        }

        var presetKeys = new HashSet<string>(
            preset.AccountKeys,
            StringComparer.OrdinalIgnoreCase);
        foreach (var account in _accounts)
            account.IsSelected = presetKeys.Contains(account.Profile.Key);
        AccountsList.Items.Refresh();
        SetDelay(preset.DelaySeconds);
        PresetNameBox.Text = preset.Name;
        SetStatus(
            $"Loaded {preset.Name}: {SelectedOptionCount()} account(s), {preset.DelaySeconds} second delay.");
    }

    private void SavePresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedDelay(out var delaySeconds))
        {
            SetValidation("Select a valid delay before saving the preset.");
            return;
        }

        if (!BatchLaunchPreferences.TryCreatePreset(
                PresetNameBox.Text,
                GetSelectedProfiles(),
                delaySeconds,
                out var preset,
                out var error))
        {
            SetValidation(error);
            return;
        }

        var existingIndex = _presets.FindIndex(candidate =>
            candidate.Name.Equals(
                preset!.Name,
                StringComparison.OrdinalIgnoreCase));
        if (existingIndex < 0 &&
            _presets.Count >= BatchLaunchPreferences.MaximumPresets)
        {
            SetValidation(
                $"You can save up to {BatchLaunchPreferences.MaximumPresets} batch presets. Delete one before adding another.");
            return;
        }

        if (existingIndex >= 0)
            _presets[existingIndex] = preset!;
        else
            _presets.Add(preset!);
        PresetsChanged = true;
        RefreshPresetList(preset!.Name);
        SetStatus(existingIndex >= 0
            ? $"Updated preset {preset.Name}."
            : $"Saved preset {preset.Name}.");
    }

    private void DeletePresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (PresetComboBox.SelectedItem is not BatchLaunchPreset preset)
        {
            SetValidation("Choose a saved preset to delete.");
            return;
        }

        _presets.Remove(preset);
        PresetsChanged = true;
        RefreshPresetList();
        PresetNameBox.Clear();
        SetStatus($"Deleted preset {preset.Name}.");
    }

    private void RefreshPresetList(string? selectedName = null)
    {
        PresetComboBox.ItemsSource = null;
        PresetComboBox.ItemsSource = _presets;
        var selected = selectedName is null
            ? _presets.FirstOrDefault()
            : _presets.FirstOrDefault(preset => preset.Name.Equals(
                selectedName,
                StringComparison.OrdinalIgnoreCase));
        PresetComboBox.SelectedItem = selected;
        PresetComboBox.IsEnabled = _presets.Count > 0;
        PresetCountText.Text = _presets.Count == 0
            ? "None yet"
            : $"{_presets.Count} of {BatchLaunchPreferences.MaximumPresets}";
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedProfiles();
        var minimumSelection = _retryMode ? 1 : 2;
        if (selected.Count < minimumSelection)
        {
            SetValidation(_retryMode
                ? "Select at least one failed account to retry."
                : "Select at least two accounts for a batch launch.");
            return;
        }
        if (string.IsNullOrWhiteSpace(selected[0].Destination))
        {
            SetValidation(
                "The first selected account needs a destination. Blank accounts after it will use that destination.");
            return;
        }

        if (!TryGetSelectedDelay(out var delaySeconds))
        {
            SetValidation("Select a valid delay.");
            return;
        }

        SelectedAccounts = selected;
        Delay = TimeSpan.FromSeconds(delaySeconds);
        DialogResult = true;
    }

    private IReadOnlyList<AccountProfile> GetSelectedProfiles() =>
        _accounts
            .Where(account => account.IsSelected)
            .Select(account => account.Profile)
            .ToArray();

    private int SelectedOptionCount() =>
        _accounts.Count(account => account.IsSelected);

    private bool TryGetSelectedDelay(out int delaySeconds)
    {
        if (DelayComboBox.SelectedItem is ComboBoxItem { Tag: string value } &&
            int.TryParse(value, out delaySeconds) &&
            BatchLaunchPreferences.SupportedDelaySeconds.Contains(delaySeconds))
        {
            return true;
        }

        delaySeconds = BatchLaunchPreferences.DefaultDelaySeconds;
        return false;
    }

    private void SetDelay(int delaySeconds)
    {
        var normalized = BatchLaunchPreferences.NormalizeDelaySeconds(
            delaySeconds);
        DelayComboBox.SelectedItem = DelayComboBox.Items
            .OfType<ComboBoxItem>()
            .First(item => string.Equals(
                item.Tag as string,
                normalized.ToString(),
                StringComparison.Ordinal));
        Delay = TimeSpan.FromSeconds(normalized);
    }

    private void SetValidation(string message)
    {
        ValidationText.SetResourceReference(
            TextBlock.ForegroundProperty,
            "ErrorTextBrush");
        ValidationText.Text = message;
    }

    private void SetStatus(string message)
    {
        ValidationText.SetResourceReference(
            TextBlock.ForegroundProperty,
            "MutedBrush");
        ValidationText.Text = message;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) =>
        DialogResult = false;

    private sealed class BatchLaunchAccountOption(
        AccountProfile profile,
        bool isSelected)
    {
        public AccountProfile Profile { get; } = profile;
        public string DisplayName { get; } = profile.Label ?? $"@{profile.Username}";
        public string Identity { get; } = profile.Label is null
            ? $"User ID {profile.UserId}"
            : $"@{profile.Username}  -  User ID {profile.UserId}";
        public string ColorHex { get; } = profile.ColorHex ?? "#7C5CFC";
        public string? GroupName { get; } =
            BatchLaunchPreferences.NormalizeAccountGroup(profile.Group);
        public Visibility GroupVisibility => GroupName is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        public string DestinationSummary { get; } =
            string.IsNullOrWhiteSpace(profile.Destination)
                ? "Destination: uses first selected"
                : $"Destination: {profile.Destination.Trim()}";
        public string DestinationToolTip { get; } =
            string.IsNullOrWhiteSpace(profile.Destination)
                ? "This account will use the first selected account's destination."
                : profile.Destination.Trim();
        public string AutomationName { get; } =
            $"Include {profile.Label ?? $"@{profile.Username}"}";
        public string AutomationHelpText { get; } =
            string.IsNullOrWhiteSpace(profile.Group)
                ? "Choose whether this account is part of the batch"
                : $"Account group: {profile.Group}";
        public bool IsSelected { get; set; } = isSelected;
    }

    private sealed record BatchGroupOption(string Name);
}
