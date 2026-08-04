using System.Diagnostics;
using System.IO;
using System.Windows.Media;

namespace SessionDock.Services;

public sealed class UiSoundService : IDisposable
{
    public const string StartupOff = "off";
    public const string StartupSoft = "soft";
    public const string StartupBright = "bright";
    public const string StartupArcade = "arcade";
    public const string StartupCustom = "custom";
    public const string DefaultStartupSound = StartupSoft;
    private const long MaximumImportedSoundBytes = 25 * 1024 * 1024;
    private static readonly HashSet<string> SupportedImportedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".wav", ".mp3", ".wma", ".m4a"
        };
    private static readonly HashSet<string> StartupSounds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            StartupOff,
            StartupSoft,
            StartupBright,
            StartupArcade,
            StartupCustom
        };
    private readonly string _soundsDirectory;
    private readonly MediaPlayer _uiPlayer = new();
    private readonly MediaPlayer _startupPlayer = new();
    private bool _disposed;

    public UiSoundService()
    {
        _soundsDirectory = Path.Combine(AppDataPaths.RootDirectory, "Sounds");
        _uiPlayer.Volume = 1;
        _startupPlayer.Volume = 1;
        try
        {
            Directory.CreateDirectory(_soundsDirectory);
            ThrowIfReparsePoint(_soundsDirectory);
            OpenPlayer(_uiPlayer, EnsureBuiltInSound("ui-v1", [(940, 34)]));
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Trace.WriteLine($"UI sounds are unavailable: {ex.GetType().Name}.");
        }
    }

    public static bool IsValidStartupSound(string? value) =>
        value is not null && StartupSounds.Contains(value);

    internal static string? ResolveCustomStartupSoundFileName(
        string startupSound,
        string? importedFileName,
        string? existingFileName) =>
        startupSound.Equals(
            StartupCustom,
            StringComparison.OrdinalIgnoreCase)
            ? importedFileName ?? existingFileName
            : null;

    public static bool IsValidImportedFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Path.GetFileName(value).Equals(value, StringComparison.Ordinal))
        {
            return false;
        }

        return SupportedImportedExtensions.Contains(Path.GetExtension(value));
    }

    public void PlayUiClick(bool enabled)
    {
        if (!enabled || _disposed)
            return;
        try
        {
            _uiPlayer.Stop();
            _uiPlayer.Position = TimeSpan.Zero;
            _uiPlayer.Play();
        }
        catch (InvalidOperationException)
        {
            // Optional audio feedback must never interrupt a UI action.
        }
    }

    public void PlayStartup(string preset, string? customFileName)
    {
        if (_disposed || preset.Equals(StartupOff, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            var path = preset.ToLowerInvariant() switch
            {
                StartupSoft => EnsureBuiltInSound(
                    "startup-soft-v1",
                    [(493.88, 130), (0, 35), (659.26, 230)]),
                StartupBright => EnsureBuiltInSound(
                    "startup-bright-v1",
                    [(659.26, 90), (783.99, 90), (1046.50, 210)]),
                StartupArcade => EnsureBuiltInSound(
                    "startup-arcade-v1",
                    [(440, 75), (659.26, 75), (880, 75), (1174.66, 150)]),
                StartupCustom => ResolveImportedSound(customFileName),
                _ => null
            };
            PlayStartupPath(path);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException)
        {
            Trace.WriteLine($"Startup sound was not played: {ex.GetType().Name}.");
        }
    }

    public void Preview(string preset, string? customFileName, string? pendingSourcePath)
    {
        if (preset.Equals(StartupCustom, StringComparison.OrdinalIgnoreCase) &&
            pendingSourcePath is not null)
        {
            ValidateImportSource(pendingSourcePath);
            PlayStartupPath(Path.GetFullPath(pendingSourcePath));
            return;
        }

        PlayStartup(preset, customFileName);
    }

    public string ImportStartupSound(string sourcePath)
    {
        using var source = OpenValidatedImportSource(sourcePath, out var extension);
        var fileName = $"startup-custom-{Guid.NewGuid():N}{extension}";
        var destination = GetSafeSoundPath(fileName);
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var output = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                var buffer = new byte[81920];
                long copied = 0;
                while (true)
                {
                    var read = source.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                        break;
                    copied += read;
                    if (copied > MaximumImportedSoundBytes)
                        throw new InvalidDataException(
                            "The startup sound is larger than 25 MB.");
                    output.Write(buffer, 0, read);
                }
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }

        return fileName;
    }

    public bool TryDeleteImportedStartupSound(string? fileName)
    {
        if (!IsManagedImportedFileName(fileName))
            return false;

        try
        {
            var path = GetSafeSoundPath(fileName!);
            if (File.Exists(path))
                File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException)
        {
            Trace.WriteLine(
                $"Imported startup sound cleanup failed: {exception.GetType().Name}.");
            return false;
        }
    }

    internal int CleanupOrphanedImportedSounds(
        IReadOnlyCollection<string> retainedFileNames,
        bool reconciliationIsSafe,
        IReadOnlyCollection<string>? ownedFileNames = null,
        CancellationToken cancellationToken = default) =>
        CleanupOrphanedImportedSounds(
            _soundsDirectory,
            retainedFileNames,
            reconciliationIsSafe,
            ownedFileNames,
            cancellationToken);

    internal int ReconcileImportedSounds(
        ImportedSoundRetention retention,
        IReadOnlyCollection<string> ownedFileNames,
        CancellationToken cancellationToken = default) =>
        ReconcileImportedSounds(
            _soundsDirectory,
            retention,
            ownedFileNames,
            cancellationToken);

    internal static int ReconcileImportedSounds(
        string soundsDirectory,
        ImportedSoundRetention retention,
        IReadOnlyCollection<string> ownedFileNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ownedFileNames);
        return CleanupOrphanedImportedSounds(
            soundsDirectory,
            retention.FileNames,
            retention.CanReconcile,
            ownedFileNames,
            cancellationToken);
    }

    internal static int CleanupOrphanedImportedSounds(
        string soundsDirectory,
        IReadOnlyCollection<string> retainedFileNames,
        bool reconciliationIsSafe,
        IReadOnlyCollection<string>? ownedFileNames = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(soundsDirectory);
        ArgumentNullException.ThrowIfNull(retainedFileNames);
        ownedFileNames ??= [];
        if (!reconciliationIsSafe && ownedFileNames.Count == 0)
            return 0;
        var retained = retainedFileNames
            .Where(IsManagedImportedFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var owned = ownedFileNames
            .Where(IsManagedImportedFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        try
        {
            var fullDirectory = Path.GetFullPath(soundsDirectory);
            if (!Directory.Exists(fullDirectory))
                return 0;
            ThrowIfReparsePoint(fullDirectory);

            foreach (var path in Directory.EnumerateFiles(
                         fullDirectory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(path);
                var isImportedSound = IsManagedImportedFileName(fileName);
                if (!isImportedSound &&
                    !IsManagedImportedTemporaryFileName(fileName))
                {
                    continue;
                }
                if (!reconciliationIsSafe &&
                    (!isImportedSound || !owned.Contains(fileName)))
                {
                    continue;
                }
                if (isImportedSound &&
                    retained.Contains(fileName))
                {
                    continue;
                }

                try
                {
                    if ((File.GetAttributes(path) &
                         FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                    File.Delete(path);
                    if (!File.Exists(path))
                        removed++;
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    Trace.WriteLine(
                        $"Orphaned startup sound cleanup failed: {exception.GetType().Name}.");
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException)
        {
            Trace.WriteLine(
                $"Imported startup sound reconciliation failed: {exception.GetType().Name}.");
        }
        return removed;
    }

    public void StopPreview()
    {
        if (!_disposed)
        {
            _startupPlayer.Stop();
            _startupPlayer.Close();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _uiPlayer.Close();
        _startupPlayer.Close();
    }

    private void PlayStartupPath(string? path)
    {
        if (path is null || !File.Exists(path))
            return;
        _startupPlayer.Stop();
        OpenPlayer(_startupPlayer, path);
        _startupPlayer.Play();
    }

    private string? ResolveImportedSound(string? fileName)
    {
        if (!IsValidImportedFileName(fileName))
            return null;
        var path = GetSafeSoundPath(fileName!);
        return File.Exists(path) ? path : null;
    }

    private void ValidateImportSource(string sourcePath)
    {
        using var source = OpenValidatedImportSource(sourcePath, out _);
    }

    private static FileStream OpenValidatedImportSource(
        string sourcePath,
        out string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullPath = Path.GetFullPath(sourcePath);
        extension = Path.GetExtension(fullPath).ToLowerInvariant();
        if (!SupportedImportedExtensions.Contains(extension))
        {
            throw new InvalidDataException(
                "Choose a WAV, MP3, WMA, or M4A audio file.");
        }

        var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        if (stream.Length is <= 0 or > MaximumImportedSoundBytes)
        {
            stream.Dispose();
            throw new InvalidDataException(
                "The startup sound must be between 1 byte and 25 MB.");
        }

        if (!HasExpectedAudioSignature(stream, extension))
        {
            stream.Dispose();
            throw new InvalidDataException("The selected file is not valid supported audio.");
        }

        stream.Position = 0;
        return stream;
    }

    private static bool HasExpectedAudioSignature(
        Stream stream,
        string extension)
    {
        Span<byte> header = stackalloc byte[16];
        var read = stream.Read(header);
        if (read < 12)
            return false;

        return extension.ToLowerInvariant() switch
        {
            ".wav" => header[..4].SequenceEqual("RIFF"u8) &&
                      header.Slice(8, 4).SequenceEqual("WAVE"u8),
            ".mp3" => header[..3].SequenceEqual("ID3"u8) ||
                      header[0] == 0xFF && (header[1] & 0xE0) == 0xE0,
            ".wma" => header.SequenceEqual(
                new byte[]
                {
                    0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11,
                    0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C
                }),
            ".m4a" => header.Slice(4, 4).SequenceEqual("ftyp"u8),
            _ => false
        };
    }

    private string EnsureBuiltInSound(
        string name,
        IReadOnlyList<(double Frequency, int DurationMilliseconds)> tones)
    {
        var path = GetSafeSoundPath($"{name}.wav");
        if (File.Exists(path))
            return path;

        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            WriteWave(temporary, tones);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }

        return path;
    }

    private string GetSafeSoundPath(string fileName)
    {
        ThrowIfReparsePoint(_soundsDirectory);
        var root = Path.GetFullPath(_soundsDirectory) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(_soundsDirectory, fileName));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The sound path is outside SessionDock.");
        if (File.Exists(path) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("A sound file cannot be a reparse point.");
        }
        return path;
    }

    private static bool IsManagedImportedFileName(string? fileName)
    {
        if (!IsValidImportedFileName(fileName))
            return false;

        var stem = Path.GetFileNameWithoutExtension(fileName!);
        if (stem.Equals("startup-custom", StringComparison.OrdinalIgnoreCase))
            return true;
        const string prefix = "startup-custom-";
        return stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(stem[prefix.Length..], "N", out _);
    }

    private static bool IsManagedImportedTemporaryFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            !Path.GetFileName(fileName).Equals(fileName, StringComparison.Ordinal) ||
            !Path.GetExtension(fileName).Equals(
                ".tmp",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stagedName = Path.GetFileNameWithoutExtension(fileName);
        var separator = stagedName.LastIndexOf('.');
        return separator > 0 &&
            Guid.TryParseExact(stagedName[(separator + 1)..], "N", out _) &&
            IsManagedImportedFileName(stagedName[..separator]);
    }

    private static void ThrowIfReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The sound directory cannot be a reparse point.");
    }

    private static void OpenPlayer(MediaPlayer player, string path) =>
        player.Open(new Uri(path, UriKind.Absolute));

    private static void WriteWave(
        string path,
        IReadOnlyList<(double Frequency, int DurationMilliseconds)> tones)
    {
        const int sampleRate = 44100;
        const short channels = 1;
        const short bitsPerSample = 16;
        var sampleCount = tones.Sum(tone =>
            sampleRate * tone.DurationMilliseconds / 1000);
        var dataLength = sampleCount * channels * bitsPerSample / 8;

        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bitsPerSample / 8);
        writer.Write((short)(channels * bitsPerSample / 8));
        writer.Write(bitsPerSample);
        writer.Write("data"u8);
        writer.Write(dataLength);

        foreach (var (frequency, durationMilliseconds) in tones)
        {
            var toneSamples = sampleRate * durationMilliseconds / 1000;
            for (var sample = 0; sample < toneSamples; sample++)
            {
                if (frequency <= 0)
                {
                    writer.Write((short)0);
                    continue;
                }

                var attack = Math.Min(1d, sample / (sampleRate * 0.012));
                var release = Math.Min(
                    1d,
                    (toneSamples - sample - 1) / (sampleRate * 0.045));
                var envelope = Math.Max(0, Math.Min(attack, release));
                var value = Math.Sin(2 * Math.PI * frequency * sample / sampleRate);
                writer.Write((short)(value * envelope * short.MaxValue * 0.13));
            }
        }
    }
}
