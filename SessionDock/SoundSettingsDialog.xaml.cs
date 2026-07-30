using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SessionDock.Services;

namespace SessionDock;

public partial class SoundSettingsDialog : Window
{
    private readonly UiSoundService _soundService;
    private readonly string? _existingCustomFileName;
    private readonly AccessibilityLiveRegion _validationLiveRegion;

    public bool UiSoundsEnabled { get; private set; }
    public string StartupSound { get; private set; }
    public string? PendingCustomSourcePath { get; private set; }

    public SoundSettingsDialog(
        UiSoundService soundService,
        bool uiSoundsEnabled,
        string startupSound,
        string? customFileName)
    {
        InitializeComponent();
        _validationLiveRegion = new AccessibilityLiveRegion(ValidationText);
        WindowLayoutService.FitToWorkArea(this);
        _soundService = soundService;
        _existingCustomFileName = customFileName;
        UiSoundsEnabled = uiSoundsEnabled;
        StartupSound = UiSoundService.IsValidStartupSound(startupSound)
            ? startupSound
            : UiSoundService.DefaultStartupSound;
        UiSoundsCheckBox.IsChecked = uiSoundsEnabled;
        SelectStartupSound(StartupSound);
        ImportedSoundText.Text = customFileName is null
            ? ((App)Application.Current).LocalizationService.GetString(
                "Sound.NoCustom")
            : customFileName;
        Closed += (_, _) => _soundService.StopPreview();
    }

    private void SelectStartupSound(string value)
    {
        StartupSoundComboBox.SelectedItem = StartupSoundComboBox.Items
            .OfType<ComboBoxItem>()
            .First(item => item.Tag is string tag &&
                           tag.Equals(value, StringComparison.OrdinalIgnoreCase));
    }

    private void StartupSoundComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        SetValidation(string.Empty);
        _soundService.StopPreview();
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = ((App)Application.Current).LocalizationService.GetString(
                "Sound.ImportTitle"),
            Filter = ((App)Application.Current).LocalizationService.GetString(
                "Sound.ImportFilter"),
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            SetValidation(Localize("Sound.ImportCancelled"));
            return;
        }

        try
        {
            SelectStartupSound(UiSoundService.StartupCustom);
            _soundService.Preview(
                UiSoundService.StartupCustom,
                customFileName: null,
                dialog.FileName);
            PendingCustomSourcePath = dialog.FileName;
            ImportedSoundText.Text = Path.GetFileName(dialog.FileName);
            SetValidation(string.Empty);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
                InvalidOperationException or NotSupportedException)
        {
            SetValidation(Localize("Sound.ImportFailed"), isError: true);
        }
    }

    private void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _soundService.Preview(
                GetSelectedStartupSound(),
                _existingCustomFileName,
                PendingCustomSourcePath);
            SetValidation(string.Empty);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
                InvalidOperationException or NotSupportedException)
        {
            SetValidation(Localize("Sound.PreviewFailed"), isError: true);
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedStartupSound();
        if (selected.Equals(UiSoundService.StartupCustom, StringComparison.OrdinalIgnoreCase) &&
            PendingCustomSourcePath is null &&
            !UiSoundService.IsValidImportedFileName(_existingCustomFileName))
        {
            SetValidation(
                Localize("Sound.ImportRequired"),
                isError: true);
            return;
        }

        UiSoundsEnabled = UiSoundsCheckBox.IsChecked == true;
        StartupSound = selected;
        _soundService.StopPreview();
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _soundService.StopPreview();
        DialogResult = false;
    }

    private string GetSelectedStartupSound() =>
        StartupSoundComboBox.SelectedItem is ComboBoxItem { Tag: string value }
            ? value
            : UiSoundService.DefaultStartupSound;

    private void SetValidation(string text, bool isError = false) =>
        _validationLiveRegion.Update(
            text,
            severity: isError
                ? AccessibilityLiveRegionSeverity.Assertive
                : AccessibilityLiveRegionSeverity.Polite);

    private AppLocalizationService Localization =>
        ((App)Application.Current).LocalizationService;

    private string Localize(string key) => Localization.GetString(key);
}
