using System.Xml.Linq;

namespace SessionDock.Tests;

public sealed class MetadataTransferDialogThemeTests
{
    [Fact]
    public void MetadataTabs_UseApplicationThemeResources()
    {
        var document = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "MetadataTransferDialog.xaml"));
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml =
            "http://schemas.microsoft.com/winfx/2006/xaml";

        var controlStyle = FindStyle(
            document,
            presentation,
            xaml,
            "MetadataTabControl");
        AssertSetter(
            controlStyle,
            presentation,
            "Background",
            "{DynamicResource PanelBrush}");
        AssertSetter(
            controlStyle,
            presentation,
            "Foreground",
            "{DynamicResource TextBrush}");
        AssertSetter(
            controlStyle,
            presentation,
            "BorderBrush",
            "{DynamicResource StrokeBrush}");

        var itemStyle = FindStyle(
            document,
            presentation,
            xaml,
            "MetadataTabItem");
        AssertSetter(
            itemStyle,
            presentation,
            "Background",
            "{DynamicResource ControlSurfaceBrush}");
        AssertSetter(
            itemStyle,
            presentation,
            "Foreground",
            "{DynamicResource ControlTextBrush}");

        var tabControl = document.Descendants(presentation + "TabControl")
            .Single();
        Assert.Equal(
            "{StaticResource MetadataTabControl}",
            (string?)tabControl.Attribute("Style"));
    }

    private static XElement FindStyle(
        XDocument document,
        XNamespace presentation,
        XNamespace xaml,
        string key) =>
        document.Descendants(presentation + "Style")
            .Single(element =>
                (string?)element.Attribute(xaml + "Key") == key);

    private static void AssertSetter(
        XElement style,
        XNamespace presentation,
        string property,
        string value)
    {
        Assert.Contains(
            style.Elements(presentation + "Setter"),
            setter =>
                (string?)setter.Attribute("Property") == property &&
                (string?)setter.Attribute("Value") == value);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "SessionDock.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException(
            "Could not locate the SessionDock repository root.");
    }
}
