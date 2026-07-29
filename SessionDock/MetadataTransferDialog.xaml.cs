using System.IO;
using System.Security;
using System.Windows;
using Microsoft.Win32;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock;

public partial class MetadataTransferDialog : Window
{
    private readonly MetadataExportPackage _exportPackage;
    private readonly AppSettings _settings;
    private MetadataImportPlan? _importPlan;

    internal MetadataTransferDialog(
        MetadataExportPackage exportPackage,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(exportPackage);
        ArgumentNullException.ThrowIfNull(settings);
        InitializeComponent();
        WindowLayoutService.FitToWorkArea(this);
        _exportPackage = exportPackage;
        _settings = settings;
        ExportPreviewBox.Text = exportPackage.Json;
        ExportSummaryText.Text =
            $"Review the exact file below: {exportPackage.AccountCount} account appearance entr{(exportPackage.AccountCount == 1 ? "y" : "ies")} and {exportPackage.PublicFavoriteCount} pinned public favorite{(exportPackage.PublicFavoriteCount == 1 ? string.Empty : "s")}.";
        Loaded += MetadataTransferDialog_Loaded;
    }

    internal MetadataImportPlan? ImportPlan { get; private set; }

    private void MetadataTransferDialog_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        Loaded -= MetadataTransferDialog_Loaded;
        ExportButton.Focus();
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var saveDialog = new SaveFileDialog
        {
            AddExtension = true,
            CheckPathExists = true,
            DefaultExt = ".json",
            FileName = MetadataExportPackage.SuggestedFileName,
            Filter = "JSON document (*.json)|*.json",
            OverwritePrompt = true,
            Title = "Export privacy-safe SessionDock metadata"
        };
        if (saveDialog.ShowDialog(this) != true)
        {
            SetExportStatus("Export cancelled. No file was written.", null);
            return;
        }

        SetButtonsEnabled(false);
        SetExportStatus("Saving the reviewed safe metadata...", null);
        try
        {
            await MetadataTransferService.ExportAsync(
                saveDialog.FileName,
                _exportPackage);
            SetExportStatus(
                "Saved exactly the reviewed JSON. Nothing else was included.",
                true);
        }
        catch (Exception exception) when (IsExpectedTransferFailure(exception))
        {
            SetExportStatus(
                "The metadata file could not be saved there. Choose another folder and try again.",
                false);
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    private async void ChooseImportButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var openDialog = new OpenFileDialog
        {
            CheckFileExists = true,
            CheckPathExists = true,
            Filter = "SessionDock metadata (*.json)|*.json",
            Multiselect = false,
            Title = "Choose SessionDock metadata to preview"
        };
        if (openDialog.ShowDialog(this) != true)
        {
            SetImportStatus("Import selection cancelled. Nothing changed.", null);
            return;
        }

        _importPlan = null;
        ImportConfirmationCheckBox.IsChecked = false;
        ImportConfirmationCheckBox.IsEnabled = false;
        ConfirmImportButton.IsEnabled = false;
        ImportPreviewBox.Text = "Validating the selected file...";
        SetButtonsEnabled(false);
        SetImportStatus("Validating the selected file locally...", null);
        try
        {
            _importPlan = await MetadataTransferService.ReadImportAsync(
                openDialog.FileName,
                _settings);
            ImportPreviewBox.Text = _importPlan.Preview;
            ImportConfirmationCheckBox.IsEnabled = _importPlan.HasChanges;
            SetImportStatus(
                _importPlan.HasChanges
                    ? "Review the complete plan, then check the confirmation box to enable import."
                    : "The file is valid, but it has no changes that apply to this SessionDock.",
                _importPlan.HasChanges ? null : true);
        }
        catch (InvalidDataException exception)
        {
            ImportPreviewBox.Text =
                "The selected file was rejected. Nothing was changed.";
            SetImportStatus(exception.Message, false);
        }
        catch (Exception exception) when (IsExpectedTransferFailure(exception))
        {
            ImportPreviewBox.Text =
                "The selected file could not be read safely. Nothing was changed.";
            SetImportStatus(
                "Choose a regular local JSON file that you can read, then try again.",
                false);
        }
        finally
        {
            SetButtonsEnabled(true);
            UpdateImportConfirmationState();
        }
    }

    private void ImportConfirmationCheckBox_Changed(
        object sender,
        RoutedEventArgs e) => UpdateImportConfirmationState();

    private void ConfirmImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_importPlan?.HasChanges != true ||
            ImportConfirmationCheckBox.IsChecked != true)
        {
            return;
        }

        ImportPlan = _importPlan;
        DialogResult = true;
    }

    private void UpdateImportConfirmationState() =>
        ConfirmImportButton.IsEnabled =
            _importPlan?.HasChanges == true &&
            ImportConfirmationCheckBox.IsChecked == true;

    private void SetButtonsEnabled(bool enabled)
    {
        ExportButton.IsEnabled = enabled;
        ChooseImportButton.IsEnabled = enabled;
        DoneButton.IsEnabled = enabled;
        ImportConfirmationCheckBox.IsEnabled =
            enabled && _importPlan?.HasChanges == true;
        ConfirmImportButton.IsEnabled =
            enabled &&
            _importPlan?.HasChanges == true &&
            ImportConfirmationCheckBox.IsChecked == true;
    }

    private void SetExportStatus(string text, bool? succeeded) =>
        SetStatus(ExportStatusText, text, succeeded);

    private void SetImportStatus(string text, bool? succeeded) =>
        SetStatus(ImportStatusText, text, succeeded);

    private static void SetStatus(
        System.Windows.Controls.TextBlock status,
        string text,
        bool? succeeded)
    {
        status.Text = text;
        status.SetResourceReference(
            System.Windows.Controls.TextBlock.ForegroundProperty,
            succeeded switch
            {
                true => "SuccessTextBrush",
                false => "ErrorTextBrush",
                _ => "MutedBrush"
            });
    }

    internal static bool IsExpectedTransferFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
            SecurityException or ArgumentException or NotSupportedException;
}
