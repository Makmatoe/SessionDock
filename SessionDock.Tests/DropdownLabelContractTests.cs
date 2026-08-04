using System.Text;
using System.Xml.Linq;

namespace SessionDock.Tests;

public sealed class DropdownLabelContractTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void GlobalComboBoxTemplate_UsesTheSafeSelectedLabelResolver()
    {
        var applicationDirectory = Path.Combine(
            FindRepositoryRoot(),
            "SessionDock");
        var document = XDocument.Load(Path.Combine(
            applicationDirectory,
            "App.xaml"));
        var resolver = document.Descendants()
            .Single(element =>
                element.Name.LocalName == "DropdownSelectedLabelResolver");
        Assert.Equal(
            "DropdownSelectedLabelResolver",
            (string?)resolver.Attribute(Xaml + "Key"));

        var comboBoxStyle = document
            .Descendants(Presentation + "Style")
            .Single(element =>
                (string?)element.Attribute("TargetType") == "ComboBox" &&
                element.Attribute(Xaml + "Key") is null);
        var multiBinding = comboBoxStyle
            .Descendants(Presentation + "MultiBinding")
            .Single(element =>
                (string?)element.Attribute("Converter") ==
                "{StaticResource DropdownSelectedLabelResolver}");
        var paths = multiBinding
            .Elements(Presentation + "Binding")
            .Select(element => (string?)element.Attribute("Path"))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            new[]
            {
                "(local:DropdownLabel.Placeholder)",
                "DisplayMemberPath",
                "SelectedItem",
                "SelectionBoxItem"
            },
            paths.Order(StringComparer.Ordinal));
        var placeholderSetter = comboBoxStyle
            .Elements(Presentation + "Setter")
            .Single(element =>
                (string?)element.Attribute("Property") ==
                "local:DropdownLabel.Placeholder");
        Assert.Equal(
            "{DynamicResource Common.SelectOption}",
            (string?)placeholderSetter.Attribute("Value"));
    }

    [Fact]
    public void GlobalComboBoxTemplate_EditableModeUsesATwoWayTextPart()
    {
        var document = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "App.xaml"));
        var comboBoxStyle = document
            .Descendants(Presentation + "Style")
            .Single(element =>
                (string?)element.Attribute("TargetType") == "ComboBox" &&
                element.Attribute(Xaml + "Key") is null);
        var template = comboBoxStyle
            .Descendants(Presentation + "ControlTemplate")
            .Single(element =>
                (string?)element.Attribute("TargetType") == "ComboBox");
        var selectedLabel = template
            .Descendants(Presentation + "TextBlock")
            .Single(element =>
                (string?)element.Attribute(Xaml + "Name") ==
                "SelectedItemLabel");
        var editableTextBox = template
            .Descendants(Presentation + "TextBox")
            .Single(element =>
                (string?)element.Attribute(Xaml + "Name") ==
                "PART_EditableTextBox");

        Assert.Equal("Collapsed", (string?)editableTextBox.Attribute(
            "Visibility"));
        Assert.Null(selectedLabel.Attribute("Visibility"));
        var textBinding = (string?)editableTextBox.Attribute("Text");
        Assert.NotNull(textBinding);
        Assert.Contains("Path=Text", textBinding!, StringComparison.Ordinal);
        Assert.Contains("Mode=TwoWay", textBinding!, StringComparison.Ordinal);
        Assert.Contains(
            "UpdateSourceTrigger=PropertyChanged",
            textBinding!,
            StringComparison.Ordinal);

        var editableTrigger = template
            .Descendants(Presentation + "Trigger")
            .Single(element =>
                (string?)element.Attribute("Property") == "IsEditable" &&
                (string?)element.Attribute("Value") == "True");
        string SetterValue(string targetName) =>
            (string?)editableTrigger
                .Elements(Presentation + "Setter")
                .Single(element =>
                    (string?)element.Attribute("TargetName") == targetName)
                .Attribute("Value") ?? string.Empty;

        Assert.Equal(
            "Collapsed",
            SetterValue((string?)selectedLabel.Attribute(Xaml + "Name") ??
                string.Empty));
        Assert.Equal(
            "Visible",
            SetterValue((string?)editableTextBox.Attribute(Xaml + "Name") ??
                string.Empty));
    }

    [Fact]
    public void ObjectBackedComboBoxes_DeclareAnExplicitDisplayMember()
    {
        var applicationDirectory = Path.Combine(
            FindRepositoryRoot(),
            "SessionDock");
        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["BatchLaunchDialog.xaml"] =
                ["PresetComboBox", "GroupComboBox"],
            ["ClientMacroAssignmentDialog.xaml"] = ["MacroComboBox"],
            ["ExternalRobloxLinkDialog.xaml"] = ["AccountComboBox"],
            ["HandleScopeIntegrationDialog.xaml"] =
            [
                "RuntimeSourceComboBox",
                "ApiContractComboBox",
                "StandaloneRuntimeVersionComboBox"
            ],
            ["MacroRecorderDialog.xaml"] = ["TargetComboBox"],
            ["RunTemplateDialog.xaml"] = ["TemplateComboBox"],
            ["SessionAutomationSettingsDialog.xaml"] =
                ["PreferredMonitorComboBox"],
            ["SessionMacroControllerWindow.xaml"] = ["SpeedComboBox"],
            ["TemplateEditorDialog.xaml"] =
            [
                "DelayComboBox",
                "MacroModeComboBox",
                "SharedMacroComboBox",
                "WholeLayoutMacroComboBox"
            ]
        };

        foreach (var (fileName, names) in expected)
        {
            var document = XDocument.Load(Path.Combine(
                applicationDirectory,
                fileName));
            foreach (var name in names)
            {
                var comboBox = document
                    .Descendants(Presentation + "ComboBox")
                    .Single(element =>
                        (string?)element.Attribute(Xaml + "Name") == name);
                Assert.False(string.IsNullOrWhiteSpace(
                    (string?)comboBox.Attribute("DisplayMemberPath")));
            }
        }

        var templateEditor = XDocument.Load(Path.Combine(
            applicationDirectory,
            "TemplateEditorDialog.xaml"));
        foreach (var itemsSource in new[]
                 {
                     "{Binding DestinationOptions}",
                     "{Binding MacroOptions}"
                 })
        {
            var perClientComboBox = templateEditor
                .Descendants(Presentation + "ComboBox")
                .Single(element =>
                    (string?)element.Attribute("ItemsSource") == itemsSource);
            Assert.Equal(
                "DisplayName",
                (string?)perClientComboBox.Attribute("DisplayMemberPath"));
        }
    }

    [Fact]
    public void LegacyTemplateLabel_UsesAnEncodingSafeEmDashEscape()
    {
        var source = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "SessionDock",
                "RunTemplateDialog.xaml.cs"),
            Encoding.UTF8);

        Assert.Contains(
            "{template.Name} \\u2014 {Localize",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain('—', source);
        Assert.DoesNotContain(
            "\u00e2\u20ac\u201d",
            source,
            StringComparison.Ordinal);
    }

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
