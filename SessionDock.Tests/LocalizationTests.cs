using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Xml.Linq;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

[Collection(typeof(TimingSensitiveTestCollection))]
public sealed class LocalizationTests : IDisposable
{
    private static readonly string[] SupportedCultureNames =
    [
        LocalizationPreference.English,
        LocalizationPreference.Dutch,
        LocalizationPreference.German,
        LocalizationPreference.French,
        LocalizationPreference.Spanish
    ];

    private static readonly string[] UserFacingAttributeNames =
    [
        "Title",
        "Text",
        "Content",
        "Header",
        "ToolTip",
        "AutomationProperties.Name",
        "AutomationProperties.HelpText",
        "AutomationProperties.ItemStatus"
    ];

    private static readonly string[] LanguageAutonymKeys =
    [
        "Language.English",
        "Language.Dutch",
        "Language.German",
        "Language.French",
        "Language.Spanish"
    ];

    private static readonly IReadOnlyDictionary<string, string[]>
        SessionAutomationEnglishEquivalenceAllowlist =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [LocalizationPreference.Dutch] =
                [
                    "AutomationSettings.Layout.Cascade",
                    "AutomationSettings.MonitorSummary",
                    "Macro.PlaybackBadge",
                    "Template.Editor.MacroPerClient"
                ],
                [LocalizationPreference.German] =
                [
                    "AutomationSettings.MonitorSummary",
                    "Destinations.Name"
                ],
                [LocalizationPreference.French] =
                [
                    "AutomationSettings.Layout.Cascade",
                    "AutomationSettings.MonitorSummary",
                    "Destinations.Title",
                    "Home.Destinations",
                    "Home.RecordMacro",
                    "Macro.PlaybackBadge"
                ],
                [LocalizationPreference.Spanish] =
                [
                    "AutomationSettings.MonitorSummary",
                    "Home.RecordMacro",
                    "Macro.PlaybackBadge"
                ]
            };

    private readonly string _storageDirectory = Path.Combine(
        Path.GetTempPath(),
        $"SessionDock-localization-tests-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(null, "system")]
    [InlineData("", "system")]
    [InlineData(" SYSTEM ", "system")]
    [InlineData("EN-us", "en-US")]
    [InlineData(" nl-nl ", "nl-NL")]
    [InlineData("DE-de", "de-DE")]
    [InlineData(" fr-fr ", "fr-FR")]
    [InlineData("ES-es", "es-ES")]
    [InlineData("pt-BR", "system")]
    [InlineData("nl", "system")]
    [InlineData("../../nl-NL.xaml", "system")]
    public void Normalize_AllowsOnlySupportedCanonicalPreferences(
        string? value,
        string expected)
    {
        Assert.Equal(expected, LocalizationPreference.Normalize(value));
    }

    [Theory]
    [InlineData("system", "nl-BE", "nl-NL")]
    [InlineData("system", "de-AT", "de-DE")]
    [InlineData("system", "fr-CA", "fr-FR")]
    [InlineData("system", "es-MX", "es-ES")]
    [InlineData("system", "pt-BR", "en-US")]
    [InlineData("en-US", "nl-NL", "en-US")]
    [InlineData("nl-NL", "en-US", "nl-NL")]
    [InlineData("de-DE", "fr-FR", "de-DE")]
    public void Resolve_UsesSupportedSystemLanguageOrEnglishFallback(
        string preference,
        string systemCulture,
        string expected)
    {
        Assert.Equal(
            expected,
            LocalizationPreference.Resolve(
                preference,
                CultureInfo.GetCultureInfo(systemCulture)));
    }

    [Fact]
    public void SupportedValues_AreCanonicalAndOrdered()
    {
        Assert.Equal(
            new[]
            {
                LocalizationPreference.System,
                LocalizationPreference.English,
                LocalizationPreference.Dutch,
                LocalizationPreference.German,
                LocalizationPreference.French,
                LocalizationPreference.Spanish
            },
            LocalizationPreference.SupportedValues);
    }

    [Fact]
    public void NewAndLegacySettings_DefaultToSystemLanguage()
    {
        Assert.Equal(
            LocalizationPreference.System,
            new AppSettings().Language);

        var service = new SettingsService(_storageDirectory);
        service.Save(CreateSettings(LocalizationPreference.Dutch));
        var path = Path.Combine(_storageDirectory, "settings.json");
        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.True(document.Remove(nameof(AppSettings.Language)));
        File.WriteAllText(path, document.ToJsonString());

        var loaded = new SettingsService(_storageDirectory).Load();

        Assert.Equal(LocalizationPreference.System, loaded.Language);
    }

    [Theory]
    [InlineData("nl-NL", "nl-NL")]
    [InlineData("EN-us", "en-US")]
    [InlineData("de-DE", "de-DE")]
    [InlineData("FR-fr", "fr-FR")]
    [InlineData(" es-es ", "es-ES")]
    [InlineData("unsupported", "system")]
    public void SettingsLoad_StrictlyNormalizesAndPersistsLanguage(
        string stored,
        string expected)
    {
        var service = new SettingsService(_storageDirectory);
        service.Save(CreateSettings(stored));

        var loaded = new SettingsService(_storageDirectory).Load();
        var reloaded = new SettingsService(_storageDirectory).Load();

        Assert.Equal(expected, loaded.Language);
        Assert.Equal(expected, reloaded.Language);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public async Task CommitLanguageChange_FailedWriteRollsBackWithoutApplying(
        Type failureType)
    {
        var settings = CreateSettings(LocalizationPreference.English);
        Exception failure = failureType == typeof(IOException)
            ? new IOException("disk unavailable")
            : new UnauthorizedAccessException("write denied");
        var coordinator = new SettingsMutationCoordinator(
            settings,
            new SerializedSettingsWriter(_ => throw failure));
        var visualLanguageApplied = false;

        var result = await coordinator.CommitAsync(
            () => settings.Language = LocalizationPreference.Dutch,
            () => visualLanguageApplied = true);

        Assert.False(result.Committed);
        Assert.IsType(failureType, result.Failure);
        Assert.Equal(LocalizationPreference.English, settings.Language);
        Assert.False(visualLanguageApplied);
    }

    [Fact]
    public void LocalizationDictionaries_HaveMatchingNonEmptyKeys()
    {
        var directory = Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "Localization");
        var english = ReadStrings(Path.Combine(
            directory,
            "Strings.en-US.xaml"));

        Assert.Equal(
            SupportedCultureNames.Select(name => $"Strings.{name}.xaml")
                .Order(StringComparer.Ordinal),
            Directory.EnumerateFiles(
                    directory,
                    "Strings.*.xaml",
                    SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal));

        Assert.True(english.Count >= 250);
        Assert.DoesNotContain(english.Values, string.IsNullOrWhiteSpace);
        foreach (var cultureName in SupportedCultureNames)
        {
            var localized = ReadStrings(Path.Combine(
                directory,
                $"Strings.{cultureName}.xaml"));
            Assert.Equal(
                english.Keys.Order(StringComparer.Ordinal),
                localized.Keys.Order(StringComparer.Ordinal));
            Assert.DoesNotContain(localized.Values, string.IsNullOrWhiteSpace);
            foreach (var key in LanguageAutonymKeys)
                Assert.Equal(english[key], localized[key]);
        }

        Assert.NotEqual(
            english["Language.Title"],
            ReadStrings(Path.Combine(directory, "Strings.nl-NL.xaml"))[
                "Language.Title"]);
    }

    [Fact]
    public void LocalizationDictionaries_HaveMatchingFormatPlaceholders()
    {
        var directory = Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "Localization");
        var english = ReadStrings(Path.Combine(
            directory,
            "Strings.en-US.xaml"));
        foreach (var cultureName in SupportedCultureNames.Skip(1))
        {
            var localized = ReadStrings(Path.Combine(
                directory,
                $"Strings.{cultureName}.xaml"));
            foreach (var (key, englishValue) in english)
            {
                Assert.Equal(
                    ReadPlaceholderIndexes(englishValue),
                    ReadPlaceholderIndexes(localized[key]));
            }
        }
    }

    [Fact]
    public void SessionAutomationResources_KeepOnlyReviewedEnglishEquivalents()
    {
        var directory = Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "Localization");
        var english = ReadStrings(Path.Combine(
            directory,
            "Strings.en-US.xaml"));
        string[] prefixes =
        [
            "Accounts.",
            "AutomationSettings.",
            "Destinations.",
            "Home.",
            "Macro.",
            "Template.",
            "Tutorial."
        ];

        foreach (var cultureName in SupportedCultureNames.Skip(1))
        {
            var localized = ReadStrings(Path.Combine(
                directory,
                $"Strings.{cultureName}.xaml"));
            var identicalKeys = localized
                .Where(pair =>
                    prefixes.Any(prefix => pair.Key.StartsWith(
                        prefix,
                        StringComparison.Ordinal)) &&
                    string.Equals(
                        pair.Value,
                        english[pair.Key],
                        StringComparison.Ordinal))
                .Select(pair => pair.Key)
                .Order(StringComparer.Ordinal);

            Assert.Equal(
                SessionAutomationEnglishEquivalenceAllowlist[cultureName]
                    .Order(StringComparer.Ordinal),
                identicalKeys);
        }
    }

    [Fact]
    public void LocalizationDictionaries_FormatEveryParameterizedMessage()
    {
        var directory = Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "Localization");
        var arguments = Enumerable.Range(1, 12)
            .Select(value => (object)value)
            .ToArray();

        foreach (var cultureName in SupportedCultureNames)
        {
            var culture = CultureInfo.GetCultureInfo(cultureName);
            var localized = ReadStrings(Path.Combine(
                directory,
                $"Strings.{cultureName}.xaml"));
            foreach (var (key, value) in localized.Where(pair =>
                         ReadPlaceholderIndexes(pair.Value).Length > 0))
            {
                var formatted = string.Format(culture, value, arguments);
                Assert.False(
                    string.IsNullOrWhiteSpace(formatted),
                    $"{cultureName}:{key} formatted to an empty value.");
                Assert.Empty(ReadPlaceholderIndexes(formatted));
            }
        }
    }

    [Fact]
    public void LocalizationDictionaries_KeepExplicitSingularAndPluralPairs()
    {
        var directory = Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "Localization");
        var english = ReadStrings(Path.Combine(
            directory,
            "Strings.en-US.xaml"));
        var singularKeys = english.Keys
            .Where(key => key.EndsWith("One", StringComparison.Ordinal))
            .ToArray();

        Assert.True(singularKeys.Length >= 20);
        foreach (var singularKey in singularKeys)
        {
            var pluralKey = singularKey[..^"One".Length] + "Many";
            Assert.True(
                english.ContainsKey(pluralKey),
                $"Missing plural resource for {singularKey}.");
        }

        var grammaticalPairs = new[]
        {
            "Main.DurationSecond",
            "Clients.Refresh",
            "Main.BatchCompleteTitle",
            "Metadata.Preview.SkippedAccount"
        };
        foreach (var cultureName in SupportedCultureNames)
        {
            var localized = ReadStrings(Path.Combine(
                directory,
                $"Strings.{cultureName}.xaml"));
            foreach (var pair in grammaticalPairs)
            {
                Assert.NotEqual(
                    localized[$"{pair}One"],
                    localized[$"{pair}Many"]);
            }
        }
    }

    [Fact]
    public void LanguageSelector_OffersEverySupportedPreference()
    {
        var document = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "LanguageSettingsDialog.xaml"));
        var tags = document.Descendants()
            .Where(element => element.Name.LocalName == "ComboBoxItem")
            .Select(element => (string?)element.Attribute("Tag"))
            .Where(tag => tag is not null)
            .Cast<string>()
            .ToArray();

        Assert.Equal(LocalizationPreference.SupportedValues, tags);
    }

    [Fact]
    public void ProductionXaml_UserFacingStaticAttributesUseLocalizationResources()
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
            if (path.Contains(
                    $"{Path.DirectorySeparatorChar}Localization{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase) ||
                path.Contains(
                    $"{Path.DirectorySeparatorChar}Themes{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).Equals(
                    "App.xaml",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (var attribute in document.Descendants().Attributes())
            {
                if (!UserFacingAttributeNames.Contains(
                        attribute.Name.LocalName,
                        StringComparer.Ordinal) ||
                    string.IsNullOrWhiteSpace(attribute.Value) ||
                    attribute.Value.StartsWith('{') ||
                    attribute.Value is "SessionDock" or "SD")
                {
                    continue;
                }

                var line = ((System.Xml.IXmlLineInfo)attribute).LineNumber;
                violations.Add(
                    $"{Path.GetFileName(path)}:{line} " +
                    $"{attribute.Name.LocalName}=\"{attribute.Value}\"");
            }
            foreach (var textNode in document.DescendantNodes().OfType<XText>())
            {
                if (string.IsNullOrWhiteSpace(textNode.Value))
                    continue;

                var line = ((System.Xml.IXmlLineInfo)textNode).LineNumber;
                violations.Add(
                    $"{Path.GetFileName(path)}:{line} inline text: " +
                    $"\"{textNode.Value.Trim()}\"");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Static user-facing XAML text must use a live DynamicResource." +
            Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void ProductionXaml_LocalizedResourcesRemainLiveDynamicResources()
    {
        var applicationDirectory = Path.Combine(
            FindRepositoryRoot(),
            "SessionDock");
        var english = ReadStrings(Path.Combine(
            applicationDirectory,
            "Localization",
            "Strings.en-US.xaml"));
        var staticResourcePattern = new Regex(
            @"\{StaticResource\s+(?<key>[A-Za-z][A-Za-z0-9.]+)\}",
            RegexOptions.CultureInvariant);
        var violations = new List<string>();

        foreach (var path in Directory.EnumerateFiles(
                     applicationDirectory,
                     "*.xaml",
                     SearchOption.AllDirectories))
        {
            if (path.Contains(
                    $"{Path.DirectorySeparatorChar}Localization{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase) ||
                path.Contains(
                    $"{Path.DirectorySeparatorChar}Themes{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var source = File.ReadAllText(path);
            foreach (Match match in staticResourcePattern.Matches(source))
            {
                var key = match.Groups["key"].Value;
                if (!english.ContainsKey(key))
                    continue;

                var line = source.AsSpan(0, match.Index).Count('\n') + 1;
                violations.Add($"{Path.GetFileName(path)}:{line} {key}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Localized XAML resources must remain live DynamicResource references." +
            Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void ProductionCSharp_UserFacingUiSinksDoNotUseStringLiterals()
    {
        var applicationDirectory = Path.Combine(
            FindRepositoryRoot(),
            "SessionDock");
        var patterns = new Dictionary<string, Regex>
        {
            ["UI property assignment"] = new Regex(
                "(?:\\.|\\b)(?:Text|Content|Header|Title|ToolTip|Filter)\\s*=\\s*\\$?\"(?<value>(?:[^\"\\\\]|\\\\.)+)\"",
                RegexOptions.CultureInvariant),
            ["status call"] = new Regex(
                "\\bSetStatus\\s*\\(\\s*\\$?\"(?<value>(?:[^\"\\\\]|\\\\.)+)\"",
                RegexOptions.CultureInvariant),
            ["message box"] = new Regex(
                "\\bMessageBox\\.Show\\s*\\(\\s*(?:this\\s*,\\s*)?\\$?\"(?<value>(?:[^\"\\\\]|\\\\.)+)\"",
                RegexOptions.CultureInvariant),
            ["automation property"] = new Regex(
                "\\bAutomationProperties\\.Set(?:Name|HelpText|ItemStatus)\\s*\\(\\s*[^,]+,\\s*\\$?\"(?<value>(?:[^\"\\\\]|\\\\.)+)\"",
                RegexOptions.CultureInvariant)
        };
        var violations = new List<string>();
        foreach (var path in Directory.EnumerateFiles(
                     applicationDirectory,
                     "*.cs",
                     SearchOption.AllDirectories))
        {
            if (path.Contains(
                    $"{Path.DirectorySeparatorChar}tools{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase) ||
                path.Contains(
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase) ||
                path.Contains(
                    $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var source = File.ReadAllText(path);
            foreach (var (sink, pattern) in patterns)
            {
                foreach (Match match in pattern.Matches(source))
                {
                    var value = match.Groups["value"].Value;
                    var literalText = Regex.Replace(
                        value,
                        @"\{[^{}]+\}",
                        string.Empty,
                        RegexOptions.CultureInvariant);
                    if (literalText is "SessionDock" or "SD" ||
                        literalText.All(character => !char.IsLetterOrDigit(character)))
                    {
                        continue;
                    }

                    var line = source.AsSpan(0, match.Index).Count('\n') + 1;
                    violations.Add(
                        $"{Path.GetFileName(path)}:{line} {sink}: \"{value}\"");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "User-facing C# UI sinks must receive localized runtime text." +
            Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void AppResources_KeepEnglishFallbackAlongsideThemePalette()
    {
        var appXaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "App.xaml"));

        Assert.Contains(
            "Themes/DarkTheme.xaml",
            appXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Localization/Strings.en-US.xaml",
            appXaml,
            StringComparison.Ordinal);
        foreach (var cultureName in SupportedCultureNames.Skip(1))
        {
            Assert.DoesNotContain(
                $"Localization/Strings.{cultureName}.xaml",
                appXaml,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ProductionLocalizationReferences_ExistInEnglishFallback()
    {
        var applicationDirectory = Path.Combine(
            FindRepositoryRoot(),
            "SessionDock");
        var english = ReadStrings(Path.Combine(
            applicationDirectory,
            "Localization",
            "Strings.en-US.xaml"));
        var references = new HashSet<string>(StringComparer.Ordinal);
        var xamlPattern = new Regex(
            @"\{DynamicResource\s+(?<key>[A-Za-z][A-Za-z0-9.]+)\}",
            RegexOptions.CultureInvariant);
        foreach (var path in Directory.EnumerateFiles(
                     applicationDirectory,
                     "*.xaml",
                     SearchOption.AllDirectories))
        {
            if (path.Contains(
                    $"{Path.DirectorySeparatorChar}Localization{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase) ||
                path.Contains(
                    $"{Path.DirectorySeparatorChar}Themes{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            references.UnionWith(xamlPattern.Matches(File.ReadAllText(path))
                .Select(match => match.Groups["key"].Value)
                .Where(key => key.Contains('.', StringComparison.Ordinal)));
        }

        var resourcePrefixes = english.Keys
            .Select(key => key.Split('.')[0])
            .Concat(
            [
                "Diagnostics",
                "Startup",
                "UpdateFailure",
                "Validation",
                "WebSession"
            ])
            .Distinct(StringComparer.Ordinal)
            .Select(Regex.Escape);
        var codePattern = new Regex(
            $@"""(?<key>(?:{string.Join('|', resourcePrefixes)})(?:\.[A-Z][A-Za-z0-9]*)+)(?![A-Za-z0-9.])""",
            RegexOptions.CultureInvariant);
        foreach (var path in Directory.EnumerateFiles(
                     applicationDirectory,
                     "*.cs",
                     SearchOption.AllDirectories))
        {
            if (path.Contains(
                    $"{Path.DirectorySeparatorChar}tools{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            references.UnionWith(codePattern.Matches(File.ReadAllText(path))
                .Select(match => match.Groups["key"].Value)
                .Where(key => key.Contains('.', StringComparison.Ordinal)));
        }

        Assert.Empty(references.Except(english.Keys, StringComparer.Ordinal));
    }

    [Fact]
    public void LocalizationService_LiveSwitchesResourcesAndFallsBackSafely()
    {
        var localizationDirectory = Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "Localization");
        var expectedRuntimeMessages = SupportedCultureNames.ToDictionary(
            cultureName => cultureName,
            cultureName => string.Format(
                CultureInfo.GetCultureInfo(cultureName),
                ReadStrings(Path.Combine(
                    localizationDirectory,
                    $"Strings.{cultureName}.xaml"))[
                        "Main.DurationSecondMany"],
                3),
            StringComparer.Ordinal);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var application = new Application();
                application.Resources.MergedDictionaries.Add(
                    new ResourceDictionary
                    {
                        Source = new Uri(
                            "/SessionDock;component/Localization/Strings.en-US.xaml",
                            UriKind.Relative)
                    });
                using var service = new AppLocalizationService(
                    application,
                    CultureInfo.GetCultureInfo("en-US"));
                var languageProbe = new Window();
                var changes = 0;
                service.LanguageChanged += (_, _) => changes++;

                var expectedTitles = new Dictionary<string, string>
                {
                    [LocalizationPreference.Dutch] = "Taal",
                    [LocalizationPreference.German] = "Sprache",
                    [LocalizationPreference.French] = "Langue",
                    [LocalizationPreference.Spanish] = "Idioma",
                    [LocalizationPreference.English] = "Language"
                };
                foreach (var (preference, expectedTitle) in expectedTitles)
                {
                    service.ApplyPreference(preference);
                    Assert.Equal(
                        expectedTitle,
                        service.GetString("Language.Title"));
                    Assert.Equal(
                        expectedRuntimeMessages[preference],
                        service.Format("Main.DurationSecondMany", 3));
                    Assert.Equal(preference, service.EffectiveCulture.Name);
                    service.ApplyWindowLanguage(languageProbe);
                    Assert.Equal(
                        preference,
                        CultureInfo.GetCultureInfo(
                            languageProbe.Language.IetfLanguageTag).Name);
                }

                Assert.Equal("Missing.Key", service.GetString("Missing.Key"));
                Assert.Equal(expectedTitles.Count, changes);
                application.Shutdown();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null)
            throw new InvalidOperationException("Live language test failed.", failure);
    }

    [Fact]
    public void LanguageSaveFailure_RestoresLocaleBeforePresentingFailure()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "MainWindow.Localization.cs"));
        var failureBranch = new Regex(
            @"if \(!committed\)\s*\{\s*" +
            @"Localization\.ApplyPreference\(originalPreference\);[\s\S]*?" +
            @"SetStatus\(\s*Localize\(""Language\.SaveFailure""\)",
            RegexOptions.CultureInvariant);

        Assert.Contains("showFailure: false", source, StringComparison.Ordinal);
        Assert.Matches(failureBranch, source);
    }

    [Fact]
    public void DisplayDatesUseSelectedCulture_JsonSerializationDoesNot()
    {
        var value = new DateTimeOffset(
            2026,
            7,
            29,
            18,
            15,
            0,
            TimeSpan.Zero);
        var cultures = SupportedCultureNames
            .Select(CultureInfo.GetCultureInfo)
            .ToArray();
        var displays = cultures
            .Select(culture => LocalizationCulture.FormatLocalDateTime(
                value,
                culture))
            .ToArray();
        for (var index = 0; index < cultures.Length; index++)
        {
            Assert.Equal(
                value.ToLocalTime().ToString("g", cultures[index]),
                displays[index]);
        }
        Assert.True(displays.Distinct(StringComparer.Ordinal).Count() >= 3);

        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            var serialized = cultures.Select(culture =>
            {
                CultureInfo.CurrentCulture = culture;
                return JsonSerializer.Serialize(value);
            }).ToArray();
            Assert.Single(serialized.Distinct(StringComparer.Ordinal));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory))
            Directory.Delete(_storageDirectory, recursive: true);
    }

    private static AppSettings CreateSettings(string language)
    {
        var accountKey = Guid.NewGuid().ToString("N");
        return new AppSettings
        {
            Accounts =
            [
                new AccountProfile
                {
                    Key = accountKey,
                    UserId = 42,
                    Username = "builder",
                    SessionFolder = $@"Profiles\{accountKey}"
                }
            ],
            ActiveAccountKey = accountKey,
            Language = language
        };
    }

    private static Dictionary<string, string> ReadStrings(string path)
    {
        Assert.True(File.Exists(path), $"Localization file missing: {path}");
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        return XDocument.Load(path).Root!
            .Elements()
            .ToDictionary(
                element => (string)element.Attribute(xaml + "Key")!,
                element => element.Value,
                StringComparer.Ordinal);
    }

    private static int[] ReadPlaceholderIndexes(string value) =>
        Regex.Matches(
                value,
                @"(?<!\{)\{(?<index>[0-9]+)(?:[^}]*)\}(?!\})",
                RegexOptions.CultureInvariant)
            .Select(match => int.Parse(
                match.Groups["index"].Value,
                CultureInfo.InvariantCulture))
            .Distinct()
            .Order()
            .ToArray();

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
