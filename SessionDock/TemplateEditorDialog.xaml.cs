using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock;

internal sealed record TemplateEditorClient(
    string AccountKey,
    string DisplayName,
    NormalizedClientWindowPlacement? Placement,
    string? Destination = null);

public partial class TemplateEditorDialog : Window
{
    private readonly IReadOnlyList<TemplateEditorClient> _clients;
    private readonly IReadOnlyList<ClientMacroRow> _clientRows;
    private readonly AppLocalizationService _localization;
    private readonly AccessibilityLiveRegion _validationLiveRegion;
    private readonly SessionTemplate? _existingTemplate;
    private readonly IReadOnlyDictionary<
        string,
        SessionTemplateClientSlot> _existingSlots;

    internal TemplateEditorDialog(
        IReadOnlyList<TemplateEditorClient> clients,
        IReadOnlyList<MacroDefinition> macroDefinitions,
        int initialDelaySeconds,
        SessionTemplate? existingTemplate = null,
        IReadOnlyList<NamedDestination>? namedDestinations = null)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(macroDefinitions);
        InitializeComponent();
        _localization = ((App)Application.Current).LocalizationService;
        _validationLiveRegion = new AccessibilityLiveRegion(ValidationText);
        SourceInitialized += (_, _) =>
            WindowLayoutService.FitToWorkArea(this);

        _clients = NormalizeClients(clients);
        _existingTemplate = existingTemplate is null
            ? null
            : SessionTemplatePolicy.Normalize(
                new SessionTemplateCatalog
                {
                    Templates = [existingTemplate],
                    MacroDefinitions = [.. macroDefinitions]
                }).Templates.SingleOrDefault();
        var normalizedMacros = SessionTemplatePolicy.Normalize(
            new SessionTemplateCatalog
            {
                MacroDefinitions = [.. macroDefinitions]
            }).MacroDefinitions;
        var noMacro = new MacroChoice(
            ContentId: null,
            Localize("Template.Editor.NoMacro"));
        var clientMacros = normalizedMacros
            .Where(macro => macro.Kind == SessionMacroKind.Client)
            .Select(CreateMacroChoice)
            .Prepend(noMacro)
            .ToList();
        var wholeLayoutMacros = normalizedMacros
            .Where(macro => macro.Kind == SessionMacroKind.WholeLayout)
            .Select(CreateMacroChoice)
            .Prepend(noMacro)
            .ToList();

