using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using Microsoft.Win32;
using SessionDock.Services;

namespace SessionDock;

public partial class AboutDiagnosticsDialog : Window
{
    private readonly SupportDiagnosticsDocument _document;
    private readonly AccessibilityLiveRegion _actionStatusLiveRegion;

    internal AboutDiagnosticsDialog(
        SupportDiagnosticsDocument document,
        Version? version)
    {
        ArgumentNullException.ThrowIfNull(document);
        InitializeComponent();
        _actionStatusLiveRegion =
            new AccessibilityLiveRegion(ActionStatusText);
        WindowLayoutService.FitToWorkArea(this);

        _document = document;
        DiagnosticsPreviewBox.Text = document.Text;
        VersionText.Text = version is null
            ? Localize("About.VersionUnavailable")
            : Localize("About.Version", FormatVersion(version));
        Loaded += AboutDiagnosticsDialog_Loaded;
    }

    private void AboutDiagnosticsDialog_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        Loaded -= AboutDiagnosticsDialog_Loaded;
        CopyButton.Focus();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_document.Text);
            SetActionStatus(
                Localize("About.Copied"),
                succeeded: true);
        }
        catch (Exception exception) when (
            exception is ExternalException or ThreadStateException)
        {
            SetActionStatus(
                Localize("About.ClipboardBusy"),
                succeeded: false);
        }
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var saveDialog = new SaveFileDialog
        {
            AddExtension = true,
            CheckPathExists = true,
            DefaultExt = ".txt",
            FileName = SupportDiagnosticsExporter.SuggestedFileName,
            Filter = Localize("About.TextFilter"),
            OverwritePrompt = true,
            Title = Localize("About.ExportPickerTitle")
        };
        if (saveDialog.ShowDialog(this) != true)
        {
            SetActionStatus(
                Localize("About.ExportCancelled"),
                succeeded: null);
            return;
        }

        SetButtonsEnabled(false);
        SetActionStatus(Localize("About.Saving"), succeeded: null);
        try
        {
            await SupportDiagnosticsExporter.ExportAsync(
                saveDialog.FileName,
                _document);
            SetActionStatus(
                Localize("About.Saved"),
                succeeded: true);
        }
        catch (Exception exception) when (IsExpectedExportFailure(exception))
        {
            SetActionStatus(
                Localize("About.SaveFailed"),
                succeeded: false);
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    private void SetButtonsEnabled(bool enabled)
    {
        CopyButton.IsEnabled = enabled;
        ExportButton.IsEnabled = enabled;
        DoneButton.IsEnabled = enabled;
    }

    private void SetActionStatus(string text, bool? succeeded)
    {
        _actionStatusLiveRegion.Update(
            text,
            severity: succeeded == false
                ? AccessibilityLiveRegionSeverity.Assertive
                : AccessibilityLiveRegionSeverity.Polite);
        ActionStatusText.SetResourceReference(
            System.Windows.Controls.TextBlock.ForegroundProperty,
            succeeded switch
            {
                true => "SuccessTextBrush",
                false => "ErrorTextBrush",
                _ => "MutedBrush"
            });
    }

    internal static bool IsExpectedExportFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
            SecurityException or ArgumentException or NotSupportedException;

    private static string FormatVersion(Version version)
    {
        var fieldCount = version.Build >= 0 ? 3 : 2;
        return version.ToString(fieldCount);
    }

    private AppLocalizationService Localization =>
        ((App)Application.Current).LocalizationService;

    private string Localize(string key) => Localization.GetString(key);

    private string Localize(string key, params object?[] arguments) =>
        Localization.Format(key, arguments);
}
