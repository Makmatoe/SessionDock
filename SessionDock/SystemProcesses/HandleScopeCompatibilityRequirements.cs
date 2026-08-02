using SessionDock.ReleaseTrust;

namespace SessionDock.SystemProcesses;

internal static class HandleScopeCompatibilityRequirements
{
    internal static IReadOnlySet<string> CompiledApiContracts { get; } =
        HandleScopeCompatibilityCatalogPolicy.SessionDockApiContracts;

    internal static IReadOnlySet<string> RequiredCapabilities { get; } =
        HandleScopeCompatibilityCatalogPolicy.SessionDockRequiredCapabilities;

    internal static Version SessionDockVersion
    {
        get
        {
            var assemblyVersion = typeof(HandleScopeCompatibilityRequirements)
                .Assembly
                .GetName()
                .Version ?? new Version(0, 0, 0);
            return new Version(
                assemblyVersion.Major,
                assemblyVersion.Minor,
                Math.Max(0, assemblyVersion.Build));
        }
    }
}
