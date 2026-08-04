using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class SessionMacroLibraryPolicyTests
{
    [Theory]
    [InlineData("  Daily   harvest  ", "Daily harvest")]
    [InlineData("Farming\tloop", "Farming loop")]
    public void TryNormalizeName_ReturnsBoundedDisplayName(
        string source,
        string expected)
    {
        Assert.True(SessionMacroLibraryPolicy.TryNormalizeName(
            source,
            out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryNormalizeName_RejectsEmptyName(string? source)
    {
        Assert.False(SessionMacroLibraryPolicy.TryNormalizeName(
            source,
            out _));
    }

    [Fact]
    public void TryNormalizeName_RejectsOverlongNameInsteadOfTruncating()
    {
        Assert.False(SessionMacroLibraryPolicy.TryNormalizeName(
            new string('A', SessionTemplatePolicy.MaximumNameLength + 1),
            out _));
    }

    [Fact]
    public void FindReferences_FindsPerClientSharedAndWholeLayoutUse()
    {
        var catalog = new SessionTemplateCatalog
        {
            Templates =
            [
                new SessionTemplate
                {
                    Id = "per-client",
                    Name = "Per client template",
                    MacroMode = SessionTemplateMacroMode.PerClient,
                    ClientSlots =
                    [
                        new SessionTemplateClientSlot
                        {
                            AccountKey = "account-a",
                            PerClientMacroId = "MACRO-ONE"
                        },
                        new SessionTemplateClientSlot
                        {
                            AccountKey = "account-b",
                            PerClientMacroId = "macro-one"
                        }
                    ]
                },
                new SessionTemplate
                {
                    Id = "shared",
                    Name = "Shared template",
                    MacroMode = SessionTemplateMacroMode.Shared,
                    SharedMacroId = "macro-one"
                },
                new SessionTemplate
                {
                    Id = "whole",
                    Name = "Whole template",
                    MacroMode = SessionTemplateMacroMode.WholeLayout,
                    WholeLayoutMacroId = "macro-one"
                },
                new SessionTemplate
                {
                    Id = "unrelated",
                    Name = "Unrelated template",
                    SharedMacroId = "other-macro"
                }
            ]
        };

        var references = SessionMacroLibraryPolicy.FindReferences(
            catalog,
            "macro-one");

        Assert.Equal(4, references.Count);
        Assert.Equal(
            2,
            references.Count(reference =>
                reference.UsageMode == SessionTemplateMacroMode.PerClient));
        Assert.Single(references, reference =>
            reference.UsageMode == SessionTemplateMacroMode.Shared);
        Assert.Single(references, reference =>
            reference.UsageMode == SessionTemplateMacroMode.WholeLayout);
        Assert.DoesNotContain(references, reference =>
            reference.TemplateId == "unrelated");
    }

    [Fact]
    public void FindReferences_ReturnsEmptyForUnreferencedMacro()
    {
        var catalog = new SessionTemplateCatalog
        {
            Templates =
            [
                new SessionTemplate
                {
                    Id = "template",
                    SharedMacroId = "other"
                }
            ]
        };

        Assert.Empty(SessionMacroLibraryPolicy.FindReferences(
            catalog,
            "unreferenced"));
    }
}
