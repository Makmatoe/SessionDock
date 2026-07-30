using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SessionDock.Models;

namespace SessionDock.Services;

internal static class MetadataTransferService
{
    internal const string FormatName = "sessiondock.metadata";
    internal const int CurrentVersion = 1;
    internal const int MaximumFileBytes = 256 * 1024;
    internal const int MaximumAccounts = 128;
    internal const int MaximumPublicFavorites = 50;
    private const int MaximumAccountLabelLength = 40;
    private const int MaximumExperienceNameLength = 200;
    private const int MaximumCustomNameLength = 80;
    private static readonly string[] PreviewLocalizationKeys =
    [
        "Metadata.Preview.Title",
        "Metadata.Preview.Format",
        "Metadata.Preview.MatchedAccounts",
        "Metadata.Preview.AppearanceUpdates",
        "Metadata.Preview.AccountOrder",
        "Metadata.Preview.OrderWillUpdate",
        "Metadata.Preview.OrderMatches",
        "Metadata.Preview.FavoritesToAdd",
        "Metadata.Preview.FavoritesToUpdate",
        "Metadata.Preview.NeverImportedHeading",
        "Metadata.Preview.NeverImportedSignIns",
        "Metadata.Preview.NeverImportedAccountData",
        "Metadata.Preview.NeverImportedPrivateData",
        "Metadata.Preview.NeverImportedSettings",
        "Metadata.Preview.MatchedAppearanceHeading",
        "Metadata.Preview.OrderMovesHeading",
        "Metadata.Preview.FavoriteChangesHeading",
        "Metadata.Preview.SkippedHeading",
        "Metadata.Preview.SkippedAccountOne",
        "Metadata.Preview.SkippedAccountMany",
        "Metadata.Preview.SkippedFavoriteOne",
        "Metadata.Preview.SkippedFavoriteMany",
        "Metadata.Preview.AccountDetail",
        "Metadata.Preview.FavoriteAdd",
        "Metadata.Preview.FavoriteUpdate",
        "Metadata.Preview.OrderMove",
        "Metadata.Preview.NotSet",
        "Metadata.Preview.Default",
        "Metadata.Preview.Yes",
        "Metadata.Preview.YesUnchanged",
        "Metadata.Preview.NoToYes",
        "Metadata.Preview.Unchanged",
        "Metadata.Preview.Clear",
        "Metadata.Preview.Transition",
        "Metadata.Preview.SharedHistory",
        "Metadata.Preview.RobloxUser"
    ];
    private static readonly Lazy<LocalizedTextSnapshot> EnglishLocalization =
        new(() => CreateLocalizationSnapshot(
            CultureInfo.GetCultureInfo(LocalizationPreference.English)));
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.Default,
        MaxDepth = 16,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    internal static MetadataExportPackage CreateExport(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var accounts = settings.Accounts
            .Where(account => account.UserId > 0)
            .GroupBy(account => account.UserId)
            .Select(group => group.First())
            .Take(MaximumAccounts)
            .Select(account => new TransferAccount
            {
                RobloxUserId = account.UserId,
                Label = NormalizeOptionalText(
                    account.Label,
                    MaximumAccountLabelLength),
                Group = NormalizeOptionalText(
                    account.Group,
                    BatchLaunchPreferences.MaximumAccountGroupLength),
                Color = NormalizeColor(account.ColorHex)
            })
            .ToList();
        var exportedAccountIds = accounts
            .Select(account => account.RobloxUserId)
            .ToHashSet();
        var favorites = new List<TransferPublicFavorite>();
        var favoriteKeys = new HashSet<(long AccountUserId, long PlaceId)>();
        foreach (var recent in settings.RecentExperiences)
        {
            if (favorites.Count >= MaximumPublicFavorites)
                break;
            if (!TryGetSafePublicFavorite(
                    recent,
                    exportedAccountIds,
                    out var favorite) ||
                !favoriteKeys.Add((favorite.AccountUserId, favorite.PlaceId)))
            {
                continue;
            }

            favorites.Add(favorite);
        }

        var document = new MetadataTransferDocument
        {
            Format = FormatName,
            Version = CurrentVersion,
            Accounts = accounts,
            PublicFavorites = favorites
        };
        var json = JsonSerializer.Serialize(document, JsonOptions) +
            Environment.NewLine;
        if (Encoding.UTF8.GetByteCount(json) > MaximumFileBytes)
        {
            throw new InvalidDataException(
                "The safe metadata preview is unexpectedly too large.");
        }

        return new MetadataExportPackage(
            json,
            accounts.Count,
            favorites.Count);
    }

