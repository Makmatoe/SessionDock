using System.Text;
using SessionDock.SystemProcesses;

namespace SessionDock.Tests;

public sealed class HandleScopeSelectionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"SessionDock.HandleScope.Selection.{Guid.NewGuid():N}");

    [Fact]
    public void Read_MissingFileReturnsValidDefaultWithoutCreatingIt()
    {
        var path = Path.Combine(_root, "selection.json");
        var store = new HandleScopeSelectionStore(path);

        var result = store.Read();

        Assert.Equal(HandleScopeSelection.Default, result.Selection);
        Assert.False(result.Exists);
        Assert.True(result.IsValid);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void WriteThenRead_RoundTripsEverySelectionModeAndApiChoice()
    {
        var path = Path.Combine(_root, "selection.json");
        var store = new HandleScopeSelectionStore(path);
        var selections = new[]
        {
            HandleScopeSelection.Default,
            new HandleScopeSelection(
                HandleScopeVersionSelectionMode.KeepInstalled,
                null,
                "v1"),
            new HandleScopeSelection(
                HandleScopeVersionSelectionMode.Exact,
                new Version(0, 1, 4),
                "v2")
        };

        foreach (var selection in selections)
        {
            store.Write(selection);
            var result = store.Read();

            Assert.True(result.Exists);
            Assert.True(result.IsValid);
            Assert.Equal(selection, result.Selection);
            Assert.EndsWith(
                "\n",
                File.ReadAllText(path),
                StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"versionMode":"automatic","exactVersion":null,"apiContract":"automatic","extra":true}""")]
    [InlineData("""{"schemaVersion":1,"schemaVersion":1,"versionMode":"automatic","exactVersion":null,"apiContract":"automatic"}""")]
    [InlineData("""{"schemaVersion":1,"versionMode":"exact","exactVersion":null,"apiContract":"automatic"}""")]
    [InlineData("""{"schemaVersion":1,"versionMode":"automatic","exactVersion":"0.1.4","apiContract":"automatic"}""")]
    [InlineData("""{"schemaVersion":1,"versionMode":"exact","exactVersion":"0.1.4.0","apiContract":"automatic"}""")]
    [InlineData("""{"schemaVersion":1,"versionMode":"Automatic","exactVersion":null,"apiContract":"automatic"}""")]
    [InlineData("""{"schemaVersion":1,"versionMode":"automatic","exactVersion":null,"apiContract":"v3"}""")]
    public void Read_NonCanonicalDocumentFailsClosedToDefault(string json)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "selection.json");
        File.WriteAllText(path, json);
        var store = new HandleScopeSelectionStore(path);

        var result = store.Read();

        Assert.Equal(HandleScopeSelection.Default, result.Selection);
        Assert.True(result.Exists);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Read_InvalidUtf8OrOversizeFailsClosed()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "selection.json");
        var store = new HandleScopeSelectionStore(path);
        File.WriteAllBytes(path, [0xc3, 0x28]);

        var invalidUtf8 = store.Read();

        Assert.True(invalidUtf8.Exists);
        Assert.False(invalidUtf8.IsValid);
        Assert.Equal(HandleScopeSelection.Default, invalidUtf8.Selection);

        File.WriteAllBytes(
            path,
            new byte[HandleScopeSelectionStore.MaximumBytes + 1]);
        var oversized = store.Read();
        Assert.True(oversized.Exists);
        Assert.False(oversized.IsValid);
        Assert.Equal(HandleScopeSelection.Default, oversized.Selection);
    }

    [Fact]
    public void Write_RejectsInconsistentOrNonStableSelection()
    {
        var store = new HandleScopeSelectionStore(
            Path.Combine(_root, "selection.json"));
        var invalidSelections = new[]
        {
            new HandleScopeSelection(
                HandleScopeVersionSelectionMode.Exact,
                null,
                null),
            new HandleScopeSelection(
                HandleScopeVersionSelectionMode.Automatic,
                new Version(0, 1, 4),
                null),
            new HandleScopeSelection(
                HandleScopeVersionSelectionMode.Exact,
                new Version(0, 1, 4, 0),
                null),
            new HandleScopeSelection(
                HandleScopeVersionSelectionMode.KeepInstalled,
                null,
                "v3")
        };

        foreach (var selection in invalidSelections)
            Assert.Throws<ArgumentException>(() => store.Write(selection));
    }

    [Fact]
    public void RuntimeVersionPreference_RecoversLegacyExactPinAndPreservesApiChoice()
    {
        var path = Path.Combine(_root, "selection.json");
        var store = new HandleScopeSelectionStore(path);
        store.Write(new HandleScopeSelection(
            HandleScopeVersionSelectionMode.Exact,
            new Version(0, 2, 2),
            "v1"));

        store.WriteRuntimeVersionPreference(
            HandleScopeVersionSelectionMode.Automatic,
            exactVersion: null,
            reviewedVersions: [new Version(0, 3, 0)]);

        var recovered = store.Read();
        Assert.True(recovered.IsValid);
        Assert.Equal(HandleScopeVersionSelectionMode.Automatic,
            recovered.Selection.VersionMode);
        Assert.Null(recovered.Selection.ExactVersion);
        Assert.Equal("v1", recovered.Selection.ExactApiContract);

        store.WriteRuntimeVersionPreference(
            HandleScopeVersionSelectionMode.Exact,
            new Version(0, 3, 0),
            [new Version(0, 3, 0), new Version(0, 2, 2)]);
        var exact = store.Read();
        Assert.Equal(HandleScopeVersionSelectionMode.Exact,
            exact.Selection.VersionMode);
        Assert.Equal(new Version(0, 3, 0), exact.Selection.ExactVersion);
        Assert.Equal("v1", exact.Selection.ExactApiContract);
    }

    [Fact]
    public void RuntimeVersionPreference_RejectsUnreviewedExactVersion()
    {
        var store = new HandleScopeSelectionStore(
            Path.Combine(_root, "selection.json"));
        store.Write(HandleScopeSelection.Default);

        Assert.Throws<ArgumentException>(() =>
            store.WriteRuntimeVersionPreference(
                HandleScopeVersionSelectionMode.Exact,
                new Version(9, 9, 9),
                [new Version(0, 3, 0)]));

        Assert.Equal(HandleScopeSelection.Default, store.Read().Selection);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
