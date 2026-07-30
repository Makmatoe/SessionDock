using System.Windows;
using System.Windows.Controls;
using SessionDock.Services;

namespace SessionDock;

public partial class LanguageSettingsDialog : Window
{
    private readonly AppLocalizationService _localization;
    private readonly string _originalPreference;

    public LanguageSettingsDialog(string currentPreference)
    {
        InitializeComponent();
        WindowLayoutService.FitToWorkArea(this);
        _localization = ((App)Application.Current).LocalizationService;
        _originalPreference = LocalizationPreference.Normalize(
            currentPreference);
        SelectedLanguage = _originalPreference;
        LanguageComboBox.SelectedItem = LanguageComboBox.Items
            .OfType<ComboBoxItem>()
            .First(item => item.Tag is string value &&
                           value.Equals(
                               SelectedLanguage,
                               StringComparison.Ordinal));
        UpdatePreview();
    }

    public string SelectedLanguage { get; private set; }

    private void LanguageComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (LanguageComboBox.SelectedItem is not ComboBoxItem
            {
                Tag: string value
            })
        {
            return;
        }

        SelectedLanguage = LocalizationPreference.Normalize(value);
        _localization.ApplyPreference(SelectedLanguage);
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var cultureName = _localization.EffectiveCulture.NativeName;
        PreviewText.Text = _localization.Format(
            "Language.Preview",
            cultureName);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e) =>
        DialogResult = true;

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _localization.ApplyPreference(_originalPreference);
        DialogResult = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DialogResult != true)
            _localization.ApplyPreference(_originalPreference);
        base.OnClosed(e);
    }
}
