using SessionDock.Models;

namespace SessionDock.Tests;

public sealed class TemplateEditorSharedMacroTests
{
    [Fact]
    public void CreateSharedMacroTargetSelection_PersistsOnlyCheckedClientsInOrder()
    {
        var selected = TemplateEditorDialog.CreateSharedMacroTargetSelection(
        [
            ("first-account", true),
            ("second-account", false),
            ("third-account", true)
        ]);

        Assert.Equal(["first-account", "third-account"], selected);
    }

    [Fact]
    public void SelectClientMacroPlaybackSlots_SharedModeRunsOnlySelectedClients()
    {
        var template = new SessionTemplate
        {
            MacroMode = SessionTemplateMacroMode.Shared,
            SharedMacroId = "shared-macro",
            SharedMacroAccountKeys = ["first-account", "third-account"],
            ClientSlots =
            [
                Slot("first-account", 0),
                Slot("second-account", 1),
                Slot("third-account", 2)
            ]
        };

        var shared = MainWindow.SelectClientMacroPlaybackSlots(
            template,
            template.SharedMacroId);
        var perClient = MainWindow.SelectClientMacroPlaybackSlots(
            template,
            sharedMacroId: null);

        Assert.Equal(
            ["first-account", "third-account"],
            shared.Select(slot => slot.AccountKey));
        Assert.Equal(
            ["first-account", "second-account", "third-account"],
            perClient.Select(slot => slot.AccountKey));
    }

    private static SessionTemplateClientSlot Slot(
        string accountKey,
        int order) => new()
        {
            SlotId = $"slot-{order}",
            AccountKey = accountKey,
            Order = order
        };
}
