using System.IO;
using SessionDock.ExactWheel;
using SessionDock.Models;

namespace SessionDock.Services;

internal enum SessionTemplateMacroPreflightFailureKind
{
    None,
    InvalidAssignment,
    MacroUnavailable
}

internal sealed record SessionTemplateMacroPreflightResult(
    bool Success,
    SessionTemplateMacroPreflightFailureKind FailureKind,
    string? MacroId = null)
{
    internal static SessionTemplateMacroPreflightResult Passed { get; } =
        new(true, SessionTemplateMacroPreflightFailureKind.None);

    internal static SessionTemplateMacroPreflightResult Invalid(
        string? macroId = null) =>
        new(
            false,
            SessionTemplateMacroPreflightFailureKind.InvalidAssignment,
            macroId);

    internal static SessionTemplateMacroPreflightResult Unavailable(
        string? macroId = null) =>
        new(
            false,
            SessionTemplateMacroPreflightFailureKind.MacroUnavailable,
            macroId);
}

internal static class SessionTemplateMacroPreflight
{
    internal static SessionTemplateMacroPreflightResult Validate(
        SessionTemplate template,
        SessionTemplateCatalog catalog,
        ExactWheelMacroStore macroStore)
    {
        ArgumentNullException.ThrowIfNull(macroStore);
        using var playbackCache = new SessionMacroPlaybackCache();
        return ValidateCancellable(
            template,
            catalog,
            macroStore,
            playbackCache,
            CancellationToken.None);
    }

    internal static SessionTemplateMacroPreflightResult Validate(
        SessionTemplate template,
        SessionTemplateCatalog catalog,
        ExactWheelMacroStore macroStore,
        SessionMacroPlaybackCache playbackCache)
        => ValidateCancellable(
            template,
            catalog,
            macroStore,
            playbackCache,
            CancellationToken.None);

    internal static SessionTemplateMacroPreflightResult ValidateCancellable(
        SessionTemplate template,
        SessionTemplateCatalog catalog,
        ExactWheelMacroStore macroStore,
        SessionMacroPlaybackCache playbackCache,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(macroStore);
        ArgumentNullException.ThrowIfNull(playbackCache);
        return ValidateCancellable(
            template,
            catalog,
            (definition, token) =>
            {
                token.ThrowIfCancellationRequested();
                var recording = playbackCache.GetOrLoadCancellable(
                    definition,
                    macroStore,
                    static (store, candidate) => store.Load(candidate),
                    token);
                token.ThrowIfCancellationRequested();
                _ = definition.Kind switch
                {
                    SessionMacroKind.Client =>
                        ExactWheelCoordinateTransforms
                            .CreateClientRelativePlaybackTransformCancellable(
                                recording,
                                recording.Display,
                                recording.Target,
                                token),
                    SessionMacroKind.WholeLayout =>
                        ExactWheelCoordinateTransforms
                            .CreateVirtualDesktopNormalizedPlaybackTransform(
                                recording,
                                recording.Display,
                                recording.Target),
                    _ => throw new InvalidDataException(
                        "The macro kind is not supported for playback.")
                };
            },
            cancellationToken);
    }

    internal static SessionTemplateMacroPreflightResult Validate(
        SessionTemplate template,
        SessionTemplateCatalog catalog,
        Action<MacroDefinition> validateMacro)
    {
        ArgumentNullException.ThrowIfNull(validateMacro);
        return ValidateCancellable(
            template,
            catalog,
            (definition, _) => validateMacro(definition),
            CancellationToken.None);
    }

    internal static SessionTemplateMacroPreflightResult ValidateCancellable(
        SessionTemplate template,
        SessionTemplateCatalog catalog,
        Action<MacroDefinition, CancellationToken> validateMacro,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(validateMacro);
        cancellationToken.ThrowIfCancellationRequested();

        var assignments = SessionTemplateMacroAssignmentPolicy.Resolve(
            template,
            catalog);
        if (assignments.InvalidAssignments.Count > 0)
        {
            return SessionTemplateMacroPreflightResult.Invalid(
                assignments.InvalidAssignments[0].MacroId);
        }
        if (template.MacroMode == SessionTemplateMacroMode.None)
            return SessionTemplateMacroPreflightResult.Passed;

        var resolvedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolvedDefinitions = new List<MacroDefinition>();
        foreach (var assignment in assignments.ValidAssignments)
        {
            if (resolvedIds.Add(assignment.Definition.ContentId))
                resolvedDefinitions.Add(assignment.Definition);
        }

        foreach (var definition in resolvedDefinitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                validateMacro(definition, cancellationToken);
            }
            catch (Exception exception) when (
                exception is IOException or InvalidDataException or
                    UnauthorizedAccessException or InvalidOperationException or
                    ArgumentException or NotSupportedException or
                    OverflowException or System.Security.SecurityException)
            {
                return SessionTemplateMacroPreflightResult.Unavailable(
                    definition.ContentId);
            }
        }

        return SessionTemplateMacroPreflightResult.Passed;
    }
}
