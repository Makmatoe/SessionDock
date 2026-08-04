using System.Globalization;
using System.IO;
using System.Text;
using SessionDock.ExactWheel;
using SessionDock.Models;

namespace SessionDock.Services;

internal static class SessionTemplatePolicy
{
    internal const int LegacyCatalogSchemaVersion = 1;
    internal const int PreviousCatalogSchemaVersion = 2;
    internal const int CatalogSchemaVersion = 3;
    internal const int TemplateSchemaVersion = 1;
    internal const int MaximumTemplates = 128;
    internal const int MaximumMacroDefinitions = 512;
    internal const int MaximumSlotsPerTemplate = 128;
    internal const int MaximumNameLength = 80;
    internal const int MaximumIdentifierLength = 128;
    internal const int MaximumDestinationLength = 4096;
    internal const int MaximumSafeFileNameLength = 128;
    // Keep the metadata boundary aligned with ExactWheel's recording limit.
    internal const int MaximumEventCount =
        checked((int)ExactWheelLimits.MaximumEventCount);
    internal const long MaximumDurationMilliseconds = 86_400_000;
    internal const double MinimumMacroPlaybackSpeed = 0.1;
    internal const double MaximumMacroPlaybackSpeed = 2;
    // ExactWheel accepts at most 64 monitors, so the largest valid index is 63.
    internal const int MaximumMonitorIndex = 63;
    internal const int MaximumMonitorStableIdLength = 512;

