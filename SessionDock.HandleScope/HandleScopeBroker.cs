using System.Buffers;
using System.Net;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using HandleScope.Api;
using HandleScope.Models;
using HandleScope.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace SessionDock.HandleScope;

public sealed class HandleScopeBroker
{
    public const string ComponentVersion = "0.3.0";
    public const int HandshakeSchemaVersion = 1;
    public const int MaximumHandshakeBytes = 2048;
    private const string ApiVersion = "v1";
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    public async Task RunAsync(
        Stream handshakeStream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handshakeStream);
        if (!handshakeStream.CanWrite)
            throw new ArgumentException(
                "The HandleScope handshake stream must be writable.",
                nameof(handshakeStream));

        cancellationToken.ThrowIfCancellationRequested();
        var identity = GetCurrentIdentity();
        if (!HandleScopeBrokerRuntimeGuard.IsAllowed(identity))
        {
            throw new SecurityException(
                "The embedded HandleScope broker requires a non-elevated " +
                "interactive user session.");
        }

        using var instanceSemaphore = new Semaphore(
            initialCount: 1,
            maximumCount: 1,
            HandleScopeBrokerRuntimeGuard.GetInstanceName(identity),
            out _);
        if (!instanceSemaphore.WaitOne(0))
        {
            throw new InvalidOperationException(
                "The embedded HandleScope broker is already active for this user session.");
        }

        try
        {
            await RunHostAsync(handshakeStream, cancellationToken);
        }
        finally
        {
            instanceSemaphore.Release();
        }
    }

    internal static string CreateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        try
        {
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static async Task WriteHandshakeAsync(
        Stream stream,
        Uri baseUrl,
        string token,
        int processId,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentException.ThrowIfNullOrEmpty(token);
        if (!stream.CanWrite)
            throw new ArgumentException("The handshake stream must be writable.", nameof(stream));
        if (!IsCanonicalBaseUrl(baseUrl))
            throw new ArgumentException("The API URL must be an explicit IPv4 loopback URL.", nameof(baseUrl));
        if (!IsCanonicalToken(token))
            throw new ArgumentException("The API token is not canonical base64url.", nameof(token));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        if (startedAtUtc == default)
            throw new ArgumentException("The API start time is required.", nameof(startedAtUtc));

        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", HandshakeSchemaVersion);
            writer.WriteString("componentVersion", ComponentVersion);
            writer.WriteString("apiVersion", ApiVersion);
            writer.WriteString(
                "baseUrl",
                $"http://127.0.0.1:{baseUrl.Port}");
            writer.WriteString("token", token);
            writer.WriteNumber("processId", processId);
            writer.WriteString("startedAtUtc", startedAtUtc);
            writer.WriteEndObject();
        }

        if (buffer.WrittenCount + 1 > MaximumHandshakeBytes)
        {
            throw new InvalidOperationException(
                "The HandleScope handshake exceeded its fixed size limit.");
        }

        var payload = GC.AllocateUninitializedArray<byte>(buffer.WrittenCount + 1);
        buffer.WrittenSpan.CopyTo(payload);
        payload[^1] = (byte)'\n';
        try
        {
            await stream.WriteAsync(payload, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static ProcessIdentity GetCurrentIdentity()
    {
        try
        {
            return new ProcessIdentityService().GetIdentity(Environment.ProcessId);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ArgumentException or
                System.ComponentModel.Win32Exception)
        {
            throw new SecurityException(
                "The embedded HandleScope broker could not verify its Windows identity.",
                exception);
        }
    }

    private static async Task RunHostAsync(
        Stream handshakeStream,
        CancellationToken cancellationToken)
    {
        var token = CreateToken();
        await using var app = ApiHost.Build(new ApiRuntimeOptions(0, token));
        var started = false;
        try
        {
            await app.StartAsync(cancellationToken);
            started = true;
            var baseUrl = GetBoundBaseUrl(app.Services);
            await WriteHandshakeAsync(
                handshakeStream,
                baseUrl,
                token,
                Environment.ProcessId,
                DateTimeOffset.UtcNow,
                cancellationToken);
            await app.WaitForShutdownAsync(cancellationToken);
        }
        finally
        {
            if (started)
            {
                using var stop = new CancellationTokenSource(StopTimeout);
                try
                {
                    await app.StopAsync(stop.Token);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    // Disposal is still attempted below; shutdown stays bounded.
                }
            }
        }
    }

    private static Uri GetBoundBaseUrl(IServiceProvider services)
    {
        var addresses = services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            ?.Addresses;
        var address = addresses?.SingleOrDefault() ??
            throw new InvalidOperationException(
                "The embedded HandleScope API address is unavailable.");
        if (!Uri.TryCreate(address, UriKind.Absolute, out var baseUrl) ||
            !IsCanonicalBaseUrl(baseUrl))
        {
            throw new InvalidOperationException(
                "The embedded HandleScope API did not bind to IPv4 loopback.");
        }

        return baseUrl;
    }

    private static bool IsCanonicalBaseUrl(Uri value) =>
        value.IsAbsoluteUri &&
        value.Scheme == Uri.UriSchemeHttp &&
        value.Host.Equals(IPAddress.Loopback.ToString(), StringComparison.Ordinal) &&
        value.Port is > 0 and <= IPEndPoint.MaxPort &&
        !value.IsDefaultPort &&
        string.IsNullOrEmpty(value.UserInfo) &&
        value.AbsolutePath == "/" &&
        string.IsNullOrEmpty(value.Query) &&
        string.IsNullOrEmpty(value.Fragment);

    private static bool IsCanonicalToken(string value) =>
        value.Length == 43 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '_' or '-');
}
