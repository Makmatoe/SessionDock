using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SessionDock.ExactWheel;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock;

public partial class ClientMacroAssignmentDialog : Window
{
    private static readonly TimeSpan ForegroundPollInterval =
        TimeSpan.FromMilliseconds(250);
    private readonly SessionMacroLaunchContext _context;
    private readonly RobloxWindowService _windowService;
    private readonly IReadOnlySet<string> _selectableAccountKeys;
    private readonly IReadOnlyList<SessionMacroClientTarget> _selectableClients;
    private readonly IReadOnlyDictionary<nint, SessionMacroClientTarget>
        _selectableClientsByWindowHandle;
    private readonly IReadOnlyDictionary<string, string> _definitionNames;
    private readonly AppLocalizationService _localization;
    private readonly DispatcherTimer _foregroundTimer;
    private readonly ObservableCollection<AssignmentRow> _rows = [];
    private bool _assigning;
    private bool _isLoaded;
    private bool? _statusIsError;
    private nint _lastAssignedHandle;

    internal ClientMacroAssignmentDialog(
        SessionMacroLaunchContext context,
        IReadOnlyList<MacroDefinition> definitions,
        RobloxWindowService windowService,
        IReadOnlySet<string> selectableAccountKeys)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        ArgumentNullException.ThrowIfNull(definitions);
        _windowService = windowService ??
            throw new ArgumentNullException(nameof(windowService));
        _selectableAccountKeys = selectableAccountKeys ??
            throw new ArgumentNullException(nameof(selectableAccountKeys));
        InitializeComponent();
        _localization = ((App)Application.Current).LocalizationService;
        WindowLayoutService.FitToWorkArea(this);
        _foregroundTimer = new DispatcherTimer(
            ForegroundPollInterval,
            DispatcherPriority.Background,
            ForegroundTimer_Tick,
            Dispatcher);
        _selectableClients = _context.Snapshot().Clients
            .Where(client =>
                _selectableAccountKeys.Contains(client.AccountKey))
            .ToArray();
        _selectableClientsByWindowHandle = _selectableClients
            .Where(client => client.WindowHandle != nint.Zero)
            .GroupBy(client => client.WindowHandle)
            .Where(group => group.Count() == 1)
            .ToDictionary(
                group => group.Key,
                group => group.Single());

        var macroOptions = definitions
            .Where(definition =>
                definition.Kind == SessionMacroKind.Client &&
                !string.IsNullOrWhiteSpace(definition.ContentId))
            .GroupBy(
                definition => definition.ContentId,
                StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .OrderBy(
                definition => definition.Name,
                StringComparer.CurrentCultureIgnoreCase)
            .Select(definition => new MacroOption(
                definition,
                string.IsNullOrWhiteSpace(definition.Name)
                    ? definition.ContentId
                    : definition.Name))
            .Prepend(new MacroOption(
                null,
                Localize("Macro.AssignChooseMacro")))
            .ToArray();
        _definitionNames = macroOptions
            .Where(option => option.Definition is not null)
            .ToDictionary(
                option => option.Definition!.ContentId,
                option => option.DisplayName,
                StringComparer.OrdinalIgnoreCase);
        MacroComboBox.ItemsSource = macroOptions;
        MacroComboBox.SelectedIndex = 0;

        RefreshRows();
        AssignmentsList.ItemsSource = _rows;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _isLoaded = true;
        UpdateForegroundPolling();
        MacroComboBox.Focus();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        _isLoaded = false;
        _foregroundTimer.Stop();
    }

