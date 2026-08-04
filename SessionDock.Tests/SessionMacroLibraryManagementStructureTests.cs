namespace SessionDock.Tests;

public sealed class SessionMacroLibraryManagementStructureTests
{
    [Fact]
    public void Rename_ChangesOnlyDisplayNameAndKeepsStablePayloadIdentity()
    {
        var source = ReadDialogSource();
        var rename = Slice(
            source,
            "private void RenameMacroButton_Click(",
            "private void RemoveMacroButton_Click(");

        Assert.Contains("definition.Name = normalizedName", rename);
        Assert.DoesNotContain("definition.ContentId =", rename);
        Assert.DoesNotContain("definition.SafeFileName =", rename);
        Assert.DoesNotContain("definition.Sha256 =", rename);
        Assert.DoesNotContain("definition.Kind =", rename);
        Assert.DoesNotContain("definition.EventCount =", rename);
        Assert.DoesNotContain("definition.DurationMilliseconds =", rename);
    }

    [Fact]
    public void Remove_BlocksReferencesAndOnlyMutatesTheCatalogDraft()
    {
        var source = ReadDialogSource();
        var remove = Slice(
            source,
            "private void RemoveMacroButton_Click(",
            "private void DimensionBox_TextChanged(");

        AssertInOrder(
            remove,
            "SessionMacroLibraryPolicy.FindReferences",
            "if (references.Count > 0)",
            "ShowValidation(",
            "MessageBox.Show(",
            "if (confirmation != MessageBoxResult.Yes)",
            "_workingCatalog.MacroDefinitions.Remove(definition)");
        Assert.DoesNotContain("File.Delete", remove);
        Assert.DoesNotContain("Directory.Delete", remove);
        Assert.Contains("AutomationSettings.RemoveMacroResult", remove);
    }

    [Fact]
    public void CatalogCommit_CleansOnlyPayloadsAbsentFromResultingCatalog()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "MainWindow.Templates.cs"));
        var settingsFlow = Slice(
            source,
            "private async Task SessionAutomationSettingsButtonClickAsync(",
            "private async Task RunTemplateButtonClickAsync(");
        AssertInOrder(
            settingsFlow,
            "if (!TrySaveSessionTemplateCatalog(updated))",
            "return;",
            "CleanupRemovedMacroArtifacts(catalog, _sessionTemplateCatalog!)");

        var cleanup = Slice(
            source,
            "private void CleanupRemovedMacroArtifacts(",
            "private async void SaveTemplateButtonClick(");
        Assert.Contains(
            "FindNewlyUnreferencedPayloads(",
            cleanup,
            StringComparison.Ordinal);
        Assert.Contains(
            "resultingCatalog.MacroDefinitions",
            cleanup,
            StringComparison.Ordinal);
        Assert.Contains(
            "TryDeleteExactBytesIfUnreferenced(",
            cleanup,
            StringComparison.Ordinal);
        Assert.Contains(
            "Removed macro payload cleanup failed",
            cleanup,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RecordingFailure_CleansOnlyANewUncommittedPayload()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "MainWindow.Templates.cs"));
        var recording = Slice(
            source,
            "private async Task RecordMacroButtonClickAsync(",
            "private void CleanupNewMacroAfterFailedCatalogSave(");
        Assert.Contains("SaveWithResult(", recording);
        AssertInOrder(
            recording,
            "if (!TrySaveSessionTemplateCatalog(updated))",
            "return;",
            "macroCatalogWasCommitted = true;");
        Assert.Contains(
            "{ PayloadCreated: true, Definition: { } definition }",
            recording,
            StringComparison.Ordinal);
        Assert.Contains(
            "!macroCatalogWasCommitted",
            recording,
            StringComparison.Ordinal);
        Assert.Contains(
            "CleanupNewMacroAfterFailedCatalogSave(definition)",
            recording,
            StringComparison.Ordinal);

        var cleanup = Slice(
            source,
            "private void CleanupNewMacroAfterFailedCatalogSave(",
            "private void CleanupRemovedMacroArtifacts(");
        Assert.Contains(
            "catalogRead.Catalog.MacroDefinitions",
            cleanup,
            StringComparison.Ordinal);
        Assert.Contains("if (!catalogRead.IsValid)", cleanup);
        Assert.Contains(
            "TryDeleteExactBytesIfUnreferenced(",
            cleanup,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MacroLibrary_ExposesSelectionRenameRemoveAndRecordControls()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "SessionAutomationSettingsDialog.xaml"));

        Assert.Contains("MacroListBox_SelectionChanged", xaml);
        Assert.Contains("x:Name=\"RenameMacroButton\"", xaml);
        Assert.Contains("x:Name=\"RemoveMacroButton\"", xaml);
        Assert.Contains("x:Name=\"RecordMacroButton\"", xaml);
    }

    private static string ReadDialogSource() => File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        "SessionDock",
        "SessionAutomationSettingsDialog.xaml.cs"));

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Could not find source marker: {start}");
        var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Could not find source marker: {end}");
        return source[startIndex..endIndex];
    }

    private static void AssertInOrder(string source, params string[] values)
    {
        var offset = 0;
        foreach (var value in values)
        {
            var index = source.IndexOf(value, offset, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Could not find ordered marker: {value}");
            offset = index + value.Length;
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SessionDock.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root.");
    }
}
