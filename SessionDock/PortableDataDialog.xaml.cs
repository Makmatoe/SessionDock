using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security;
using System.Windows;
using Microsoft.Win32;
using SessionDock.ExactWheel;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock;

public partial class PortableDataDialog : Window
{
    private readonly AppSettings _settings;
    private readonly SessionTemplateCatalog _catalog;
    private readonly Func<MacroDefinition, byte[]> _readMacroBytes;
    private readonly AccessibilityLiveRegion _exportStatusLiveRegion;
    private readonly AccessibilityLiveRegion _importStatusLiveRegion;
    private readonly List<PortableSelectionRow> _templateRows = [];
    private readonly List<PortableSelectionRow> _macroRows = [];
    private readonly List<PortableSelectionRow> _destinationRows = [];
    private readonly List<PortableSelectionRow> _presetRows = [];
    private PortableExportPackage? _exportPackage;
    private PortableImportPlan? _importPlan;
    private int _ineligibleDestinationCount;
    private bool _updatingSelection;
    private bool _transferBusy;

    internal PortableDataDialog(
        AppSettings settings,
        SessionTemplateCatalog catalog,
        Func<MacroDefinition, byte[]> readMacroBytes)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(readMacroBytes);

        InitializeComponent();
        _settings = settings;
        _catalog = catalog;
        _readMacroBytes = readMacroBytes;
        _exportStatusLiveRegion = new AccessibilityLiveRegion(ExportStatusText);
        _importStatusLiveRegion = new AccessibilityLiveRegion(ImportStatusText);
        WindowLayoutService.FitToWorkArea(this);

