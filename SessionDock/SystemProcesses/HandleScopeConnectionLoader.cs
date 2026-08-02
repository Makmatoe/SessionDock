using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SessionDock.SystemProcesses;

internal sealed class HandleScopeConnectionLoader
{
    private const int MaximumConnectionBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 4
    };
    private static readonly Regex TokenPattern = new(
        "^[A-Za-z0-9_-]{43}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly string _connectionPath;
    private readonly string _localAppDataRoot;
    private readonly Func<string, bool>? _isReparsePoint;

    public HandleScopeConnectionLoader(string? connectionPath = null)
        : this(connectionPath, localAppDataRoot: null, isReparsePoint: null)
    {
    }

    internal HandleScopeConnectionLoader(
        string? connectionPath,
        string? localAppDataRoot,
        Func<string, bool>? isReparsePoint)
    {
        _localAppDataRoot = Path.GetFullPath(localAppDataRoot ??
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        _connectionPath = Path.GetFullPath(connectionPath ?? Path.Combine(
            _localAppDataRoot,
            "HandleScope",
            "connection.json"));
        _isReparsePoint = isReparsePoint;
    }

    public HandleScopeConnection? Load()
    {
        if (!File.Exists(_connectionPath))
        {
            Trace.WriteLine("HandleScope connection is unavailable.");
            return null;
        }

        try
        {
            var directory = Path.GetDirectoryName(
                _connectionPath);
            if (directory is null ||
                !HandleScopePathSecurity.IsSafeExistingPath(
                    _localAppDataRoot,
                    directory,
                    targetMustExist: true,
                    _isReparsePoint) ||
                !HandleScopePathSecurity.IsSafeExistingPath(
                    _localAppDataRoot,
                    _connectionPath,
                    targetMustExist: true,
                    _isReparsePoint))
            {
                Trace.WriteLine("HandleScope connection was rejected.");
                return null;
            }

            var attributes = File.GetAttributes(_connectionPath);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Trace.WriteLine("HandleScope connection was rejected.");
                return null;
            }

            var json = ReadBoundedConnectionFile();
            using var parsed = JsonDocument.Parse(
                json,
                new JsonDocumentOptions { MaxDepth = 4 });
            if (!IsStrictConnectionDocument(parsed.RootElement))
            {
                Trace.WriteLine("HandleScope connection was rejected.");
                return null;
            }

            var document = JsonSerializer.Deserialize<ConnectionDocument>(
                json,
                JsonOptions);
            if (document is null ||
                !TryValidateBaseUrl(document.BaseUrl, out var baseUrl) ||
                document.Token is null ||
                !TokenPattern.IsMatch(document.Token) ||
                !string.Equals(
                    document.ApiVersion,
                    "v1",
                    StringComparison.Ordinal) ||
                document.ProcessId is not > 0 ||
                document.StartedAtUtc is null ||
                document.StartedAtUtc == default)
            {
                Trace.WriteLine("HandleScope connection was rejected.");
                return null;
            }

            return new HandleScopeConnection(
                baseUrl!,
                document.Token,
                document.ApiVersion!,
                document.ProcessId.Value,
                document.StartedAtUtc.Value);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
                NotSupportedException or InvalidDataException)
        {
            Trace.WriteLine(
                $"HandleScope connection could not be read: {ex.GetType().Name}.");
            return null;
        }
    }

    private static bool IsStrictConnectionDocument(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            root.GetPropertyCount() != 5)
        {
            return false;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Add(property.Name))
                return false;

            var expectedKind = property.Name switch
            {
                "apiVersion" or "baseUrl" or "token" or "startedAtUtc" =>
                    JsonValueKind.String,
                "processId" => JsonValueKind.Number,
                _ => JsonValueKind.Undefined
            };
            if (property.Value.ValueKind != expectedKind)
                return false;
        }

        return names.SetEquals(
            ["apiVersion", "baseUrl", "token", "processId", "startedAtUtc"]);
    }

    internal static bool TryValidateBaseUrl(string? value, out Uri? baseUrl)
    {
        baseUrl = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttp ||
            !parsed.Host.Equals("127.0.0.1", StringComparison.Ordinal) ||
            parsed.Port is <= 0 or > 65535 ||
            parsed.IsDefaultPort ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            parsed.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment))
        {
            return false;
        }

        baseUrl = parsed;
        return true;
    }

    private byte[] ReadBoundedConnectionFile()
    {
        using var stream = new FileStream(
            _connectionPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = stream.Read(chunk, 0, chunk.Length);
            if (read == 0)
                return buffer.ToArray();
            if (buffer.Length + read > MaximumConnectionBytes)
                throw new InvalidDataException(
                    "The HandleScope connection is too large.");
            buffer.Write(chunk, 0, read);
        }
    }

    private sealed class ConnectionDocument
    {
        public string? BaseUrl { get; set; }
        public string? Token { get; set; }
        public string? ApiVersion { get; set; }
        public int? ProcessId { get; set; }
        public DateTimeOffset? StartedAtUtc { get; set; }
    }
}

internal sealed record HandleScopeConnection(
    Uri BaseUrl,
    string Token,
    string ApiVersion,
    int ApiProcessId,
    DateTimeOffset StartedAtUtc)
{
    internal HandleScopeRuntimeIdentity? RuntimeIdentity { get; init; }

    internal HandleScopeApiMetadata? RuntimeMetadata { get; init; }

    internal HandleScopeProtocolAdapter? NegotiatedProtocol { get; init; }
}
