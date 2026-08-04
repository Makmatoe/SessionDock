using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class ThemeResourceTests
{
    private static readonly string[] ThemeFileNames =
    [
        "DarkTheme.xaml",
        "LightTheme.xaml",
        "HighContrastTheme.xaml"
    ];

    private static readonly string[] RequiredSemanticBrushKeys =
    [
        "BackgroundBrush",
        "SidebarBrush",
        "PanelBrush",
        "RaisedPanelBrush",
        "StrokeBrush",
        "TextBrush",
        "MutedBrush",
        "SubtleBrush",
        "AccentBrush",
        "AccentHoverBrush",
        "AccentSurfaceBrush",
        "FocusBrush",
        "OnAccentTextBrush",
        "CaptionButtonHoverBrush",
        "CaptionButtonPressedBrush",
        "CaptionButtonHoverTextBrush",
        "CaptionCloseHoverBrush",
        "CaptionClosePressedBrush",
        "CaptionCloseHoverTextBrush",
        "FieldBackgroundBrush",
        "FieldBorderBrush",
        "FieldHoverBorderBrush",
        "FieldCaretBrush",
        "SelectionBrush",
        "ListSurfaceBrush",
        "ListBorderBrush",
        "ListItemHoverBrush",
        "ListItemSelectedBrush",
        "ListItemSelectedBorderBrush",
        "ScrollTrackBrush",
        "ScrollThumbBrush",
        "ScrollThumbHoverBrush",
        "MenuBackgroundBrush",
        "MenuBorderBrush",
        "MenuHoverBrush",
        "MenuSelectedBrush",
        "MenuSelectedTextBrush",
        "ControlBackgroundBrush",
        "ControlForegroundBrush",
        "ControlBorderBrush",
        "ControlHoverBorderBrush",
        "CardBackgroundBrush",
        "CardBorderBrush",
        "CardSelectedBackgroundBrush",
        "CardSelectedBorderBrush",
        "InsetBackgroundBrush",
        "InsetBorderBrush",
        "WorkspaceBackgroundBrush",
        "WorkspaceBorderBrush",
        "UtilityBackgroundBrush",
        "UtilityForegroundBrush",
        "RailBackgroundBrush",
        "RailForegroundBrush",
        "RailDividerBrush",
        "BadgeBackgroundBrush",
        "BadgeForegroundBrush",
        "InfoForegroundBrush",
        "InfoSurfaceBrush",
        "InfoBorderBrush",
        "SuccessForegroundBrush",
        "SuccessSurfaceBrush",
        "SuccessBorderBrush",
        "WarningForegroundBrush",
        "WarningSurfaceBrush",
        "WarningBorderBrush",
        "ErrorForegroundBrush",
        "ErrorSurfaceBrush",
        "ErrorBorderBrush",
        "VioletForegroundBrush",
        "VioletSurfaceBrush",
        "VioletBorderBrush",
        "AccountStripBackgroundBrush",
        "AccountStripBorderBrush",
        "OnAccountColorBrush",
        "AccountNeutralBrush"
    ];

    private static readonly HashSet<string> AccountPalette =
        SettingsService.AccountColors.ToHashSet(
            StringComparer.OrdinalIgnoreCase);

    private static readonly Regex HexColorPattern = new(
        "(?<![0-9A-Fa-f])#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6}|" +
        "[0-9A-Fa-f]{4}|[0-9A-Fa-f]{3})(?![0-9A-Fa-f])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DynamicResourcePattern = new(
        @"\{DynamicResource\s+(?<key>[A-Za-z][A-Za-z0-9]*)\}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CSharpBrushKeyPattern = new(
        "\"(?<key>[A-Za-z][A-Za-z0-9]*Brush)\"",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    [Fact]
    public void ThemeDictionaries_DefineTheSameRequiredSemanticBrushes()
    {
        var themeDirectory = Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "Themes");
        var keySets = ThemeFileNames
            .Select(fileName => ReadBrushKeys(Path.Combine(
                themeDirectory,
                fileName)))
            .ToArray();

        foreach (var keys in keySets)
        {
            Assert.Empty(RequiredSemanticBrushKeys.Except(
                keys,
                StringComparer.Ordinal));
        }

        Assert.Equal(
            keySets[0].Order(StringComparer.Ordinal),
            keySets[1].Order(StringComparer.Ordinal));
        Assert.Equal(
            keySets[0].Order(StringComparer.Ordinal),
            keySets[2].Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ProductionXaml_UsesSemanticThemeResourcesInsteadOfInlineColors()
    {
        var applicationDirectory = Path.Combine(
            FindRepositoryRoot(),
            "SessionDock");
        var violations = new List<string>();
        foreach (var path in Directory.EnumerateFiles(
                     applicationDirectory,
                     "*.xaml",
                     SearchOption.AllDirectories))
        {
            if (ThemeFileNames.Contains(
                    Path.GetFileName(path),
                    StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (var attribute in document.Descendants()
                         .Attributes())
            {
                foreach (Match match in HexColorPattern.Matches(
                             attribute.Value))
                {
                    if (IsAccountPaletteAttribute(path, attribute, match.Value))
                        continue;

                    var line = ((IXmlLineInfo)attribute).LineNumber;
                    violations.Add(
                        $"{Path.GetRelativePath(applicationDirectory, path)}:{line} " +
                        $"{attribute.Name.LocalName}=\"{attribute.Value}\"");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Theme-dependent hexadecimal colors must live in a theme " +
            "dictionary and be referenced by semantic brush key." +
            Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void ProductionXaml_DynamicThemeResourcesExistInEveryPalette()
    {
        var applicationDirectory = Path.Combine(
            FindRepositoryRoot(),
            "SessionDock");
        var referencedKeys = Directory.EnumerateFiles(
                applicationDirectory,
                "*.xaml",
                SearchOption.AllDirectories)
            .Where(path => !ThemeFileNames.Contains(
                Path.GetFileName(path),
                StringComparer.OrdinalIgnoreCase))
            .SelectMany(path => DynamicResourcePattern.Matches(
                    File.ReadAllText(path))
                .Select(match => match.Groups["key"].Value))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(
                     applicationDirectory,
                     "*.cs",
                     SearchOption.AllDirectories)
                     .Where(path => !HasPathSegment(path, "bin") &&
                                    !HasPathSegment(path, "obj") &&
                                    !HasPathSegment(path, "tools")))
        {
            referencedKeys.UnionWith(CSharpBrushKeyPattern.Matches(
                    File.ReadAllText(path))
                .Select(match => match.Groups["key"].Value));
        }

        foreach (var themeFileName in ThemeFileNames)
        {
            var keys = ReadBrushKeys(Path.Combine(
                applicationDirectory,
                "Themes",
                themeFileName));
            Assert.Empty(referencedKeys.Except(keys, StringComparer.Ordinal));
        }
    }

    [Theory]
    [InlineData("DarkTheme.xaml")]
    [InlineData("LightTheme.xaml")]
    public void StandardPalettes_TextAndStatePairsMeetNormalTextContrast(
        string themeFileName)
    {
        var colors = ReadLiteralBrushColors(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "Themes",
            themeFileName));
        var pairs = new (string Foreground, string Background)[]
        {
            ("TextBrush", "BackgroundBrush"),
            ("TextBrush", "PanelBrush"),
            ("MutedBrush", "BackgroundBrush"),
            ("SubtleBrush", "BackgroundBrush"),
            ("TextBrush", "ListSurfaceBrush"),
            ("MutedBrush", "ListSurfaceBrush"),
            ("TextBrush", "ListItemHoverBrush"),
            ("MutedBrush", "ListItemHoverBrush"),
            ("TextBrush", "ListItemSelectedBrush"),
            ("MutedBrush", "ListItemSelectedBrush"),
            ("ControlTextBrush", "ControlSurfaceBrush"),
            ("MenuSelectedTextBrush", "MenuSelectedBrush"),
            ("SelectedControlTextBrush", "SelectedControlSurfaceBrush"),
            ("BadgeTextBrush", "BadgeSurfaceBrush"),
            ("InfoTextBrush", "InfoSurfaceBrush"),
            ("SuccessTextBrush", "SuccessSurfaceBrush"),
            ("WarningTextBrush", "WarningSurfaceBrush"),
            ("WarningMutedTextBrush", "WarningSurfaceBrush"),
            ("ErrorTextBrush", "ErrorSurfaceBrush"),
            ("ErrorMutedTextBrush", "ErrorSurfaceBrush"),
            ("VioletTextBrush", "VioletSurfaceBrush"),
            ("CaptionButtonHoverTextBrush", "CaptionButtonHoverBrush"),
            ("CaptionButtonHoverTextBrush", "CaptionButtonPressedBrush"),
            ("CaptionCloseHoverTextBrush", "CaptionCloseHoverBrush"),
            ("CaptionCloseHoverTextBrush", "CaptionClosePressedBrush")
        };

        foreach (var (foregroundKey, backgroundKey) in pairs)
        {
            var contrast = CalculateContrast(
                colors[foregroundKey],
                colors[backgroundKey]);
            Assert.True(
                contrast >= 4.5,
                $"{themeFileName} {foregroundKey} on {backgroundKey} " +
                $"has only {contrast:0.00}:1 contrast.");
        }
    }

    [Theory]
    [InlineData("DarkTheme.xaml")]
    [InlineData("LightTheme.xaml")]
    public void StandardPalettes_InteractiveBoundariesMeetNonTextContrast(
        string themeFileName)
    {
        var colors = ReadLiteralBrushColors(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "Themes",
            themeFileName));
        var pairs = new (string Foreground, string Background)[]
        {
            ("FocusBrush", "BackgroundBrush"),
            ("FieldBorderBrush", "FieldBackgroundBrush"),
            ("ListBorderBrush", "ListSurfaceBrush"),
            ("ListItemSelectedBorderBrush", "ListItemSelectedBrush"),
            ("ScrollThumbBrush", "ScrollTrackBrush"),
            ("CardSelectedBorderBrush", "CardSelectedBackgroundBrush")
        };

        foreach (var (foregroundKey, backgroundKey) in pairs)
        {
            var contrast = CalculateContrast(
                colors[foregroundKey],
                colors[backgroundKey]);
            Assert.True(
                contrast >= 3,
                $"{themeFileName} {foregroundKey} on {backgroundKey} " +
                $"has only {contrast:0.00}:1 contrast.");
        }
    }

    [Fact]
    public void HighContrastCaptionStates_UseMatchingSystemHighlightColors()
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "Themes",
            "HighContrastTheme.xaml");
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var colors = XDocument.Load(path).Root!
            .Elements()
            .Where(element => element.Name.LocalName == "SolidColorBrush")
            .ToDictionary(
                element => (string)element.Attribute(xaml + "Key")!,
                element => (string)element.Attribute("Color")!,
                StringComparer.Ordinal);

        Assert.Contains(
            "SystemColors.HighlightColor",
            colors["CaptionButtonHoverBrush"],
            StringComparison.Ordinal);
        Assert.Contains(
            "SystemColors.HighlightColor",
            colors["CaptionButtonPressedBrush"],
            StringComparison.Ordinal);
        Assert.Contains(
            "SystemColors.HighlightTextColor",
            colors["CaptionButtonHoverTextBrush"],
            StringComparison.Ordinal);
    }

    [Fact]
    public void HighContrastListStates_PreserveWindowTextPairingAndHighlightOutline()
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "Themes",
            "HighContrastTheme.xaml");
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var colors = XDocument.Load(path).Root!
            .Elements()
            .Where(element => element.Name.LocalName == "SolidColorBrush")
            .ToDictionary(
                element => (string)element.Attribute(xaml + "Key")!,
                element => (string)element.Attribute("Color")!,
                StringComparer.Ordinal);

        foreach (var surfaceKey in new[]
                 {
                     "ListSurfaceBrush",
                     "ListItemHoverBrush",
                     "ListItemSelectedBrush"
                 })
        {
            Assert.Contains(
                "SystemColors.WindowColor",
                colors[surfaceKey],
                StringComparison.Ordinal);
        }

        Assert.Contains(
            "SystemColors.WindowTextColor",
            colors["ListBorderBrush"],
            StringComparison.Ordinal);
        Assert.Contains(
            "SystemColors.HighlightColor",
            colors["ListItemSelectedBorderBrush"],
            StringComparison.Ordinal);
    }

    [Fact]
    public void AccountColorForegroundHelper_MeetsNormalTextContrast()
    {
        foreach (var accountColor in SettingsService.AccountColors)
        {
            var background = (Color)ColorConverter.ConvertFromString(
                accountColor);
            var foreground = MainWindow.GetContrastingAccountForeground(
                background);
            var contrast = CalculateContrast(
                (foreground.R, foreground.G, foreground.B),
                (background.R, background.G, background.B));
            Assert.True(
                contrast >= 4.5,
                $"The adaptive account foreground has only {contrast:0.00}:1 " +
                $"contrast on account color {accountColor}; " +
                "normal account initials require at least 4.5:1.");
        }
    }

    [Theory]
    [InlineData("DarkTheme.xaml")]
    [InlineData("LightTheme.xaml")]
    [InlineData("HighContrastTheme.xaml")]
    public void AccountSwatchForeground_MeetsNormalTextContrast(
        string themeFileName)
    {
        var colors = ReadLiteralBrushColors(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "Themes",
            themeFileName));
        var foreground = colors["OnAccountColorBrush"];
        foreach (var accountColor in SettingsService.AccountColors)
        {
            var backgroundColor = (Color)ColorConverter.ConvertFromString(
                accountColor);
            var contrast = CalculateContrast(
                foreground,
                (backgroundColor.R, backgroundColor.G, backgroundColor.B));
            Assert.True(
                contrast >= 4.5,
                $"{themeFileName} account swatch foreground has only " +
                $"{contrast:0.00}:1 contrast on {accountColor}.");
        }
    }

    private static HashSet<string> ReadBrushKeys(string path)
    {
        Assert.True(File.Exists(path), $"Theme dictionary is missing: {path}");
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(path);
        return document.Root!
            .Elements()
            .Where(element => element.Name.LocalName.EndsWith(
                "Brush",
                StringComparison.Ordinal))
            .Select(element => (string?)element.Attribute(xaml + "Key"))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static Dictionary<string, (byte Red, byte Green, byte Blue)>
        ReadLiteralBrushColors(string path)
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(path);
        return document.Root!
            .Elements()
            .Select(element => new
            {
                Key = (string?)element.Attribute(xaml + "Key"),
                Color = (string?)element.Attribute("Color")
            })
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.Key) &&
                item.Color?.Length == 7 &&
                item.Color[0] == '#')
            .ToDictionary(
                item => item.Key!,
                item =>
                {
                    var color = (Color)ColorConverter.ConvertFromString(
                        item.Color!);
                    return (color.R, color.G, color.B);
                },
                StringComparer.Ordinal);
    }

    private static double CalculateContrast(
        (byte Red, byte Green, byte Blue) first,
        (byte Red, byte Green, byte Blue) second)
    {
        var firstLuminance = CalculateRelativeLuminance(first);
        var secondLuminance = CalculateRelativeLuminance(second);
        var lighter = Math.Max(firstLuminance, secondLuminance);
        var darker = Math.Min(firstLuminance, secondLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double CalculateRelativeLuminance(
        (byte Red, byte Green, byte Blue) color) =>
        0.2126 * Linearize(color.Red) +
        0.7152 * Linearize(color.Green) +
        0.0722 * Linearize(color.Blue);

    private static double Linearize(byte component)
    {
        var normalized = component / 255d;
        return normalized <= 0.04045
            ? normalized / 12.92
            : Math.Pow((normalized + 0.055) / 1.055, 2.4);
    }

    private static bool IsAccountPaletteAttribute(
        string path,
        XAttribute attribute,
        string color)
    {
        if (!Path.GetFileName(path).Equals(
                "AccountAppearanceDialog.xaml",
                StringComparison.OrdinalIgnoreCase) ||
            !AccountPalette.Contains(color) ||
            attribute.Parent?.Name.LocalName != "Button")
        {
            return false;
        }

        return attribute.Name.LocalName is "Tag" or "Background";
    }

    private static bool HasPathSegment(string path, string segment) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Contains(segment, StringComparer.OrdinalIgnoreCase);

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
            "The SessionDock repository root could not be located for source validation.");
    }
}