    private const double DefaultTargetWidth = 800;
    private const double DefaultTargetHeight = 600;
    private const double DefaultMinimumWidth = 640;
    private const double DefaultMinimumHeight = 480;
    private const double DefaultRevealX = 56;
    private const double DefaultRevealY = 36;
    private const double DefaultMacroPlaybackSpeed = 1.0;
    private static readonly HashSet<string> ReservedFileNames = new(
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5",
            "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5",
            "LPT6", "LPT7", "LPT8", "LPT9"
        ],
        StringComparer.OrdinalIgnoreCase);

    internal static SessionTemplateCatalog Normalize(
        SessionTemplateCatalog source)
    {
        if (!TryNormalize(source, out var normalized))
        {
            throw new ArgumentException(
                "The session-template catalog schema is unsupported.",
                nameof(source));
        }

        return normalized;
    }

    internal static bool TryNormalize(
        SessionTemplateCatalog? source,
        out SessionTemplateCatalog normalized)
    {
        normalized = CreateDefault();
        if (source is null ||
            source.SchemaVersion is not (
                LegacyCatalogSchemaVersion or
                PreviousCatalogSchemaVersion or
                CatalogSchemaVersion))
        {
            return false;
        }

        var macroCandidates = new List<MacroDefinition>();
        foreach (var definition in source.MacroDefinitions ?? [])
        {
            var candidate = NormalizeMacroDefinition(definition);
            if (candidate is not null)
                macroCandidates.Add(candidate);
        }

        // A content id must identify exactly one intrinsic macro definition.
        // Drop every member of an ambiguous group instead of choosing one by
        // catalog order. Assignment resolution can then report the reference
        // as missing without ever selecting attacker-controlled metadata.
        var ambiguousMacroIds = macroCandidates
            .GroupBy(
                definition => definition.ContentId,
                StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Skip(1).Any())
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var macroDefinitions = macroCandidates
            .Where(definition =>
                !ambiguousMacroIds.Contains(definition.ContentId))
            .Take(MaximumMacroDefinitions)
            .ToList();

        var templates = new List<SessionTemplate>();
        var templateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var template in source.Templates ?? [])
        {
            if (templates.Count >= MaximumTemplates)
                break;
            var candidate = NormalizeTemplate(template);
            if (candidate is null || !templateIds.Add(candidate.Id))
                continue;
            templates.Add(candidate);
        }

        normalized = new SessionTemplateCatalog
        {
            SchemaVersion = CatalogSchemaVersion,
            Templates = templates,
            MacroDefinitions = macroDefinitions,
            TemplatePreferences = NormalizePreferences(
                source.TemplatePreferences)
        };
        return true;
    }

    internal static SessionTemplateCatalog CreateDefault() => new()
    {
        SchemaVersion = CatalogSchemaVersion,
        Templates = [],
        MacroDefinitions = [],
        TemplatePreferences = new TemplatePreferences()
    };

    internal static string? NormalizeMacroName(string? value) =>
        NormalizeDisplayText(value, MaximumNameLength);

    internal static bool AreEquivalent(
        SessionTemplateCatalog? first,
        SessionTemplateCatalog? second)
    {
        if (ReferenceEquals(first, second))
            return true;
        if (first is null || second is null ||
            first.SchemaVersion != second.SchemaVersion ||
            !AreEquivalent(
                first.TemplatePreferences,
                second.TemplatePreferences))
        {
            return false;
        }

        var firstTemplates = first.Templates ?? [];
        var secondTemplates = second.Templates ?? [];
        if (firstTemplates.Count != secondTemplates.Count)
            return false;
        for (var index = 0; index < firstTemplates.Count; index++)
        {
            if (!AreEquivalent(
                    firstTemplates[index],
                    secondTemplates[index]))
            {
                return false;
            }
        }

        var firstMacros = first.MacroDefinitions ?? [];
        var secondMacros = second.MacroDefinitions ?? [];
        if (firstMacros.Count != secondMacros.Count)
            return false;
        for (var index = 0; index < firstMacros.Count; index++)
        {
            if (!AreEquivalent(firstMacros[index], secondMacros[index]))
                return false;
        }

        return true;
    }

    private static SessionTemplate? NormalizeTemplate(
        SessionTemplate? source)
    {
        if (source is null ||
            source.SchemaVersion != TemplateSchemaVersion ||
            !Enum.IsDefined(source.LayoutMode) ||
            !Enum.IsDefined(source.MacroMode))
        {
            return null;
        }

        var id = NormalizeIdentifier(source.Id);
        var name = NormalizeDisplayText(source.Name, MaximumNameLength);
        if (id is null || name is null)
            return null;

        var slots = new List<(SessionTemplateClientSlot Slot, int Index)>();
        var slotIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var accountKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceSlots = source.ClientSlots ?? [];
        for (var index = 0;
             index < sourceSlots.Count && slots.Count < MaximumSlotsPerTemplate;
             index++)
        {
            var slot = NormalizeSlot(sourceSlots[index], source.MacroMode);
            if (slot is null ||
                !slotIds.Add(slot.SlotId) ||
                !accountKeys.Add(slot.AccountKey))
                continue;
            slots.Add((slot, index));
        }

        var orderedSlots = slots
            .OrderBy(item => item.Slot.Order)
            .ThenBy(item => item.Index)
            .Select((item, order) =>
            {
                item.Slot.Order = order;
                return item.Slot;
            })
            .ToList();
        var sharedMacroId = NormalizeIdentifier(source.SharedMacroId);
        List<string>? sharedMacroAccountKeys = null;
        var wholeLayoutMacroId = NormalizeIdentifier(source.WholeLayoutMacroId);
        var repeatWholeLayoutMacro = source.RepeatWholeLayoutMacro;
        switch (source.MacroMode)
        {
            case SessionTemplateMacroMode.None:
            case SessionTemplateMacroMode.PerClient:
                sharedMacroId = null;
                wholeLayoutMacroId = null;
                repeatWholeLayoutMacro = false;
                break;
            case SessionTemplateMacroMode.Shared:
                sharedMacroAccountKeys = NormalizeSharedMacroAccountKeys(
                    source.SharedMacroAccountKeys,
                    orderedSlots);
                wholeLayoutMacroId = null;
                repeatWholeLayoutMacro = false;
                break;
            case SessionTemplateMacroMode.WholeLayout:
                sharedMacroId = null;
                break;
        }

        return new SessionTemplate
        {
            SchemaVersion = TemplateSchemaVersion,
            Id = id,
            Name = name,
            DelaySeconds = BatchLaunchPreferences.NormalizeDelaySeconds(
                source.DelaySeconds),
            LayoutMode = source.LayoutMode,
            MacroMode = source.MacroMode,
            ClientSlots = orderedSlots,
            SharedMacroId = sharedMacroId,
            SharedMacroAccountKeys = sharedMacroAccountKeys,
            WholeLayoutMacroId = wholeLayoutMacroId,
            RepeatWholeLayoutMacro = repeatWholeLayoutMacro,
            UpdatedAtUtc = NormalizeTimestamp(source.UpdatedAtUtc),
            LegacyPresetName = NormalizeDisplayText(
                source.LegacyPresetName,
                MaximumNameLength)
        };
    }

    internal static IReadOnlyList<SessionTemplateClientSlot>
        SelectSharedMacroTargetSlots(SessionTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.MacroMode != SessionTemplateMacroMode.Shared)
            return [];

        var orderedSlots = (template.ClientSlots ?? [])
            .OrderBy(slot => slot.Order)
            .ToArray();
        if (template.SharedMacroAccountKeys is null)
            return orderedSlots;

        var selected = template.SharedMacroAccountKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return orderedSlots
            .Where(slot => selected.Contains(slot.AccountKey))
            .ToArray();
    }

    private static List<string>? NormalizeSharedMacroAccountKeys(
        IReadOnlyList<string>? source,
        IReadOnlyList<SessionTemplateClientSlot> orderedSlots)
    {
        // Catalogs written before target selection did not contain this field.
        // Preserve null as the explicit backward-compatible "all slots" value.
        if (source is null)
            return null;

        var requested = source
            .Select(NormalizeIdentifier)
            .Where(key => key is not null)
            .Select(key => key!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return orderedSlots
            .Where(slot => requested.Contains(slot.AccountKey))
            .Select(slot => slot.AccountKey)
            .ToList();
    }

    private static SessionTemplateClientSlot? NormalizeSlot(
        SessionTemplateClientSlot? source,
        SessionTemplateMacroMode macroMode)
    {
        if (source is null)
            return null;
        var slotId = NormalizeIdentifier(source.SlotId);
        var accountKey = NormalizeIdentifier(source.AccountKey);
        if (slotId is null || accountKey is null)
            return null;

        return new SessionTemplateClientSlot
        {
            SlotId = slotId,
            AccountKey = accountKey,
            Order = source.Order,
            Destination = NormalizeDestination(source.Destination),
            Placement = NormalizePlacement(source.Placement),
            PerClientMacroId = macroMode ==
                SessionTemplateMacroMode.PerClient
                ? NormalizeIdentifier(source.PerClientMacroId)
                : null
        };
    }

    private static NormalizedClientWindowPlacement? NormalizePlacement(
        NormalizedClientWindowPlacement? source)
    {
        if (source is null ||
            !double.IsFinite(source.Left) ||
            !double.IsFinite(source.Top) ||
            !double.IsFinite(source.Width) ||
            !double.IsFinite(source.Height) ||
            source.Width <= 0 || source.Height <= 0)
        {
            return null;
        }

        var width = Math.Clamp(source.Width, 0.01, 1);
        var height = Math.Clamp(source.Height, 0.01, 1);
        var left = Math.Clamp(source.Left, 0, 1 - width);
        var top = Math.Clamp(source.Top, 0, 1 - height);
        var monitorStableId = NormalizeMonitorStableId(source.MonitorStableId);
        if (source.MonitorStableId is not null && monitorStableId is null)
            return null;
        return new NormalizedClientWindowPlacement
        {
            MonitorStableId = monitorStableId,
            MonitorDeviceName = NormalizeMonitorName(
                source.MonitorDeviceName),
            MonitorIndex = Math.Clamp(
                source.MonitorIndex,
                0,
                MaximumMonitorIndex),
            Left = left,
            Top = top,
            Width = width,
            Height = height
        };
    }

    private static MacroDefinition? NormalizeMacroDefinition(
        MacroDefinition? source)
    {
        if (source is null || !Enum.IsDefined(source.Kind))
            return null;
        var contentId = NormalizeIdentifier(source.ContentId);
        var safeFileName = NormalizeSafeFileName(source.SafeFileName);
        var sha256 = NormalizeSha256(source.Sha256);
        if (contentId is null || safeFileName is null || sha256 is null)
            return null;

        return new MacroDefinition
        {
            ContentId = contentId,
            SafeFileName = safeFileName,
            Name = NormalizeDisplayText(source.Name, MaximumNameLength) ??
                contentId,
            Kind = source.Kind,
            RecordedAccountKey = NormalizeIdentifier(
                source.RecordedAccountKey),
            DurationMilliseconds = Math.Clamp(
                source.DurationMilliseconds,
                0,
                MaximumDurationMilliseconds),
            EventCount = Math.Clamp(
                source.EventCount,
                0,
                MaximumEventCount),
            Sha256 = sha256,
            RecordedAtUtc = NormalizeTimestamp(source.RecordedAtUtc)
        };
    }

    private static TemplatePreferences NormalizePreferences(
        TemplatePreferences? source)
    {
        source ??= new TemplatePreferences();
        var minimumWidth = NormalizeDimension(
            source.MinimumWidth,
            DefaultMinimumWidth,
            320,
            7680);
        var minimumHeight = NormalizeDimension(
            source.MinimumHeight,
            DefaultMinimumHeight,
            240,
            4320);
        var targetWidth = Math.Max(
            minimumWidth,
            NormalizeDimension(
                source.TargetWidth,
                DefaultTargetWidth,
                320,
                7680));
        var targetHeight = Math.Max(
            minimumHeight,
            NormalizeDimension(
                source.TargetHeight,
                DefaultTargetHeight,
                240,
                4320));

        return new TemplatePreferences
        {
            AutoArrangeNormalBatch = source.AutoArrangeNormalBatch,
            TargetWidth = targetWidth,
            TargetHeight = targetHeight,
            MinimumWidth = minimumWidth,
            MinimumHeight = minimumHeight,
            RevealX = Math.Min(
                targetWidth,
                NormalizeDimension(
                    source.RevealX,
                    DefaultRevealX,
                    16,
                    256)),
            RevealY = Math.Min(
                targetHeight,
                NormalizeDimension(
                    source.RevealY,
                    DefaultRevealY,
                    16,
                    160)),
            PreferredMonitorDeviceName = NormalizeMonitorName(
                source.PreferredMonitorDeviceName),
            MacroPlaybackSpeed = NormalizeDimension(
                source.MacroPlaybackSpeed,
                DefaultMacroPlaybackSpeed,
                MinimumMacroPlaybackSpeed,
                MaximumMacroPlaybackSpeed),
            MacroRecordingStopHotkey =
                MacroRecordingHotkeyPolicy.Normalize(
                    source.MacroRecordingStopHotkey)
        };
    }

    private static bool AreEquivalent(
        TemplatePreferences? first,
        TemplatePreferences? second) =>
        first is not null && second is not null &&
        first.AutoArrangeNormalBatch == second.AutoArrangeNormalBatch &&
        first.TargetWidth.Equals(second.TargetWidth) &&
        first.TargetHeight.Equals(second.TargetHeight) &&
        first.MinimumWidth.Equals(second.MinimumWidth) &&
        first.MinimumHeight.Equals(second.MinimumHeight) &&
        first.RevealX.Equals(second.RevealX) &&
        first.RevealY.Equals(second.RevealY) &&
        first.MacroPlaybackSpeed.Equals(second.MacroPlaybackSpeed) &&
        string.Equals(
            first.MacroRecordingStopHotkey,
            second.MacroRecordingStopHotkey,
            StringComparison.Ordinal) &&
        string.Equals(
            first.PreferredMonitorDeviceName,
            second.PreferredMonitorDeviceName,
            StringComparison.Ordinal);

    private static bool AreEquivalent(
        SessionTemplate? first,
        SessionTemplate? second)
    {
        if (first is null || second is null ||
            first.SchemaVersion != second.SchemaVersion ||
            !string.Equals(first.Id, second.Id, StringComparison.Ordinal) ||
            !string.Equals(first.Name, second.Name, StringComparison.Ordinal) ||
            first.DelaySeconds != second.DelaySeconds ||
            first.LayoutMode != second.LayoutMode ||
            first.MacroMode != second.MacroMode ||
            !string.Equals(
                first.SharedMacroId,
                second.SharedMacroId,
                StringComparison.Ordinal) ||
            !AreEquivalent(
                first.SharedMacroAccountKeys,
                second.SharedMacroAccountKeys) ||
            !string.Equals(
                first.WholeLayoutMacroId,
                second.WholeLayoutMacroId,
                StringComparison.Ordinal) ||
            first.RepeatWholeLayoutMacro != second.RepeatWholeLayoutMacro ||
            first.UpdatedAtUtc != second.UpdatedAtUtc ||
            !string.Equals(
                first.LegacyPresetName,
                second.LegacyPresetName,
                StringComparison.Ordinal))
        {
            return false;
        }

        var firstSlots = first.ClientSlots ?? [];
        var secondSlots = second.ClientSlots ?? [];
        if (firstSlots.Count != secondSlots.Count)
            return false;
        for (var index = 0; index < firstSlots.Count; index++)
        {
            if (!AreEquivalent(firstSlots[index], secondSlots[index]))
                return false;
        }

        return true;
    }

    private static bool AreEquivalent(
        IReadOnlyList<string>? first,
        IReadOnlyList<string>? second)
    {
        if (ReferenceEquals(first, second))
            return true;
        if (first is null || second is null || first.Count != second.Count)
            return false;
        for (var index = 0; index < first.Count; index++)
        {
            if (!string.Equals(
                    first[index],
                    second[index],
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreEquivalent(
        SessionTemplateClientSlot? first,
        SessionTemplateClientSlot? second) =>
        first is not null && second is not null &&
        string.Equals(first.SlotId, second.SlotId, StringComparison.Ordinal) &&
        string.Equals(
            first.AccountKey,
            second.AccountKey,
            StringComparison.Ordinal) &&
        first.Order == second.Order &&
        string.Equals(
            first.Destination,
            second.Destination,
            StringComparison.Ordinal) &&
        AreEquivalent(first.Placement, second.Placement) &&
        string.Equals(
            first.PerClientMacroId,
            second.PerClientMacroId,
            StringComparison.Ordinal);

    private static bool AreEquivalent(
        NormalizedClientWindowPlacement? first,
        NormalizedClientWindowPlacement? second) =>
        ReferenceEquals(first, second) ||
        first is not null && second is not null &&
        string.Equals(
            first.MonitorStableId,
            second.MonitorStableId,
            StringComparison.Ordinal) &&
        string.Equals(
            first.MonitorDeviceName,
            second.MonitorDeviceName,
            StringComparison.Ordinal) &&
        first.MonitorIndex == second.MonitorIndex &&
        first.Left.Equals(second.Left) &&
        first.Top.Equals(second.Top) &&
        first.Width.Equals(second.Width) &&
        first.Height.Equals(second.Height);

    private static bool AreEquivalent(
        MacroDefinition? first,
        MacroDefinition? second) =>
        first is not null && second is not null &&
        string.Equals(
            first.ContentId,
            second.ContentId,
            StringComparison.Ordinal) &&
        string.Equals(
            first.SafeFileName,
            second.SafeFileName,
            StringComparison.Ordinal) &&
        string.Equals(first.Name, second.Name, StringComparison.Ordinal) &&
        first.Kind == second.Kind &&
        string.Equals(
            first.RecordedAccountKey,
            second.RecordedAccountKey,
            StringComparison.Ordinal) &&
        first.DurationMilliseconds == second.DurationMilliseconds &&
        first.EventCount == second.EventCount &&
        string.Equals(first.Sha256, second.Sha256, StringComparison.Ordinal) &&
        first.RecordedAtUtc == second.RecordedAtUtc;

    private static string? NormalizeIdentifier(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ||
               normalized.Length > MaximumIdentifierLength ||
               normalized.Any(character =>
                   !char.IsAsciiLetterOrDigit(character) &&
                   character is not ('_' or '-'))
            ? null
            : normalized;
    }

    private static string? NormalizeSafeFileName(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized) ||
            normalized.Length > MaximumSafeFileNameLength ||
            !string.Equals(
                normalized,
                Path.GetFileName(normalized),
                StringComparison.Ordinal) ||
            normalized is "." or ".." ||
            normalized.EndsWith(' ') || normalized.EndsWith('.') ||
            normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        var stem = normalized.Split('.', 2)[0];
        return ReservedFileNames.Contains(stem) ? null : normalized;
    }

    private static string? NormalizeSha256(string? value)
    {
        var normalized = value?.Trim();
        return normalized is { Length: 64 } &&
               normalized.All(char.IsAsciiHexDigit)
            ? normalized.ToUpperInvariant()
            : null;
    }

    private static string? NormalizeDisplayText(
        string? value,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var builder = new StringBuilder(Math.Min(value.Length, maximumLength));
        var pendingSpace = false;
        foreach (var rune in value.Trim().EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune) ||
                Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control)
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            var requiredLength = rune.Utf16SequenceLength +
                (pendingSpace ? 1 : 0);
            if (builder.Length + requiredLength > maximumLength)
                break;
            if (pendingSpace)
                builder.Append(' ');
            pendingSpace = false;
            builder.Append(rune.ToString());
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static string? NormalizeDestination(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ||
               normalized.Length > MaximumDestinationLength ||
               normalized.Any(char.IsControl)
            ? null
            : normalized;
    }

    private static string? NormalizeMonitorName(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ||
               normalized.Length >
                   WindowPlacementPolicy.MaximumMonitorDeviceNameLength ||
               normalized.Any(char.IsControl)
            ? null
            : normalized;
    }

    private static string? NormalizeMonitorStableId(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ||
               normalized.Length > MaximumMonitorStableIdLength ||
               normalized.Any(char.IsControl)
            ? null
            : normalized;
    }

    private static double NormalizeDimension(
        double value,
        double fallback,
        double minimum,
        double maximum) =>
        double.IsFinite(value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;

    private static DateTimeOffset NormalizeTimestamp(DateTimeOffset value) =>
        value == default ? DateTimeOffset.UnixEpoch : value.ToUniversalTime();
}
