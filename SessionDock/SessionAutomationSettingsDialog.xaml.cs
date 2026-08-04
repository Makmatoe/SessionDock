using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock;

internal enum SessionAutomationSettingsRoute
{
    WindowLayout,
    MacroLibrary,
    Templates
}

internal enum SessionAutomationSettingsDialogAction
{
    None,
    RecordMacro,
    SaveCurrentTemplate
}

public partial class SessionAutomationSettingsDialog : Window
{
    private readonly AppLocalizationService _localization;
    private readonly SessionTemplateCatalog _workingCatalog;
    private readonly ObservableCollection<TemplateSummaryRow> _templateRows;
    private readonly ObservableCollection<MacroSummaryRow> _macroRows;
    private readonly AccessibilityLiveRegion? _validationLiveRegion;
    private readonly IReadOnlyDictionary<string, AccountProfile> _accounts;
    private readonly IReadOnlyList<NamedDestination> _namedDestinations;
    private readonly SessionAutomationSettingsRoute _route;

    internal SessionAutomationSettingsDialog(
        SessionTemplateCatalog catalog,
        IReadOnlyList<RobloxMonitor> monitors,
        IReadOnlyList<AccountProfile>? accounts = null,
        SessionAutomationSettingsRoute route =
            SessionAutomationSettingsRoute.WindowLayout,
        IReadOnlyList<NamedDestination>? namedDestinations = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(monitors);
        InitializeComponent();
        _localization = ((App)Application.Current).LocalizationService;
        _validationLiveRegion = new AccessibilityLiveRegion(ValidationText);
        _route = route;
        _accounts = (accounts ?? [])
            .Where(account => !string.IsNullOrWhiteSpace(account.Key))
            .GroupBy(account => account.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        _namedDestinations = NamedDestinationPolicy.Normalize(
            namedDestinations,
            _accounts.Values.ToArray());

        // Normalization produces a deep policy-safe clone. All deletions and
        // edits stay isolated until a validated Save or action sets
        // UpdatedCatalog for the owner to persist.
        _workingCatalog = SessionTemplatePolicy.Normalize(catalog);
        _templateRows = new ObservableCollection<TemplateSummaryRow>(
            _workingCatalog.Templates.Select(CreateTemplateRow));
        TemplateListBox.ItemsSource = _templateRows;
        UpdateTemplateEmptyState();
        _macroRows = new ObservableCollection<MacroSummaryRow>(
            _workingCatalog.MacroDefinitions.Select(CreateMacroRow));
        MacroListBox.ItemsSource = _macroRows;
        UpdateMacroEmptyState();

        var preferences = _workingCatalog.TemplatePreferences;
        AutoArrangeCheckBox.IsChecked = preferences.AutoArrangeNormalBatch;
        TargetWidthBox.Text = FormatNumber(preferences.TargetWidth);
        TargetHeightBox.Text = FormatNumber(preferences.TargetHeight);
        MinimumWidthBox.Text = FormatNumber(preferences.MinimumWidth);
        MinimumHeightBox.Text = FormatNumber(preferences.MinimumHeight);
        RevealXBox.Text = FormatNumber(preferences.RevealX);
        RevealYBox.Text = FormatNumber(preferences.RevealY);
        RecordingStopHotkeyComboBox.ItemsSource =
            MacroRecordingHotkeyPolicy.Suggestions;
        RecordingStopHotkeyComboBox.Text =
            MacroRecordingHotkeyPolicy.Normalize(
                preferences.MacroRecordingStopHotkey);

        var monitorOptions = CreateMonitorOptions(
            monitors,
            preferences.PreferredMonitorDeviceName);
        PreferredMonitorComboBox.ItemsSource = monitorOptions;
        PreferredMonitorComboBox.SelectedItem = monitorOptions.First(option =>
            string.Equals(
                option.DeviceName,
                preferences.PreferredMonitorDeviceName,
                StringComparison.OrdinalIgnoreCase));
        ApplyRoutePresentation();
        WindowLayoutService.FitToWorkArea(this);
        Loaded += (_, _) => FocusRouteEntryPoint();
    }

    internal SessionTemplateCatalog? UpdatedCatalog { get; private set; }

    internal SessionAutomationSettingsDialogAction RequestedAction
    {
        get;
        private set;
    }

    private void ApplyRoutePresentation()
    {
        WindowLayoutSection.Visibility = _route ==
            SessionAutomationSettingsRoute.WindowLayout
                ? Visibility.Visible
                : Visibility.Collapsed;
        TemplatesSection.Visibility = _route ==
            SessionAutomationSettingsRoute.Templates
                ? Visibility.Visible
                : Visibility.Collapsed;
        MacroLibrarySection.Visibility = _route ==
            SessionAutomationSettingsRoute.MacroLibrary
                ? Visibility.Visible
                : Visibility.Collapsed;
        LibrarySectionsGrid.Visibility = _route ==
            SessionAutomationSettingsRoute.WindowLayout
                ? Visibility.Collapsed
                : Visibility.Visible;

        Grid.SetColumn(TemplatesSection, 0);
        Grid.SetColumnSpan(TemplatesSection, 3);
        Grid.SetColumn(MacroLibrarySection, 0);
        Grid.SetColumnSpan(MacroLibrarySection, 3);

        var (headingKey, detailKey, width, height, minimumHeight) = _route switch
        {
            SessionAutomationSettingsRoute.WindowLayout =>
                ("AutomationSettings.SizeHeading",
                    "AutomationSettings.SizeHelp", 660d, 650d, 560d),
            SessionAutomationSettingsRoute.MacroLibrary =>
                ("AutomationSettings.MacrosHeading",
                    "AutomationSettings.MacrosHelp", 620d, 600d, 530d),
            SessionAutomationSettingsRoute.Templates =>
                ("AutomationSettings.TemplatesHeading",
                    "AutomationSettings.TemplatesHelp", 660d, 570d, 500d),
            _ => throw new InvalidOperationException(
                "Unexpected automation settings route.")
        };
        Title = Localize(headingKey);
        DialogHeadingText.Text = Localize(headingKey);
        DialogIntroText.Text = Localize(detailKey);
        Width = width;
        Height = height;
        MinHeight = minimumHeight;
    }

    private void FocusRouteEntryPoint()
    {
        switch (_route)
        {
            case SessionAutomationSettingsRoute.WindowLayout:
                AutoArrangeCheckBox.Focus();
                break;
            case SessionAutomationSettingsRoute.MacroLibrary:
                if (_macroRows.Count > 0)
                    MacroListBox.Focus();
                else
                    RecordMacroButton.Focus();
                break;
            case SessionAutomationSettingsRoute.Templates:
                if (_templateRows.Count > 0)
                    TemplateListBox.Focus();
                else
                    SaveCurrentSessionButton.Focus();
                break;
            default:
                throw new InvalidOperationException(
                    "Unexpected automation settings route.");
        }
    }

    private void RecordMacroButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        CompleteDialog(SessionAutomationSettingsDialogAction.RecordMacro);
    }