    private void MacroComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        // A new macro selection is a new assignment operation. Resetting the
        // handle lets the user change the macro for the same client after
        // returning focus to that exact client window.
        _lastAssignedHandle = nint.Zero;
        UpdateForegroundPolling();
        if (IsInitialized)
            SetStatus(Localize("Macro.AssignWaiting"));
    }

    private async void ForegroundTimer_Tick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (_assigning ||
            MacroComboBox.SelectedItem is not MacroOption
            {
                Definition: { } definition
            })
        {
            return;
        }

        var foregroundWindow =
            ExactWheelDesktopCapture.GetForegroundRootWindow();
        if (foregroundWindow == nint.Zero ||
            foregroundWindow == _lastAssignedHandle)
            return;
        if (!_selectableClientsByWindowHandle.TryGetValue(
                foregroundWindow,
                out var match))
        {
            return;
        }

        _assigning = true;
        try
        {
            var client = match;
            var valid = await _windowService.CaptureAsync(
                client.Identity,
                client.WindowHandle);
            if (!valid.Success ||
                ExactWheelDesktopCapture.GetForegroundRootWindow() !=
                    client.WindowHandle)
            {
                SetStatus(
                    Localize("Macro.AssignClientUnavailable"),
                    isError: true);
                return;
            }

            if (!_context.TrySetClientAssignment(
                    client.AccountKey,
                    definition))
            {
                SetStatus(
                    Localize("Macro.AssignRejected"),
                    isError: true);
                return;
            }

            _lastAssignedHandle = client.WindowHandle;
            RefreshRows();
            SetStatus(Localize(
                "Macro.AssignConfirmed",
                string.IsNullOrWhiteSpace(definition.Name)
                    ? definition.ContentId
                    : definition.Name,
                client.DisplayName));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ArgumentException)
        {
            SetStatus(
                Localize("Macro.AssignFailure", exception.Message),
                isError: true);
        }
        finally
        {
            _assigning = false;
        }
    }

    private void RemoveAssignmentButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Button { Tag: string accountKey })
            return;
        _ = _context.RemoveClientAssignment(accountKey);
        _lastAssignedHandle = nint.Zero;
        RefreshRows();
        SetStatus(Localize("Macro.AssignRemoved"));
    }

    private void RefreshRows()
    {
        var snapshot = _context.Snapshot();
        var existingRows = _rows.ToDictionary(
            row => row.AccountKey,
            StringComparer.OrdinalIgnoreCase);
        foreach (var client in _selectableClients)
        {
            var macroId = snapshot.ClientMacroAssignments
                .GetValueOrDefault(client.AccountKey);
            var label = macroId is not null &&
                _definitionNames.TryGetValue(macroId, out var macroName)
                    ? Localize("Macro.AssignAssigned", macroName)
                    : macroId is null
                        ? Localize("Macro.AssignUnassigned")
                        : Localize("Macro.AssignUnavailable", macroId);
            if (existingRows.TryGetValue(client.AccountKey, out var row))
            {
                row.Update(label, macroId is not null);
            }
            else
            {
                _rows.Add(new AssignmentRow(
                    client.AccountKey,
                    client.DisplayName,
                    label,
                    macroId is not null));
            }
        }

        EmptyClientsText.Visibility = _rows.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        AssignmentsList.Visibility = _rows.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void SetStatus(string text, bool isError = false)
    {
        if (!string.Equals(StatusText.Text, text, StringComparison.Ordinal))
            StatusText.Text = text;
        if (_statusIsError == isError)
            return;
        _statusIsError = isError;
        StatusText.SetResourceReference(
            TextBlock.ForegroundProperty,
            isError ? "ErrorTextBrush" : "SuccessTextBrush");
    }

    private void UpdateForegroundPolling()
    {
        var shouldPoll = _isLoaded &&
            MacroComboBox.SelectedItem is MacroOption
            {
                Definition: not null
            };
        if (shouldPoll)
        {
            if (!_foregroundTimer.IsEnabled)
                _foregroundTimer.Start();
        }
        else
        {
            _foregroundTimer.Stop();
        }
    }

    private string Localize(string key, params object?[] arguments) =>
        arguments.Length == 0
            ? _localization.GetString(key)
            : _localization.Format(key, arguments);

    private sealed record MacroOption(
        MacroDefinition? Definition,
        string DisplayName) : IDropdownLabel;

    private sealed class AssignmentRow : INotifyPropertyChanged
    {
        private string _assignmentLabel;
        private bool _hasAssignment;

        internal AssignmentRow(
            string accountKey,
            string displayName,
            string assignmentLabel,
            bool hasAssignment)
        {
            AccountKey = accountKey;
            DisplayName = displayName;
            _assignmentLabel = assignmentLabel;
            _hasAssignment = hasAssignment;
        }

        public string AccountKey { get; }

        public string DisplayName { get; }

        public string AssignmentLabel
        {
            get => _assignmentLabel;
            private set
            {
                if (_assignmentLabel == value)
                    return;
                _assignmentLabel = value;
                OnPropertyChanged();
            }
        }

        public bool HasAssignment
        {
            get => _hasAssignment;
            private set
            {
                if (_hasAssignment == value)
                    return;
                _hasAssignment = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        internal void Update(string assignmentLabel, bool hasAssignment)
        {
            AssignmentLabel = assignmentLabel;
            HasAssignment = hasAssignment;
        }

        private void OnPropertyChanged(
            [CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(propertyName));
    }
}
