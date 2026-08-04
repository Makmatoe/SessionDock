using System.Security.Cryptography;
using SessionDock.ExactWheel;
using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class ExactWheelMacroStoreTests
{
    private static readonly DateTimeOffset RecordedAt =
        new(2026, 8, 3, 10, 20, 30, TimeSpan.FromHours(2));

    [Fact]
    public void SaveAndLoad_SemanticContentAddressedMacro_RoundTrips()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);
        var recording = ExactWheelTestData.Recording();

        var definition = store.Save(
            "  My   client\tmacro  ",
            SessionMacroKind.Client,
            recording,
            "account_1");
        var loaded = store.Load(definition);
        var path = System.IO.Path.Combine(
            directory.Path,
            "Macros",
            definition.SafeFileName);
        var expectedHash = Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(path)));

        Assert.Equal("My client macro", definition.Name);
        Assert.Equal(SessionMacroKind.Client, definition.Kind);
        Assert.Equal("account_1", definition.RecordedAccountKey);
        Assert.Equal(500, definition.DurationMilliseconds);
        Assert.Equal(recording.Events.Count, definition.EventCount);
        Assert.Equal(expectedHash, definition.Sha256);
        Assert.Equal(
            "ew-client-" + expectedHash.ToLowerInvariant(),
            definition.ContentId);
        Assert.Equal(expectedHash.ToLowerInvariant() + ".ewmacro", definition.SafeFileName);
        Assert.Equal(RecordedAt.ToUniversalTime(), definition.RecordedAtUtc);
        Assert.Equal(recording.Events, loaded.Events);
        Assert.Empty(Directory.EnumerateFiles(
            System.IO.Path.Combine(directory.Path, "Macros"),
            "*.tmp"));
    }

    [Fact]
    public void SaveWithResult_ReportsOnlyTheFirstPhysicalPayloadAsCreated()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);
        var recording = ExactWheelTestData.Recording();

        var first = store.SaveWithResult(
            "First",
            SessionMacroKind.Client,
            recording,
            "account_1");
        var deduplicated = store.SaveWithResult(
            "Renamed metadata",
            SessionMacroKind.Client,
            recording,
            "account_2");

        Assert.True(first.PayloadCreated);
        Assert.False(deduplicated.PayloadCreated);
        Assert.Equal(
            first.Definition.SafeFileName,
            deduplicated.Definition.SafeFileName);
        Assert.Single(Directory.EnumerateFiles(
            System.IO.Path.Combine(directory.Path, "Macros"),
            "*.ewmacro"));
    }

    [Fact]
    public void ReadAndSaveExactBytes_PortablePayload_RoundTripsWithoutRewrite()
    {
        using var sourceDirectory = new TemporaryDirectory();
        var sourceStore = CreateStore(sourceDirectory.Path);
        var definition = sourceStore.Save(
            "Portable macro",
            SessionMacroKind.Client,
            ExactWheelTestData.Recording(),
            "account_1");

        var exactBytes = sourceStore.ReadExactBytes(definition);
        using var destinationDirectory = new TemporaryDirectory();
        var destinationStore = CreateStore(destinationDirectory.Path);
        destinationStore.SaveExactBytes(definition, exactBytes);

        Assert.Equal(exactBytes, destinationStore.ReadExactBytes(definition));
        Assert.Equal(
            definition.Sha256,
            Convert.ToHexString(SHA256.HashData(exactBytes)));
    }

    [Fact]
    public void SaveExactBytes_TamperedPortablePayload_IsRejectedWithoutWriting()
    {
        using var sourceDirectory = new TemporaryDirectory();
        var sourceStore = CreateStore(sourceDirectory.Path);
        var definition = sourceStore.Save(
            "Portable macro",
            SessionMacroKind.WholeLayout,
            ExactWheelTestData.Recording());
        var tampered = sourceStore.ReadExactBytes(definition);
        tampered[^1] ^= 1;

        using var destinationDirectory = new TemporaryDirectory();
        var destinationStore = CreateStore(destinationDirectory.Path);
        Assert.Throws<InvalidDataException>(() =>
            destinationStore.SaveExactBytes(definition, tampered));

        Assert.False(Directory.Exists(System.IO.Path.Combine(
            destinationDirectory.Path,
            "Macros")));
    }

    [Fact]
    public void SaveExactBytes_ExistingVerifiedPayload_IsIdempotent()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);
        var definition = store.Save(
            "Existing",
            SessionMacroKind.Client,
            ExactWheelTestData.Recording());
        var bytes = store.ReadExactBytes(definition);

        var created = store.SaveExactBytes(definition, bytes);

        Assert.False(created);
        Assert.Equal(bytes, store.ReadExactBytes(definition));
    }

    [Fact]
    public void SaveExactBytes_ExistingWrongPayload_IsRejectedWithoutOverwrite()
    {
        using var sourceDirectory = new TemporaryDirectory();
        var sourceStore = CreateStore(sourceDirectory.Path);
        var expected = sourceStore.Save(
            "Expected",
            SessionMacroKind.Client,
            ExactWheelTestData.Recording());
        var expectedBytes = sourceStore.ReadExactBytes(expected);

        using var otherDirectory = new TemporaryDirectory();
        var otherStore = CreateStore(otherDirectory.Path);
        var otherRecording = ExactWheelTestData.Recording(
            durationMicroseconds: 600_000);
        var other = otherStore.Save(
            "Other",
            SessionMacroKind.Client,
            otherRecording);
        var wrongBytes = otherStore.ReadExactBytes(other);

        using var destinationDirectory = new TemporaryDirectory();
        var macrosDirectory = System.IO.Path.Combine(
            destinationDirectory.Path,
            "Macros");
        Directory.CreateDirectory(macrosDirectory);
        var destinationPath = System.IO.Path.Combine(
            macrosDirectory,
            expected.SafeFileName);
        File.WriteAllBytes(destinationPath, wrongBytes);
        var destinationStore = CreateStore(destinationDirectory.Path);

        Assert.Throws<InvalidDataException>(() =>
            destinationStore.SaveExactBytes(expected, expectedBytes));
        Assert.Equal(wrongBytes, File.ReadAllBytes(destinationPath));
        Assert.False(destinationStore.TryDeleteExactBytes(expected));
        Assert.Equal(wrongBytes, File.ReadAllBytes(destinationPath));
    }

    [Fact]
    public void TryDeleteExactBytes_VerifiedPortablePayload_DeletesOnlyMatch()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);
        var definition = store.Save(
            "Rollback",
            SessionMacroKind.WholeLayout,
            ExactWheelTestData.Recording());
        var path = System.IO.Path.Combine(
            directory.Path,
            "Macros",
            definition.SafeFileName);

        Assert.True(store.TryDeleteExactBytes(definition));
        Assert.False(File.Exists(path));
        Assert.True(store.TryDeleteExactBytes(definition));
    }

    [Fact]
    public void ReferenceSafeCleanup_SharedPayloadIsRetainedUntilLastDefinition()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);
        var recording = ExactWheelTestData.Recording();
        var client = store.Save(
            "Client",
            SessionMacroKind.Client,
            recording,
            "account_1");
        var wholeLayout = store.Save(
            "Whole layout",
            SessionMacroKind.WholeLayout,
            recording);
        var path = System.IO.Path.Combine(
            directory.Path,
            "Macros",
            client.SafeFileName);

        Assert.Empty(ExactWheelMacroStore.FindNewlyUnreferencedPayloads(
            [client, wholeLayout],
            [wholeLayout]));
        Assert.Equal(
            MacroArtifactCleanupResult.Retained,
            store.TryDeleteExactBytesIfUnreferenced(client, [wholeLayout]));
        Assert.True(File.Exists(path));

        var candidates =
            ExactWheelMacroStore.FindNewlyUnreferencedPayloads(
                [client, wholeLayout],
                []);
        var candidate = Assert.Single(candidates);
        Assert.Equal(client.SafeFileName, candidate.SafeFileName);
        Assert.Equal(
            MacroArtifactCleanupResult.DeletedOrMissing,
            store.TryDeleteExactBytesIfUnreferenced(candidate, []));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ReferenceSafeCleanup_VerificationFailureRetainsChangedPayload()
    {
        using var expectedDirectory = new TemporaryDirectory();
        var expectedStore = CreateStore(expectedDirectory.Path);
        var definition = expectedStore.Save(
            "Expected",
            SessionMacroKind.Client,
            ExactWheelTestData.Recording());

        using var changedDirectory = new TemporaryDirectory();
        var changedStore = CreateStore(changedDirectory.Path);
        var changed = changedStore.Save(
            "Changed",
            SessionMacroKind.Client,
            ExactWheelTestData.Recording(durationMicroseconds: 600_000));
        var changedBytes = changedStore.ReadExactBytes(changed);
        var macrosDirectory = System.IO.Path.Combine(
            expectedDirectory.Path,
            "Macros");
        var expectedPath = System.IO.Path.Combine(
            macrosDirectory,
            definition.SafeFileName);
        File.WriteAllBytes(expectedPath, changedBytes);

        Assert.Equal(
            MacroArtifactCleanupResult.Failed,
            expectedStore.TryDeleteExactBytesIfUnreferenced(definition, []));
        Assert.Equal(changedBytes, File.ReadAllBytes(expectedPath));
    }

    [Fact]
    public void Save_IdenticalContentWithSameKind_DeduplicatesDefinitionAndFile()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);
        var recording = ExactWheelTestData.Recording();

        var first = store.Save("First", SessionMacroKind.Client, recording);
        var second = store.Save("Second", SessionMacroKind.Client, recording);

        Assert.Equal(first.ContentId, second.ContentId);
        Assert.Equal(first.SafeFileName, second.SafeFileName);
        Assert.Equal("First", first.Name);
        Assert.Equal("Second", second.Name);
        Assert.Single(Directory.EnumerateFiles(
            System.IO.Path.Combine(directory.Path, "Macros"),
            "*.ewmacro"));
    }

    [Fact]
    public void Save_IdenticalContentWithDifferentKinds_UsesDistinctDefinitions()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);
        var recording = ExactWheelTestData.Recording();

        var client = store.Save(
            "Client",
            SessionMacroKind.Client,
            recording,
            "account_1");
        var wholeLayout = store.Save(
            "Whole layout",
            SessionMacroKind.WholeLayout,
            recording);

        Assert.NotEqual(client.ContentId, wholeLayout.ContentId);
        Assert.Equal(
            "ew-client-" + client.Sha256.ToLowerInvariant(),
            client.ContentId);
        Assert.Equal(
            "ew-whole-layout-" + wholeLayout.Sha256.ToLowerInvariant(),
            wholeLayout.ContentId);
        Assert.Equal(client.SafeFileName, wholeLayout.SafeFileName);
        Assert.Equal(client.Sha256, wholeLayout.Sha256);
        Assert.Single(Directory.EnumerateFiles(
            System.IO.Path.Combine(directory.Path, "Macros"),
            "*.ewmacro"));
        Assert.Equal(recording.Events, store.Load(client).Events);
        Assert.Equal(recording.Events, store.Load(wholeLayout).Events);
    }

    [Theory]
    [InlineData(SessionMacroKind.Client)]
    [InlineData(SessionMacroKind.WholeLayout)]
    public void Load_LegacyByteOnlyDefinitionId_RemainsSupported(
        SessionMacroKind kind)
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);
        var recording = ExactWheelTestData.Recording();
        var definition = store.Save(
            "Legacy catalog entry",
            kind,
            recording,
            kind == SessionMacroKind.Client ? "account_1" : null);
        definition.ContentId = "ew-" + definition.Sha256.ToLowerInvariant();

        var loaded = store.Load(definition);

        Assert.Equal(recording.Events, loaded.Events);
    }

    [Fact]
    public void Load_ContentTampering_IsRejectedBeforeDeserialization()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);
        var definition = store.Save(
            "Macro",
            SessionMacroKind.Client,
            ExactWheelTestData.Recording());
        var path = System.IO.Path.Combine(
            directory.Path,
            "Macros",
            definition.SafeFileName);
        var bytes = File.ReadAllBytes(path);
        bytes[36] ^= 1;
        File.WriteAllBytes(path, bytes);

        var exception = Assert.Throws<InvalidDataException>(() =>
            store.Load(definition));

        Assert.Contains("hash", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_CatalogMetadataMismatch_IsRejected()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);
        var definition = store.Save(
            "Macro",
            SessionMacroKind.Client,
            ExactWheelTestData.Recording());
        definition.EventCount++;

        var exception = Assert.Throws<InvalidDataException>(() =>
            store.Load(definition));

        Assert.Contains("metadata", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../outside.ewmacro")]
    [InlineData("subdir/macro.ewmacro")]
    [InlineData("macro.txt")]
    [InlineData("")]
    public void Load_UnsafeOrNonContentAddressedName_IsRejected(string fileName)
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory.Path);
        var definition = store.Save(
            "Macro",
            SessionMacroKind.Client,
            ExactWheelTestData.Recording());
        definition.SafeFileName = fileName;

        Assert.Throws<InvalidDataException>(() => store.Load(definition));
    }

    [Fact]
    public void Save_ReparsePointMacrosDirectory_IsRejected()
    {
        using var directory = new TemporaryDirectory();
        var templateStore = new SessionTemplateStore(directory.Path);
        var macrosPath = System.IO.Path.Combine(directory.Path, "Macros");
        var store = new ExactWheelMacroStore(
            templateStore,
            path => path.Equals(macrosPath, StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.Directory | FileAttributes.ReparsePoint
                : File.GetAttributes(path));

        Assert.Throws<IOException>(() => store.Save(
            "Macro",
            SessionMacroKind.Client,
            ExactWheelTestData.Recording()));
    }

    [Fact]
    public void Save_ReparsePointDestination_IsRejected()
    {
        using var directory = new TemporaryDirectory();
        var recording = ExactWheelTestData.Recording();
        var serialized = ExactWheelMacroSerializer.Serialize(recording);
        var fileName = Convert.ToHexString(SHA256.HashData(serialized))
            .ToLowerInvariant() + ".ewmacro";
        var store = new ExactWheelMacroStore(
            new SessionTemplateStore(directory.Path),
            path => path.EndsWith(fileName, StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.ReparsePoint
                : File.GetAttributes(path));

        Assert.Throws<IOException>(() => store.Save(
            "Macro",
            SessionMacroKind.Client,
            recording));
    }

    [Fact]
    public void Load_ReparsePointMacroFile_IsRejectedBeforeRead()
    {
        using var directory = new TemporaryDirectory();
        var ordinaryStore = CreateStore(directory.Path);
        var definition = ordinaryStore.Save(
            "Macro",
            SessionMacroKind.Client,
            ExactWheelTestData.Recording());
        var macroPath = System.IO.Path.Combine(
            directory.Path,
            "Macros",
            definition.SafeFileName);
        var reparseAwareStore = new ExactWheelMacroStore(
            new SessionTemplateStore(directory.Path),
            path => path.Equals(macroPath, StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.ReparsePoint
                : File.GetAttributes(path));

        Assert.Throws<IOException>(() => reparseAwareStore.Load(definition));
    }

    [Fact]
    public void ImportLegacyWholeLayout_IsExplicitAndDefaultsToWholeLayout()
    {
        using var directory = new TemporaryDirectory();
        var source = System.IO.Path.Combine(directory.Path, "legacy.ewmacro");
        var recording = ExactWheelTestData.Recording();
        ExactWheelMacroSerializer.SaveAtomic(source, recording);
        var store = CreateStore(directory.Path);

        var definition = store.ImportLegacyWholeLayout(source, "Legacy macro");

        Assert.Equal(SessionMacroKind.WholeLayout, definition.Kind);
        Assert.Null(definition.RecordedAccountKey);
        Assert.Equal(recording.Events, store.Load(definition).Events);
    }

    [Fact]
    public void ImportLegacyWholeLayout_NonMacroExtension_IsRejected()
    {
        using var directory = new TemporaryDirectory();
        var source = System.IO.Path.Combine(directory.Path, "legacy.bin");
        File.WriteAllBytes(
            source,
            ExactWheelMacroSerializer.Serialize(ExactWheelTestData.Recording()));
        var store = CreateStore(directory.Path);

        Assert.Throws<InvalidDataException>(() =>
            store.ImportLegacyWholeLayout(source, "Legacy macro"));
    }

    [Fact]
    public void Load_TruncatedContentAddressedFile_IsRejectedBySizeBoundary()
    {
        using var directory = new TemporaryDirectory();
        var macros = System.IO.Path.Combine(directory.Path, "Macros");
        Directory.CreateDirectory(macros);
        var hash = new string('0', 64);
        var definition = new MacroDefinition
        {
            ContentId = "ew-" + hash,
            SafeFileName = hash + ".ewmacro",
            Name = "Broken",
            Kind = SessionMacroKind.WholeLayout,
            Sha256 = hash,
            EventCount = 0,
            DurationMilliseconds = 0
        };
        File.WriteAllBytes(
            System.IO.Path.Combine(macros, definition.SafeFileName),
            [1, 2, 3]);
        var store = CreateStore(directory.Path);

        Assert.Throws<InvalidDataException>(() => store.Load(definition));
    }

    [Fact]
    public void Load_OversizedContentAddressedFile_IsRejectedBeforeRead()
    {
        using var directory = new TemporaryDirectory();
        var macros = System.IO.Path.Combine(directory.Path, "Macros");
        Directory.CreateDirectory(macros);
        var hash = new string('0', 64);
        var definition = new MacroDefinition
        {
            ContentId = "ew-" + hash,
            SafeFileName = hash + ".ewmacro",
            Name = "Oversized",
            Kind = SessionMacroKind.WholeLayout,
            Sha256 = hash,
            EventCount = 0,
            DurationMilliseconds = 0
        };
        var path = System.IO.Path.Combine(
            macros,
            definition.SafeFileName);
        using (var stream = new FileStream(
                   path,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.SetLength(ExactWheelLimits.MaximumMacroFileBytes + 1);
        }
        var store = CreateStore(directory.Path);

        var exception = Assert.Throws<InvalidDataException>(() =>
            store.Load(definition));

        Assert.Contains(
            "size boundary",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private static ExactWheelMacroStore CreateStore(string root) =>
        new(
            new SessionTemplateStore(root),
            getAttributes: null,
            utcNow: static () => RecordedAt);

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"SessionDock.ExactWheelMacroStore.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
