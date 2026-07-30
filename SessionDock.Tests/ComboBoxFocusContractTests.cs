using System.Xml.Linq;

namespace SessionDock.Tests;

public sealed class ComboBoxFocusContractTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void ProductionComboBoxes_InheritThemeAwareKeyboardFocusVisual()
    {
        var applicationDirectory = Path.Combine(
            FindRepositoryRoot(),
            "SessionDock");
        var applicationResources = XDocument.Load(Path.Combine(
            applicationDirectory,
            "App.xaml"));
        var comboBoxStyle = applicationResources
            .Descendants(Presentation + "Style")
            .Single(element =>
                (string?)element.Attribute("TargetType") == "ComboBox" &&
                element.Attribute(Xaml + "Key") is null);
        var focusVisualSetter = comboBoxStyle
            .Elements(Presentation + "Setter")
            .Single(element =>
                (string?)element.Attribute("Property") == "FocusVisualStyle");

        Assert.Equal(
            "{StaticResource KeyboardFocusVisual}",
            (string?)focusVisualSetter.Attribute("Value"));

        var comboBoxTemplate = comboBoxStyle
            .Elements(Presentation + "Setter")
            .Single(element =>
                (string?)element.Attribute("Property") == "Template")
            .Descendants(Presentation + "ControlTemplate")
            .Single(element =>
                (string?)element.Attribute("TargetType") == "ComboBox");
        var dropDownToggle = comboBoxTemplate
            .Descendants(Presentation + "ToggleButton")
            .Single(element =>
                (string?)element.Attribute(Xaml + "Name") ==
                "DropDownToggle");

        Assert.Equal("False", (string?)dropDownToggle.Attribute("Focusable"));

        var keyboardFocusStyle = applicationResources
            .Descendants(Presentation + "Style")
            .Single(element =>
                (string?)element.Attribute(Xaml + "Key") ==
                "KeyboardFocusVisual");
        var focusBorder = Assert.Single(
            keyboardFocusStyle.Descendants(Presentation + "Border"));

        Assert.Equal(
            "{DynamicResource FocusBrush}",
            (string?)focusBorder.Attribute("BorderBrush"));
        var borderThickness =
            (string?)focusBorder.Attribute("BorderThickness") ?? string.Empty;
        Assert.True(
            borderThickness
                .Split(',', StringSplitOptions.TrimEntries)
                .Any(component =>
                    double.TryParse(
                        component,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var value) &&
                    value > 0),
            "The keyboard focus border must have a visible thickness.");

        var comboBoxes = Directory
            .EnumerateFiles(
                applicationDirectory,
                "*.xaml",
                SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).Equals(
                "App.xaml",
                StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => XDocument.Load(path)
                .Descendants(Presentation + "ComboBox")
                .Select(element => new
                {
                    Path = path,
                    Element = element
                }))
            .ToArray();

        Assert.NotEmpty(comboBoxes);
        var overrides = comboBoxes
            .Where(item =>
                item.Element.Attribute("Style") is not null ||
                item.Element.Attribute("FocusVisualStyle") is not null ||
                item.Element.Elements(Presentation + "ComboBox.Style").Any() ||
                item.Element.Elements(
                    Presentation + "ComboBox.FocusVisualStyle").Any())
            .Select(item =>
                $"{Path.GetRelativePath(applicationDirectory, item.Path)}:" +
                $"{(string?)item.Element.Attribute(Xaml + "Name") ?? "<unnamed>"}")
            .ToArray();

        Assert.True(
            overrides.Length == 0,
            "Production ComboBoxes must inherit the shared focus visual. " +
            "Explicit styles require an equivalent focus contract:" +
            Environment.NewLine + string.Join(Environment.NewLine, overrides));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "SessionDock.slnx")))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