    private void SaveCurrentSessionButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        CompleteDialog(
            SessionAutomationSettingsDialogAction.SaveCurrentTemplate);
    }

    private void MacroListBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        var selected = MacroListBox.SelectedItem is MacroSummaryRow;
        RenameMacroButton.IsEnabled = selected;
        RemoveMacroButton.IsEnabled = selected;
    }

    private void RenameMacroButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (MacroListBox.SelectedItem is not MacroSummaryRow selectedRow)
            return;
        var definition = _workingCatalog.MacroDefinitions.FirstOrDefault(
            macro => macro.ContentId.Equals(
                selectedRow.ContentId,
                StringComparison.OrdinalIgnoreCase));
        if (definition is null)
            return;

        var dialog = new TextPromptDialog(
            Localize("AutomationSettings.RenameMacro"),
            Localize("AutomationSettings.RenameMacroPrompt"),
            definition.Name)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
            return;
        if (!SessionMacroLibraryPolicy.TryNormalizeName(
                dialog.Value,
                out var normalizedName))
        {
            ShowValidation(
                "AutomationSettings.ValidationMacroName",
                MacroListBox,
                SessionTemplatePolicy.MaximumNameLength);
            return;
        }

        // Name is the only mutable display field. The stable content ID,
        // payload filename, kind, hash, timing, and account attribution remain
        // exactly as recorded.
        definition.Name = normalizedName;
        var index = _macroRows.IndexOf(selectedRow);
        if (index >= 0)
        {
            _macroRows[index] = CreateMacroRow(definition);
            MacroListBox.SelectedIndex = index;
        }
        ShowStatus("AutomationSettings.RenameMacroResult", normalizedName);
    }

    private void RemoveMacroButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (MacroListBox.SelectedItem is not MacroSummaryRow selectedRow)
            return;
        var definition = _workingCatalog.MacroDefinitions.FirstOrDefault(
            macro => macro.ContentId.Equals(
                selectedRow.ContentId,
                StringComparison.OrdinalIgnoreCase));
        if (definition is null)
            return;

        var references = SessionMacroLibraryPolicy.FindReferences(
            _workingCatalog,
            definition.ContentId);
        if (references.Count > 0)
        {
            var templateNames = references
                .Select(reference => reference.TemplateName)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            var visibleNames = string.Join(", ", templateNames
                .Take(5)
                .Select(name => $"\"{name}\""));
            if (templateNames.Length > 5)
            {
                visibleNames = Localize(
                    "AutomationSettings.MacroReferencesMore",
                    visibleNames,
                    templateNames.Length - 5);
            }
            ShowValidation(
                "AutomationSettings.RemoveMacroReferenced",
                MacroListBox,
                definition.Name,
                visibleNames);
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            Localize(
                "AutomationSettings.RemoveMacroConfirmPrompt",
                definition.Name),
            Localize("AutomationSettings.RemoveMacroConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
            return;

        var nextIndex = _macroRows.IndexOf(selectedRow);
        _workingCatalog.MacroDefinitions.Remove(definition);
        _macroRows.Remove(selectedRow);
        RenameMacroButton.IsEnabled = false;
        RemoveMacroButton.IsEnabled = false;
        UpdateMacroEmptyState();
        ShowStatus("AutomationSettings.RemoveMacroResult", definition.Name);
        if (_macroRows.Count > 0)
        {
            MacroListBox.SelectedIndex = Math.Min(
                Math.Max(nextIndex, 0),
                _macroRows.Count - 1);
            MacroListBox.Focus();
        }
        else
        {
            RecordMacroButton.Focus();
        }
    }

    private void DimensionBox_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_validationLiveRegion is not null)
        {
            _validationLiveRegion.Update(
                string.Empty,
                announceChanges: false);
        }
    }

    private void TemplateListBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        DeleteTemplatesButton.IsEnabled =
            TemplateListBox.SelectedItems.Count > 0;
        EditTemplateButton.IsEnabled =
            TemplateListBox.SelectedItems.Count == 1;
    }

    private void EditTemplateButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (TemplateListBox.SelectedItems
                .OfType<TemplateSummaryRow>()
                .SingleOrDefault() is not { } selectedRow)
        {
            return;
        }
        var templateIndex = _workingCatalog.Templates.FindIndex(template =>
            template.Id.Equals(
                selectedRow.Id,
                StringComparison.OrdinalIgnoreCase));
        if (templateIndex < 0)
            return;
        var template = _workingCatalog.Templates[templateIndex];
        var clients = template.ClientSlots
            .OrderBy(slot => slot.Order)
            .Select(slot =>
            {
                _accounts.TryGetValue(slot.AccountKey, out var account);
                var displayName = account is null
                    ? slot.AccountKey
                    : string.IsNullOrWhiteSpace(account.Label)
                        ? $"@{account.Username}"
                        : $"{account.Label} (@{account.Username})";
                return new TemplateEditorClient(
                    slot.AccountKey,
                    displayName,
                    slot.Placement,
                    slot.Destination);
            })
            .ToArray();
        if (clients.Length == 0)
            return;

        var dialog = new TemplateEditorDialog(
            clients,
            _workingCatalog.MacroDefinitions,
            template.DelaySeconds,
            template,
            _namedDestinations)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true ||
            dialog.SavedTemplate is not { } saved)
        {
            return;
        }

        _workingCatalog.Templates[templateIndex] = saved;
        var rowIndex = _templateRows.IndexOf(selectedRow);
        if (rowIndex >= 0)
        {
            _templateRows[rowIndex] = CreateTemplateRow(saved);
            TemplateListBox.SelectedIndex = rowIndex;
        }
        ShowStatus("AutomationSettings.EditResult", saved.Name);
    }

    private void DeleteTemplatesButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var selected = TemplateListBox.SelectedItems
            .OfType<TemplateSummaryRow>()
            .ToArray();
        if (selected.Length == 0)
            return;

        var nextFocusIndex = selected
            .Select(row => _templateRows.IndexOf(row))
            .Where(index => index >= 0)
            .DefaultIfEmpty(0)
            .Min();
        var selectedIds = selected
            .Select(row => row.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _workingCatalog.Templates.RemoveAll(template =>
            selectedIds.Contains(template.Id));
        foreach (var row in selected)
            _templateRows.Remove(row);

        DeleteTemplatesButton.IsEnabled = false;
        EditTemplateButton.IsEnabled = false;
        UpdateTemplateEmptyState();
        ShowStatus(
            "AutomationSettings.DeleteResult",
            selected.Length);
        if (_templateRows.Count > 0)
        {
            TemplateListBox.SelectedIndex = Math.Min(
                nextFocusIndex,
                _templateRows.Count - 1);
            TemplateListBox.Focus();
        }
        else
        {
            SaveCurrentSessionButton.Focus();
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        CompleteDialog(SessionAutomationSettingsDialogAction.None);
    }

    private void CompleteDialog(
        SessionAutomationSettingsDialogAction requestedAction)
    {
        var updatedCatalog = TryCreateValidatedCatalog();
        if (updatedCatalog is null)
            return;

        UpdatedCatalog = updatedCatalog;
        RequestedAction = requestedAction;
        DialogResult = true;
    }

    private SessionTemplateCatalog? TryCreateValidatedCatalog()
    {
        if (!MacroRecordingHotkeyPolicy.TryParse(
                RecordingStopHotkeyComboBox.Text,
                out var recordingStopHotkey))
        {
            ShowValidation(
                "AutomationSettings.ValidationRecordingStopHotkey",
                RecordingStopHotkeyComboBox);
            return null;
        }

        if (!TryReadNumber(
                TargetWidthBox,
                "AutomationSettings.TargetWidth",
                out var targetWidth) ||
            !TryReadNumber(
                TargetHeightBox,
                "AutomationSettings.TargetHeight",
                out var targetHeight) ||
            !TryReadNumber(
                MinimumWidthBox,
                "AutomationSettings.MinimumWidth",
                out var minimumWidth) ||
            !TryReadNumber(
                MinimumHeightBox,
                "AutomationSettings.MinimumHeight",
                out var minimumHeight) ||
            !TryReadNumber(
                RevealXBox,
                "AutomationSettings.RevealX",
                out var revealX) ||
            !TryReadNumber(
                RevealYBox,
                "AutomationSettings.RevealY",
                out var revealY))
        {
            return null;
        }

        var monitorDeviceName =
            PreferredMonitorComboBox.SelectedItem is MonitorOption monitor
                ? monitor.DeviceName
                : null;
        var candidate = new SessionTemplateCatalog
        {
            SchemaVersion = _workingCatalog.SchemaVersion,
            Templates = [.. _workingCatalog.Templates],
            MacroDefinitions = [.. _workingCatalog.MacroDefinitions],
            TemplatePreferences = new TemplatePreferences
            {
                AutoArrangeNormalBatch = AutoArrangeCheckBox.IsChecked == true,
                TargetWidth = targetWidth,
                TargetHeight = targetHeight,
                MinimumWidth = minimumWidth,
                MinimumHeight = minimumHeight,
                RevealX = revealX,
                RevealY = revealY,
                PreferredMonitorDeviceName = monitorDeviceName,
                MacroPlaybackSpeed =
                    _workingCatalog.TemplatePreferences.MacroPlaybackSpeed,
                MacroRecordingStopHotkey =
                    recordingStopHotkey.PersistedValue
            }
        };
        var normalized = SessionTemplatePolicy.Normalize(candidate);
        var normalizedPreferences = normalized.TemplatePreferences;
        if (!normalizedPreferences.MinimumWidth.Equals(minimumWidth) ||
            !normalizedPreferences.MinimumHeight.Equals(minimumHeight) ||
            !normalizedPreferences.TargetWidth.Equals(targetWidth) ||
            !normalizedPreferences.TargetHeight.Equals(targetHeight))
        {
            ShowValidation(
                "AutomationSettings.ValidationDimensions",
                !normalizedPreferences.MinimumWidth.Equals(minimumWidth)
                    ? MinimumWidthBox
                    : !normalizedPreferences.MinimumHeight.Equals(minimumHeight)
                        ? MinimumHeightBox
                        : !normalizedPreferences.TargetWidth.Equals(targetWidth)
                            ? TargetWidthBox
                            : TargetHeightBox);
            return null;
        }
        if (!normalizedPreferences.RevealX.Equals(revealX) ||
            !normalizedPreferences.RevealY.Equals(revealY))
        {
            ShowValidation(
                "AutomationSettings.ValidationReveal",
                !normalizedPreferences.RevealX.Equals(revealX)
                    ? RevealXBox
                    : RevealYBox);
            return null;
        }

        return normalized;
    }

    private bool TryReadNumber(
        TextBox textBox,
        string labelKey,
        out double value)
    {
        if (double.TryParse(
                textBox.Text,
                NumberStyles.Float,
                _localization.EffectiveCulture,
                out value) &&
            double.IsFinite(value))
        {
            return true;
        }

        ShowValidation(
            "AutomationSettings.ValidationNumber",
            textBox,
            Localize(labelKey));
        return false;
    }

    private void UpdateTemplateEmptyState()
    {
        var isEmpty = _templateRows.Count == 0;
        TemplateListBox.Visibility = isEmpty
            ? Visibility.Collapsed
            : Visibility.Visible;
        EmptyTemplatesText.Visibility = isEmpty
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateMacroEmptyState()
    {
        var isEmpty = _macroRows.Count == 0;
        MacroListBox.Visibility = isEmpty
            ? Visibility.Collapsed
            : Visibility.Visible;
        EmptyMacrosText.Visibility = isEmpty
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ShowValidation(
        string key,
        IInputElement focusTarget,
        params object?[] arguments)
    {
        ValidationText.SetResourceReference(
            TextBlock.ForegroundProperty,
            "ErrorTextBrush");
        var message = Localize(key, arguments);
        _validationLiveRegion!.Update(
            message,
            message,
            AccessibilityLiveRegionSeverity.Assertive);
        focusTarget.Focus();
    }

    private void ShowStatus(
        string key,
        params object?[] arguments)
    {
        ValidationText.SetResourceReference(
            TextBlock.ForegroundProperty,
            "SuccessTextBrush");
        var message = Localize(key, arguments);
        _validationLiveRegion!.Update(
            message,
            message,
            AccessibilityLiveRegionSeverity.Polite);
    }

    private TemplateSummaryRow CreateTemplateRow(SessionTemplate template)
    {
        var displayName = string.IsNullOrWhiteSpace(template.Name)
            ? template.Id
            : template.Name;
        return new TemplateSummaryRow(
            template.Id,
            displayName,
            Localize(
                "AutomationSettings.TemplateSummary",
                template.ClientSlots.Count,
                Localize($"AutomationSettings.Layout.{template.LayoutMode}"),
                Localize($"AutomationSettings.Macro.{template.MacroMode}"),
                template.DelaySeconds));
    }

    private MacroSummaryRow CreateMacroRow(MacroDefinition definition)
    {
        var name = string.IsNullOrWhiteSpace(definition.Name)
            ? definition.ContentId
            : definition.Name;
        var displayName = name.Equals(
            definition.ContentId,
            StringComparison.OrdinalIgnoreCase)
                ? name
                : $"{name} [{definition.ContentId}]";
        return new MacroSummaryRow(
            definition.ContentId,
            displayName,
            Localize(
                "AutomationSettings.MacroSummary",
                Localize($"AutomationSettings.MacroKind.{definition.Kind}"),
                definition.EventCount,
                definition.DurationMilliseconds));
    }

    private IReadOnlyList<MonitorOption> CreateMonitorOptions(
        IReadOnlyList<RobloxMonitor> monitors,
        string? selectedDeviceName)
    {
        var options = new List<MonitorOption>
        {
            new(null, Localize("AutomationSettings.MonitorAutomatic"))
        };
        options.AddRange(monitors
            .Where(monitor =>
                monitor is not null &&
                !string.IsNullOrWhiteSpace(monitor.DeviceName) &&
                monitor.WorkArea.IsValid)
            .GroupBy(
                monitor => monitor.DeviceName,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(monitor => monitor.Index)
            .ThenBy(monitor => monitor.DeviceName, StringComparer.OrdinalIgnoreCase)
            .Select(monitor => new MonitorOption(
                monitor.DeviceName,
                Localize(
                    "AutomationSettings.MonitorSummary",
                    monitor.DeviceName,
                    monitor.WorkArea.Width,
                    monitor.WorkArea.Height,
                    monitor.IsPrimary
                        ? Localize("AutomationSettings.MonitorPrimary")
                        : string.Empty))));

        if (!string.IsNullOrWhiteSpace(selectedDeviceName) &&
            options.All(option => !string.Equals(
                option.DeviceName,
                selectedDeviceName,
                StringComparison.OrdinalIgnoreCase)))
        {
            options.Add(new MonitorOption(
                selectedDeviceName,
                Localize(
                    "AutomationSettings.MonitorUnavailable",
                    selectedDeviceName)));
        }

        return options;
    }

    private string FormatNumber(double value) =>
        value.ToString("0.##", _localization.EffectiveCulture);

    private string Localize(string key, params object?[] arguments) =>
        arguments.Length == 0
            ? _localization.GetString(key)
            : _localization.Format(key, arguments);

    private sealed record TemplateSummaryRow(
        string Id,
        string DisplayName,
        string Summary);

    private sealed record MacroSummaryRow(
        string ContentId,
        string DisplayName,
        string Summary);

    private sealed record MonitorOption(
        string? DeviceName,
        string DisplayName) : IDropdownLabel;
}
