using System.Globalization;
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
    private readonly AccessibilityLiveRegion _validationLiveRegion;

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
        _validationLiveRegion = new AccessibilityLiveRegion(ValidationText);
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
                selectedKeys is null || selectedKeys.Contains(account.Key),
                Localization))
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
            Title = Localize("Batch.RetryTitle");
            DialogTitleText.Text = Localize("Batch.RetryHeading");
            DialogIntroText.Text = _accounts.Count == 1
                ? Localize("Batch.RetryIntroOne")
                : Localize("Batch.RetryIntroMany");
            CloseClientsWarningText.Text = Localize("Batch.RetryWarning");
            PresetsPanel.Visibility = Visibility.Collapsed;
            StartButton.Content = _accounts.Count == 1
                ? Localize("Batch.RetryStartOne")
                : Localize("Batch.RetryStartMany");
            System.Windows.Automation.AutomationProperties.SetName(
                StartButton,
                _accounts.Count == 1
                    ? Localize("Batch.RetryStartNameOne")
                    : Localize("Batch.RetryStartNameMany"));
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
            SetValidation(Localize("Batch.NoGroup"));
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
        var selectedCount = SelectedOptionCount();
        SetStatus(selectedCount == 1
            ? Localize("Batch.SelectedGroupOne", group.Name)
            : Localize(
                "Batch.SelectedGroupMany",
                selectedCount,
                group.Name));
    }

    private void SetAllSelected(bool selected)
    {
        foreach (var account in _accounts)
            account.IsSelected = selected;
        AccountsList.Items.Refresh();
        SetStatus(selected
            ? _accounts.Count == 1
                ? Localize("Batch.SelectedAllOne")
                : Localize("Batch.SelectedAllMany", _accounts.Count)
            : Localize("Batch.ClearedSelection"));
    }

    private void LoadPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (PresetComboBox.SelectedItem is not BatchLaunchPreset preset)
        {
            SetValidation(Localize("Batch.ChoosePresetToLoad"));
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
        var accountCount = SelectedOptionCount();
        SetStatus(accountCount == 1
            ? Localize(
                "Batch.LoadedPresetOne",
                preset.Name,
                preset.DelaySeconds)
            : Localize(
                "Batch.LoadedPresetMany",
                preset.Name,
                accountCount,
                preset.DelaySeconds));
    }

    private void SavePresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedDelay(out var delaySeconds))
        {
            SetValidation(Localize("Batch.InvalidDelayForPreset"));
            return;
        }

        var selectedProfiles = GetSelectedProfiles();
        if (BatchLaunchPreferences.NormalizePresetName(PresetNameBox.Text) is null)
        {
            SetValidation(Localize("Batch.PresetNameRequired"));
            return;
        }
        if (selectedProfiles.Count < 2)
        {
            SetValidation(Localize("Batch.PresetMinimumAccounts"));
            return;
        }
        if (selectedProfiles.Count > BatchLaunchPreferences.MaximumAccountsPerPreset)
        {
            SetValidation(Localize(
                "Batch.PresetMaximumAccounts",
                BatchLaunchPreferences.MaximumAccountsPerPreset));
            return;
        }

        if (!BatchLaunchPreferences.TryCreatePreset(
                PresetNameBox.Text,
                selectedProfiles,
                delaySeconds,
                out var preset,
                out _))
        {
            SetValidation(Localize("Batch.PresetInvalid"));
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
                Localize(
                    "Batch.MaximumPresets",
                    BatchLaunchPreferences.MaximumPresets));
            return;
        }

        if (existingIndex >= 0)
            _presets[existingIndex] = preset!;
        else
            _presets.Add(preset!);
        PresetsChanged = true;
        RefreshPresetList(preset!.Name);
        SetStatus(existingIndex >= 0
            ? Localize("Batch.UpdatedPreset", preset.Name)
            : Localize("Batch.SavedPreset", preset.Name));
    }

    private void DeletePresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (PresetComboBox.SelectedItem is not BatchLaunchPreset preset)
        {
            SetValidation(Localize("Batch.ChoosePresetToDelete"));
            return;
        }

        _presets.Remove(preset);
        PresetsChanged = true;
        RefreshPresetList();
        PresetNameBox.Clear();
        SetStatus(Localize("Batch.DeletedPreset", preset.Name));
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
            ? Localize("Batch.NoPresets")
            : Localize(
                "Batch.PresetCount",
                _presets.Count,
                BatchLaunchPreferences.MaximumPresets);
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedProfiles();
        var minimumSelection = _retryMode ? 1 : 2;
        if (selected.Count < minimumSelection)
        {
            SetValidation(_retryMode
                ? Localize("Batch.RetryMinimumSelection")
                : Localize("Batch.MinimumSelection"));
            return;
        }
        if (string.IsNullOrWhiteSpace(selected[0].Destination))
        {
            SetValidation(
                Localize("Batch.FirstDestinationRequired"));
            return;
        }

        if (!TryGetSelectedDelay(out var delaySeconds))
        {
            SetValidation(Localize("Batch.InvalidDelay"));
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
        _validationLiveRegion.Update(
            message,
            severity: AccessibilityLiveRegionSeverity.Assertive);
    }

    private void SetStatus(string message)
    {
        ValidationText.SetResourceReference(
            TextBlock.ForegroundProperty,
            "MutedBrush");
        _validationLiveRegion.Update(message);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) =>
        DialogResult = false;

    private AppLocalizationService Localization =>
        ((App)Application.Current).LocalizationService;

    private string Localize(string key) => Localization.GetString(key);

    private string Localize(string key, params object?[] arguments) =>
        Localization.Format(key, arguments);

    private sealed class BatchLaunchAccountOption(
        AccountProfile profile,
        bool isSelected,
        AppLocalizationService localization)
    {
        public AccountProfile Profile { get; } = profile;
        public string DisplayName { get; } = profile.Label ?? $"@{profile.Username}";
        public string Identity { get; } = profile.Label is null
            ? localization.Format(
                "Main.UserId",
                profile.UserId.ToString(CultureInfo.InvariantCulture))
            : localization.Format(
                "Main.AccountIdentity",
                profile.Username,
                profile.UserId.ToString(CultureInfo.InvariantCulture));
        public string ColorHex { get; } = profile.ColorHex ?? "#7C5CFC";
        public string? GroupName { get; } =
            BatchLaunchPreferences.NormalizeAccountGroup(profile.Group);
        public Visibility GroupVisibility => GroupName is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        public string DestinationSummary { get; } =
            string.IsNullOrWhiteSpace(profile.Destination)
                ? localization.GetString("Batch.DestinationUsesFirst")
                : localization.Format(
                    "Batch.DestinationValue",
                    profile.Destination.Trim());
        public string DestinationToolTip { get; } =
            string.IsNullOrWhiteSpace(profile.Destination)
                ? localization.GetString("Batch.DestinationUsesFirstHelp")
                : profile.Destination.Trim();
        public string AutomationName { get; } =
            localization.Format(
                "Batch.IncludeAccount",
                profile.Label ?? $"@{profile.Username}");
        public string AutomationHelpText { get; } =
            string.IsNullOrWhiteSpace(profile.Group)
                ? localization.GetString("Batch.IncludeHelp")
                : localization.Format("Main.AccountGroup", profile.Group);
        public bool IsSelected { get; set; } = isSelected;
    }

    private sealed record BatchGroupOption(string Name) : IDropdownLabel
    {
        public string DisplayName => Name;
    }
}