    internal static async Task ExportAsync(
        string path,
        MetadataExportPackage package,
        CancellationToken cancellationToken = default) =>
        await ExportAsync(
            path,
            package,
            CommitExport,
            cancellationToken).ConfigureAwait(false);

    internal static async Task ExportAsync(
        string path,
        MetadataExportPackage package,
        Action<string, string, bool> commitExport,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(commitExport);
        cancellationToken.ThrowIfCancellationRequested();
        var destinationExists = RejectUnsafeOutputPath(path);
        var contents = Encoding.UTF8.GetBytes(package.Json);
        if (contents.Length > MaximumFileBytes)
        {
            throw new InvalidDataException(
                "The safe metadata preview is unexpectedly too large.");
        }

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ??
            throw new DirectoryNotFoundException(
                "The selected export folder does not exist.");
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             8192,
                             FileOptions.Asynchronous |
                             FileOptions.SequentialScan))
            {
                await stream.WriteAsync(contents, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            commitExport(temporaryPath, fullPath, destinationExists);
            temporaryPath = string.Empty;
        }
        finally
        {
            DeleteTemporaryExportBestEffort(temporaryPath);
        }
    }

    internal static async Task<MetadataImportPlan> ReadImportAsync(
        string path,
        AppSettings currentSettings,
        CancellationToken cancellationToken = default)
    {
        return await ReadImportAsync(
            path,
            currentSettings,
            EnglishLocalization.Value,
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<MetadataImportPlan> ReadImportAsync(
        string path,
        AppSettings currentSettings,
        AppLocalizationService localization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localization);
        var snapshot = LocalizedTextSnapshot.Capture(
            localization,
            PreviewLocalizationKeys);
        return await ReadImportAsync(
            path,
            currentSettings,
            snapshot,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<MetadataImportPlan> ReadImportAsync(
        string path,
        AppSettings currentSettings,
        LocalizedTextSnapshot localization,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(currentSettings);
        ArgumentNullException.ThrowIfNull(localization);
        var contents = await ReadBoundedFileAsync(path, cancellationToken)
            .ConfigureAwait(false);
        return CreateImportPlan(contents, currentSettings, localization);
    }

    internal static MetadataImportPlan CreateImportPlan(
        ReadOnlySpan<byte> contents,
        AppSettings currentSettings)
    {
        return CreateImportPlan(
            contents,
            currentSettings,
            EnglishLocalization.Value);
    }

    internal static MetadataImportPlan CreateImportPlan(
        ReadOnlySpan<byte> contents,
        AppSettings currentSettings,
        AppLocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(localization);
        return CreateImportPlan(
            contents,
            currentSettings,
            LocalizedTextSnapshot.Capture(
                localization,
                PreviewLocalizationKeys));
    }

    internal static LocalizedTextSnapshot CreateLocalizationSnapshot(
        CultureInfo culture) =>
        LocalizedTextSnapshot.FromResources(
            culture,
            PreviewLocalizationKeys);

    internal static MetadataImportPlan CreateImportPlan(
        ReadOnlySpan<byte> contents,
        AppSettings currentSettings,
        LocalizedTextSnapshot localization)
    {
        ArgumentNullException.ThrowIfNull(currentSettings);
        ArgumentNullException.ThrowIfNull(localization);
        if (contents.IsEmpty)
            throw new InvalidDataException("The metadata file is empty.");
        if (contents.Length > MaximumFileBytes)
            throw new InvalidDataException("The metadata file is too large.");

        MetadataTransferDocument document;
        try
        {
            using var jsonDocument = JsonDocument.Parse(
                contents.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16
                });
            RejectDuplicateProperties(jsonDocument.RootElement);
            document = JsonSerializer.Deserialize<MetadataTransferDocument>(
                    contents,
                    JsonOptions) ??
                throw new InvalidDataException(
                    "The metadata file does not contain a document.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The metadata file is not valid SessionDock JSON.",
                exception);
        }

        ValidateDocument(document);
        return BuildImportPlan(document, currentSettings, localization);
    }

    private static MetadataImportPlan BuildImportPlan(
        MetadataTransferDocument document,
        AppSettings currentSettings,
        LocalizedTextSnapshot localization)
    {
        var currentAccounts = currentSettings.Accounts
            .Where(account => account.UserId > 0)
            .GroupBy(account => account.UserId)
            .ToDictionary(group => group.Key, group => group.First());
        var matchedAccounts = new List<ImportedAccountMetadata>();
        var accountPreviewDetails = new List<string>();
        var skippedAccountCount = 0;
        var accountUpdateCount = 0;
        foreach (var imported in document.Accounts!)
        {
            if (!currentAccounts.TryGetValue(
                    imported.RobloxUserId,
                    out var current))
            {
                skippedAccountCount++;
                continue;
            }

            var metadata = new ImportedAccountMetadata(
                imported.RobloxUserId,
                imported.Label,
                imported.Group,
                imported.Color);
            matchedAccounts.Add(metadata);
            accountPreviewDetails.Add(
                localization.Format(
                    "Metadata.Preview.AccountDetail",
                    metadata.RobloxUserId,
                    DescribeTransition(
                        current.Label,
                        metadata.Label,
                        "Metadata.Preview.NotSet",
                        localization),
                    DescribeTransition(
                        current.Group,
                        metadata.Group,
                        "Metadata.Preview.NotSet",
                        localization),
                    DescribeTransition(
                        current.ColorHex,
                        metadata.Color,
                        "Metadata.Preview.Default",
                        localization)));
            if (!metadata.Matches(current))
                accountUpdateCount++;
        }

        var desiredMatchedOrder = matchedAccounts
            .Select(account => account.RobloxUserId)
            .ToArray();
        var currentMatchedOrder = currentSettings.Accounts
            .Where(account => desiredMatchedOrder.Contains(account.UserId))
            .Select(account => account.UserId)
            .ToArray();
        var orderWillChange = !currentMatchedOrder.SequenceEqual(
            desiredMatchedOrder);
        var orderChangeDetails = BuildOrderChangeDetails(
            currentSettings.Accounts,
            desiredMatchedOrder,
            localization);

        var favoriteActions = new List<ImportedFavoriteMetadata>();
        var favoriteChangeDetails = new List<string>();
        var skippedFavoriteCount = 0;
        var favoritesToAdd = 0;
        var favoritesToUpdate = 0;
        var pinnedCapacity = Math.Max(
            0,
            MaximumPublicFavorites -
            currentSettings.RecentExperiences.Count(item => item.IsPinned));
        foreach (var imported in document.PublicFavorites!)
        {
            if (imported.AccountUserId != 0 &&
                !currentAccounts.ContainsKey(imported.AccountUserId))
            {
                skippedFavoriteCount++;
                continue;
            }

            var existing = currentSettings.RecentExperiences.FirstOrDefault(
                recent => IsMatchingPublicFavorite(recent, imported));
            var needsPinnedSlot = existing is null || !existing.IsPinned;
            if (needsPinnedSlot && pinnedCapacity == 0)
            {
                skippedFavoriteCount++;
                continue;
            }
            if (needsPinnedSlot)
                pinnedCapacity--;

            var action = new ImportedFavoriteMetadata(
                imported.PlaceId,
                imported.AccountUserId,
                imported.Name,
                imported.CustomName);
            if (existing is null)
            {
                favoritesToAdd++;
                favoriteActions.Add(action);
                favoriteChangeDetails.Add(
                    localization.Format(
                        "Metadata.Preview.FavoriteAdd",
                        action.PlaceId,
                        DescribeFavoriteOwner(
                            action.AccountUserId,
                            localization),
                        DescribeValue(
                            action.Name,
                            localization.GetString(
                                "Metadata.Preview.NotSet")),
                        DescribeValue(
                            action.CustomName,
                            localization.GetString(
                                "Metadata.Preview.NotSet")),
                        localization.GetString("Metadata.Preview.Yes")));
                continue;
            }

            if (!existing.IsPinned ||
                !string.Equals(
                    existing.Name,
                    imported.Name,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    existing.CustomName,
                    imported.CustomName,
                    StringComparison.Ordinal))
            {
                favoritesToUpdate++;
                favoriteActions.Add(action);
                favoriteChangeDetails.Add(
                    localization.Format(
                        "Metadata.Preview.FavoriteUpdate",
                        action.PlaceId,
                        DescribeFavoriteOwner(
                            action.AccountUserId,
                            localization),
                        DescribeTransition(
                            existing.Name,
                            action.Name,
                            "Metadata.Preview.NotSet",
                            localization),
                        DescribeTransition(
                            existing.CustomName,
                            action.CustomName,
                            "Metadata.Preview.NotSet",
                            localization),
                        localization.GetString(
                            existing.IsPinned
                                ? "Metadata.Preview.YesUnchanged"
                                : "Metadata.Preview.NoToYes")));
            }
        }

        var preview = BuildPreview(
            matchedAccounts.Count,
            skippedAccountCount,
            accountUpdateCount,
            orderWillChange,
            favoritesToAdd,
            favoritesToUpdate,
            skippedFavoriteCount,
            accountPreviewDetails,
            orderChangeDetails,
            favoriteChangeDetails,
            localization);
        return new MetadataImportPlan(
            matchedAccounts,
            desiredMatchedOrder,
            favoriteActions,
            accountUpdateCount,
            orderWillChange,
            favoritesToAdd,
            favoritesToUpdate,
            skippedAccountCount,
            skippedFavoriteCount,
            preview);
    }

    private static string BuildPreview(
        int matchedAccounts,
        int skippedAccounts,
        int accountUpdates,
        bool orderWillChange,
        int favoritesToAdd,
        int favoritesToUpdate,
        int skippedFavorites,
        IReadOnlyList<string> accountPreviewDetails,
        IReadOnlyList<string> orderChangeDetails,
        IReadOnlyList<string> favoriteChangeDetails,
        LocalizedTextSnapshot localization)
    {
        var lines = new List<string>
        {
            localization.GetString("Metadata.Preview.Title"),
            string.Empty,
            localization.Format(
                "Metadata.Preview.Format",
                FormatName,
                CurrentVersion),
            localization.Format(
                "Metadata.Preview.MatchedAccounts",
                matchedAccounts),
            localization.Format(
                "Metadata.Preview.AppearanceUpdates",
                accountUpdates),
            localization.Format(
                "Metadata.Preview.AccountOrder",
                localization.GetString(
                    orderWillChange
                        ? "Metadata.Preview.OrderWillUpdate"
                        : "Metadata.Preview.OrderMatches")),
            localization.Format(
                "Metadata.Preview.FavoritesToAdd",
                favoritesToAdd),
            localization.Format(
                "Metadata.Preview.FavoritesToUpdate",
                favoritesToUpdate),
            string.Empty,
            localization.GetString("Metadata.Preview.NeverImportedHeading"),
            localization.GetString("Metadata.Preview.NeverImportedSignIns"),
            localization.GetString("Metadata.Preview.NeverImportedAccountData"),
            localization.GetString("Metadata.Preview.NeverImportedPrivateData"),
            localization.GetString("Metadata.Preview.NeverImportedSettings")
        };
        if (accountPreviewDetails.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add(localization.GetString(
                "Metadata.Preview.MatchedAppearanceHeading"));
            lines.AddRange(accountPreviewDetails.Select(detail => $"- {detail}"));
        }
        if (orderChangeDetails.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add(localization.GetString(
                "Metadata.Preview.OrderMovesHeading"));
            lines.AddRange(orderChangeDetails.Select(detail => $"- {detail}"));
        }
        if (favoriteChangeDetails.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add(localization.GetString(
                "Metadata.Preview.FavoriteChangesHeading"));
            lines.AddRange(favoriteChangeDetails.Select(detail => $"- {detail}"));
        }
        if (skippedAccounts > 0 || skippedFavorites > 0)
        {
            lines.Add(string.Empty);
            lines.Add(localization.GetString(
                "Metadata.Preview.SkippedHeading"));
            if (skippedAccounts > 0)
            {
                lines.Add(localization.Format(
                    skippedAccounts == 1
                        ? "Metadata.Preview.SkippedAccountOne"
                        : "Metadata.Preview.SkippedAccountMany",
                    skippedAccounts));
            }
            if (skippedFavorites > 0)
            {
                lines.Add(localization.Format(
                    skippedFavorites == 1
                        ? "Metadata.Preview.SkippedFavoriteOne"
                        : "Metadata.Preview.SkippedFavoriteMany",
                    skippedFavorites));
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static IReadOnlyList<string> BuildOrderChangeDetails(
        IReadOnlyList<AccountProfile> currentAccounts,
        IReadOnlyList<long> desiredMatchedOrder,
        LocalizedTextSnapshot localization)
    {
        var remainingMatchedIds = desiredMatchedOrder.ToHashSet();
        var desiredQueue = new Queue<long>(desiredMatchedOrder);
        var currentOrder = currentAccounts
            .Select(account => account.UserId)
            .ToArray();
        var projectedOrder = currentAccounts
            .Select(account =>
                remainingMatchedIds.Remove(account.UserId) &&
                desiredQueue.Count > 0
                    ? desiredQueue.Dequeue()
                    : account.UserId)
            .ToArray();
        var details = new List<string>();
        foreach (var userId in desiredMatchedOrder)
        {
            var currentIndex = Array.IndexOf(currentOrder, userId);
            var projectedIndex = Array.IndexOf(projectedOrder, userId);
            if (currentIndex >= 0 &&
                projectedIndex >= 0 &&
                currentIndex != projectedIndex)
            {
                details.Add(
                    localization.Format(
                        "Metadata.Preview.OrderMove",
                        userId,
                        currentIndex + 1,
                        projectedIndex + 1));
            }
        }

        return details;
    }

    private static string DescribeTransition(
        string? current,
        string? imported,
        string emptyDescriptionKey,
        LocalizedTextSnapshot localization)
    {
        var emptyDescription = localization.GetString(emptyDescriptionKey);
        var currentDescription = DescribeValue(current, emptyDescription);
        if (string.Equals(current, imported, StringComparison.Ordinal))
        {
            return localization.Format(
                "Metadata.Preview.Unchanged",
                currentDescription);
        }
        var importedDescription = imported is null
            ? localization.Format(
                "Metadata.Preview.Clear",
                emptyDescription)
            : DescribeValue(imported, emptyDescription);
        return localization.Format(
            "Metadata.Preview.Transition",
            currentDescription,
            importedDescription);
    }

    private static string DescribeValue(
        string? value,
        string emptyDescription) =>
        value is null
            ? emptyDescription
            : JsonSerializer.Serialize(value);

    private static string DescribeFavoriteOwner(
        long accountUserId,
        LocalizedTextSnapshot localization) =>
        accountUserId == 0
            ? localization.GetString("Metadata.Preview.SharedHistory")
            : localization.Format(
                "Metadata.Preview.RobloxUser",
                accountUserId);


    private static void ValidateDocument(MetadataTransferDocument document)
    {
        if (!string.Equals(document.Format, FormatName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "This is not a SessionDock metadata file.");
        }
        if (document.Version != CurrentVersion)
        {
            throw new InvalidDataException(
                "This metadata file version is not supported.");
        }
        if (document.Accounts is null || document.PublicFavorites is null)
        {
            throw new InvalidDataException(
                "The metadata file is missing a required list.");
        }
        if (document.Accounts.Count > MaximumAccounts)
            throw new InvalidDataException("The metadata file has too many accounts.");
        if (document.PublicFavorites.Count > MaximumPublicFavorites)
        {
            throw new InvalidDataException(
                "The metadata file has too many public favorites.");
        }

        var accountIds = new HashSet<long>();
        foreach (var account in document.Accounts)
        {
            if (account is null || account.RobloxUserId <= 0)
            {
                throw new InvalidDataException(
                    "The metadata file contains an invalid account matcher.");
            }
            if (!accountIds.Add(account.RobloxUserId))
            {
                throw new InvalidDataException(
                    "The metadata file contains a duplicate account matcher.");
            }
            ValidateOptionalText(
                account.Label,
                MaximumAccountLabelLength,
                "account label");
            ValidateOptionalText(
                account.Group,
                BatchLaunchPreferences.MaximumAccountGroupLength,
                "account group");
            if (account.Color is not null &&
                NormalizeColor(account.Color) != account.Color)
            {
                throw new InvalidDataException(
                    "The metadata file contains an unsupported account color.");
            }
        }

        var favoriteKeys = new HashSet<(long AccountUserId, long PlaceId)>();
        foreach (var favorite in document.PublicFavorites)
        {
            if (favorite is null ||
                favorite.PlaceId <= 0 ||
                favorite.AccountUserId < 0)
            {
                throw new InvalidDataException(
                    "The metadata file contains an invalid public favorite.");
            }
            if (!favoriteKeys.Add((favorite.AccountUserId, favorite.PlaceId)))
            {
                throw new InvalidDataException(
                    "The metadata file contains a duplicate public favorite.");
            }
            ValidateOptionalText(
                favorite.Name,
                MaximumExperienceNameLength,
                "experience name");
            ValidateOptionalText(
                favorite.CustomName,
                MaximumCustomNameLength,
                "favorite name");
        }
    }

    private static void ValidateOptionalText(
        string? value,
        int maximumLength,
        string description)
    {
        if (value is null)
            return;
        if (!string.Equals(
                value,
                NormalizeOptionalText(value, maximumLength),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The metadata file contains an invalid {description}.");
        }
    }

    private static bool TryGetSafePublicFavorite(
        RecentExperience recent,
        IReadOnlySet<long> exportedAccountIds,
        out TransferPublicFavorite favorite)
    {
        favorite = null!;
        if (!recent.IsPinned ||
            recent.IsPrivateServer ||
            recent.ServerJobId is not null ||
            recent.PlaceId <= 0 ||
            recent.AccountUserId < 0 ||
            recent.AccountUserId != 0 &&
            !exportedAccountIds.Contains(recent.AccountUserId) ||
            !DestinationParser.TryParse(
                recent.Destination,
                out var target,
                out _) ||
            target!.IsPrivateServer ||
            target.PlaceId != recent.PlaceId)
        {
            return false;
        }

        favorite = new TransferPublicFavorite
        {
            PlaceId = recent.PlaceId,
            AccountUserId = recent.AccountUserId,
            Name = NormalizeOptionalText(
                recent.Name,
                MaximumExperienceNameLength),
            CustomName = NormalizeOptionalText(
                recent.CustomName,
                MaximumCustomNameLength)
        };
        return true;
    }

    private static bool IsMatchingPublicFavorite(
        RecentExperience recent,
        TransferPublicFavorite imported) =>
        recent.AccountUserId == imported.AccountUserId &&
        recent.PlaceId == imported.PlaceId &&
        !recent.IsPrivateServer &&
        DestinationParser.TryParse(recent.Destination, out var target, out _) &&
        !target!.IsPrivateServer &&
        target.PlaceId == imported.PlaceId;

    private static string? NormalizeColor(string? color) =>
        SettingsService.AccountColors.Contains(
            color ?? string.Empty,
            StringComparer.OrdinalIgnoreCase)
            ? color!.ToUpperInvariant()
            : null;

    private static string? NormalizeOptionalText(
        string? value,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var builder = new StringBuilder(Math.Min(value.Length, maximumLength));
        var pendingSpace = false;
        foreach (var rune in value.Trim().EnumerateRunes())
        {
            if (Rune.IsControl(rune) || Rune.IsWhiteSpace(rune))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            var requiredLength = rune.Utf16SequenceLength +
                (pendingSpace ? 1 : 0);
            if (builder.Length + requiredLength > maximumLength)
                break;
            if (pendingSpace)
                builder.Append(' ');
            pendingSpace = false;
            builder.Append(rune.ToString());
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException(
                        "The metadata file contains a duplicate JSON property.");
                }
                RejectDuplicateProperties(property.Value);
            }
            return;
        }

        if (element.ValueKind != JsonValueKind.Array)
            return;
        foreach (var item in element.EnumerateArray())
            RejectDuplicateProperties(item);
    }

    private static async Task<byte[]> ReadBoundedFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new FileNotFoundException(
                "The metadata file could not be found.",
                path,
                exception);
        }
        if ((attributes & FileAttributes.Directory) != 0)
            throw new IOException("The selected metadata path is a directory.");
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                "Metadata files reached through links are not accepted.");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            8192,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumFileBytes)
            throw new InvalidDataException("The metadata file is too large.");
        using var output = new MemoryStream((int)stream.Length);
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                return output.ToArray();
            if (output.Length + read > MaximumFileBytes)
                throw new InvalidDataException("The metadata file is too large.");
            output.Write(buffer, 0, read);
        }
    }

    private static bool RejectUnsafeOutputPath(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0)
                throw new IOException("The selected export path is a directory.");
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    "Metadata cannot be exported through a file-system link.");
            }
            return true;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                throw new DirectoryNotFoundException(
                    "The selected export folder does not exist.");
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    "Metadata cannot be exported through a file-system link.");
            }
            return false;
        }
    }

    private static void CommitExport(
        string temporaryPath,
        string destinationPath,
        bool destinationExists)
    {
        if (destinationExists)
        {
            File.Replace(
                temporaryPath,
                destinationPath,
                destinationBackupFileName: null,
                ignoreMetadataErrors: true);
            return;
        }

        File.Move(temporaryPath, destinationPath);
    }

    private static void DeleteTemporaryExportBestEffort(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                System.Security.SecurityException)
        {
            System.Diagnostics.Trace.WriteLine(
                $"Temporary metadata export cleanup failed: {exception.GetType().Name}.");
        }
    }

    private sealed class MetadataTransferDocument
    {
        public string? Format { get; set; }
        public int Version { get; set; }
        public List<TransferAccount>? Accounts { get; set; }
        public List<TransferPublicFavorite>? PublicFavorites { get; set; }
    }

    private sealed class TransferAccount
    {
        public long RobloxUserId { get; set; }
        public string? Label { get; set; }
        public string? Group { get; set; }
        public string? Color { get; set; }
    }

    private sealed class TransferPublicFavorite
    {
        public long PlaceId { get; set; }
        public long AccountUserId { get; set; }
        public string? Name { get; set; }
        public string? CustomName { get; set; }
    }
}