        _existingSlots = (_existingTemplate?.ClientSlots ?? [])
            .GroupBy(
                slot => slot.AccountKey,
                StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(
                group => group.Key,
                group => group.Single(),
                StringComparer.OrdinalIgnoreCase);

        var destinationDefinitions = NamedDestinationPolicy.Normalize(
            namedDestinations,
            _clients.Select(client => new AccountProfile
            {
                Key = client.AccountKey
            }).ToArray());

        _clientRows = _clients
            .Select(client =>
            {
                _existingSlots.TryGetValue(client.AccountKey, out var slot);
                var selectedId = _existingTemplate?.MacroMode ==
                    SessionTemplateMacroMode.PerClient
                        ? slot?.PerClientMacroId
                        : null;
                var choices = clientMacros.ToList();
                var destination = slot is null
                    ? client.Destination
                    : slot.Destination;
                var destinationChoices = CreateDestinationChoices(
                    destinationDefinitions,
                    destination);
                return new ClientMacroRow(
                    client,
                    choices,
                    ResolveChoice(choices, selectedId, noMacro),
                    destination,
                    destinationChoices,
                    ResolveDestinationChoice(
                        destinationChoices,
                        destination),
                    Localize(
                        "Template.Editor.ClientMacroName",
                        client.DisplayName),
                    Localize(
                        "Template.Editor.NamedDestinationForClient",
                        client.DisplayName),
                    Localize(
                        "Template.Editor.CustomDestinationForClient",
                        client.DisplayName),
                    Localize(
                        "Template.Editor.DestinationHelpForClient",
                        client.DisplayName));
            })
            .ToArray();
        ClientMacroList.ItemsSource = _clientRows;
        ClientDestinationList.ItemsSource = _clientRows;
        SharedTargetList.ItemsSource = _clientRows;
        SharedMacroComboBox.ItemsSource = clientMacros;
        SharedMacroComboBox.SelectedItem = ResolveChoice(
            clientMacros,
            _existingTemplate?.MacroMode == SessionTemplateMacroMode.Shared
                ? _existingTemplate.SharedMacroId
                : null,
            noMacro);
        WholeLayoutMacroComboBox.ItemsSource = wholeLayoutMacros;
        WholeLayoutMacroComboBox.SelectedItem = ResolveChoice(
            wholeLayoutMacros,
            _existingTemplate?.MacroMode ==
                SessionTemplateMacroMode.WholeLayout
                ? _existingTemplate.WholeLayoutMacroId
                : null,
            noMacro);

        MacroModeComboBox.ItemsSource = new[]
        {
            new MacroModeChoice(
                SessionTemplateMacroMode.None,
                Localize("Template.Editor.MacroNone")),
            new MacroModeChoice(
                SessionTemplateMacroMode.PerClient,
                Localize("Template.Editor.MacroPerClient")),
            new MacroModeChoice(
                SessionTemplateMacroMode.Shared,
                Localize("Template.Editor.MacroShared")),
            new MacroModeChoice(
                SessionTemplateMacroMode.WholeLayout,
                Localize("Template.Editor.MacroWholeLayout"))
        };
        MacroModeComboBox.SelectedItem = MacroModeComboBox.Items
            .OfType<MacroModeChoice>()
            .First(choice => choice.Mode ==
                (_existingTemplate?.MacroMode ??
                    SessionTemplateMacroMode.None));

        var delayOptions = BatchLaunchPreferences.SupportedDelaySeconds
            .Select(seconds => new DelayChoice(
                seconds,
                Localize("Template.Editor.DelaySeconds", seconds)))
            .ToArray();
        DelayComboBox.ItemsSource = delayOptions;
        var normalizedDelay = BatchLaunchPreferences.NormalizeDelaySeconds(
            _existingTemplate?.DelaySeconds ?? initialDelaySeconds);
        DelayComboBox.SelectedItem = delayOptions.First(option =>
            option.Seconds == normalizedDelay);

        if (_existingTemplate?.LayoutMode == SessionTemplateLayoutMode.Cascade)
            CascadeRadio.IsChecked = true;
        else if (_clients.All(client => client.Placement is not null))
            SavedPositionsRadio.IsChecked = true;
        else
            CascadeRadio.IsChecked = true;

        NameBox.Text = _existingTemplate?.Name ?? string.Empty;
        if (_existingTemplate?.MacroMode == SessionTemplateMacroMode.Shared &&
            _existingTemplate.SharedMacroAccountKeys is { } selectedTargets)
        {
            var selectedKeys = selectedTargets.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            foreach (var row in _clientRows)
            {
                row.IsSharedMacroTarget = selectedKeys.Contains(
                    row.Client.AccountKey);
            }
        }

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    internal SessionTemplate? SavedTemplate { get; private set; }

    private void MacroModeComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        var mode = SelectedMacroMode;
        NoMacroDetailText.Visibility = mode == SessionTemplateMacroMode.None
            ? Visibility.Visible
            : Visibility.Collapsed;
        PerClientMacroPanel.Visibility = mode ==
            SessionTemplateMacroMode.PerClient
            ? Visibility.Visible
            : Visibility.Collapsed;
        SharedMacroPanel.Visibility = mode == SessionTemplateMacroMode.Shared
            ? Visibility.Visible
            : Visibility.Collapsed;
        WholeLayoutMacroPanel.Visibility = mode ==
            SessionTemplateMacroMode.WholeLayout
            ? Visibility.Visible
            : Visibility.Collapsed;
        ClearValidation();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var name = NameBox.Text;
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowValidation("Template.Editor.ValidationName", NameBox);
            return;
        }