        BuildInventory();
        BindInventory();
        RefreshSelectionState();
        Loaded += PortableDataDialog_Loaded;
    }

    internal PortableImportPlan? ImportPlan { get; private set; }

    internal bool OpenLegacyTransferRequested { get; private set; }

    internal event EventHandler? LegacyMetadataRequested;

    private void PortableDataDialog_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        Loaded -= PortableDataDialog_Loaded;
        ActionTabControl.Focus();
    }

    private void BuildInventory()
    {
        var normalizedCatalog = SessionTemplatePolicy.Normalize(_catalog);
        var definitionsById = normalizedCatalog.MacroDefinitions
            .Where(definition => !string.IsNullOrWhiteSpace(definition.ContentId))
            .GroupBy(
                definition => definition.ContentId,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var definition in definitionsById.Values.OrderBy(
                     definition => definition.Name,
                     StringComparer.CurrentCultureIgnoreCase))
        {
            var inspection = InspectMacro(definition);
            var kind = definition.Kind == SessionMacroKind.Client
                ? "Client-relative"
                : "Whole layout";
            var input = inspection.IsAvailable
                ? inspection.HasKeyboardInput
                    ? "Contains keyboard input; select only after review"
                    : "Mouse-only recording"
                : "Unavailable; the recording could not be safely inspected";
            _macroRows.Add(new PortableSelectionRow(
                PortableSelectionKind.Macro,
                definition.ContentId,
                DisplayName(definition.Name, definition.ContentId),
                $"{kind} · {FormatDuration(definition.DurationMilliseconds)} · " +
                $"{definition.EventCount:N0} events · {input}",
                inspection.IsAvailable,
                inspection.HasKeyboardInput,
                [],
                OnSelectionChanged));
        }

        foreach (var template in normalizedCatalog.Templates.OrderBy(
                     template => template.Name,
                     StringComparer.CurrentCultureIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(template.Id))
                continue;
            var dependencies = GetMacroDependencies(template);
            var missingDependencies = dependencies.Count(contentId =>
                !definitionsById.ContainsKey(contentId));
            var detail =
                $"{template.ClientSlots.Count} account slots · " +
                $"{DescribeLayoutMode(template.LayoutMode)} · " +
                DescribeMacroMode(template.MacroMode);
            if (dependencies.Count > 0)
            {
                detail += $" · {dependencies.Count} macro " +
                    (dependencies.Count == 1 ? "dependency" : "dependencies");
            }
            if (missingDependencies > 0)
                detail += $" · {missingDependencies} unavailable";

            _templateRows.Add(new PortableSelectionRow(
                PortableSelectionKind.Template,
                template.Id,
                DisplayName(template.Name, template.Id),
                detail,
                missingDependencies == 0,
                hasKeyboardInput: false,
                dependencies,
                OnSelectionChanged));
        }

        var normalizedDestinations = NamedDestinationPolicy.Normalize(
            _settings.NamedDestinations,
            _settings.Accounts);
        foreach (var destination in normalizedDestinations.OrderBy(
                     destination => destination.Name,
                     StringComparer.CurrentCultureIgnoreCase))
        {
            if (!TryGetPublicPlaceId(destination.Value, out var placeId))
            {
                _ineligibleDestinationCount++;
                continue;
            }
            _destinationRows.Add(new PortableSelectionRow(
                PortableSelectionKind.Destination,
                destination.Id,
                destination.Name,
                $"Public place {placeId} · " +
                $"{destination.AccountKeys.Count} account assignments",
                isSelectable: true,
                hasKeyboardInput: false,
                [],
                OnSelectionChanged));
        }

        var normalizedPresets = BatchLaunchPreferences.NormalizePresets(
            _settings.BatchLaunchPresets,
            _settings.Accounts);
        foreach (var preset in normalizedPresets.OrderBy(
                     preset => preset.Name,
                     StringComparer.CurrentCultureIgnoreCase))
        {
            _presetRows.Add(new PortableSelectionRow(
                PortableSelectionKind.Preset,
                preset.Name,
                preset.Name,
                $"{preset.AccountKeys.Count} accounts · " +
                $"{preset.DelaySeconds}-second launch delay",
                preset.AccountKeys.Count >= 2,
                hasKeyboardInput: false,
                [],
                OnSelectionChanged));
        }
    }

    private void BindInventory()
    {
        TemplateItemsControl.ItemsSource = _templateRows;
        MacroItemsControl.ItemsSource = _macroRows;
        DestinationItemsControl.ItemsSource = _destinationRows;
        PresetItemsControl.ItemsSource = _presetRows;
    }

    private MacroInspection InspectMacro(MacroDefinition definition)
    {
        try
        {
            var bytes = _readMacroBytes(definition);
            var recording = ExactWheelMacroSerializer.Deserialize(bytes);
            return new MacroInspection(
                IsAvailable: true,
                HasKeyboardInput: recording.Events.Any(input =>
                    input.IsKeyboardEvent));
        }
        catch (Exception exception) when (IsExpectedPackageFailure(exception))
        {
            return new MacroInspection(
                IsAvailable: false,
                HasKeyboardInput: true);
        }
    }

    private void OnSelectionChanged(PortableSelectionRow row)
    {
        if (_updatingSelection)
            return;

        if (row.Kind == PortableSelectionKind.Template && row.IsSelected)
        {
            _updatingSelection = true;
            try
            {
                foreach (var dependencyId in row.MacroDependencies)
                {
                    var dependency = _macroRows.FirstOrDefault(candidate =>
                        candidate.Id.Equals(
                            dependencyId,
                            StringComparison.OrdinalIgnoreCase));
                    if (dependency is
                        {
                            IsSelectable: true,
                            HasKeyboardInput: false
                        })
                    {
                        dependency.IsSelected = true;
                    }
                }
            }
            finally
            {
                _updatingSelection = false;
            }
        }

        RefreshSelectionState();
    }

    private void SelectEligibleButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _updatingSelection = true;
        try
        {
            foreach (var row in AllRows())
            {
                var containsKeyboardInput =
                    row.Kind == PortableSelectionKind.Macro &&
                    row.HasKeyboardInput;
                var requiresKeyboardDependency =
                    row.Kind == PortableSelectionKind.Template &&
                    row.MacroDependencies.Any(dependencyId =>
                        _macroRows.Any(macro =>
                            macro.Id.Equals(
                                dependencyId,
                                StringComparison.OrdinalIgnoreCase) &&
                            macro.HasKeyboardInput));
                row.IsSelected = row.IsSelectable &&
                    !containsKeyboardInput &&
                    !requiresKeyboardDependency;
            }
        }
        finally
        {
            _updatingSelection = false;
        }
        RefreshSelectionState();
    }

    private void ClearSelectionButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _updatingSelection = true;
        try
        {
            foreach (var row in AllRows())
                row.IsSelected = false;
            ExportKeyboardAcknowledgementCheckBox.IsChecked = false;
        }
        finally
        {
            _updatingSelection = false;
        }
        RefreshSelectionState();
    }

    private void ExportAcknowledgementCheckBox_Changed(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!_updatingSelection)
            RefreshSelectionState(invalidatePreparedPackage: false);
    }

    private void RefreshSelectionState(
        bool invalidatePreparedPackage = true)
    {
        if (invalidatePreparedPackage)
            InvalidatePreparedExport();

        var templates = SelectedCount(_templateRows);
        var macros = SelectedCount(_macroRows);
        var destinations = SelectedCount(_destinationRows);
        var presets = SelectedCount(_presetRows);
        var total = templates + macros + destinations + presets;
        var keyboardMacroIds = GetEffectiveKeyboardMacroIds();

        TemplatesCategoryHeader.Text = CategoryCount(
            "Templates",
            templates,
            _templateRows.Count);
        MacrosCategoryHeader.Text = CategoryCount(
            "Macros",
            macros,
            _macroRows.Count);
        DestinationsCategoryHeader.Text = CategoryCount(
            "Public named destinations",
            destinations,
            _destinationRows.Count);
        PresetsCategoryHeader.Text = CategoryCount(
            "Launch presets",
            presets,
            _presetRows.Count);
        ExportSelectedCountText.Text = total == 0
            ? Localize("Portable.ExportNoneSelected")
            : $"{total} selected: {templates} templates, {macros} macros, " +
              $"{destinations} destinations, {presets} launch presets.";

        DestinationEligibilityText.Text = _ineligibleDestinationCount == 0
            ? Localize("Portable.PublicOnlyDestinations")
            : $"{_ineligibleDestinationCount} private or unsupported named " +
              "destinations in this local library are not shown because they " +
              "cannot be exported.";

        var requiresKeyboardAcknowledgement = keyboardMacroIds.Count > 0;
        ExportKeyboardAcknowledgementCheckBox.Visibility =
            requiresKeyboardAcknowledgement
                ? Visibility.Visible
                : Visibility.Collapsed;
        if (!requiresKeyboardAcknowledgement)
            ExportKeyboardAcknowledgementCheckBox.IsChecked = false;

        ExportPrivacySummaryText.Text = BuildExportPrivacySummary(
            keyboardMacroIds.Count);
        ReviewExportButton.IsEnabled = !_transferBusy &&
            total > 0 &&
            (!requiresKeyboardAcknowledgement ||
             ExportKeyboardAcknowledgementCheckBox.IsChecked == true);
    }

    private string BuildExportPrivacySummary(int keyboardMacroCount)
    {
        var selectedTemplates = _templateRows
            .Where(row => row.IsSelected)
            .ToArray();
        var selectedTemplateIds = selectedTemplates
            .Select(row => row.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalizedCatalog = SessionTemplatePolicy.Normalize(_catalog);
        var omittedSlotDestinations = normalizedCatalog.Templates
            .Where(template => selectedTemplateIds.Contains(template.Id))
            .SelectMany(template => template.ClientSlots)
            .Count(slot =>
                !string.IsNullOrWhiteSpace(slot.Destination) &&
                !TryGetPublicPlaceId(slot.Destination, out _));
        var selectedDependencies = selectedTemplates
            .SelectMany(row => row.MacroDependencies)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var lines = new List<string>
        {
            Localize("Portable.DependenciesDetail"),
            $"Selected template macro dependencies: {selectedDependencies}.",
            Localize("Portable.SourceIdsStripped"),
            Localize("Portable.PublicOnlyDestinations"),
            Localize("Portable.ExactMacroBytes")
        };
        if (_ineligibleDestinationCount > 0)
        {
            lines.Add(
                $"Unavailable library-wide: {_ineligibleDestinationCount} " +
                "private or unsupported named destinations are not offered " +
                "for portable export.");
        }
        if (omittedSlotDestinations > 0)
        {
            lines.Add(
                $"Omitted from selected templates: {omittedSlotDestinations} " +
                "non-public template destinations.");
        }
        if (keyboardMacroCount > 0)
        {
            lines.Add(Localize(
                "Portable.KeyboardMacroCount",
                keyboardMacroCount));
        }
        lines.Add(Localize("Portable.ExclusionsDetail"));
        return string.Join(Environment.NewLine, lines);
    }

    private void InvalidatePreparedExport()
    {
        _exportPackage = null;
        ExportPackageButton.IsEnabled = false;
        ExportReviewPanel.Visibility = Visibility.Collapsed;
        ShowManifestCheckBox.IsChecked = false;
        ManifestPreviewBox.Clear();
    }

    private void ReviewExportButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        SetTransferBusy(true);
        SetExportStatus("Preparing and validating the selected package...", false);
        try
        {
            var selection = CreateSelection();
            var package = PortableDataPackageService.PrepareExport(
                _settings,
                _catalog,
                selection,
                _readMacroBytes);
            if (package.ContainsKeyboardInput &&
                ExportKeyboardAcknowledgementCheckBox.IsChecked != true)
            {
                _exportPackage = null;
                ExportKeyboardAcknowledgementCheckBox.Visibility =
                    Visibility.Visible;
                SetExportStatus(
                    "Review and acknowledge the keyboard-input macros before exporting.",
                    true);
                return;
            }

            _exportPackage = package;
            ManifestPreviewBox.Text = package.ManifestJson;
            ExportReviewSummaryText.Text = BuildExportReview(package);
            ExportReviewPanel.Visibility = Visibility.Visible;
            ExportPackageButton.IsEnabled = true;
            SetExportStatus(
                "The package is valid. Review the counts and save it when ready.",
                false);
        }
        catch (Exception exception) when (IsExpectedPackageFailure(exception))
        {
            _exportPackage = null;
            ExportPackageButton.IsEnabled = false;
            ExportReviewPanel.Visibility = Visibility.Collapsed;
            SetExportStatus(
                "The selection could not be prepared safely. Repair unavailable dependencies or choose fewer items.",
                true);
        }
        finally
        {
            SetTransferBusy(false);
            RefreshSelectionState(invalidatePreparedPackage: false);
            ExportPackageButton.IsEnabled = _exportPackage is not null;
        }
    }

    private string BuildExportReview(PortableExportPackage package)
    {
        var lines = new List<string>
        {
            $"Templates: {package.TemplateCount}",
            $"Macros: {package.MacroCount}",
            $"Public destinations: {package.NamedDestinationCount}",
            $"Launch presets: {package.BatchPresetCount}",
            $"Package size: {FormatBytes(package.ArchiveBytes.LongLength)}",
            Localize(
                "Portable.KeyboardMacroCount",
                package.KeyboardMacroContentIds.Count),
            $"Private or unsupported named destinations omitted: " +
            package.Omissions.NamedDestinations,
            $"Non-public template destinations omitted: " +
            package.Omissions.TemplateSlotDestinations,
            Localize("Portable.ExclusionsDetail")
        };
        return string.Join(Environment.NewLine, lines);
    }

    private PortablePackageSelection CreateSelection() => new()
    {
        TemplateIds = SelectedIds(_templateRows),
        MacroContentIds = SelectedIds(_macroRows),
        NamedDestinationIds = SelectedIds(_destinationRows),
        BatchPresetIds = SelectedIds(_presetRows)
    };

    private async void ExportPackageButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_exportPackage is not { } package)
            return;

        var saveDialog = new SaveFileDialog
        {
            AddExtension = true,
            CheckPathExists = true,
            DefaultExt = ".sessiondock",
            FileName = PortableExportPackage.SuggestedFileName,
            Filter = Localize("Portable.PackageFilter"),
            OverwritePrompt = true,
            Title = Localize("Portable.ExportPickerTitle")
        };
        if (saveDialog.ShowDialog(this) != true)
        {
            SetExportStatus(Localize("Portable.ExportCancelled"), false);
            return;
        }

        SetTransferBusy(true);
        SetExportStatus(Localize("Portable.Saving"), false);
        try
        {
            await PortableDataPackageService.WritePackageFileAsync(
                saveDialog.FileName,
                package);
            SetExportStatus(Localize("Portable.Saved"), false);
        }
        catch (Exception exception) when (IsExpectedPackageFailure(exception))
        {
            SetExportStatus(Localize("Portable.SaveFailed"), true);
        }
        finally
        {
            SetTransferBusy(false);
            RefreshSelectionState(invalidatePreparedPackage: false);
            ExportPackageButton.IsEnabled = _exportPackage is not null;
        }
    }

    private async void ChooseImportPackageButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var openDialog = new OpenFileDialog
        {
            CheckFileExists = true,
            CheckPathExists = true,
            DefaultExt = ".sessiondock",
            Filter = Localize("Portable.PackageFilter"),
            Multiselect = false,
            Title = Localize("Portable.ImportPickerTitle")
        };
        if (openDialog.ShowDialog(this) != true)
        {
            SetImportStatus(Localize("Portable.ImportCancelled"), false);
            return;
        }

        ResetImportPlan();
        SetTransferBusy(true);
        SetImportStatus(Localize("Portable.Reading"), false);
        try
        {
            var bytes = await PortableDataPackageService.ReadPackageFileAsync(
                openDialog.FileName);
            ExactWheelDisplayTopology currentDisplay;
            try
            {
                currentDisplay = ExactWheelDesktopCapture.CaptureDisplayTopology();
            }
            catch (Exception exception) when (IsExpectedDisplayFailure(exception))
            {
                throw new InvalidDataException(
                    "The current display layout could not be reviewed safely.",
                    exception);
            }

            var plan = PortableDataPackageService.PrepareImport(
                bytes,
                _settings,
                _catalog,
                currentDisplay);
            _importPlan = plan;
            ImportPreviewBox.Text = BuildImportPreview(plan, currentDisplay);
            ImportConfirmationCheckBox.IsEnabled = plan.HasChanges;
            ImportKeyboardAcknowledgementCheckBox.Visibility =
                plan.ContainsKeyboardInput
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            ImportTopologyAcknowledgementCheckBox.Visibility =
                plan.UnassignedWholeLayoutMacroCount > 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            if (!plan.HasChanges)
            {
                SetImportStatus(
                    Localize("Portable.NoApplicableChanges"),
                    false);
            }
            else
            {
                SetImportStatus(Localize("Portable.ReviewRequired"), false);
            }
            UpdateImportButtonState();
        }
        catch (Exception exception) when (IsExpectedPackageFailure(exception))
        {
            ResetImportPlan();
            ImportPreviewBox.Text = Localize("Portable.InvalidPackage");
            SetImportStatus(Localize("Portable.ReadFailed"), true);
        }
        finally
        {
            SetTransferBusy(false);
            UpdateImportButtonState();
        }
    }

    private string BuildImportPreview(
        PortableImportPlan plan,
        ExactWheelDisplayTopology currentDisplay)
    {
        var lines = new List<string>
        {
            "Validated SessionDock portable import plan",
            string.Empty,
            Localize("Portable.ResultTemplates", plan.ImportedTemplateCount),
            Localize(
                "Portable.ResultSkippedTemplates",
                plan.SkippedTemplateCount),
            Localize(
                "Portable.ResultMacros",
                plan.ImportedMacroCount,
                plan.DeduplicatedMacroCount),
            Localize(
                "Portable.ResultDestinations",
                plan.ImportedNamedDestinationCount),
            Localize(
                "Portable.ResultPresets",
                plan.ImportedBatchPresetCount),
            Localize(
                "Portable.ResultUnmatchedAccounts",
                plan.UnmatchedAccountReferenceCount),
            Localize(
                "Portable.ResultWholeLayoutUnassigned",
                plan.UnassignedWholeLayoutMacroCount),
            string.Empty,
            "Portable layout profile:",
            $"- target {plan.LayoutProfile.TargetWidth:0.#} × " +
            $"{plan.LayoutProfile.TargetHeight:0.#} logical pixels",
            $"- minimum {plan.LayoutProfile.MinimumWidth:0.#} × " +
            $"{plan.LayoutProfile.MinimumHeight:0.#} logical pixels",
            $"- cascade reveal {plan.LayoutProfile.RevealX:0.#} × " +
            $"{plan.LayoutProfile.RevealY:0.#} logical pixels",
            string.Empty,
            "Package adjustments:",
            $"- private or unsupported named destinations omitted by source: " +
            plan.Omissions.NamedDestinations,
            $"- non-public template destinations omitted by source: " +
            plan.Omissions.TemplateSlotDestinations,
            $"- macros containing keyboard input: " +
            plan.KeyboardMacroContentIds.Count,
            Localize("Portable.ClientAdaptsAtPlayback"),
            Localize("Portable.WholeLayoutUnassigned")
        };

        if (plan.WholeLayoutAssignments.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Whole-layout assignment review:");
            foreach (var assignment in plan.WholeLayoutAssignments)
            {
                var current =
                    $"current {currentDisplay.Monitors.Count} monitors, " +
                    $"{currentDisplay.VirtualWidth} × " +
                    $"{currentDisplay.VirtualHeight}";
                var result = assignment.IsAssigned
                    ? "compatible and retained"
                    : "incompatible and left unassigned";
                var reasons = assignment.AdaptationReasons.Count == 0
                    ? string.Empty
                    : $" ({string.Join(", ", assignment.AdaptationReasons)})";
                lines.Add(
                    $"- recorded {assignment.RecordedMonitorCount} monitors, " +
                    $"{assignment.RecordedVirtualWidth} × " +
                    $"{assignment.RecordedVirtualHeight}; {current}: " +
                    result + reasons);
            }
        }

        lines.Add(string.Empty);
        lines.Add(Localize("Portable.ExclusionsHeading") + ":");
        lines.Add(Localize("Portable.ExclusionsDetail"));
        return string.Join(Environment.NewLine, lines);
    }

    private void ImportAcknowledgementCheckBox_Changed(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        UpdateImportButtonState();
    }

    private void UpdateImportButtonState()
    {
        var plan = _importPlan;
        var keyboardConfirmed = plan?.ContainsKeyboardInput != true ||
            ImportKeyboardAcknowledgementCheckBox.IsChecked == true;
        var topologyConfirmed = plan?.UnassignedWholeLayoutMacroCount <= 0 ||
            ImportTopologyAcknowledgementCheckBox.IsChecked == true;
        ConfirmImportButton.IsEnabled = !_transferBusy &&
            plan?.HasChanges == true &&
            ImportConfirmationCheckBox.IsChecked == true &&
            keyboardConfirmed &&
            topologyConfirmed;
    }

    private void ConfirmImportButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_importPlan is null || !ConfirmImportButton.IsEnabled)
            return;
        ImportPlan = _importPlan;
        DialogResult = true;
    }

    private void ResetImportPlan()
    {
        _importPlan = null;
        ImportPlan = null;
        ImportConfirmationCheckBox.IsChecked = false;
        ImportConfirmationCheckBox.IsEnabled = false;
        ImportKeyboardAcknowledgementCheckBox.IsChecked = false;
        ImportKeyboardAcknowledgementCheckBox.Visibility = Visibility.Collapsed;
        ImportTopologyAcknowledgementCheckBox.IsChecked = false;
        ImportTopologyAcknowledgementCheckBox.Visibility = Visibility.Collapsed;
        ConfirmImportButton.IsEnabled = false;
    }

    private void ShowManifestCheckBox_Changed(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ManifestPreviewBorder.Visibility = ShowManifestCheckBox.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void LegacyMetadataButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        OpenLegacyTransferRequested = true;
        LegacyMetadataRequested?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void SetTransferBusy(bool busy)
    {
        _transferBusy = busy;
        ActionTabControl.IsEnabled = !busy;
        LegacyMetadataButton.IsEnabled = !busy;
        DoneButton.IsEnabled = !busy;
    }

    private void SetExportStatus(string text, bool assertive) =>
        _exportStatusLiveRegion.Update(
            text,
            text,
            assertive
                ? AccessibilityLiveRegionSeverity.Assertive
                : AccessibilityLiveRegionSeverity.Polite);

    private void SetImportStatus(string text, bool assertive) =>
        _importStatusLiveRegion.Update(
            text,
            text,
            assertive
                ? AccessibilityLiveRegionSeverity.Assertive
                : AccessibilityLiveRegionSeverity.Polite);

    private IReadOnlyList<string> GetEffectiveKeyboardMacroIds()
    {
        var selectedIds = _macroRows
            .Where(row => row.IsSelected && row.HasKeyboardInput)
            .Select(row => row.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var macroRowsById = _macroRows.ToDictionary(
            row => row.Id,
            StringComparer.OrdinalIgnoreCase);
        foreach (var dependencyId in _templateRows
                     .Where(row => row.IsSelected)
                     .SelectMany(row => row.MacroDependencies))
        {
            if (macroRowsById.TryGetValue(dependencyId, out var macro) &&
                macro.HasKeyboardInput)
            {
                selectedIds.Add(macro.Id);
            }
        }
        return selectedIds.ToArray();
    }

    private IEnumerable<PortableSelectionRow> AllRows() =>
        _templateRows
            .Concat(_macroRows)
            .Concat(_destinationRows)
            .Concat(_presetRows);

    private static int SelectedCount(
        IEnumerable<PortableSelectionRow> rows) =>
        rows.Count(row => row.IsSelected);

    private static IReadOnlyCollection<string> SelectedIds(
        IEnumerable<PortableSelectionRow> rows) =>
        rows.Where(row => row.IsSelected)
            .Select(row => row.Id)
            .ToArray();

    private static string CategoryCount(
        string name,
        int selected,
        int total) =>
        $"{name} — {selected} selected of {total}";

    private static IReadOnlyList<string> GetMacroDependencies(
        SessionTemplate template)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        switch (template.MacroMode)
        {
            case SessionTemplateMacroMode.None:
                break;
            case SessionTemplateMacroMode.PerClient:
                foreach (var contentId in template.ClientSlots
                             .Select(slot => slot.PerClientMacroId)
                             .Where(contentId =>
                                 !string.IsNullOrWhiteSpace(contentId)))
                {
                    result.Add(contentId!);
                }
                break;
            case SessionTemplateMacroMode.Shared:
                if (!string.IsNullOrWhiteSpace(template.SharedMacroId))
                    result.Add(template.SharedMacroId);
                break;
            case SessionTemplateMacroMode.WholeLayout:
                if (!string.IsNullOrWhiteSpace(template.WholeLayoutMacroId))
                    result.Add(template.WholeLayoutMacroId);
                break;
            default:
                throw new InvalidDataException(
                    "The template macro mode is unsupported.");
        }
        return result.ToArray();
    }

    private static bool TryGetPublicPlaceId(
        string destination,
        out long placeId)
    {
        placeId = 0;
        if (!DestinationParser.TryParse(destination, out var target, out _) ||
            target is null ||
            target.IsPrivateServer ||
            target.PlaceId <= 0)
        {
            return false;
        }
        placeId = target.PlaceId;
        return true;
    }

    private static string DescribeLayoutMode(
        SessionTemplateLayoutMode mode) => mode switch
        {
            SessionTemplateLayoutMode.Cascade => "Clickable cascade",
            SessionTemplateLayoutMode.Saved => "Saved normalized positions",
            _ => "Unknown layout"
        };

    private static string DescribeMacroMode(
        SessionTemplateMacroMode mode) => mode switch
        {
            SessionTemplateMacroMode.None => "No macro",
            SessionTemplateMacroMode.PerClient => "Per-client macros",
            SessionTemplateMacroMode.Shared => "Shared client macro",
            SessionTemplateMacroMode.WholeLayout => "Whole-layout macro",
            _ => "Unknown macro mode"
        };

    private static string DisplayName(string? name, string fallback) =>
        string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();

    private static string FormatDuration(long milliseconds)
    {
        var duration = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L)
            return $"{bytes / (1024d * 1024d):0.##} MiB";
        if (bytes >= 1024L)
            return $"{bytes / 1024d:0.##} KiB";
        return $"{bytes} bytes";
    }

    private string Localize(string key, params object[] arguments)
    {
        var format = TryFindResource(key) as string ?? key;
        return arguments.Length == 0
            ? format
            : string.Format(CultureInfo.CurrentCulture, format, arguments);
    }

    private static bool IsExpectedDisplayFailure(Exception exception) =>
        exception is InvalidOperationException or InvalidDataException or
            ArgumentException or NotSupportedException or OverflowException;

    private static bool IsExpectedPackageFailure(Exception exception) =>
        exception is IOException or InvalidDataException or
            UnauthorizedAccessException or SecurityException or
            ArgumentException or NotSupportedException or OverflowException;

    private sealed record MacroInspection(
        bool IsAvailable,
        bool HasKeyboardInput);

    private enum PortableSelectionKind
    {
        Template,
        Macro,
        Destination,
        Preset
    }

    private sealed class PortableSelectionRow : INotifyPropertyChanged
    {
        private readonly Action<PortableSelectionRow> _selectionChanged;
        private bool _isSelected;

        internal PortableSelectionRow(
            PortableSelectionKind kind,
            string id,
            string displayName,
            string detail,
            bool isSelectable,
            bool hasKeyboardInput,
            IReadOnlyList<string> macroDependencies,
            Action<PortableSelectionRow> selectionChanged)
        {
            Kind = kind;
            Id = id;
            DisplayName = displayName;
            Detail = detail;
            IsSelectable = isSelectable;
            HasKeyboardInput = hasKeyboardInput;
            MacroDependencies = macroDependencies;
            _selectionChanged = selectionChanged;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        internal PortableSelectionKind Kind { get; }

        internal string Id { get; }

        public string DisplayName { get; }

        public string Detail { get; }

        public string AutomationName => $"{DisplayName}. {Detail}";

        public bool IsSelectable { get; }

        internal bool HasKeyboardInput { get; }

        internal IReadOnlyList<string> MacroDependencies { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                    return;
                _isSelected = value;
                OnPropertyChanged();
                _selectionChanged(this);
            }
        }

        private void OnPropertyChanged(
            [CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(propertyName));
    }
}
