using System.Windows;
using SessionDock.ReleaseTrust;
using SessionDock.Services;

namespace SessionDock;

public partial class UpdateConfirmationDialog : Window
{
    public UpdateConfirmationDialog(
        VerifiedReleaseDescriptor update,
        bool alreadyDownloaded)
    {
        ArgumentNullException.ThrowIfNull(update);
        InitializeComponent();
        WindowLayoutService.FitToWorkArea(this);
        var localization = ((App)Application.Current).LocalizationService;

        UpdateTitleText.Text = localization.Format(
            alreadyDownloaded
                ? "Update.RestartQuestion"
                : "Update.InstallQuestion",
            update.Version.ToString(3));
        PublishedText.Text = localization.Format(
            "Update.Published",
            LocalizationCulture.FormatLocalDateTime(
                update.PublishedAt,
                localization.EffectiveCulture));
        SizeText.Text = localization.Format(
            "Update.SizeMegabytes",
            update.Descriptor.PackageSize / (1024d * 1024d));
        var releaseNotes = ReleaseNotesTextFormatter.Format(
            update.Descriptor.ReleaseNotes);
        ReleaseNotesBox.Text = localization.EffectiveCulture.Name.Equals(
                LocalizationPreference.English,
                StringComparison.Ordinal)
            ? releaseNotes
            : $"{localization.GetString("Update.ReleaseNotesEnglishFallback")}" +
              $"{Environment.NewLine}{Environment.NewLine}{releaseNotes}";
        IntegrityText.Text = alreadyDownloaded
            ? localization.Format(
                "Update.IntegrityDownloaded",
                update.Descriptor.PackageSha256[..16])
            : localization.Format(
                "Update.IntegrityPending",
                update.Descriptor.PackageSha256[..16]);
    }

    private void InstallButton_Click(object sender, RoutedEventArgs e) =>
        DialogResult = true;

    private void CancelButton_Click(object sender, RoutedEventArgs e) =>
        DialogResult = false;
}
