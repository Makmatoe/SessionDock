using System.Globalization;
using System.Windows;

namespace SessionDock.Services;

internal sealed class AppLocalizationService : IDisposable
{
    private const string EnglishSource =
        "/SessionDock;component/Localization/Strings.en-US.xaml";
    private const string DutchSource =
        "/SessionDock;component/Localization/Strings.nl-NL.xaml";

    private readonly Application _application;
    private readonly CultureInfo _systemCulture;
    private readonly CultureInfo _originalCulture;
    private readonly CultureInfo _originalUiCulture;
    private readonly CultureInfo? _originalDefaultCulture;
    private readonly CultureInfo? _originalDefaultUiCulture;
    private ResourceDictionary? _activeOverlay;
    private bool _disposed;

    internal AppLocalizationService(
        Application application,
        CultureInfo? systemCulture = null)
    {
        _application = application ??
            throw new ArgumentNullException(nameof(application));
        _application.Dispatcher.VerifyAccess();
        _systemCulture = CultureInfo.ReadOnly(
            systemCulture ?? CultureInfo.CurrentUICulture);
        _originalCulture = CultureInfo.CurrentCulture;
        _originalUiCulture = CultureInfo.CurrentUICulture;
        _originalDefaultCulture = CultureInfo.DefaultThreadCurrentCulture;
        _originalDefaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;
        CurrentPreference = LocalizationPreference.System;
        EffectiveCulture = CultureInfo.GetCultureInfo(
            LocalizationPreference.Resolve(CurrentPreference, _systemCulture));
        ApplyCulture(EffectiveCulture);
    }

    internal string CurrentPreference { get; private set; }

    internal CultureInfo EffectiveCulture { get; private set; }

    internal event EventHandler? LanguageChanged;

    internal void ApplyPreference(string? preference)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _application.Dispatcher.VerifyAccess();

        var normalized = LocalizationPreference.Normalize(preference);
        var cultureName = LocalizationPreference.Resolve(
            normalized,
            _systemCulture);
        var culture = CultureInfo.GetCultureInfo(cultureName);
        var requiresDutchOverlay = cultureName.Equals(
            LocalizationPreference.Dutch,
            StringComparison.Ordinal);
        var overlayIsDutch = HasSource(_activeOverlay, DutchSource);
        var resourcesChanged = requiresDutchOverlay != overlayIsDutch;

        if (resourcesChanged)
        {
            var dictionaries = _application.Resources.MergedDictionaries;
            if (_activeOverlay is not null)
            {
                dictionaries.Remove(_activeOverlay);
                _activeOverlay = null;
            }
            if (requiresDutchOverlay)
            {
                _activeOverlay = new ResourceDictionary
                {
                    Source = new Uri(DutchSource, UriKind.Relative)
                };
                dictionaries.Add(_activeOverlay);
            }
        }

        var preferenceChanged = !CurrentPreference.Equals(
            normalized,
            StringComparison.Ordinal);
        var cultureChanged = !EffectiveCulture.Name.Equals(
            culture.Name,
            StringComparison.Ordinal);
        CurrentPreference = normalized;
        EffectiveCulture = culture;
        ApplyCulture(culture);
        if (resourcesChanged || preferenceChanged || cultureChanged)
            LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    internal string GetString(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _application.TryFindResource(key) as string ?? key;
    }

    internal string Format(string key, params object?[] arguments) =>
        LocalizationCulture.Format(
            EffectiveCulture,
            GetString(key),
            arguments);

    public void Dispose()
    {
        if (_disposed)
            return;

        if (_activeOverlay is not null)
        {
            _application.Resources.MergedDictionaries.Remove(_activeOverlay);
            _activeOverlay = null;
        }
        CultureInfo.CurrentCulture = _originalCulture;
        CultureInfo.CurrentUICulture = _originalUiCulture;
        CultureInfo.DefaultThreadCurrentCulture = _originalDefaultCulture;
        CultureInfo.DefaultThreadCurrentUICulture = _originalDefaultUiCulture;
        _disposed = true;
    }

    private static bool HasSource(
        ResourceDictionary? dictionary,
        string expectedSuffix) =>
        dictionary?.Source?.OriginalString.EndsWith(
            expectedSuffix,
            StringComparison.OrdinalIgnoreCase) == true;

    private static void ApplyCulture(CultureInfo culture)
    {
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
}
