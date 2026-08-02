using System.Globalization;
using System.Security.Cryptography;
using SessionDock.ReleaseTrust;

return await ReleaseSignerProgram.RunAsync(args);

internal static class ReleaseSignerProgram
{
    private static readonly HashSet<string> DescriptorOptions = new(
        [
            "package", "notes", "output", "repository", "channel",
            "version", "tag", "identity"
        ],
        StringComparer.Ordinal);
    private static readonly HashSet<string> PrepareOptions = new(
        DescriptorOptions.Append("digest-output"),
        StringComparer.Ordinal);
    private static readonly HashSet<string> CompleteOptions = new(
        ["manifest", "package", "signature-file", "output", "public-key"],
        StringComparer.Ordinal);
    private static readonly HashSet<string> LocalSignOptions = new(
        DescriptorOptions.Append("private-key-file"),
        StringComparer.Ordinal);
    private static readonly HashSet<string> VerifyOptions = new(
        ["manifest", "package", "public-key"],
        StringComparer.Ordinal);
    private static readonly HashSet<string> CatalogPrepareOptions = new(
        [
            "manifest", "output", "digest-output", "sessiondock-version",
            "prior-manifest", "prior-directory", "public-key"
        ],
        StringComparer.Ordinal);
    private static readonly HashSet<string> CatalogCompleteOptions = new(
        [
            "manifest", "signature-file", "output", "public-key",
            "sessiondock-version"
        ],
        StringComparer.Ordinal);
    private static readonly HashSet<string> CatalogVerifyOptions = new(
        ["manifest", "public-key", "sessiondock-version"],
        StringComparer.Ordinal);

