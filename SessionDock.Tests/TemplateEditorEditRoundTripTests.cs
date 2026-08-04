using System.Text.RegularExpressions;
using System.Xml.Linq;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class TemplateEditorEditRoundTripTests
{
    [Fact]
    public void ExistingTemplate_SavePreservesTemplateAndPerAccountSlotIds()
    {
        var source = ReadProductionFile("TemplateEditorDialog.xaml.cs");
        var saveBlock = Slice(
            source,
            "private void SaveButton_Click",
            "private SessionTemplateMacroMode SelectedMacroMode");

        Assert.Matches(
            new Regex(
                @"Id\s*=\s*_existingTemplate\?\.Id\s*\?\?\s*" +
                @"Guid\.NewGuid\(\)\.ToString\(""N""\)",
                RegexOptions.CultureInvariant),
            saveBlock);
        Assert.Matches(
            new Regex(
                @"SlotId\s*=\s*_existingSlots\.GetValueOrDefault\(\s*" +
                @"client\.AccountKey\s*\)\?\.SlotId\s*\?\?\s*" +
                @"Guid\.NewGuid\(\)\.ToString\(""N""\)",
                RegexOptions.CultureInvariant),
            saveBlock);
        Assert.Contains(
            "LegacyPresetName = _existingTemplate?.LegacyPresetName",
            saveBlock,
            StringComparison.Ordinal);

        var settingsSource = ReadProductionFile(
            "SessionAutomationSettingsDialog.xaml.cs");
        var editBlock = Slice(
            settingsSource,
            "private void EditTemplateButton_Click",
            "private void DeleteTemplatesButton_Click");
        Assert.Contains(
            "var templateIndex = _workingCatalog.Templates.FindIndex",
            editBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "template.Id.Equals(",
            editBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "selectedRow.Id",
            editBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "_workingCatalog.Templates[templateIndex] = saved",
            editBlock,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StableIds_SurviveTheEditorsFinalPolicyRoundTrip()
    {
        var source = new SessionTemplate
        {
            Id = "stable-template-id",
            Name = "Edited template",
            MacroMode = SessionTemplateMacroMode.PerClient,
            ClientSlots =
            [
                new SessionTemplateClientSlot
                {
                    SlotId = "stable-slot-alpha",
                    AccountKey = "account-alpha",
                    Order = 1,
                    PerClientMacroId = "client-alpha"
                },
                new SessionTemplateClientSlot
                {
                    SlotId = "stable-slot-beta",
                    AccountKey = "account-beta",
                    Order = 0,
                    PerClientMacroId = "client-beta"
                }
            ]
        };

        var normalized = SessionTemplatePolicy.Normalize(
            new SessionTemplateCatalog { Templates = [source] });
        var saved = Assert.Single(normalized.Templates);

        Assert.Equal("stable-template-id", saved.Id);
        var slots = saved.ClientSlots.ToDictionary(
            slot => slot.AccountKey,
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal("stable-slot-alpha", slots["account-alpha"].SlotId);
        Assert.Equal("stable-slot-beta", slots["account-beta"].SlotId);
        Assert.Equal(
            "client-alpha",
            slots["account-alpha"].PerClientMacroId);
        Assert.Equal(
            "client-beta",
            slots["account-beta"].PerClientMacroId);
    }

    [Fact]
    public void MacroSelectors_FilterKindsAndMarkMissingAssignmentsUnavailable()
    {
        var source = ReadProductionFile("TemplateEditorDialog.xaml.cs");
        var constructor = Slice(
            source,
            "internal TemplateEditorDialog(",
            "internal SessionTemplate? SavedTemplate");
        Assert.Matches(
            new Regex(
                @"clientMacros\s*=.*?\.Where\(macro\s*=>\s*" +
                @"macro\.Kind\s*==\s*SessionMacroKind\.Client\)",
                RegexOptions.CultureInvariant | RegexOptions.Singleline),
            constructor);
        Assert.Matches(
            new Regex(
                @"wholeLayoutMacros\s*=.*?\.Where\(macro\s*=>\s*" +
                @"macro\.Kind\s*==\s*SessionMacroKind\.WholeLayout\)",
                RegexOptions.CultureInvariant | RegexOptions.Singleline),
            constructor);

        var resolver = Slice(
            source,
            "private MacroChoice ResolveChoice",
            "private static IReadOnlyList<TemplateEditorClient> NormalizeClients");
        Assert.Contains("IsAvailable: false", resolver, StringComparison.Ordinal);
        Assert.Contains("choices.Add(unavailable)", resolver, StringComparison.Ordinal);

        var saveBlock = Slice(
            source,
            "private void SaveButton_Click",
            "private MacroChoice ResolveChoice");
        Assert.Contains(
            "Template.Editor.ValidationUnavailableMacro",
            saveBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "MacroChoice { IsAvailable: true }",
            saveBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "SelectedMacro.IsAvailable",
            saveBlock,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Destinations_AreAlwaysEditableAndExistingSlotValueWins()
    {
        var source = ReadProductionFile("TemplateEditorDialog.xaml.cs");
        var constructor = Slice(
            source,
            "internal TemplateEditorDialog(",
            "internal SessionTemplate? SavedTemplate");
        Assert.Matches(
            new Regex(
                @"var\s+destination\s*=\s*slot\s+is\s+null\s*" +
                @"\?\s*client\.Destination\s*:\s*slot\.Destination",
                RegexOptions.CultureInvariant),
            constructor);
        Assert.Contains(
            "NamedDestinationPolicy.Normalize(",
            constructor,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateDestinationChoices(",
            constructor,
            StringComparison.Ordinal);

        var saveBlock = Slice(
            source,
            "private void SaveButton_Click",
            "private SessionTemplateMacroMode SelectedMacroMode");
        Assert.Contains(
            "NamedDestinationPolicy.TryNormalizeValue(",
            saveBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "Destination = _clientRows[order].Destination",
            saveBlock,
            StringComparison.Ordinal);

        var document = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "TemplateEditorDialog.xaml"));
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml =
            "http://schemas.microsoft.com/winfx/2006/xaml";
        var destinationList = document
            .Descendants(presentation + "ListBox")
            .Single(element =>
                (string?)element.Attribute(xaml + "Name") ==
                "ClientDestinationList");
        Assert.Null(destinationList.Attribute("Visibility"));
        Assert.Contains(
            destinationList.Descendants(presentation + "ComboBox"),
            combo => (string?)combo.Attribute("ItemsSource") ==
                "{Binding DestinationOptions}");
        Assert.Contains(
            destinationList.Descendants(presentation + "ComboBox"),
            combo =>
                (string?)combo.Attribute("AutomationProperties.Name") ==
                    "{Binding DestinationChoiceAutomationName}" &&
                (string?)combo.Attribute("AutomationProperties.HelpText") ==
                    "{Binding DestinationAutomationHelp}");
        Assert.Contains(
            destinationList.Descendants(presentation + "TextBox"),
            textBox => ((string?)textBox.Attribute("Text"))?.Contains(
                "Binding Destination",
                StringComparison.Ordinal) == true);
        Assert.Contains(
            destinationList.Descendants(presentation + "TextBox"),
            textBox =>
                (string?)textBox.Attribute("AutomationProperties.Name") ==
                    "{Binding DestinationValueAutomationName}" &&
                (string?)textBox.Attribute("AutomationProperties.HelpText") ==
                    "{Binding DestinationAutomationHelp}");
        Assert.Contains("SourceInitialized +=", source);
        Assert.Contains("WindowLayoutService.FitToWorkArea(this);", source);
        Assert.Contains("ShowClientDestinationValidation(row);", source);
        Assert.Contains("ClientDestinationList.ScrollIntoView(row);", source);
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        Assert.True(end > start, $"Missing source marker: {endMarker}");
        return source[start..end];
    }

    private static string ReadProductionFile(string fileName) =>
        File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            fileName));

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[]
                 {
                     Environment.CurrentDirectory,
                     AppContext.BaseDirectory
                 })
        {
            for (var directory = new DirectoryInfo(start);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(
                        directory.FullName,
                        "SessionDock.slnx")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException(
            "The SessionDock repository root could not be located.");
    }
}
