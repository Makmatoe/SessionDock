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
    private readonly AccessibilityLiveRegion _exportStatusLiveRegion;
    private readonly AccessibilityLiveRegion _importStatusLiveRegion;
    private MetadataImportPlan? _importPlan;

    internal MetadataTransferDialog(
        MetadataExportPackage exportPackage,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(exportPackage);
        ArgumentNullException.ThrowIfNull(settings);
        InitializeComponent();
        _exportStatusLiveRegion = new AccessibilityLiveRegion(ExportStatusText);
        _importStatusLiveRegion = new AccessibilityLiveRegion(ImportStatusText);
        WindowLayoutService.FitToWorkArea(this);
        _exportPackage = exportPackage;
        _settings = settings;
        ExportPreviewBox.Text = exportPackage.Json;
        ExportSummaryText.Text = CreateExportSummary(
            exportPackage.AccountCount,
            exportPackage.PublicFavoriteCount);
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
            Filter = Localize("Metadata.JsonFilter"),
            OverwritePrompt = true,
            Title = Localize("Metadata.ExportPickerTitle")
        };
        if (saveDialog.ShowDialog(this) != true)
        {
            SetExportStatus(Localize("Metadata.ExportCancelled"), null);
            return;
        }

        SetButtonsEnabled(false);
        SetExportStatus(Localize("Metadata.Saving"), null);
        try
        {
            await MetadataTransferService.ExportAsync(
                saveDialog.FileName,
                _exportPackage);
            SetExportStatus(
                Localize("Metadata.Saved"),
                true);
        }
        catch (Exception exception) when (IsExpectedTransferFailure(exception))
        {
            SetExportStatus(
                Localize("Metadata.SaveFailed"),
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
            Filter = Localize("Metadata.ImportFilter"),
            Multiselect = false,
            Title = Localize("Metadata.ImportPickerTitle")
        };
        if (openDialog.ShowDialog(this) != true)
        {
            SetImportStatus(Localize("Metadata.ImportCancelled"), null);
            return;
        }

        _importPlan = null;
        ImportConfirmationCheckBox.IsChecked = false;
        ImportConfirmationCheckBox.IsEnabled = false;
        ConfirmImportButton.IsEnabled = false;
        ImportPreviewBox.Text = Localize("Metadata.ValidatingFile");
        SetButtonsEnabled(false);
        SetImportStatus(Localize("Metadata.ValidatingLocal"), null);
        try
        {
            _importPlan = await MetadataTransferService.ReadImportAsync(
                openDialog.FileName,
                _settings);
            ImportPreviewBox.Text = _importPlan.Preview;
            ImportConfirmationCheckBox.IsEnabled = _importPlan.HasChanges;
            SetImportStatus(
                _importPlan.HasChanges
                    ? Localize("Metadata.ReviewToEnable")
                    : Localize("Metadata.NoApplicableChanges"),
                _importPlan.HasChanges ? null : true);
        }
        catch (InvalidDataException)
        {
            ImportPreviewBox.Text = Localize("Metadata.Rejected");
            SetImportStatus(Localize("Metadata.InvalidDetail"), false);
        }
        catch (Exception exception) when (IsExpectedTransferFailure(exception))
        {
            ImportPreviewBox.Text = Localize("Metadata.ReadFailed");
            SetImportStatus(
                Localize("Metadata.ReadFailedDetail"),
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
        SetStatus(
            ExportStatusText,
            _exportStatusLiveRegion,
            text,
            succeeded);

    private void SetImportStatus(string text, bool? succeeded) =>
        SetStatus(
            ImportStatusText,
            _importStatusLiveRegion,
            text,
            succeeded);

    private static void SetStatus(
        System.Windows.Controls.TextBlock status,
        AccessibilityLiveRegion liveRegion,
        string text,
        bool? succeeded)
    {
        liveRegion.Update(
            text,
            severity: succeeded == false
                ? AccessibilityLiveRegionSeverity.Assertive
                : AccessibilityLiveRegionSeverity.Polite);
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

    private AppLocalizationService Localization =>
        ((App)Application.Current).LocalizationService;

    private string Localize(string key) => Localization.GetString(key);

    private string Localize(string key, params object?[] arguments) =>
        Localization.Format(key, arguments);

    private string CreateExportSummary(
        int accountCount,
        int favoriteCount) =>
        (accountCount == 1, favoriteCount == 1) switch
        {
            (true, true) => Localize("Metadata.ExportSummaryOneOne"),
            (true, false) => Localize(
                "Metadata.ExportSummaryOneMany",
                favoriteCount),
            (false, true) => Localize(
                "Metadata.ExportSummaryManyOne",
                accountCount),
            _ => Localize(
                "Metadata.ExportSummaryManyMany",
                accountCount,
                favoriteCount)
        };
}
