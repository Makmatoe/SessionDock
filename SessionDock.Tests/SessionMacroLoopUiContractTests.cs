using System.Xml.Linq;

namespace SessionDock.Tests;

public sealed class SessionMacroLoopUiContractTests
{
    [Fact]
    public void TemplateEditor_UsesContinuousLoopContractWithoutRepeatToggle()
    {
        var xaml = File.ReadAllText(RepoFile(
            "SessionDock",
            "TemplateEditorDialog.xaml"));
        var source = File.ReadAllText(RepoFile(
            "SessionDock",
            "TemplateEditorDialog.xaml.cs"));
        var model = File.ReadAllText(RepoFile(
            "SessionDock",
            "Models",
            "SessionTemplate.cs"));

        Assert.DoesNotContain("RepeatWholeLayoutMacroCheckBox", xaml);
        Assert.DoesNotContain(
            "Template.Editor.RepeatWholeLayoutMacro",
            xaml);
        Assert.Contains(
            "_existingTemplate?.RepeatWholeLayoutMacro == true",
            source);
        Assert.Contains("public bool RepeatWholeLayoutMacro", model);
    }

    [Fact]
    public void ControllerClose_CancelsActiveLoopBeforeHiding()
    {
        var source = File.ReadAllText(RepoFile(
            "SessionDock",
            "SessionMacroControllerWindow.xaml.cs"));
        var start = source.IndexOf(
            "private void Window_Closing",
            StringComparison.Ordinal);
        var end = source.IndexOf(
            "private string Localize",
            start,
            StringComparison.Ordinal);
        var closing = source[start..end];

        var cancel = closing.IndexOf(
            "_playbackCancellation?.Cancel();",
            StringComparison.Ordinal);
        var hide = closing.IndexOf("Hide();", StringComparison.Ordinal);
        Assert.True(cancel >= 0);
        Assert.True(hide > cancel);
    }

    [Fact]
    public void EveryLocale_DescribesContinuousPlaybackAndNormalStop()
    {
        var localeFiles = new[]
        {
            "Strings.en-US.xaml",
            "Strings.nl-NL.xaml",
            "Strings.de-DE.xaml",
            "Strings.fr-FR.xaml",
            "Strings.es-ES.xaml"
        };

        foreach (var localeFile in localeFiles)
        {
            var document = XDocument.Load(RepoFile(
                "SessionDock",
                "Localization",
                localeFile));
            XNamespace x =
                "http://schemas.microsoft.com/winfx/2006/xaml";
            var values = document.Root!
                .Elements()
                .ToDictionary(
                    element => element.Attribute(x + "Key")?.Value ?? "",
                    element => element.Value,
                    StringComparer.Ordinal);

            Assert.True(values.ContainsKey("Macro.ControllerStoppedDetail"));
            Assert.False(values.ContainsKey(
                "Template.Editor.RepeatWholeLayoutMacro"));
            Assert.False(values.ContainsKey(
                "Template.Editor.RepeatWholeLayoutMacroHelp"));
            Assert.False(string.IsNullOrWhiteSpace(
                values["Template.Editor.MacroModeHelp"]));
            Assert.False(string.IsNullOrWhiteSpace(
                values["Macro.ControllerPlayHelp"]));
        }

        var english = File.ReadAllText(RepoFile(
            "SessionDock",
            "Localization",
            "Strings.en-US.xaml"));
        Assert.Contains(
            "every assignment loops continuously until Stop",
            english,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "start the continuous loop again",
            english,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string RepoFile(params string[] components)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null &&
               !File.Exists(Path.Combine(current.FullName, "SessionDock.slnx")))
        {
            current = current.Parent;
        }

        if (current is null)
            throw new DirectoryNotFoundException("Repository root not found.");
        return Path.Combine([current.FullName, .. components]);
    }
}
