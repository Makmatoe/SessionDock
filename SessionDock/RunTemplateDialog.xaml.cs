using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock;

public partial class RunTemplateDialog : Window
{
    private readonly IReadOnlyDictionary<string, AccountProfile> _accounts;
    private readonly AppLocalizationService _localization;
    private readonly AccessibilityLiveRegion _summaryLiveRegion;
    private readonly AccessibilityLiveRegion _validationLiveRegion;

    internal RunTemplateDialog(
        IReadOnlyList<SessionTemplate> templates,
        IReadOnlyList<BatchLaunchPreset> legacyPresets,
        IReadOnlyList<AccountProfile> accounts)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(legacyPresets);
        ArgumentNullException.ThrowIfNull(accounts);
        InitializeComponent();
        _localization = ((App)Application.Current).LocalizationService;
        _summaryLiveRegion = new AccessibilityLiveRegion(TemplateSummaryText);
        _validationLiveRegion = new AccessibilityLiveRegion(ValidationText);
        WindowLayoutService.FitToWorkArea(this);
        _accounts = accounts
            .Where(account => !string.IsNullOrWhiteSpace(account.Key))
            .GroupBy(account => account.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);

        var options = templates
            .Select(template => new TemplateOption(template, template.Name))
            .Concat(legacyPresets.Select(CreateLegacyOption))
            .OrderBy(option => option.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        TemplateComboBox.ItemsSource = options;
        if (options.Length > 0)
            TemplateComboBox.SelectedIndex = 0;
        else
            ShowEmptyState();
        Loaded += (_, _) => TemplateComboBox.Focus();
    }

    internal SessionTemplate? SelectedTemplate { get; private set; }

    private void TemplateComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (TemplateComboBox.SelectedItem is not TemplateOption option)
        {
            ShowEmptyState();
            return;
        }

        var template = option.Template;
        var available = template.ClientSlots.Count(slot =>
            _accounts.ContainsKey(slot.AccountKey));
        var missing = template.ClientSlots.Count - available;
        TemplateSummaryTitle.Text = option.DisplayName;
        var summary = string.Join(
            Environment.NewLine,
            Localize(
                "Template.AccountSummary",
                available,
                template.ClientSlots.Count),
            Localize(
                "Template.LayoutSummary",
                Localize($"Template.Layout.{template.LayoutMode}")),
            Localize(
                "Template.MacroSummary",
                Localize($"Template.Macro.{template.MacroMode}")),
            Localize("Template.DelaySummary", template.DelaySeconds));
        _summaryLiveRegion.Update(
            summary,
            $"{option.DisplayName}. {summary}",
            AccessibilityLiveRegionSeverity.Polite);
        var validation = missing > 0
            ? Localize("Template.MissingAccounts", missing)
            : string.Empty;
        _validationLiveRegion.Update(
            validation,
            validation,
            AccessibilityLiveRegionSeverity.Assertive);
        ValidationText.Visibility = missing > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        RunButton.IsEnabled = available > 0 && missing == 0;
    }

    private void RunButton_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (TemplateComboBox.SelectedItem is not TemplateOption option ||
            option.Template.ClientSlots.Count == 0 ||
            option.Template.ClientSlots.Any(slot =>
                !_accounts.ContainsKey(slot.AccountKey)))
        {
            return;
        }

        SelectedTemplate = option.Template;
        DialogResult = true;
    }

    private void ShowEmptyState()
    {
        TemplateSummaryTitle.Text = Localize("Template.NoTemplatesTitle");
        var summary = Localize("Template.NoTemplatesDescription");
        _summaryLiveRegion.Update(
            summary,
            $"{TemplateSummaryTitle.Text}. {summary}",
            AccessibilityLiveRegionSeverity.Polite);
        _validationLiveRegion.Update(string.Empty);
        ValidationText.Visibility = Visibility.Collapsed;
        RunButton.IsEnabled = false;
    }

    private TemplateOption CreateLegacyOption(BatchLaunchPreset preset)
    {
        var identityMaterial = string.Join(
            "\n",
            preset.Name,
            preset.DelaySeconds,
            string.Join("\n", preset.AccountKeys));
        var id = "legacy-" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(identityMaterial)))
            .ToLowerInvariant()[..24];
        var template = new SessionTemplate
        {
            Id = id,
            Name = preset.Name,
            DelaySeconds = preset.DelaySeconds,
            LayoutMode = SessionTemplateLayoutMode.Cascade,
            MacroMode = SessionTemplateMacroMode.None,
            LegacyPresetName = preset.Name,
            ClientSlots = preset.AccountKeys
                .Select((key, order) => new SessionTemplateClientSlot
                {
                    SlotId = $"legacy-slot-{order}",
                    AccountKey = key,
                    Order = order
                })
                .ToList()
        };
        return new TemplateOption(
            template,
            $"{template.Name} \u2014 {Localize("Template.LegacyPreset")}");
    }

    private string Localize(string key, params object?[] arguments)
    {
        return arguments.Length == 0
            ? _localization.GetString(key)
            : _localization.Format(key, arguments);
    }

    private sealed record TemplateOption(
        SessionTemplate Template,
        string DisplayName) : IDropdownLabel;
}
