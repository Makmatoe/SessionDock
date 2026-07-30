using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SessionDock.Services;

namespace SessionDock;

public partial class ReleaseNotesDialog : Window
{
    private readonly bool _hasPreviousRelease;

    internal ReleaseNotesDialog(BundledReleaseNotes notes)
    {
        ArgumentNullException.ThrowIfNull(notes);
        InitializeComponent();
        WindowLayoutService.FitToWorkArea(this);

        var currentVersion = notes.Current.Version.ToString(3);
        CurrentVersionText.Text = Localize(
            "Release.Version",
            currentVersion);
        CurrentNotesBox.Text = FormatNotes(notes.Current);
        AutomationProperties.SetName(
            CurrentReleaseButton,
            Localize("Release.CurrentAutomation", currentVersion));
        AutomationProperties.SetName(
            CurrentNotesBox,
            Localize("Release.NotesAutomation", currentVersion));

        _hasPreviousRelease = notes.Previous is not null;
        if (notes.Previous is { } previous)
        {
            var previousVersion = previous.Version.ToString(3);
            PreviousVersionText.Text = Localize(
                "Release.Version",
                previousVersion);
            PreviousNotesBox.Text = FormatNotes(previous);
            AutomationProperties.SetName(
                PreviousReleaseButton,
                Localize("Release.PreviousAutomation", previousVersion));
            AutomationProperties.SetName(
                PreviousNotesBox,
                Localize("Release.NotesAutomation", previousVersion));
        }
        else
        {
            PreviousVersionText.Text = Localize("Release.NoEarlierTitle");
            PreviousNotesBox.Text = Localize("Release.NoEarlierDetail");
            PreviousReleaseButton.IsEnabled = false;
            AutomationProperties.SetName(
                PreviousReleaseButton,
                Localize("Release.NoPreviousAutomation"));
        }

        ShowRelease(current: true, moveFocus: false);
        Loaded += ReleaseNotesDialog_Loaded;
    }

    private void ReleaseNotesDialog_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= ReleaseNotesDialog_Loaded;
        CurrentReleaseButton.Focus();
    }

    private void CurrentReleaseButton_Click(object sender, RoutedEventArgs e) =>
        ShowRelease(current: true, moveFocus: false);

    private void PreviousReleaseButton_Click(object sender, RoutedEventArgs e) =>
        ShowRelease(current: false, moveFocus: false);

    private void ReleaseTabButton_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (e.Key == Key.Left)
        {
            ShowRelease(current: true, moveFocus: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Right && _hasPreviousRelease)
        {
            ShowRelease(current: false, moveFocus: true);
            e.Handled = true;
        }
    }

    private void ShowRelease(bool current, bool moveFocus)
    {
        if (!current && !_hasPreviousRelease)
            return;

        CurrentNotesPanel.Visibility = current
            ? Visibility.Visible
            : Visibility.Collapsed;
        PreviousNotesPanel.Visibility = current
            ? Visibility.Collapsed
            : Visibility.Visible;
        SetTabSelected(CurrentReleaseButton, current);
        SetTabSelected(PreviousReleaseButton, !current);
        AutomationProperties.SetItemStatus(
            CurrentReleaseButton,
            Localize(current ? "Common.Selected" : "Common.NotSelected"));
        AutomationProperties.SetItemStatus(
            PreviousReleaseButton,
            Localize(current ? "Common.NotSelected" : "Common.Selected"));

        if (moveFocus)
        {
            (current
                ? CurrentReleaseButton
                : PreviousReleaseButton).Focus();
        }
    }

    private void SetTabSelected(Button button, bool selected)
    {
        button.IsTabStop = selected;
        button.SetResourceReference(
            Control.BackgroundProperty,
            selected
                ? "ReleaseTabSelectedBrush"
                : "ReleaseTabIdleBrush");
        button.SetResourceReference(
            Control.ForegroundProperty,
            selected
                ? "ReleaseTabSelectedTextBrush"
                : "ReleaseTabIdleTextBrush");
    }

    private AppLocalizationService Localization =>
        ((App)Application.Current).LocalizationService;

    private string Localize(string key) => Localization.GetString(key);

    private string Localize(string key, params object?[] arguments) =>
        Localization.Format(key, arguments);

    private string FormatNotes(BundledReleaseNote note) =>
        note.IsEnglishFallback
            ? $"{Localize("Release.EnglishFallback")}" +
              $"{Environment.NewLine}{Environment.NewLine}{note.DisplayText}"
            : note.DisplayText;
}
