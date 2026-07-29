using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock;

public partial class AccountAppearanceDialog : Window
{
    private static readonly IReadOnlyDictionary<string, string> ColorNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["#7C5CFC"] = "Purple",
            ["#4D8DFF"] = "Blue",
            ["#27B58A"] = "Green",
            ["#E0A33A"] = "Gold",
            ["#E36B8D"] = "Rose",
            ["#A56DE2"] = "Violet"
        };

    public string? AccountLabel { get; private set; }
    public string? AccountGroup { get; private set; }
    public string SelectedColor { get; private set; }

    public AccountAppearanceDialog(AccountProfile account)
    {
        InitializeComponent();
        WindowLayoutService.FitToWorkArea(this);
        AccountIdentityText.Text = $"@{account.Username}  •  User ID {account.UserId}";
        LabelBox.Text = account.Label ?? string.Empty;
        GroupBox.Text = account.Group ?? string.Empty;
        SelectedColor = SettingsService.AccountColors.Contains(account.ColorHex)
            ? account.ColorHex!
            : SettingsService.AccountColors[0];
        UpdateColorPreview();
        Loaded += (_, _) =>
        {
            LabelBox.Focus();
            LabelBox.SelectAll();
        };
    }

    private void ColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color } &&
            SettingsService.AccountColors.Contains(
                color,
                StringComparer.OrdinalIgnoreCase))
        {
            SelectedColor = color;
            UpdateColorPreview();
        }
    }

    private void UpdateColorPreview()
    {
        SelectedColorPreview.Background =
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(SelectedColor));
        SelectedColorText.Text = $"Selected: {ColorNames[SelectedColor]}";
        foreach (var button in ColorChoices.Children.OfType<Button>())
        {
            var selected = button.Tag is string color &&
                           color.Equals(
                               SelectedColor,
                               StringComparison.OrdinalIgnoreCase);
            button.Content = selected ? "✓" : null;
            button.Opacity = selected ? 1 : 0.72;
            System.Windows.Automation.AutomationProperties.SetItemStatus(
                button,
                selected ? "Selected" : "Not selected");
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        AccountLabel = string.IsNullOrWhiteSpace(LabelBox.Text)
            ? null
            : LabelBox.Text.Trim();
        AccountGroup = BatchLaunchPreferences.NormalizeAccountGroup(
            GroupBox.Text);
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) =>
        DialogResult = false;
}
