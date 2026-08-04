using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class UiSoundImportCleanupTests : IDisposable
{
    private readonly string _soundsDirectory = Path.Combine(
        Path.GetTempPath(),
        $"SessionDock-sound-cleanup-{Guid.NewGuid():N}");

    [Fact]
    public void CleanupOrphanedImportedSounds_PreservesReferencedAndUnmanagedFiles()
    {
        Directory.CreateDirectory(_soundsDirectory);
        var referenced = $"startup-custom-{Guid.NewGuid():N}.wav";
        var orphan = $"startup-custom-{Guid.NewGuid():N}.mp3";
        var temporary =
            $"startup-custom-{Guid.NewGuid():N}.m4a.{Guid.NewGuid():N}.tmp";
        var builtIn = "startup-soft-v1.wav";
        WriteFile(referenced);
        WriteFile(orphan);
        WriteFile(temporary);
        WriteFile(builtIn);

        var removed = UiSoundService.CleanupOrphanedImportedSounds(
            _soundsDirectory,
            [referenced],
            reconciliationIsSafe: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, removed);
        Assert.True(File.Exists(Path.Combine(_soundsDirectory, referenced)));
        Assert.False(File.Exists(Path.Combine(_soundsDirectory, orphan)));
        Assert.False(File.Exists(Path.Combine(_soundsDirectory, temporary)));
        Assert.True(File.Exists(Path.Combine(_soundsDirectory, builtIn)));
    }

    [Fact]
    public void CleanupOrphanedImportedSounds_InvalidReferencePreservesNothingManaged()
    {
        Directory.CreateDirectory(_soundsDirectory);
        var orphan = $"startup-custom-{Guid.NewGuid():N}.wma";
        WriteFile(orphan);

        var removed = UiSoundService.CleanupOrphanedImportedSounds(
            _soundsDirectory,
            ["..\\outside.wav"],
            reconciliationIsSafe: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(Path.Combine(_soundsDirectory, orphan)));
    }

    [Fact]
    public void CleanupOrphanedImportedSounds_UncertainRecoveryDeletesNothing()
    {
        Directory.CreateDirectory(_soundsDirectory);
        var possiblyReferenced = $"startup-custom-{Guid.NewGuid():N}.wav";
        var temporary =
            $"startup-custom-{Guid.NewGuid():N}.mp3.{Guid.NewGuid():N}.tmp";
        WriteFile(possiblyReferenced);
        WriteFile(temporary);

        var removed = UiSoundService.CleanupOrphanedImportedSounds(
            _soundsDirectory,
            retainedFileNames: [],
            reconciliationIsSafe: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, removed);
        Assert.True(File.Exists(
            Path.Combine(_soundsDirectory, possiblyReferenced)));
        Assert.True(File.Exists(Path.Combine(_soundsDirectory, temporary)));
    }

    [Fact]
    public void CleanupOrphanedImportedSounds_PreservesPrimaryAndBackupReferences()
    {
        Directory.CreateDirectory(_soundsDirectory);
        var primary = $"startup-custom-{Guid.NewGuid():N}.wav";
        var backup = $"startup-custom-{Guid.NewGuid():N}.mp3";
        var orphan = $"startup-custom-{Guid.NewGuid():N}.m4a";
        var temporary =
            $"startup-custom-{Guid.NewGuid():N}.wma.{Guid.NewGuid():N}.tmp";
        WriteFile(primary);
        WriteFile(backup);
        WriteFile(orphan);
        WriteFile(temporary);

        var removed = UiSoundService.CleanupOrphanedImportedSounds(
            _soundsDirectory,
            [primary, backup],
            reconciliationIsSafe: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, removed);
        Assert.True(File.Exists(Path.Combine(_soundsDirectory, primary)));
        Assert.True(File.Exists(Path.Combine(_soundsDirectory, backup)));
        Assert.False(File.Exists(Path.Combine(_soundsDirectory, orphan)));
        Assert.False(File.Exists(Path.Combine(_soundsDirectory, temporary)));
    }

    [Fact]
    public void CleanupOrphanedImportedSounds_GuardedRecoveryDeletesOnlyOwnedOrphan()
    {
        Directory.CreateDirectory(_soundsDirectory);
        var retainedOwned = $"startup-custom-{Guid.NewGuid():N}.wav";
        var orphanedOwned = $"startup-custom-{Guid.NewGuid():N}.mp3";
        var unknownOrphan = $"startup-custom-{Guid.NewGuid():N}.m4a";
        WriteFile(retainedOwned);
        WriteFile(orphanedOwned);
        WriteFile(unknownOrphan);

        var removed = UiSoundService.CleanupOrphanedImportedSounds(
            _soundsDirectory,
            [retainedOwned],
            reconciliationIsSafe: false,
            cancellationToken: TestContext.Current.CancellationToken,
            ownedFileNames: [retainedOwned, orphanedOwned]);

        Assert.Equal(1, removed);
        Assert.True(File.Exists(Path.Combine(_soundsDirectory, retainedOwned)));
        Assert.False(File.Exists(Path.Combine(_soundsDirectory, orphanedOwned)));
        Assert.True(File.Exists(Path.Combine(_soundsDirectory, unknownOrphan)));
    }

    [Fact]
    public void ReconcileImportedSounds_IncompleteReferencesStillCleansOwnedOrphan()
    {
        Directory.CreateDirectory(_soundsDirectory);
        var retainedOwned = $"startup-custom-{Guid.NewGuid():N}.wav";
        var orphanedOwned = $"startup-custom-{Guid.NewGuid():N}.mp3";
        var unknownOrphan = $"startup-custom-{Guid.NewGuid():N}.m4a";
        WriteFile(retainedOwned);
        WriteFile(orphanedOwned);
        WriteFile(unknownOrphan);
        var retention = new ImportedSoundRetention(
            CanReconcile: false,
            ReferencesAreComplete: false,
            FileNames: new HashSet<string>(
                [retainedOwned],
                StringComparer.OrdinalIgnoreCase));

        var removed = UiSoundService.ReconcileImportedSounds(
            _soundsDirectory,
            retention,
            [retainedOwned, orphanedOwned],
            TestContext.Current.CancellationToken);

        Assert.Equal(1, removed);
        Assert.True(File.Exists(Path.Combine(_soundsDirectory, retainedOwned)));
        Assert.False(File.Exists(Path.Combine(_soundsDirectory, orphanedOwned)));
        Assert.True(File.Exists(Path.Combine(_soundsDirectory, unknownOrphan)));
    }

    [Fact]
    public void SoundImportFailureClassifier_ContainsValidationButNotProgrammerFaults()
    {
        Assert.True(MainWindow.IsExpectedSoundImportFailure(
            new InvalidDataException("audio changed")));
        Assert.True(MainWindow.IsExpectedSoundImportFailure(
            new UnauthorizedAccessException("source locked")));
        Assert.False(MainWindow.IsExpectedSoundImportFailure(
            new InvalidOperationException("programmer fault")));
    }

    [Fact]
    public void ResolveCustomStartupSoundFileName_BuiltInSelectionClearsCustomFile()
    {
        var resolved = UiSoundService.ResolveCustomStartupSoundFileName(
            UiSoundService.StartupSoft,
            importedFileName: null,
            existingFileName: "startup-custom-existing.wav");

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveCustomStartupSoundFileName_CustomSelectionRetainsOrReplacesFile()
    {
        var retained = UiSoundService.ResolveCustomStartupSoundFileName(
            UiSoundService.StartupCustom,
            importedFileName: null,
            existingFileName: "startup-custom-existing.wav");
        var replaced = UiSoundService.ResolveCustomStartupSoundFileName(
            UiSoundService.StartupCustom,
            importedFileName: "startup-custom-new.wav",
            existingFileName: "startup-custom-existing.wav");

        Assert.Equal("startup-custom-existing.wav", retained);
        Assert.Equal("startup-custom-new.wav", replaced);
    }

    public void Dispose()
    {
        if (Directory.Exists(_soundsDirectory))
            Directory.Delete(_soundsDirectory, recursive: true);
    }

    private void WriteFile(string fileName) =>
        File.WriteAllBytes(
            Path.Combine(_soundsDirectory, fileName),
            [1, 2, 3]);
}
