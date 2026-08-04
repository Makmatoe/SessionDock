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
        return Validate(
            template,
            catalog,
            definition =>
            {
                var recording = macroStore.Load(definition);
                _ = definition.Kind switch
                {
                    SessionMacroKind.Client =>
                        ExactWheelCoordinateTransforms.TransformClientRelative(
                            recording,
                            recording.Display,
                            recording.Target),
                    SessionMacroKind.WholeLayout =>
                        ExactWheelCoordinateTransforms
                            .TransformVirtualDesktopNormalized(
                                recording,
                                recording.Display,
                                recording.Target),
                    _ => throw new InvalidDataException(
                        "The macro kind is not supported for playback.")
                };
            });
    }

    internal static SessionTemplateMacroPreflightResult Validate(
        SessionTemplate template,
        SessionTemplateCatalog catalog,
        Action<MacroDefinition> validateMacro)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(validateMacro);

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
            try
            {
                validateMacro(definition);
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
