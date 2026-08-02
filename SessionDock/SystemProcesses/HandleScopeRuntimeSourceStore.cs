using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SessionDock.Services;

namespace SessionDock.SystemProcesses;

internal sealed record HandleScopeRuntimeSourceReadResult(
    HandleScopeRuntimeSource Source,
    bool Exists,
    bool IsValid);

internal sealed class HandleScopeRuntimeSourceStore
{
    internal const int SchemaVersion = 1;
    internal const int MaximumBytes = 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _path;

    internal HandleScopeRuntimeSourceStore(string? path = null)
    {
        _path = Path.GetFullPath(path ?? Path.Combine(
            AppDataPaths.RootDirectory,
            "handlescope-runtime.json"));
    }

    internal HandleScopeRuntimeSourceReadResult Read()
    {
        if (!File.Exists(_path))
        {
            return new(
                HandleScopeRuntimeSource.Bundled,
                Exists: false,
                IsValid: true);
        }

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
                    MaxDepth = 2
                });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.GetPropertyCount() != 2 ||
                !HasUniqueProperties(root) ||
                !root.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var schemaVersion) ||
                schemaVersion != SchemaVersion ||
                !root.TryGetProperty("runtimeSource", out var source) ||
                source.ValueKind != JsonValueKind.String ||
                !TryParseSource(source.GetString(), out var parsed))
            {
                return Invalid();
            }

            return new(parsed, Exists: true, IsValid: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                JsonException or DecoderFallbackException or
                ArgumentException or NotSupportedException)
        {
            return Invalid();
        }
    }

    internal void Write(HandleScopeRuntimeSource source)
    {
        if (!Enum.IsDefined(source))
            throw new ArgumentOutOfRangeException(nameof(source));

        var directory = Path.GetDirectoryName(_path) ??
            throw new InvalidOperationException(
                "The HandleScope runtime preference has no parent directory.");
        Directory.CreateDirectory(directory);
        if ((new DirectoryInfo(directory).Attributes &
             FileAttributes.ReparsePoint) != 0 ||
            File.Exists(_path) &&
            (File.GetAttributes(_path) &
             (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new IOException(
                "The HandleScope runtime preference path is not a regular local path.");
        }

        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   output,
                   new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString(
                "runtimeSource",
                source == HandleScopeRuntimeSource.Bundled
                    ? "bundled"
                    : "standalone");
            writer.WriteEndObject();
        }

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
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Preference-only temporary data is safe to leave behind.
            }
        }
    }

    private static HandleScopeRuntimeSourceReadResult Invalid() =>
        new(HandleScopeRuntimeSource.Bundled, Exists: true, IsValid: false);

    private static bool HasUniqueProperties(JsonElement root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return root.EnumerateObject().All(property => names.Add(property.Name));
    }

    private static bool TryParseSource(
        string? value,
        out HandleScopeRuntimeSource source)
    {
        source = value switch
        {
            "bundled" => HandleScopeRuntimeSource.Bundled,
            "standalone" => HandleScopeRuntimeSource.Standalone,
            _ => (HandleScopeRuntimeSource)(-1)
        };
        return Enum.IsDefined(source);
    }
}