internal sealed record MetadataExportPackage(
    string Json,
    int AccountCount,
    int PublicFavoriteCount)
{
    internal const string SuggestedFileName = "SessionDock-metadata.json";
}

internal sealed class MetadataImportPlan
{
    private readonly IReadOnlyList<ImportedAccountMetadata> _accounts;
    private readonly IReadOnlyList<long> _accountOrder;
    private readonly IReadOnlyList<ImportedFavoriteMetadata> _favorites;

    internal MetadataImportPlan(
        IReadOnlyList<ImportedAccountMetadata> accounts,
        IReadOnlyList<long> accountOrder,
        IReadOnlyList<ImportedFavoriteMetadata> favorites,
        int accountUpdateCount,
        bool orderWillChange,
        int favoritesToAdd,
        int favoritesToUpdate,
        int skippedAccountCount,
        int skippedFavoriteCount,
        string preview)
    {
        _accounts = accounts;
        _accountOrder = accountOrder;
        _favorites = favorites;
        AccountUpdateCount = accountUpdateCount;
        OrderWillChange = orderWillChange;
        FavoritesToAdd = favoritesToAdd;
        FavoritesToUpdate = favoritesToUpdate;
        SkippedAccountCount = skippedAccountCount;
        SkippedFavoriteCount = skippedFavoriteCount;
        Preview = preview;
    }