        var layoutMode = SavedPositionsRadio.IsChecked == true
            ? SessionTemplateLayoutMode.Saved
            : SessionTemplateLayoutMode.Cascade;
        if (layoutMode == SessionTemplateLayoutMode.Saved)
        {
            var missingPositions = _clients.Count(client =>
                client.Placement is null);
            if (missingPositions > 0)
            {
                ShowValidation(
                    "Template.Editor.ValidationMissingPositions",
                    SavedPositionsRadio,
                    missingPositions);
                return;
            }
        }

        var macroMode = SelectedMacroMode;
        var sharedMacroId = SelectedMacroId(SharedMacroComboBox);
        var wholeLayoutMacroId = SelectedMacroId(
            WholeLayoutMacroComboBox);
        if (macroMode == SessionTemplateMacroMode.PerClient &&
            _clientRows.All(row =>
                row.SelectedMacro.ContentId is null ||
                !row.SelectedMacro.IsAvailable))
        {
            ShowValidation(
                "Template.Editor.ValidationPerClientMacro",
                ClientMacroList);
            return;
        }
        if (macroMode == SessionTemplateMacroMode.PerClient &&
            _clientRows.Any(row =>
                row.SelectedMacro.ContentId is not null &&
                !row.SelectedMacro.IsAvailable))
        {
            ShowValidation(
                "Template.Editor.ValidationUnavailableMacro",
                ClientMacroList);
            return;
        }
        if (macroMode == SessionTemplateMacroMode.Shared &&
            sharedMacroId is null)
        {
            ShowValidation(
                "Template.Editor.ValidationSharedMacro",
                SharedMacroComboBox);
            return;
        }
        if (macroMode == SessionTemplateMacroMode.Shared &&
            _clientRows.All(row => !row.IsSharedMacroTarget))
        {
            ShowValidation(
                "Template.Editor.ValidationSharedTargets",
                SharedTargetList);
            return;
        }
        if (macroMode == SessionTemplateMacroMode.WholeLayout &&
            wholeLayoutMacroId is null)
        {
            ShowValidation(
                "Template.Editor.ValidationWholeLayoutMacro",
                WholeLayoutMacroComboBox);
            return;
        }

        var delaySeconds = DelayComboBox.SelectedItem is DelayChoice delay
            ? delay.Seconds
            : BatchLaunchPreferences.DefaultDelaySeconds;
        foreach (var row in _clientRows)
        {
            if (string.IsNullOrWhiteSpace(row.Destination))
                continue;
            if (!NamedDestinationPolicy.TryNormalizeValue(
                    row.Destination,
                    out var normalizedDestination))
            {
                ShowClientDestinationValidation(row);
                return;
            }
            row.Destination = normalizedDestination;
        }
        var draft = new SessionTemplate
        {
            Id = _existingTemplate?.Id ?? Guid.NewGuid().ToString("N"),
            Name = name,
            DelaySeconds = delaySeconds,
            LayoutMode = layoutMode,
            MacroMode = macroMode,
            SharedMacroId = macroMode == SessionTemplateMacroMode.Shared
                ? sharedMacroId
                : null,
            SharedMacroAccountKeys = macroMode ==
                SessionTemplateMacroMode.Shared
                ? CreateSharedMacroTargetSelection(_clientRows.Select(row =>
                    (row.Client.AccountKey, row.IsSharedMacroTarget)))
                : null,
            WholeLayoutMacroId = macroMode ==
                SessionTemplateMacroMode.WholeLayout
                ? wholeLayoutMacroId
                : null,
            // Retain the obsolete schema field when editing an older catalog.
            // Playback now loops every assigned macro until Stop, regardless
            // of this value, so new templates leave it at the default false.
            RepeatWholeLayoutMacro =
                _existingTemplate?.RepeatWholeLayoutMacro == true,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            LegacyPresetName = _existingTemplate?.LegacyPresetName,
            ClientSlots = _clients
                .Select((client, order) => new SessionTemplateClientSlot
                {
                    SlotId = _existingSlots.GetValueOrDefault(
                        client.AccountKey)?.SlotId ??
                        Guid.NewGuid().ToString("N"),
                    AccountKey = client.AccountKey,
                    Order = order,
                    Destination = _clientRows[order].Destination,
                    Placement = layoutMode == SessionTemplateLayoutMode.Saved
                        ? ClonePlacement(client.Placement)
                        : null,
                    PerClientMacroId = macroMode ==
                        SessionTemplateMacroMode.PerClient
                        ? _clientRows[order].SelectedMacro.IsAvailable
                            ? _clientRows[order].SelectedMacro.ContentId
                            : null
                        : null
                })
                .ToList()
        };
        var normalized = SessionTemplatePolicy.Normalize(
            new SessionTemplateCatalog { Templates = [draft] });
        var saved = normalized.Templates.SingleOrDefault();
        if (saved is null || saved.ClientSlots.Count != _clients.Count)
        {
            ShowValidation(
                "Template.Editor.ValidationInvalid",
                NameBox);
            return;
        }

