namespace SessionDock.Tests;

public sealed class PortableImportIntegrationStructureTests
{
    [Fact]
    public void PortableImport_CommitsBeforeBestEffortRefreshAndCleansRollbackBlobs()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "MainWindow.MetadataTransfer.cs"));

        Assert.Contains(
            "new PortableDataDialog(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_exactWheelMacroStore.ReadExactBytes",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_exactWheelMacroStore.SaveExactBytes(",
            source,
            StringComparison.Ordinal);
        var portableApplyStart = source.IndexOf(
            "private async Task ApplyPortableImportAsync(",
            StringComparison.Ordinal);
        var portableApplyEnd = source.IndexOf(
            "private void RefreshPortableImportUiBestEffort()",
            StringComparison.Ordinal);
        Assert.True(
            portableApplyStart >= 0 && portableApplyEnd > portableApplyStart);
        var portableApply = source[portableApplyStart..portableApplyEnd];
        Assert.DoesNotContain(
            "onCommitted:",
            portableApply,
            StringComparison.Ordinal);

        var committed = portableApply.IndexOf(
            "settingsWereCommitted = true;",
            StringComparison.Ordinal);
        var refreshed = portableApply.IndexOf(
            "RefreshPortableImportUiBestEffort();",
            StringComparison.Ordinal);
        Assert.True(committed >= 0 && refreshed > committed);
        var refreshStart = source.IndexOf(
            "private void RefreshPortableImportUiBestEffort()",
            StringComparison.Ordinal);
        var refreshEnd = source.IndexOf(
            "private void CleanupPortableImportMacros(",
            StringComparison.Ordinal);
        Assert.True(refreshStart >= 0 && refreshEnd > refreshStart);
        var refresh = source[refreshStart..refreshEnd];
        Assert.Contains(
            "ShowDestinationForProfile(_activeProfile);",
            refresh,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (!settingsWereCommitted && !catalogWasWritten)",
            portableApply,
            StringComparison.Ordinal);
        Assert.Contains(
            "priorFiles.Contains(definition.SafeFileName)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "TryDeleteExactBytes(definition)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "OpenLegacyTransferRequested",
            source,
            StringComparison.Ordinal);
        var transferStart = source.IndexOf(
            "private async Task ShowMetadataTransferAsync(",
            StringComparison.Ordinal);
        var catalogLoad = source.IndexOf(
            "var catalog = TryLoadSessionTemplateCatalog();",
            transferStart,
            StringComparison.Ordinal);
        var destinationFlush = source.IndexOf(
            "await FlushDestinationPersistenceAsync()",
            transferStart,
            StringComparison.Ordinal);
        Assert.True(
            destinationFlush > transferStart && destinationFlush < catalogLoad,
            "Metadata transfer must flush the active destination draft before it snapshots export/import state.");
    }

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
            "Could not locate the SessionDock repository root.");
    }
}