    internal int AccountUpdateCount { get; }
    internal bool OrderWillChange { get; }
    internal int FavoritesToAdd { get; }
    internal int FavoritesToUpdate { get; }
    internal int SkippedAccountCount { get; }
    internal int SkippedFavoriteCount { get; }
    internal string Preview { get; }
    internal bool HasChanges =>
        AccountUpdateCount > 0 ||
        OrderWillChange ||
        FavoritesToAdd > 0 ||
        FavoritesToUpdate > 0;

    internal void Apply(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var accountsByUserId = settings.Accounts
            .Where(account => account.UserId > 0)
            .GroupBy(account => account.UserId)
            .ToDictionary(group => group.Key, group => group.First());
        foreach (var imported in _accounts)
        {
            if (!accountsByUserId.TryGetValue(imported.RobloxUserId, out var account))
                continue;
            account.Label = imported.Label;
            account.Group = imported.Group;
            account.ColorHex = imported.Color;
        }

        var orderedIds = _accountOrder.ToHashSet();
        var orderedAccounts = new Queue<AccountProfile>(_accountOrder
            .Where(accountsByUserId.ContainsKey)
            .Select(userId => accountsByUserId[userId]));
        settings.Accounts = settings.Accounts
            .Select(account => orderedIds.Contains(account.UserId)
                ? orderedAccounts.Dequeue()
                : account)
            .ToList();

        foreach (var imported in _favorites)
        {
            if (imported.AccountUserId != 0 &&
                !accountsByUserId.ContainsKey(imported.AccountUserId))
            {
                continue;
            }
            var existing = settings.RecentExperiences.FirstOrDefault(recent =>
                imported.Matches(recent));
            if (existing is not null)
            {
                existing.Name = imported.Name;
                existing.CustomName = imported.CustomName;
                existing.IsPinned = true;
                continue;
            }
            if (settings.RecentExperiences.Count(item => item.IsPinned) >=
                MetadataTransferService.MaximumPublicFavorites)
            {
                continue;
            }

            accountsByUserId.TryGetValue(imported.AccountUserId, out var account);
            settings.RecentExperiences.Add(new RecentExperience
            {
                Destination = imported.PlaceId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                PlaceId = imported.PlaceId,
                Name = imported.Name,
                CustomName = imported.CustomName,
                IsPinned = true,
                IsPrivateServer = false,
                ServerJobId = null,
                AccountUserId = imported.AccountUserId,
                AccountUsername = account?.Username,
                LastLaunchedAt = DateTimeOffset.UtcNow
            });
        }
    }
}

internal sealed record ImportedAccountMetadata(
    long RobloxUserId,
    string? Label,
    string? Group,
    string? Color)
{
    internal bool Matches(AccountProfile account) =>
        string.Equals(Label, account.Label, StringComparison.Ordinal) &&
        string.Equals(Group, account.Group, StringComparison.Ordinal) &&
        string.Equals(Color, account.ColorHex, StringComparison.Ordinal);
}

internal sealed record ImportedFavoriteMetadata(
    long PlaceId,
    long AccountUserId,
    string? Name,
    string? CustomName)
{
    internal bool Matches(RecentExperience recent) =>
        recent.AccountUserId == AccountUserId &&
        recent.PlaceId == PlaceId &&
        !recent.IsPrivateServer &&
        DestinationParser.TryParse(recent.Destination, out var target, out _) &&
        !target!.IsPrivateServer &&
        target.PlaceId == PlaceId;
}
