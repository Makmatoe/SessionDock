using System.Windows;
using SessionDock.Services;

namespace SessionDock;

public partial class MainWindow
{
    private AppLocalizationService Localization =>
        ((App)Application.Current).LocalizationService;

    private string Localize(string key) => Localization.GetString(key);

    private string Localize(string key, params object?[] arguments) =>
        Localization.Format(key, arguments);

    internal void VerifyLocalizationSwitchForRuntimeSmoke()
    {
        const string InvalidDestinationProbe =
            "https://example.invalid/games/123456";
        Dispatcher.VerifyAccess();
        var originalPreference = Localization.CurrentPreference;
        var originalDestination = PlaceIdBox.Text;
        try
        {
            foreach (var preference in LocalizationPreference.SupportedValues
                         .Where(value => !value.Equals(
                             LocalizationPreference.System,
                             StringComparison.Ordinal)))
            {
                Localization.ApplyPreference(preference);
                var expectedName = Localize("Main.LanguageSettings");
                var expectedDestinationHelp = Localize(
                    _joinUserMode
                        ? "Main.JoinUserHelp"
                        : "Main.DestinationHelp");
                var expectedDestinationName = Localize(
                    _joinUserMode
                        ? "Main.JoinUserInputName"
                        : "Main.DestinationInputName");
                PlaceIdBox.Text = InvalidDestinationProbe;
                RefreshLaunchAvailability();
                if (!string.Equals(
                        System.Windows.Automation.AutomationProperties.GetName(
                            LanguageSettingsButton),
                        expectedName,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        LanguageSettingsButton.ToolTip as string,
                        expectedName,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        DestinationHelpText.Text,
                        expectedDestinationHelp,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        System.Windows.Automation.AutomationProperties.GetName(
                            PlaceIdBox),
                        expectedDestinationName,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        DestinationValidationText.Text,
                        Localize(
                            "Validation.Destination.OfficialLinksOnly"),
                        StringComparison.Ordinal) ||
                    !Localization.EffectiveCulture.Name.Equals(
                        preference,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The existing main window did not adopt a supported switched language.");
                }
                PlaceIdBox.Text = originalDestination;
                RefreshLaunchAvailability();
            }
        }
        finally
        {
            PlaceIdBox.Text = originalDestination;
            RefreshLaunchAvailability();
            Localization.ApplyPreference(originalPreference);
        }
    }

    private async void LanguageSettingsButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await RunWindowOperationAsync(_ => LanguageSettingsButtonClickAsync());

    private async Task LanguageSettingsButtonClickAsync()
    {
        if (_operationBusy)
            return;

        var originalPreference = _settings.Language;
        var dialog = new LanguageSettingsDialog(originalPreference)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
            return;

        var selected = LocalizationPreference.Normalize(
            dialog.SelectedLanguage);
        if (selected.Equals(originalPreference, StringComparison.Ordinal))
            return;

        SetOperationBusy(true);
        try
        {
            var committed = await TryCommitSettingsMutationAsync(
                () => _settings.Language = selected,
                Localize("Language.SaveFailure"),
                showFailure: false,
                onCommitted: () =>
                    Localization.ApplyPreference(selected));
            if (!committed)
            {
                Localization.ApplyPreference(originalPreference);
                if (!_operationLifetime.IsShuttingDown)
                {
                    SetStatus(
                        Localize("Language.SaveFailure"),
                        Localize("Main.SettingsRollbackDetail"),
                        Localize("Main.SettingsErrorBadge"),
                        StatusTone.Error);
                }
            }
        }
        finally
        {
            if (!_operationLifetime.IsShuttingDown)
                SetOperationBusy(false);
        }
    }

    private void LocalizationService_LanguageChanged(
        object? sender,
        EventArgs e)
    {
        UpdateUpdateTooltip();
        UpdateThemeTogglePresentation();
        UpdateDestinationModePresentation();
        RenderAccountList();
        RenderRecentExperiences();
        UpdateAccountControlAvailability();
        UpdateClearHistoryButton();
        RefreshBatchRetryState();
        RefreshLaunchAvailability(announceValidation: false);
        UpdateAutoJoinActionPresentation();
        RefreshAutoJoinLocalizedState();
        if (!IsAutoJoinWatchActive && !_operationBusy)
        {
            if (_currentUser is not null && _activeProfile is not null)
                SetReadyState(announceStatus: false);
            else
                SetSignedOutState(announceStatus: false);
        }
    }

    private void UpdateUpdateTooltip() =>
        InstallUpdateButton.ToolTip = Localize(
            "Main.UpdateTooltipVersion",
            _updateService.CurrentVersion);
}
