using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace HandleScope.Compatibility;

public enum ApiCompatibilityMode
{
    Automatic,
    V2,
    V1
}

public sealed record ApiCompatibilityReadResult(
    ApiCompatibilityMode Mode,
    bool Exists,
    bool IsValid);

public static class ApiCompatibilityPolicy
{
    public const string DiscoveryApiVersion = "v1";
    public const string CurrentApiVersion = "v2";
    public const string LegacyApiVersion = "v1";

    public static IReadOnlyList<string> SupportedApiVersions { get; } =
        [LegacyApiVersion, CurrentApiVersion];

    public static string Resolve(ApiCompatibilityMode mode) => mode switch
    {
        ApiCompatibilityMode.Automatic or ApiCompatibilityMode.V2 =>
            CurrentApiVersion,
        ApiCompatibilityMode.V1 => LegacyApiVersion,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public static string ToStorageValue(ApiCompatibilityMode mode) => mode switch
    {
        ApiCompatibilityMode.Automatic => "automatic",
        ApiCompatibilityMode.V2 => CurrentApiVersion,
        ApiCompatibilityMode.V1 => LegacyApiVersion,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public static bool TryParseStorageValue(
        string? value,
        out ApiCompatibilityMode mode)
    {
        mode = value switch
        {
            "automatic" => ApiCompatibilityMode.Automatic,
            CurrentApiVersion => ApiCompatibilityMode.V2,
            LegacyApiVersion => ApiCompatibilityMode.V1,
            _ => (ApiCompatibilityMode)(-1)
        };
        return Enum.IsDefined(mode);
    }
}

public sealed class ApiCompatibilityPreferenceStore
{
    public const int SchemaVersion = 1;
    public const int MaximumBytes = 4096;
    private const string FileName = "compatibility.json";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _path;

    public ApiCompatibilityPreferenceStore(string? path = null)
    {
        _path = Path.GetFullPath(path ?? DefaultPath);
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HandleScope");

    public static string DefaultPath => Path.Combine(DefaultDirectory, FileName);

    public ApiCompatibilityReadResult Read()
    {
        if (!File.Exists(_path))
            return new(ApiCompatibilityMode.Automatic, Exists: false, IsValid: true);

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
                root.GetPropertyCount() != 2 ||
                !HasUniqueProperties(root) ||
                !root.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var schemaVersion) ||
                schemaVersion != SchemaVersion ||
                !root.TryGetProperty("mode", out var modeValue) ||
                modeValue.ValueKind != JsonValueKind.String ||
                !ApiCompatibilityPolicy.TryParseStorageValue(
                    modeValue.GetString(),
                    out var mode))
            {
                return Invalid();
            }

            return new(mode, Exists: true, IsValid: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                JsonException or DecoderFallbackException or
                NotSupportedException or ArgumentException)
        {
            return Invalid();
        }
    }

    public void Write(ApiCompatibilityMode mode)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));

        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException(
                "The compatibility preference path has no parent directory.");
        Directory.CreateDirectory(directory);
        RejectReparsePoint(directory);
        if (File.Exists(_path))
            RejectUnsafeFile(_path);

        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   output,
                   new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("mode", ApiCompatibilityPolicy.ToStorageValue(mode));
            writer.WriteEndObject();
        }

        if (output.Length is <= 0 or > MaximumBytes)
            throw new InvalidOperationException(
                "The compatibility preference is outside its size boundary.");

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

            ProtectDefaultDirectory(directory);
            ProtectDefaultFile(temporaryPath);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch { /* Best-effort cleanup of preference-only data. */ }
        }
    }

    private static ApiCompatibilityReadResult Invalid() =>
        new(ApiCompatibilityMode.Automatic, Exists: true, IsValid: false);

    private static bool HasUniqueProperties(JsonElement root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return root.EnumerateObject().All(property => names.Add(property.Name));
    }

    private static void RejectReparsePoint(string path)
    {
        var info = new DirectoryInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException(
                "The HandleScope compatibility directory cannot be a reparse point.");
    }

    private static void RejectUnsafeFile(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory |
                           FileAttributes.ReparsePoint)) != 0)
        {
            throw new IOException(
                "The HandleScope compatibility preference must be a regular file.");
        }
    }

    private static void ProtectDefaultDirectory(string directory)
    {
        if (!Path.GetFullPath(directory).Equals(
                Path.GetFullPath(DefaultDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException(
                "The current Windows SID is unavailable.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }

    private static void ProtectDefaultFile(string path)
    {
        if (!Path.GetFullPath(Path.GetDirectoryName(path)!).Equals(
                Path.GetFullPath(DefaultDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException(
                "The current Windows SID is unavailable.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }
}
