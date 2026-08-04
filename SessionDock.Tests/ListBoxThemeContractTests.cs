using System.Xml.Linq;

namespace SessionDock.Tests;

public sealed class ListBoxThemeContractTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void SharedListStyles_OwnThemeSurfaceSelectionAndOverflow()
    {
        var document = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "App.xaml"));
        var listStyle = FindImplicitStyle(document, "ListBox");
        AssertSetter(
            listStyle,
            "Background",
            "{DynamicResource ListSurfaceBrush}");
        AssertSetter(
            listStyle,
            "BorderBrush",
            "{DynamicResource ListBorderBrush}");
        AssertSetter(
            listStyle,
            "Foreground",
            "{DynamicResource TextBrush}");
        AssertSetter(
            listStyle,
            "ScrollViewer.HorizontalScrollBarVisibility",
            "Disabled");

        var itemStyle = FindImplicitStyle(document, "ListBoxItem");
        AssertSetter(
            itemStyle,
            "Foreground",
            "{DynamicResource TextBrush}");
        AssertSetter(
            itemStyle,
            "FocusVisualStyle",
            "{StaticResource KeyboardFocusVisual}");

        var template = itemStyle
            .Descendants(Presentation + "ControlTemplate")
            .Single(element =>
                (string?)element.Attribute("TargetType") == "ListBoxItem");
        AssertTriggerSetter(
            template,
            "IsMouseOver",
            "Background",
            "{DynamicResource ListItemHoverBrush}");
        AssertTriggerSetter(
            template,
            "IsMouseOver",
            "BorderBrush",
            "{DynamicResource ListItemSelectedBorderBrush}");
        AssertTriggerSetter(
            template,
            "IsSelected",
            "Background",
            "{DynamicResource ListItemSelectedBrush}");
        AssertTriggerSetter(
            template,
            "IsSelected",
            "BorderBrush",
            "{DynamicResource ListItemSelectedBorderBrush}");
    }

    private static XElement FindImplicitStyle(
        XDocument document,
        string targetType) =>
        document.Descendants(Presentation + "Style")
            .Single(element =>
                (string?)element.Attribute("TargetType") == targetType &&
                element.Attribute(Xaml + "Key") is null);

    private static void AssertSetter(
        XElement style,
        string property,
        string value)
    {
        Assert.Contains(
            style.Elements(Presentation + "Setter"),
            setter =>
                (string?)setter.Attribute("Property") == property &&
                (string?)setter.Attribute("Value") == value);
    }

    private static void AssertTriggerSetter(
        XElement template,
        string triggerProperty,
        string setterProperty,
        string value)
    {
        var trigger = template
            .Descendants(Presentation + "Trigger")
            .Single(element =>
                (string?)element.Attribute("Property") == triggerProperty &&
                (string?)element.Attribute("Value") == "True");
        Assert.Contains(
            trigger.Elements(Presentation + "Setter"),
            setter =>
                (string?)setter.Attribute("Property") == setterProperty &&
                (string?)setter.Attribute("Value") == value);
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
