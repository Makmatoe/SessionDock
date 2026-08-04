using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock;

public partial class ExternalRobloxLinkDialog : Window
{
    internal ExternalRobloxLinkDialog(
        ExternalRobloxLink link,
        IReadOnlyList<AccountProfile> accounts,
        string? activeAccountKey)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(accounts);
        InitializeComponent();
        WindowLayoutService.FitToWorkArea(this);
        var preview = CreatePreview(link);
        PreviewTitleText.Text = preview.Title;
        PreviewDetailText.Text = preview.Detail;
        PrivateNoticePanel.Visibility = link.IsPrivateServer
            ? Visibility.Visible
            : Visibility.Collapsed;
        var options = accounts
            .Select(account => new ExternalLinkAccountOption(
                account.Key,
                string.IsNullOrWhiteSpace(account.Label)
                    ? $"@{account.Username}"
                    : $"{account.Label} (@{account.Username})"))
            .ToArray();
        AccountComboBox.ItemsSource = options;
        AccountComboBox.SelectedItem = options.FirstOrDefault(option =>
            option.Key.Equals(
                activeAccountKey,
                StringComparison.OrdinalIgnoreCase)) ?? options.FirstOrDefault();
        UpdateReviewAvailability();
    }

    internal string? SelectedAccountKey { get; private set; }

    private void AccountComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e) => UpdateReviewAvailability();

    private void UpdateReviewAvailability() =>
        ReviewButton.IsEnabled =
            AccountComboBox.SelectedItem is ExternalLinkAccountOption;

    private void ReviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (AccountComboBox.SelectedItem is not ExternalLinkAccountOption option)
            return;
        SelectedAccountKey = option.Key;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) =>
        DialogResult = false;

    private AppLocalizationService Localization =>
        ((App)Application.Current).LocalizationService;

    private string Localize(string key) => Localization.GetString(key);

    private string Localize(string key, params object?[] arguments) =>
        Localization.Format(key, arguments);

    private (string Title, string Detail) CreatePreview(
        ExternalRobloxLink link)
    {
        var title = Localize(
            link.IsPrivateServer
                ? "ExternalLink.PrivateTitle"
                : "ExternalLink.PublicTitle");
        var placeDetail = link.Target.PlaceId > 0
            ? Localize(
                "ExternalLink.PlaceDetail",
                link.Target.PlaceId.ToString(CultureInfo.InvariantCulture))
            : Localize("ExternalLink.ResolveDetail");
        var detail = link.IsPrivateServer
            ? Localize("ExternalLink.PrivateDetail", placeDetail)
            : placeDetail;
        return (title, detail);
    }

    private sealed record ExternalLinkAccountOption(
        string Key,
        string DisplayName) : IDropdownLabel;
}
