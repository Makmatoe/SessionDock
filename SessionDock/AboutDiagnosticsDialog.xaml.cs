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

    internal AboutDiagnosticsDialog(
        SupportDiagnosticsDocument document,
        Version? version)
    {
        ArgumentNullException.ThrowIfNull(document);
        InitializeComponent();
        WindowLayoutService.FitToWorkArea(this);

        _document = document;
        DiagnosticsPreviewBox.Text = document.Text;
        VersionText.Text = version is null
            ? "Version unavailable"
            : $"Version {FormatVersion(version)}";
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
                "Copied the complete preview. Nothing else was included.",
                succeeded: true);
        }
        catch (Exception exception) when (
            exception is ExternalException or ThreadStateException)
        {
            SetActionStatus(
                "The clipboard is busy. Close other clipboard tools and try again.",
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
            Filter = "Text document (*.txt)|*.txt",
            OverwritePrompt = true,
            Title = "Export privacy-safe SessionDock diagnostics"
        };
        if (saveDialog.ShowDialog(this) != true)
        {
            SetActionStatus(
                "Export cancelled. No file was written.",
                succeeded: null);
            return;
        }

        SetButtonsEnabled(false);
        SetActionStatus("Saving the diagnostics preview...", succeeded: null);
        try
        {
            await SupportDiagnosticsExporter.ExportAsync(
                saveDialog.FileName,
                _document);
            SetActionStatus(
                "Saved the complete preview as a text file.",
                succeeded: true);
        }
        catch (Exception exception) when (IsExpectedExportFailure(exception))
        {
            SetActionStatus(
                "The diagnostics file could not be saved there. Choose another folder and try again.",
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
        ActionStatusText.Text = text;
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
}
