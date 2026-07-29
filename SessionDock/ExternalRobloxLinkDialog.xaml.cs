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
        PreviewTitleText.Text = link.PreviewTitle;
        PreviewDetailText.Text = link.PreviewDetail;
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

    private sealed record ExternalLinkAccountOption(
        string Key,
        string DisplayName);
}
