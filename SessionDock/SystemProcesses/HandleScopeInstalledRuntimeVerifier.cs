using System.IO;
using System.Security.Cryptography;

namespace SessionDock.SystemProcesses;

internal interface IHandleScopeInstalledRuntimeVerifier
{
    bool IsAuthorized(string executablePath);
}

internal sealed class HandleScopeInstalledRuntimeVerifier :
    IHandleScopeInstalledRuntimeVerifier
{
    internal const string SupportedVersion = "0.1.3";
    internal const long ExpectedExecutableSize = 50_275_056;
    internal const string ExpectedExecutableSha256 =
        "ca273df4b3822e358658c43fd764c70661f9279b37d883d11a470cd363ad7852";

    private readonly long _expectedSize;
    private readonly byte[] _expectedSha256;

    internal HandleScopeInstalledRuntimeVerifier()
        : this(
            ExpectedExecutableSize,
            Convert.FromHexString(ExpectedExecutableSha256))
    {
    }

    internal HandleScopeInstalledRuntimeVerifier(
        long expectedSize,
        ReadOnlySpan<byte> expectedSha256)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedSize);
        if (expectedSha256.Length != SHA256.HashSizeInBytes)
        {
            throw new ArgumentException(
                "A SHA-256 digest must contain exactly 32 bytes.",
                nameof(expectedSha256));
        }

        _expectedSize = expectedSize;
        _expectedSha256 = expectedSha256.ToArray();
    }

    public bool IsAuthorized(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return false;

        try
        {
            using var stream = new FileStream(
                Path.GetFullPath(executablePath),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length != _expectedSize)
                return false;

            return CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(stream),
                _expectedSha256);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or
                CryptographicException)
        {
            return false;
        }
    }
}
