using System.Text.Json;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class OnboardingStateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"SessionDock.Onboarding.{Guid.NewGuid():N}");

    [Fact]
    public void Read_MissingStateReturnsBothVersionsZeroWithoutCreatingRoot()
    {
        var store = new OnboardingStateStore(_root);

        var result = store.Read();

        Assert.Equal(OnboardingState.Default, result.State);
        Assert.Equal(0, result.State.GetStartedTutorialVersion);
        Assert.Equal(0, result.State.AdvancedTutorialVersion);
        Assert.False(result.Exists);
        Assert.True(result.IsValid);
        Assert.False(result.RequiresMigration);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void WriteThenRead_RoundTripsIndependentVersionsAndAtomicUpdates()
    {
        var store = new OnboardingStateStore(_root);

        store.Write(new OnboardingState(1, 4));
        Assert.Equal(
            new OnboardingState(1, 4),
            store.Read().State);

        store.Write(new OnboardingState(3, 7));
        var updated = store.Read();

        Assert.True(updated.Exists);
        Assert.True(updated.IsValid);
        Assert.False(updated.RequiresMigration);
        Assert.Equal(3, updated.State.GetStartedTutorialVersion);
        Assert.Equal(7, updated.State.AdvancedTutorialVersion);
        Assert.Equal(3, updated.State.CompletedTutorialVersion);
        using var document = JsonDocument.Parse(File.ReadAllBytes(StatePath));
        var root = document.RootElement;
        Assert.Equal(3, root.GetPropertyCount());
        Assert.Equal(
            OnboardingStateStore.SchemaVersion,
            root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            3,
            root.GetProperty(
                "completedGetStartedTutorialVersion").GetInt32());
        Assert.Equal(
            7,
            root.GetProperty(
                "completedAdvancedTutorialVersion").GetInt32());
        Assert.False(root.TryGetProperty(
            "completedTutorialVersion",
            out _));
        Assert.EndsWith(
            "\n",
            File.ReadAllText(StatePath),
            StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

    [Fact]
    public void Read_SchemaOneMigratesIntoGetStartedWithoutMutatingFile()
    {
        Directory.CreateDirectory(_root);
        const string legacy =
            """{"schemaVersion":1,"completedTutorialVersion":6}""";
        File.WriteAllText(StatePath, legacy);
        var store = new OnboardingStateStore(_root);

        var migrated = store.Read();

        Assert.True(migrated.Exists);
        Assert.True(migrated.IsValid);
        Assert.True(migrated.RequiresMigration);
        Assert.Equal(6, migrated.State.GetStartedTutorialVersion);
        Assert.Equal(0, migrated.State.AdvancedTutorialVersion);
        Assert.Equal(legacy, File.ReadAllText(StatePath));

        store.Write(migrated.State);
        var rewritten = store.Read();

        Assert.True(rewritten.IsValid);
        Assert.False(rewritten.RequiresMigration);
        Assert.Equal(new OnboardingState(6, 0), rewritten.State);
        Assert.Contains(
            "\"schemaVersion\": 2",
            File.ReadAllText(StatePath),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"completedTutorialVersion\"",
            File.ReadAllText(StatePath),
            StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyConstructor_PreservesGetStartedCompatibilityOnly()
    {
        var state = new OnboardingState(9);

        Assert.Equal(9, state.GetStartedTutorialVersion);
        Assert.Equal(0, state.AdvancedTutorialVersion);
        Assert.Equal(9, state.CompletedTutorialVersion);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(
        OnboardingStateStore.MaximumTutorialVersion,
        OnboardingStateStore.MaximumTutorialVersion)]
    public void WriteThenRead_AcceptsInclusiveVersionBoundaries(
        int getStartedVersion,
        int advancedVersion)
    {
        var store = new OnboardingStateStore(_root);
        var expected = new OnboardingState(
            getStartedVersion,
            advancedVersion);

        store.Write(expected);

        var result = store.Read();
        Assert.True(result.IsValid);
        Assert.False(result.RequiresMigration);
        Assert.Equal(expected, result.State);
    }

    [Fact]
    public void Read_CorruptDuplicateInvalidUtf8AndOversizedStateFailsClosed()
    {
        Directory.CreateDirectory(_root);
        var store = new OnboardingStateStore(_root);
        File.WriteAllText(StatePath, "{not-json");
        AssertInvalid(store.Read());

        File.WriteAllText(
            StatePath,
            """{"schemaVersion":2,"schemaVersion":2,"completedGetStartedTutorialVersion":1,"completedAdvancedTutorialVersion":0}""");
        AssertInvalid(store.Read());

        File.WriteAllBytes(StatePath, [0xFF]);
        AssertInvalid(store.Read());

        File.WriteAllBytes(
            StatePath,
            new byte[OnboardingStateStore.MaximumBytes + 1]);
        AssertInvalid(store.Read());
    }

    [Theory]
    [MemberData(nameof(InvalidDocuments))]
    public void Read_NonCanonicalOrOutOfRangeShapeFailsClosed(string contents)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(StatePath, contents);

        AssertInvalid(new OnboardingStateStore(_root).Read());
    }

    [Fact]
    public void ReadAndWrite_RejectInjectedReparseRoot()
    {
        Directory.CreateDirectory(_root);
        var redirectedStore = new OnboardingStateStore(
            _root,
            path => File.GetAttributes(path) | FileAttributes.ReparsePoint);

        AssertInvalid(redirectedStore.Read());
        Assert.Throws<IOException>(() =>
            redirectedStore.Write(new OnboardingState(1, 2)));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(OnboardingStateStore.MaximumTutorialVersion + 1, 0)]
    [InlineData(0, OnboardingStateStore.MaximumTutorialVersion + 1)]
    public void Write_RejectsEitherOutOfRangeTutorialVersion(
        int getStartedVersion,
        int advancedVersion)
    {
        var store = new OnboardingStateStore(_root);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.Write(new OnboardingState(
                getStartedVersion,
                advancedVersion)));
        Assert.False(Directory.Exists(_root));
    }

    public static IEnumerable<object[]> InvalidDocuments()
    {
        yield return [string.Empty];
        yield return ["[]"];
        yield return
        [
            """{"schemaVersion":2,"completedGetStartedTutorialVersion":1}"""
        ];
        yield return
        [
            """{"schemaVersion":2,"completedGetStartedTutorialVersion":1,"completedAdvancedTutorialVersion":0,"extra":0}"""
        ];
        yield return
        [
            """{"schemaVersion":2,"completedTutorialVersion":1,"completedAdvancedTutorialVersion":0}"""
        ];
        yield return
        [
            """{"schemaVersion":1,"completedTutorialVersion":1,"completedAdvancedTutorialVersion":0}"""
        ];
        yield return
        [
            """{"schemaVersion":1,"completedTutorialVersion":1000001}"""
        ];
        yield return
        [
            """{"schemaVersion":3,"completedGetStartedTutorialVersion":1,"completedAdvancedTutorialVersion":0}"""
        ];
        yield return
        [
            """{"schemaVersion":"2","completedGetStartedTutorialVersion":1,"completedAdvancedTutorialVersion":0}"""
        ];
        yield return
        [
            """{"schemaVersion":2,"completedGetStartedTutorialVersion":1.5,"completedAdvancedTutorialVersion":0}"""
        ];
        yield return
        [
            """{"schemaVersion":2,"completedGetStartedTutorialVersion":-1,"completedAdvancedTutorialVersion":0}"""
        ];
        yield return
        [
            """{"schemaVersion":2,"completedGetStartedTutorialVersion":0,"completedAdvancedTutorialVersion":1000001}"""
        ];
        yield return
        [
            """{"schemaVersion":2,"completedGetStartedTutorialVersion":null,"completedAdvancedTutorialVersion":0}"""
        ];
        yield return
        [
            """{"schemaVersion":2,"CompletedGetStartedTutorialVersion":1,"completedAdvancedTutorialVersion":0}"""
        ];
        yield return
        [
            """{"schemaVersion":2,"completedGetStartedTutorialVersion":1,"completedAdvancedTutorialVersion":0,"nested":{"too":{"deep":true}}}"""
        ];
    }

    private string StatePath => Path.Combine(
        _root,
        OnboardingStateStore.FileName);

    private static void AssertInvalid(OnboardingStateReadResult result)
    {
        Assert.True(result.Exists);
        Assert.False(result.IsValid);
        Assert.False(result.RequiresMigration);
        Assert.Equal(OnboardingState.Default, result.State);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