    public static async Task<int> RunAsync(string[] arguments)
    {
        try
        {
            if (arguments.Length == 0)
            {
                throw new ArgumentException(
                    "Specify prepare, complete, verify, sign-local, prepare-catalog, complete-catalog, or verify-catalog.");
            }

            var command = arguments[0];
            var options = ParseOptions(arguments[1..], command switch
            {
                "prepare" => PrepareOptions,
                "complete" => CompleteOptions,
                "verify" => VerifyOptions,
                "sign-local" => LocalSignOptions,
                "prepare-catalog" => CatalogPrepareOptions,
                "complete-catalog" => CatalogCompleteOptions,
                "verify-catalog" => CatalogVerifyOptions,
                _ => throw new ArgumentException(
                    "Specify prepare, complete, verify, sign-local, prepare-catalog, complete-catalog, or verify-catalog.")
            });

            switch (command)
            {
                case "prepare":
                    await PrepareAsync(options);
                    break;
                case "complete":
                    await CompleteAsync(options);
                    break;
                case "sign-local":
                    await SignLocalAsync(options);
                    break;
                case "verify":
                    await VerifyAsync(options);
                    break;
                case "prepare-catalog":
                    await PrepareCatalogAsync(options);
                    break;
                case "complete-catalog":
                    await CompleteCatalogAsync(options);
                    break;
                case "verify-catalog":
                    await VerifyCatalogAsync(options);
                    break;
                default:
                    throw new ArgumentException("The release signing command is invalid.");
            }
            return 0;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or
            UnauthorizedAccessException or CryptographicException or
            ReleaseTrustException)
        {
            Console.Error.WriteLine(
                $"Release signing operation failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task PrepareAsync(
        IReadOnlyDictionary<string, string> options)
    {
        var descriptor = await CreateUnsignedDescriptorAsync(options);
        var outputPath = Require(options, "output");
        var digestPath = Require(options, "digest-output");
        await WriteDescriptorAsync(outputPath, descriptor);
        var digest = SHA256.HashData(
            ReleaseDescriptorPolicy.CreateCanonicalPayload(descriptor));
        await WriteCanonicalTextAsync(
            digestPath,
            Base64UrlEncode(digest));
    }

    private static async Task CompleteAsync(
        IReadOnlyDictionary<string, string> options)
    {
        var manifestPath = RequireFile(options, "manifest");
        var packagePath = RequireFile(options, "package");
        var signaturePath = RequireFile(options, "signature-file");
        var publicKeyPath = RequireFile(options, "public-key");
        var unsigned = ReleaseDescriptorPolicy.Deserialize(
            await File.ReadAllTextAsync(manifestPath));
        if (!string.IsNullOrEmpty(unsigned.Signature))
        {
            throw new ArgumentException(
                "The prepared descriptor already contains a signature.");
        }
        var signature = await ReadManagedSignatureAsync(signaturePath);
        var signed = unsigned with
        {
            Signature = Convert.ToBase64String(signature)
        };
        var package = new FileInfo(packagePath);
        var identity = new ReleaseAssetIdentity(
            unsigned.Version,
            package.Name,
            package.Length,
            await ComputeSha256Async(packagePath));
        _ = ReleaseDescriptorPolicy.Verify(
            ReleaseDescriptorPolicy.Serialize(signed),
            identity,
            await File.ReadAllTextAsync(publicKeyPath));
        await WriteDescriptorAsync(Require(options, "output"), signed);
    }

    private static async Task SignLocalAsync(
        IReadOnlyDictionary<string, string> options)
    {
        var descriptor = await CreateUnsignedDescriptorAsync(options);
        var privateKeyPath = RequireFile(options, "private-key-file");
        var privateKeyText = await File.ReadAllTextAsync(privateKeyPath);
        byte[] signature;
        using (var privateKey = ECDsa.Create())
        {
            privateKey.ImportFromPem(privateKeyText);
            if (privateKey.KeySize != 256)
            {
                throw new ArgumentException(
                    "The developer-only private key must be P-256.");
            }
            signature = privateKey.SignData(
                ReleaseDescriptorPolicy.CreateCanonicalPayload(descriptor),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        await WriteDescriptorAsync(
            Require(options, "output"),
            descriptor with { Signature = Convert.ToBase64String(signature) });
    }

    private static async Task<ReleaseDescriptor> CreateUnsignedDescriptorAsync(
        IReadOnlyDictionary<string, string> options)
    {
        var packagePath = RequireFile(options, "package");
        var notesPath = RequireFile(options, "notes");
        var repository = Require(options, "repository");
        var channel = Require(options, "channel");
        var versionText = Require(options, "version");
        var tag = Require(options, "tag");
        var identityName = options.GetValueOrDefault("identity", "current");
        var identity = identityName switch
        {
            "current" => new ReleaseIdentity(
                ReleaseDescriptorPolicy.Product,
                ReleaseDescriptorPolicy.Repository,
                ReleaseDescriptorPolicy.Channel,
                ReleaseDescriptorPolicy.KeyId),
            "legacy" => new ReleaseIdentity(
                ReleaseDescriptorPolicy.LegacyProduct,
                ReleaseDescriptorPolicy.LegacyRepository,
                ReleaseDescriptorPolicy.LegacyChannel,
                ReleaseDescriptorPolicy.LegacyKeyId),
            _ => throw new ArgumentException(
                "The release identity must be current or legacy.")
        };
        if (repository != identity.Repository || channel != identity.Channel)
            throw new ArgumentException("Repository or channel does not match release policy.");
        if (!Version.TryParse(versionText, out var version) ||
            version.Build < 0 || version.Revision >= 0 ||
            version.ToString(3) != versionText || tag != $"v{versionText}")
        {
            throw new ArgumentException(
                "Version must be a three-part stable version matching the tag.");
        }

        var package = new FileInfo(packagePath);
        if (package.Length is < ReleaseDescriptorPolicy.MinimumPackageSize or
            > ReleaseDescriptorPolicy.MaximumPackageSize ||
            !package.Name.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The release package has an invalid name or size.");
        }
        return new ReleaseDescriptor(
            ReleaseDescriptorPolicy.SchemaVersion,
            identity.Product,
            repository,
            channel,
            identity.KeyId,
            versionText,
            tag,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            package.Name,
            package.Length,
            await ComputeSha256Async(packagePath),
            NormalizeReleaseNotes(await File.ReadAllTextAsync(notesPath)),
            string.Empty);
    }

    private static async Task VerifyAsync(
        IReadOnlyDictionary<string, string> options)
    {
        var manifestPath = RequireFile(options, "manifest");
        var packagePath = RequireFile(options, "package");
        var publicKeyPath = RequireFile(options, "public-key");
        var package = new FileInfo(packagePath);
        var descriptor = ReleaseDescriptorPolicy.Deserialize(
            await File.ReadAllTextAsync(manifestPath));
        var identity = new ReleaseAssetIdentity(
            descriptor.Version,
            package.Name,
            package.Length,
            await ComputeSha256Async(packagePath));
        var verified = ReleaseDescriptorPolicy.Verify(
            await File.ReadAllTextAsync(manifestPath),
            identity,
            await File.ReadAllTextAsync(publicKeyPath));
        Console.WriteLine(
            $"Verified {verified.Descriptor.Tag} ({verified.Descriptor.PackageFile}).");
    }

    private static async Task PrepareCatalogAsync(
        IReadOnlyDictionary<string, string> options)
    {
        var manifestPath = RequireFile(options, "manifest");
        var expectedVersion = RequireStableVersion(
            options,
            "sessiondock-version");
        var verified = HandleScopeCompatibilityCatalogPolicy.VerifyEmbedded(
            await File.ReadAllTextAsync(manifestPath));
        EnsureCatalogReleaseBinding(verified, expectedVersion);
        await EnsureCatalogFreshnessAsync(verified, options);
        if (verified.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentException(
                "The HandleScope compatibility catalog is already expired.");
        }

        await WriteCatalogAsync(Require(options, "output"), verified.Catalog);
        var digest = SHA256.HashData(
            HandleScopeCompatibilityCatalogPolicy.CreateCanonicalPayload(
                verified.Catalog));
        await WriteCanonicalTextAsync(
            Require(options, "digest-output"),
            Base64UrlEncode(digest));
    }

    private static async Task CompleteCatalogAsync(
        IReadOnlyDictionary<string, string> options)
    {
        var manifestPath = RequireFile(options, "manifest");
        var signaturePath = RequireFile(options, "signature-file");
        var publicKeyPath = RequireFile(options, "public-key");
        var expectedVersion = RequireStableVersion(
            options,
            "sessiondock-version");
        var unsigned = HandleScopeCompatibilityCatalogPolicy.VerifyEmbedded(
            await File.ReadAllTextAsync(manifestPath));
        EnsureCatalogReleaseBinding(unsigned, expectedVersion);
        if (unsigned.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentException(
                "The HandleScope compatibility catalog is already expired.");
        }

        var signature = await ReadManagedSignatureAsync(signaturePath);
        var signed = unsigned.Catalog with
        {
            Signature = Convert.ToBase64String(signature)
        };
        var verified = HandleScopeCompatibilityCatalogPolicy.Verify(
            HandleScopeCompatibilityCatalogPolicy.Serialize(signed),
            await File.ReadAllTextAsync(publicKeyPath));
        EnsureCatalogReleaseBinding(verified, expectedVersion);
        await WriteCatalogAsync(Require(options, "output"), signed);
    }

    private static async Task VerifyCatalogAsync(
        IReadOnlyDictionary<string, string> options)
    {
        var manifestPath = RequireFile(options, "manifest");
        var publicKeyPath = RequireFile(options, "public-key");
        var expectedVersion = RequireStableVersion(
            options,
            "sessiondock-version");
        var verified = HandleScopeCompatibilityCatalogPolicy.Verify(
            await File.ReadAllTextAsync(manifestPath),
            await File.ReadAllTextAsync(publicKeyPath));
        EnsureCatalogReleaseBinding(verified, expectedVersion);
        Console.WriteLine(
            $"Verified HandleScope compatibility catalog sequence {verified.Catalog.Sequence} for SessionDock {verified.SessionDockVersion}.");
    }

    private static Dictionary<string, string> ParseOptions(
        string[] arguments,
        HashSet<string> allowed)
    {
        if (arguments.Length % 2 != 0)
            throw new ArgumentException("Every option must have a value.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index += 2)
        {
            var option = arguments[index];
            if (!option.StartsWith("--", StringComparison.Ordinal) ||
                option.Length < 3)
            {
                throw new ArgumentException($"Invalid option '{option}'.");
            }
            var name = option[2..];
            if (!allowed.Contains(name) ||
                !result.TryAdd(name, arguments[index + 1]))
            {
                throw new ArgumentException(
                    $"Unsupported or duplicate option '--{name}'.");
            }
        }
        return result;
    }

    private static string Require(
        IReadOnlyDictionary<string, string> options,
        string name)
    {
        if (!options.TryGetValue(name, out var value) ||
            string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Missing required option '--{name}'.");
        }
        return value;
    }

    private static string RequireFile(
        IReadOnlyDictionary<string, string> options,
        string name)
    {
        var path = Path.GetFullPath(Require(options, name));
        if (!File.Exists(path))
            throw new ArgumentException($"The --{name} file does not exist.");
        return path;
    }

    private static string RequireDirectory(
        IReadOnlyDictionary<string, string> options,
        string name)
    {
        var path = Path.GetFullPath(Require(options, name));
        if (!Directory.Exists(path))
            throw new ArgumentException($"The --{name} directory does not exist.");
        return path;
    }

    private static Version RequireStableVersion(
        IReadOnlyDictionary<string, string> options,
        string name)
    {
        var value = Require(options, name);
        if (!Version.TryParse(value, out var version) ||
            version.Build < 0 || version.Revision >= 0 ||
            version.ToString(3) != value)
        {
            throw new ArgumentException(
                $"The --{name} value must be a three-part stable version.");
        }
        return version;
    }

    private static void EnsureCatalogReleaseBinding(
        VerifiedHandleScopeCompatibilityCatalog catalog,
        Version expectedSessionDockVersion)
    {
        if (catalog.SessionDockVersion != expectedSessionDockVersion)
        {
            throw new ArgumentException(
                "The HandleScope compatibility catalog does not match the SessionDock release version.");
        }
    }

    private static async Task EnsureCatalogFreshnessAsync(
        VerifiedHandleScopeCompatibilityCatalog candidate,
        IReadOnlyDictionary<string, string> options)
    {
        var hasPriorManifest = options.ContainsKey("prior-manifest");
        var hasPriorDirectory = options.ContainsKey("prior-directory");
        var hasPublicKey = options.ContainsKey("public-key");
        if (hasPriorManifest && hasPriorDirectory)
        {
            throw new ArgumentException(
                "Specify either one prior catalog or a prior catalog directory, not both.");
        }
        var hasPriorCatalogs = hasPriorManifest || hasPriorDirectory;
        if (hasPriorCatalogs != hasPublicKey)
        {
            throw new ArgumentException(
                "Prior catalogs and the public key must be supplied together.");
        }
        if (!hasPriorCatalogs)
        {
            if (candidate.Catalog.Sequence != 1)
            {
                throw new ArgumentException(
                    "The first published HandleScope compatibility catalog must use sequence 1.");
            }
            return;
        }

        string[] priorPaths;
        if (hasPriorManifest)
        {
            priorPaths = [RequireFile(options, "prior-manifest")];
        }
        else
        {
            var priorDirectory = RequireDirectory(options, "prior-directory");
            var entries = Directory.GetFileSystemEntries(
                priorDirectory,
                "*",
                SearchOption.TopDirectoryOnly);
            if (entries.Length is <= 0 or > 10_000 ||
                entries.Any(path =>
                    !File.Exists(path) ||
                    new FileInfo(path).LinkTarget is not null))
            {
                throw new ArgumentException(
                    "The prior catalog directory must contain only 1 to 10,000 regular files.");
            }
            priorPaths = entries.Order(StringComparer.Ordinal).ToArray();
        }

        var publicKeyPath = RequireFile(options, "public-key");
        var publicKey = await File.ReadAllTextAsync(publicKeyPath);
        long maximumSequence = 0;
        var maximumGeneratedAt = DateTimeOffset.MinValue;
        foreach (var priorPath in priorPaths)
        {
            var priorJson = await File.ReadAllTextAsync(priorPath);
            var priorCatalog = HandleScopeCompatibilityCatalogPolicy.Deserialize(
                priorJson);
            if (!DateTimeOffset.TryParseExact(
                    priorCatalog.GeneratedAt,
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var priorGeneratedAt) ||
                priorGeneratedAt.Offset != TimeSpan.Zero)
            {
                throw new ArgumentException(
                    "A prior HandleScope catalog generation time is invalid.");
            }
            var prior = HandleScopeCompatibilityCatalogPolicy.Verify(
                priorJson,
                publicKey,
                priorGeneratedAt);
            maximumSequence = Math.Max(
                maximumSequence,
                prior.Catalog.Sequence);
            maximumGeneratedAt = prior.GeneratedAt > maximumGeneratedAt
                ? prior.GeneratedAt
                : maximumGeneratedAt;
        }

        if (candidate.Catalog.Sequence <= maximumSequence ||
            candidate.GeneratedAt <= maximumGeneratedAt)
        {
            throw new ArgumentException(
                "The HandleScope compatibility catalog must strictly advance the authenticated historical sequence and generation-time maxima.");
        }
    }

    private static string NormalizeReleaseNotes(string value)
    {
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.Length > ReleaseDescriptorPolicy.MaximumReleaseNotesLength ||
            normalized.Any(character =>
                char.IsControl(character) && character is not ('\n' or '\t')))
        {
            throw new ArgumentException(
                "Release notes are empty, too large, or contain control characters.");
        }
        return normalized;
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static async Task WriteDescriptorAsync(
        string path,
        ReleaseDescriptor descriptor) =>
        await WriteCanonicalTextAsync(
            path,
            ReleaseDescriptorPolicy.Serialize(descriptor).TrimEnd('\n'));

    private static async Task WriteCatalogAsync(
        string path,
        HandleScopeCompatibilityCatalog catalog) =>
        await WriteCanonicalTextAsync(
            path,
            HandleScopeCompatibilityCatalogPolicy.Serialize(catalog)
                .TrimEnd('\n'));

    private static async Task WriteCanonicalTextAsync(string path, string value)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(fullPath, value + "\n");
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static async Task<byte[]> ReadManagedSignatureAsync(string path)
    {
        var signatureText = (await File.ReadAllTextAsync(path)).Trim();
        if (signatureText.Length is <= 0 or > 256 ||
            signatureText.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException(
                "The managed signature must be one canonical base64url value.");
        }
        var signature = Base64UrlDecode(signatureText);
        if (signature.Length != 64)
        {
            throw new ArgumentException(
                "The managed signer must return one 64-byte P-256 ES256 signature.");
        }
        return signature;
    }

    private static byte[] Base64UrlDecode(string value)
    {
        if (value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new ArgumentException("The managed signature is not canonical base64url.");
        }
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 += new string('=', (4 - base64.Length % 4) % 4);
        try { return Convert.FromBase64String(base64); }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                "The managed signature is not canonical base64url.",
                exception);
        }
    }

    private sealed record ReleaseIdentity(
        string Product,
        string Repository,
        string Channel,
        string KeyId);
}
