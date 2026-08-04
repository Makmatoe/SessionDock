namespace SessionDock.Tests;

public sealed class SessionAutomationSettingsCommitTests
{
    [Fact]
    public void ActionButtons_UseTheSameValidatedCatalogCommitAsSave()
    {
        var source = ReadProductionFile(
            "SessionDock",
            "SessionAutomationSettingsDialog.xaml.cs");
        var recordAction = Slice(
            source,
            "private void RecordMacroButton_Click",
            "private void SaveCurrentSessionButton_Click");
        var saveCurrentAction = Slice(
            source,
            "private void SaveCurrentSessionButton_Click",
            "private void MacroListBox_SelectionChanged");
        var saveAction = Slice(
            source,
            "private void SaveButton_Click",
            "private void CompleteDialog");
        var completion = Slice(
            source,
            "private void CompleteDialog",
            "private SessionTemplateCatalog? TryCreateValidatedCatalog");
        var validation = Slice(
            source,
            "private SessionTemplateCatalog? TryCreateValidatedCatalog",
            "private bool TryReadNumber");

        Assert.Contains(
            "CompleteDialog(SessionAutomationSettingsDialogAction.RecordMacro)",
            recordAction,
            StringComparison.Ordinal);
        Assert.Contains(
            "SessionAutomationSettingsDialogAction.SaveCurrentTemplate",
            saveCurrentAction,
            StringComparison.Ordinal);
        Assert.Contains(
            "CompleteDialog(",
            saveCurrentAction,
            StringComparison.Ordinal);
        Assert.Contains(
            "CompleteDialog(SessionAutomationSettingsDialogAction.None)",
            saveAction,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Close();", recordAction, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Close();",
            saveCurrentAction,
            StringComparison.Ordinal);

        var validate = completion.IndexOf(
            "TryCreateValidatedCatalog()",
            StringComparison.Ordinal);
        var updated = completion.IndexOf(
            "UpdatedCatalog = updatedCatalog",
            StringComparison.Ordinal);
        var requested = completion.IndexOf(
            "RequestedAction = requestedAction",
            StringComparison.Ordinal);
        var accepted = completion.IndexOf(
            "DialogResult = true",
            StringComparison.Ordinal);
        Assert.True(validate >= 0);
        Assert.True(updated > validate);
        Assert.True(requested > updated);
        Assert.True(accepted > requested);
        Assert.Contains("return null;", validation, StringComparison.Ordinal);
        Assert.Contains(
            "SessionTemplatePolicy.Normalize(candidate)",
            validation,
            StringComparison.Ordinal);
        Assert.Contains(
            "MacroRecordingStopHotkey =",
            validation,
            StringComparison.Ordinal);
        var xaml = ReadProductionFile(
            "SessionDock",
            "SessionAutomationSettingsDialog.xaml");
        Assert.Contains("IsCancel=\"True\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_PersistsAcceptedCatalogBeforeDispatchingAction()
    {
        var source = ReadProductionFile(
            "SessionDock",
            "MainWindow.Templates.cs");
        var settingsFlow = Slice(
            source,
            "private async Task SessionAutomationSettingsButtonClickAsync(",
            "private async Task RunTemplateButtonClickAsync(");

        var acceptedGate = settingsFlow.IndexOf(
            "if (!accepted || dialog.UpdatedCatalog is not { } updated)",
            StringComparison.Ordinal);
        var saveGate = settingsFlow.IndexOf(
            "if (!TrySaveSessionTemplateCatalog(updated))",
            StringComparison.Ordinal);
        Assert.True(saveGate >= 0);
        var saveFailureReturn = settingsFlow.IndexOf(
            "return;",
            saveGate,
            StringComparison.Ordinal);
        var recordAction = settingsFlow.IndexOf(
            "SessionAutomationSettingsDialogAction.RecordMacro",
            StringComparison.Ordinal);
        var saveCurrentAction = settingsFlow.IndexOf(
            "SessionAutomationSettingsDialogAction.SaveCurrentTemplate",
            StringComparison.Ordinal);

        Assert.True(acceptedGate >= 0);
        Assert.True(saveGate > acceptedGate);
        Assert.True(saveFailureReturn > saveGate);
        Assert.True(recordAction > saveFailureReturn);
        Assert.True(saveCurrentAction > saveFailureReturn);
        Assert.Contains(
            "await RecordMacroButtonClickAsync(cancellationToken);",
            settingsFlow,
            StringComparison.Ordinal);
        Assert.Contains(
            "await SaveTemplateButtonClickAsync(cancellationToken);",
            settingsFlow,
            StringComparison.Ordinal);
    }

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Missing source marker: {start}");
        var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Missing source marker: {end}");
        return source[startIndex..endIndex];
    }

    private static string ReadProductionFile(params string[] components)
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
                if (!File.Exists(Path.Combine(
                        directory.FullName,
                        "SessionDock.slnx")))
                {
                    continue;
                }

                return File.ReadAllText(Path.Combine(
                    [directory.FullName, .. components]));
            }
        }

        throw new DirectoryNotFoundException(
            "The SessionDock repository root could not be located.");
    }
}
