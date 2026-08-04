using SessionDock.Models;

namespace SessionDock.Services;

internal sealed record SessionMacroTemplateReference(
    string TemplateId,
    string TemplateName,
    SessionTemplateMacroMode UsageMode,
    string? AccountKey);

internal static class SessionMacroLibraryPolicy
{
    internal static bool TryNormalizeName(
        string? value,
        out string normalized)
    {
        normalized = string.Empty;
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) ||
            trimmed.Length > SessionTemplatePolicy.MaximumNameLength)
        {
            return false;
        }

        normalized = SessionTemplatePolicy.NormalizeMacroName(trimmed) ??
            string.Empty;
        return normalized.Length > 0;
    }

    internal static IReadOnlyList<SessionMacroTemplateReference>
        FindReferences(
            SessionTemplateCatalog catalog,
            string contentId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (string.IsNullOrWhiteSpace(contentId))
            return [];

        var references = new List<SessionMacroTemplateReference>();
        foreach (var template in catalog.Templates ?? [])
        {
            if (template is null)
                continue;
            var templateId = template.Id ?? string.Empty;
            var templateName = string.IsNullOrWhiteSpace(template.Name)
                ? templateId
                : template.Name;

            foreach (var slot in template.ClientSlots ?? [])
            {
                if (slot is not null && string.Equals(
                        slot.PerClientMacroId,
                        contentId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    references.Add(new SessionMacroTemplateReference(
                        templateId,
                        templateName,
                        SessionTemplateMacroMode.PerClient,
                        slot.AccountKey));
                }
            }

            if (string.Equals(
                    template.SharedMacroId,
                    contentId,
                    StringComparison.OrdinalIgnoreCase))
            {
                references.Add(new SessionMacroTemplateReference(
                    templateId,
                    templateName,
                    SessionTemplateMacroMode.Shared,
                    null));
            }

            if (string.Equals(
                    template.WholeLayoutMacroId,
                    contentId,
                    StringComparison.OrdinalIgnoreCase))
            {
                references.Add(new SessionMacroTemplateReference(
                    templateId,
                    templateName,
                    SessionTemplateMacroMode.WholeLayout,
                    null));
            }
        }

        return references;
    }
}
