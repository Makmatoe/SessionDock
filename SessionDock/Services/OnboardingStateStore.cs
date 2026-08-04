using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SessionDock.Services;

internal sealed record OnboardingState(
    int GetStartedTutorialVersion,
    int AdvancedTutorialVersion)
{
    internal OnboardingState(int completedTutorialVersion)
        : this(completedTutorialVersion, 0)
    {
    }

    // Compatibility alias while callers move from the schema-v1 tutorial.
    internal int CompletedTutorialVersion => GetStartedTutorialVersion;

    internal static OnboardingState Default { get; } = new(0, 0);
}

internal sealed record OnboardingStateReadResult(
    OnboardingState State,
    bool Exists,
    bool IsValid,
    bool RequiresMigration = false);

internal sealed class OnboardingStateStore
{
    internal const int SchemaVersion = 2;
    internal const int LegacySchemaVersion = 1;
    internal const int MaximumBytes = 1024;
    internal const int MaximumTutorialVersion = 1_000_000;
    internal const string FileName = "onboarding-state.json";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _rootDirectory;
    private readonly string _path;
    private readonly Func<string, FileAttributes> _getAttributes;

    internal OnboardingStateStore(
        string? rootDirectory = null,
        Func<string, FileAttributes>? getAttributes = null)
    {
        _getAttributes = getAttributes ?? File.GetAttributes;
        _rootDirectory = Path.GetFullPath(
            rootDirectory ?? AppDataPaths.RootDirectory);
        _path = Path.Combine(_rootDirectory, FileName);
    }

    internal OnboardingStateReadResult Read()
    {
        try
        {
            var rootState = GetPathState(_rootDirectory);
            if (rootState == OnboardingPathState.Missing)
            {
                return new(
                    OnboardingState.Default,
                    Exists: false,
                    IsValid: true);
            }
            if (rootState != OnboardingPathState.SafeDirectory)
                return Invalid(Exists: true);

            var pathState = GetPathState(_path);
            if (pathState == OnboardingPathState.Missing)
            {
                return new(
                    OnboardingState.Default,
                    Exists: false,
                    IsValid: true);
            }
            if (pathState != OnboardingPathState.SafeFile)
                return Invalid(Exists: true);

            var bytes = ReadBoundedFile(_path);
            _ = StrictUtf8.GetString(bytes);
            using var document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 2
                });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !HasUniqueProperties(root) ||
                !root.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var schemaVersion))
            {
                return Invalid(Exists: true);
            }

            return schemaVersion switch
            {
                LegacySchemaVersion => ReadLegacyState(root),
                SchemaVersion => ReadCurrentState(root),
                _ => Invalid(Exists: true)
            };
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                JsonException or DecoderFallbackException or
                InvalidDataException or ArgumentException or
                NotSupportedException)
        {
            return Invalid(Exists: true);
        }
    }

    internal void Write(OnboardingState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!IsValidTutorialVersion(state.GetStartedTutorialVersion) ||
            !IsValidTutorialVersion(state.AdvancedTutorialVersion))
        {
            throw new ArgumentOutOfRangeException(
                nameof(state),
                "A completed tutorial version is outside its boundary.");
        }

        EnsureSafeRoot();
        var pathState = GetPathState(_path);
        if (pathState is not (
                OnboardingPathState.Missing or OnboardingPathState.SafeFile))
        {
            throw new IOException(
                "The onboarding-state path is not a regular local file.");
        }

        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   output,
                   new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteNumber(
                "completedGetStartedTutorialVersion",
                state.GetStartedTutorialVersion);
            writer.WriteNumber(
                "completedAdvancedTutorialVersion",
                state.AdvancedTutorialVersion);
            writer.WriteEndObject();
        }
        if (output.Length + 1 > MaximumBytes)
        {
            throw new InvalidDataException(
                "The onboarding state exceeds its size boundary.");
        }

        var temporaryPath = Path.Combine(
            _rootDirectory,
            $".onboarding.{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                output.Position = 0;
                output.CopyTo(stream);
                stream.WriteByte((byte)'\n');
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // The temporary file contains only the same local version bit.
            }
        }
    }

    private void EnsureSafeRoot()
    {
        var state = GetPathState(_rootDirectory);
        if (state == OnboardingPathState.Missing)
        {
            Directory.CreateDirectory(_rootDirectory);
            state = GetPathState(_rootDirectory);
        }
        if (state != OnboardingPathState.SafeDirectory)
        {
            throw new IOException(
                "The onboarding-state root is not a regular local directory.");
        }
    }

    private static byte[] ReadBoundedFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaximumBytes)
            throw new InvalidDataException("The onboarding state is too large.");

        var contents = GC.AllocateUninitializedArray<byte>((int)stream.Length);
        stream.ReadExactly(contents);
        return contents;
    }

    private static bool HasUniqueProperties(JsonElement root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return root.EnumerateObject().All(property => names.Add(property.Name));
    }

    private static OnboardingStateReadResult ReadLegacyState(JsonElement root)
    {
        if (root.GetPropertyCount() != 2 ||
            !TryReadTutorialVersion(
                root,
                "completedTutorialVersion",
                out var completedVersion))
        {
            return Invalid(Exists: true);
        }

        return new(
            new OnboardingState(completedVersion, 0),
            Exists: true,
            IsValid: true,
            RequiresMigration: true);
    }

    private static OnboardingStateReadResult ReadCurrentState(JsonElement root)
    {
        if (root.GetPropertyCount() != 3 ||
            !TryReadTutorialVersion(
                root,
                "completedGetStartedTutorialVersion",
                out var getStartedVersion) ||
            !TryReadTutorialVersion(
                root,
                "completedAdvancedTutorialVersion",
                out var advancedVersion))
        {
            return Invalid(Exists: true);
        }

        return new(
            new OnboardingState(getStartedVersion, advancedVersion),
            Exists: true,
            IsValid: true);
    }

    private static bool TryReadTutorialVersion(
        JsonElement root,
        string propertyName,
        out int version)
    {
        version = 0;
        return root.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out version) &&
            IsValidTutorialVersion(version);
    }

    private static bool IsValidTutorialVersion(int version) =>
        version is >= 0 and <= MaximumTutorialVersion;

    private OnboardingPathState GetPathState(string path)
    {
        try
        {
            var attributes = _getAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                return OnboardingPathState.Unsafe;
            return (attributes & FileAttributes.Directory) != 0
                ? OnboardingPathState.SafeDirectory
                : OnboardingPathState.SafeFile;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return OnboardingPathState.Missing;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                NotSupportedException or ArgumentException)
        {
            return OnboardingPathState.Unsafe;
        }
    }

    private static OnboardingStateReadResult Invalid(bool Exists) => new(
        OnboardingState.Default,
        Exists,
        IsValid: false);

    private enum OnboardingPathState
    {
        Missing,
        SafeFile,
        SafeDirectory,
        Unsafe
    }
}
