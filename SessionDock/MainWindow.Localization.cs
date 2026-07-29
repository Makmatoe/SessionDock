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
        Dispatcher.VerifyAccess();
        var originalPreference = Localization.CurrentPreference;
        var switchPreference = Localization.EffectiveCulture.Name.Equals(
            LocalizationPreference.Dutch,
            StringComparison.Ordinal)
                ? LocalizationPreference.English
                : LocalizationPreference.Dutch;
        try
        {
            Localization.ApplyPreference(switchPreference);
            var expectedName = Localize("Main.LanguageSettings");
            if (!string.Equals(
                    System.Windows.Automation.AutomationProperties.GetName(
                        LanguageSettingsButton),
                    expectedName,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    LanguageSettingsButton.ToolTip as string,
                    expectedName,
                    StringComparison.Ordinal) ||
                !Localization.EffectiveCulture.Name.Equals(
                    switchPreference,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The existing main window did not adopt the switched language.");
            }
        }
        finally
        {
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
                onCommitted: () =>
                    Localization.ApplyPreference(selected));
            if (!committed)
                Localization.ApplyPreference(originalPreference);
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
    }

    private void UpdateUpdateTooltip() =>
        InstallUpdateButton.ToolTip = Localize(
            "Main.UpdateTooltipVersion",
            _updateService.CurrentVersion);
}
