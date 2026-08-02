using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SessionDock.Services;

namespace SessionDock.SystemProcesses;

internal enum HandleScopeVersionSelectionMode
{
    Automatic,
    KeepInstalled,
    Exact
}

internal sealed record HandleScopeSelection(
    HandleScopeVersionSelectionMode VersionMode,
    Version? ExactVersion,
    string? ExactApiContract)
{
    internal static HandleScopeSelection Default { get; } =
        new(HandleScopeVersionSelectionMode.Automatic, null, null);
}

internal sealed record HandleScopeSelectionReadResult(
    HandleScopeSelection Selection,
    bool Exists,
    bool IsValid);

internal sealed class HandleScopeSelectionStore
{
    internal const int SchemaVersion = 1;
    internal const int MaximumBytes = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _path;

    internal HandleScopeSelectionStore(string? path = null)
    {
        _path = Path.GetFullPath(path ?? Path.Combine(
            AppDataPaths.RootDirectory,
            "handlescope-preferences.json"));
    }

    internal HandleScopeSelectionReadResult Read()
    {
        if (!File.Exists(_path))
            return new(HandleScopeSelection.Default, Exists: false, IsValid: true);

        try
        {
            var info = new FileInfo(_path);
            if ((info.Attributes & (FileAttributes.Directory |
                                    FileAttributes.ReparsePoint)) != 0 ||
                info.Length is <= 0 or > MaximumBytes)
            {
                return Invalid();
            }
            var bytes = File.ReadAllBytes(_path);
            _ = StrictUtf8.GetString(bytes);
            using var document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 3
                });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.GetPropertyCount() != 4 ||
                !HasUniqueProperties(root) ||
                !root.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var schemaVersion) ||
                schemaVersion != SchemaVersion ||
                !root.TryGetProperty("versionMode", out var versionMode) ||
                versionMode.ValueKind != JsonValueKind.String ||
                !TryParseVersionMode(
                    versionMode.GetString(),
                    out var parsedVersionMode) ||
                !root.TryGetProperty("exactVersion", out var exactVersion) ||
                exactVersion.ValueKind is not (
                    JsonValueKind.Null or JsonValueKind.String) ||
                !root.TryGetProperty("apiContract", out var apiContract) ||
                apiContract.ValueKind != JsonValueKind.String)
            {
                return Invalid();
            }

            Version? parsedExactVersion = null;
            if (exactVersion.ValueKind == JsonValueKind.String &&
                !TryParseStableVersion(
                    exactVersion.GetString(),
                    out parsedExactVersion))
            {
                return Invalid();
            }
            if (parsedVersionMode == HandleScopeVersionSelectionMode.Exact !=
                (parsedExactVersion is not null))
            {
                return Invalid();
            }

            var apiValue = apiContract.GetString();
            string? parsedApiContract = apiValue switch
            {
                "automatic" => null,
                "v1" or "v2" => apiValue,
                _ => string.Empty
            };
            if (parsedApiContract == string.Empty)
                return Invalid();

            return new(
                new(
                    parsedVersionMode,
                    parsedExactVersion,
                    parsedApiContract),
                Exists: true,
                IsValid: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                JsonException or DecoderFallbackException or
                NotSupportedException or ArgumentException)
        {
            return Invalid();
        }
    }

    internal void Write(HandleScopeSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.VersionMode == HandleScopeVersionSelectionMode.Exact !=
                (selection.ExactVersion is not null) ||
            selection.ExactVersion is not null &&
            !IsStableVersion(selection.ExactVersion) ||
            selection.ExactApiContract is not (null or "v1" or "v2"))
        {
            throw new ArgumentException(
                "The HandleScope version selection is invalid.",
                nameof(selection));
        }

        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException(
                "The HandleScope preference path has no parent directory.");
        Directory.CreateDirectory(directory);
        if ((new DirectoryInfo(directory).Attributes &
             FileAttributes.ReparsePoint) != 0 ||
            File.Exists(_path) &&
            (File.GetAttributes(_path) &
             (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new IOException(
                "The HandleScope preference path is not a regular local path.");
        }

        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   output,
                   new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString(
                "versionMode",
                ToStorageValue(selection.VersionMode));
            if (selection.ExactVersion is null)
                writer.WriteNull("exactVersion");
            else
                writer.WriteString("exactVersion", selection.ExactVersion.ToString(3));
            writer.WriteString(
                "apiContract",
                selection.ExactApiContract ?? "automatic");
            writer.WriteEndObject();
        }
        if (output.Length is <= 0 or > MaximumBytes)
            throw new InvalidOperationException(
                "The HandleScope preference is outside its size boundary.");

        var temporaryPath = _path + "." +
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) + ".tmp";
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
            try { File.Delete(temporaryPath); }
            catch { /* Preference-only temporary data. */ }
        }
    }

    private static HandleScopeSelectionReadResult Invalid() =>
        new(HandleScopeSelection.Default, Exists: true, IsValid: false);

    private static bool HasUniqueProperties(JsonElement root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return root.EnumerateObject().All(property => names.Add(property.Name));
    }

    private static bool TryParseVersionMode(
        string? value,
        out HandleScopeVersionSelectionMode mode)
    {
        mode = value switch
        {
            "automatic" => HandleScopeVersionSelectionMode.Automatic,
            "keep-installed" => HandleScopeVersionSelectionMode.KeepInstalled,
            "exact" => HandleScopeVersionSelectionMode.Exact,
            _ => (HandleScopeVersionSelectionMode)(-1)
        };
        return Enum.IsDefined(mode);
    }

    private static string ToStorageValue(
        HandleScopeVersionSelectionMode mode) => mode switch
        {
            HandleScopeVersionSelectionMode.Automatic => "automatic",
            HandleScopeVersionSelectionMode.KeepInstalled => "keep-installed",
            HandleScopeVersionSelectionMode.Exact => "exact",
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

    private static bool TryParseStableVersion(
        string? value,
        out Version? version)
    {
        version = null;
        if (!Version.TryParse(value, out var parsed) || !IsStableVersion(parsed))
            return false;
        version = parsed;
        return true;
    }

    private static bool IsStableVersion(Version version) =>
        version.Build >= 0 &&
        version.Revision < 0 &&
        version.ToString(3) == version.ToString();
}