        SavedTemplate = saved;
        DialogResult = true;
    }

    private SessionTemplateMacroMode SelectedMacroMode =>
        MacroModeComboBox.SelectedItem is MacroModeChoice selected
            ? selected.Mode
            : SessionTemplateMacroMode.None;

    private static string? SelectedMacroId(ComboBox comboBox) =>
        comboBox.SelectedItem is MacroChoice { IsAvailable: true } choice
            ? choice.ContentId
            : null;

    private MacroChoice ResolveChoice(
        ICollection<MacroChoice> choices,
        string? contentId,
        MacroChoice noMacro)
    {
        if (string.IsNullOrWhiteSpace(contentId))
            return noMacro;
        var existing = choices.FirstOrDefault(choice => string.Equals(
            choice.ContentId,
            contentId,
            StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return existing;
        var unavailable = new MacroChoice(
            contentId,
            Localize("Template.Editor.UnavailableMacro", contentId),
            IsAvailable: false);
        choices.Add(unavailable);
        return unavailable;
    }

    private static IReadOnlyList<TemplateEditorClient> NormalizeClients(
        IReadOnlyList<TemplateEditorClient> clients)
    {
        if (clients.Count is <= 0 or >
            SessionTemplatePolicy.MaximumSlotsPerTemplate)
        {
            throw new ArgumentOutOfRangeException(
                nameof(clients),
                $"A template requires 1 to {SessionTemplatePolicy.MaximumSlotsPerTemplate} clients.");
        }

        var sourceClients = clients.ToArray();
        if (sourceClients.Any(client => client is null))
        {
            throw new ArgumentException(
                "Template clients cannot contain null entries.",
                nameof(clients));
        }

        var probe = new SessionTemplate
        {
            Id = "template-editor-probe",
            Name = "Template editor probe",
            LayoutMode = SessionTemplateLayoutMode.Saved,
            ClientSlots = sourceClients
                .Select((client, order) => new SessionTemplateClientSlot
                {
                    SlotId = $"probe-slot-{order}",
                    AccountKey = client.AccountKey,
                    Order = order,
                    Destination = client.Destination,
                    Placement = ClonePlacement(client.Placement)
                })
                .ToList()
        };
        var normalized = SessionTemplatePolicy.Normalize(
            new SessionTemplateCatalog { Templates = [probe] });
        var normalizedSlots = normalized.Templates
            .SingleOrDefault()?.ClientSlots;
        if (normalizedSlots is null ||
            normalizedSlots.Count != sourceClients.Length)
        {
            throw new ArgumentException(
                "Every template client must have a valid account key.",
                nameof(clients));
        }

        var accountKeys = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var result = new TemplateEditorClient[sourceClients.Length];
        for (var index = 0; index < sourceClients.Length; index++)
        {
            var source = sourceClients[index];
            var slot = normalizedSlots[index];
            if (!accountKeys.Add(slot.AccountKey))
            {
                throw new ArgumentException(
                    "A client account can appear only once in a template.",
                    nameof(clients));
            }

            var displayName = string.IsNullOrWhiteSpace(source.DisplayName)
                ? slot.AccountKey
                : source.DisplayName.Trim();
            result[index] = new TemplateEditorClient(
                slot.AccountKey,
                displayName,
                ClonePlacement(slot.Placement),
                slot.Destination);
        }

        return result;
    }

    internal static List<string> CreateSharedMacroTargetSelection(
        IEnumerable<(string AccountKey, bool IsSelected)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows
            .Where(row => row.IsSelected)
            .Select(row => row.AccountKey)
            .ToList();
    }

    private MacroChoice CreateMacroChoice(MacroDefinition macro)
    {
        var name = string.IsNullOrWhiteSpace(macro.Name)
            ? macro.ContentId
            : macro.Name;
        var displayName = name.Equals(
            macro.ContentId,
            StringComparison.OrdinalIgnoreCase)
            ? name
            : $"{name} [{macro.ContentId}]";
        return new MacroChoice(
            macro.ContentId,
            displayName,
            IsAvailable: true);
    }

    private IReadOnlyList<DestinationChoice> CreateDestinationChoices(
        IReadOnlyList<NamedDestination> namedDestinations,
        string? currentValue)
    {
        var choices = new List<DestinationChoice>
        {
            new(null, Localize("Template.Editor.NoDestination"))
        };
        choices.AddRange(namedDestinations.Select(destination =>
            new DestinationChoice(
                destination.Value,
                destination.Name)));
        var normalizedCurrent = currentValue?.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedCurrent) &&
            choices.All(choice => !string.Equals(
                choice.Value,
                normalizedCurrent,
                StringComparison.Ordinal)))
        {
            choices.Add(new DestinationChoice(
                normalizedCurrent,
                Localize(
                    "Template.Editor.CustomDestination",
                    normalizedCurrent)));
        }
        return choices;
    }

    private static DestinationChoice? ResolveDestinationChoice(
        IReadOnlyList<DestinationChoice> choices,
        string? destination)
    {
        var value = destination?.Trim();
        return choices.FirstOrDefault(choice => string.Equals(
            choice.Value,
            string.IsNullOrWhiteSpace(value) ? null : value,
            StringComparison.Ordinal));
    }

    private static NormalizedClientWindowPlacement? ClonePlacement(
        NormalizedClientWindowPlacement? source) =>
        source is null
            ? null
            : new NormalizedClientWindowPlacement
            {
                MonitorStableId = source.MonitorStableId,
                MonitorDeviceName = source.MonitorDeviceName,
                MonitorIndex = source.MonitorIndex,
                Left = source.Left,
                Top = source.Top,
                Width = source.Width,
                Height = source.Height
            };

    private void ShowValidation(
        string key,
        IInputElement focusTarget,
        params object?[] arguments)
    {
        _validationLiveRegion.Update(
            Localize(key, arguments),
            severity: AccessibilityLiveRegionSeverity.Assertive);
        FocusValidationTarget(focusTarget);
    }

    private void ShowClientDestinationValidation(ClientMacroRow row)
    {
        _validationLiveRegion.Update(
            Localize(
                "Template.Editor.ValidationDestination",
                row.DisplayName),
            severity: AccessibilityLiveRegionSeverity.Assertive);
        ClientDestinationList.ScrollIntoView(row);
        ClientDestinationList.UpdateLayout();
        if (ClientDestinationList.ItemContainerGenerator.ContainerFromItem(
                row) is DependencyObject container &&
            FindDescendant<TextBox>(container) is { } destinationBox)
        {
            destinationBox.BringIntoView();
            destinationBox.Focus();
            return;
        }

        FocusValidationTarget(ClientDestinationList);
    }

    private static T? FindDescendant<T>(DependencyObject parent)
        where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                return match;
            var descendant = FindDescendant<T>(child);
            if (descendant is not null)
                return descendant;
        }

        return null;
    }

    private static void FocusValidationTarget(IInputElement focusTarget)
    {
        if (focusTarget is ItemsControl itemsControl)
        {
            itemsControl.UpdateLayout();
            for (var index = 0; index < itemsControl.Items.Count; index++)
            {
                if (itemsControl.ItemContainerGenerator.ContainerFromIndex(
                        index) is not DependencyObject container)
                {
                    continue;
                }

                var input = FindFocusableDescendant(container);
                if (input?.Focus() == true)
                    return;
            }
        }

        focusTarget.Focus();
    }

    private static Control? FindFocusableDescendant(DependencyObject parent)
    {
        var childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is Control
                {
                    Focusable: true,
                    IsTabStop: true,
                    IsEnabled: true,
                    IsVisible: true
                } control)
            {
                return control;
            }

            var descendant = FindFocusableDescendant(child);
            if (descendant is not null)
                return descendant;
        }

        return null;
    }

    private void ClearValidation() => _validationLiveRegion.Update(
        string.Empty,
        announceChanges: false);

    private string Localize(string key, params object?[] arguments) =>
        arguments.Length == 0
            ? _localization.GetString(key)
            : _localization.Format(key, arguments);

    private sealed record MacroChoice(
        string? ContentId,
        string DisplayName,
        bool IsAvailable = true) : IDropdownLabel;

    private sealed record MacroModeChoice(
        SessionTemplateMacroMode Mode,
        string DisplayName) : IDropdownLabel;

    private sealed record DelayChoice(
        int Seconds,
        string DisplayName) : IDropdownLabel;

    private sealed record DestinationChoice(
        string? Value,
        string DisplayName) : IDropdownLabel;

    private sealed class ClientMacroRow : INotifyPropertyChanged
    {
        private string? _destination;
        private DestinationChoice? _selectedDestinationChoice;
        private bool _synchronizingDestination;

        internal ClientMacroRow(
            TemplateEditorClient client,
            IReadOnlyList<MacroChoice> macroOptions,
            MacroChoice selectedMacro,
            string? destination,
            IReadOnlyList<DestinationChoice> destinationOptions,
            DestinationChoice? selectedDestinationChoice,
            string macroAutomationName,
            string destinationChoiceAutomationName,
            string destinationValueAutomationName,
            string destinationAutomationHelp)
        {
            Client = client;
            MacroOptions = macroOptions;
            SelectedMacro = selectedMacro;
            _destination = destination;
            DestinationOptions = destinationOptions;
            _selectedDestinationChoice = selectedDestinationChoice;
            MacroAutomationName = macroAutomationName;
            DestinationChoiceAutomationName =
                destinationChoiceAutomationName;
            DestinationValueAutomationName = destinationValueAutomationName;
            DestinationAutomationHelp = destinationAutomationHelp;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        internal TemplateEditorClient Client { get; }

        public string DisplayName => Client.DisplayName;

        public IReadOnlyList<MacroChoice> MacroOptions { get; }

        public MacroChoice SelectedMacro { get; set; }

        public IReadOnlyList<DestinationChoice> DestinationOptions { get; }

        public DestinationChoice? SelectedDestinationChoice
        {
            get => _selectedDestinationChoice;
            set
            {
                if (ReferenceEquals(_selectedDestinationChoice, value))
                    return;
                _selectedDestinationChoice = value;
                OnPropertyChanged();
                if (_synchronizingDestination)
                    return;
                _synchronizingDestination = true;
                Destination = value?.Value;
                _synchronizingDestination = false;
            }
        }

        public string? Destination
        {
            get => _destination;
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value)
                    ? null
                    : value.Trim();
                if (string.Equals(
                        _destination,
                        normalized,
                        StringComparison.Ordinal))
                {
                    return;
                }
                _destination = normalized;
                OnPropertyChanged();
                if (_synchronizingDestination)
                    return;
                _synchronizingDestination = true;
                _selectedDestinationChoice = DestinationOptions
                    .FirstOrDefault(choice => string.Equals(
                        choice.Value,
                        normalized,
                        StringComparison.Ordinal));
                OnPropertyChanged(nameof(SelectedDestinationChoice));
                _synchronizingDestination = false;
            }
        }

        public bool IsSharedMacroTarget { get; set; } = true;

        public string MacroAutomationName { get; }

        public string DestinationChoiceAutomationName { get; }

        public string DestinationValueAutomationName { get; }

        public string DestinationAutomationHelp { get; }

        private void OnPropertyChanged(
            [CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(propertyName));
    }
}
