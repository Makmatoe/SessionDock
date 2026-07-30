using System.Globalization;
using System.Text.RegularExpressions;

namespace SessionDock.Services;

public sealed record JoinUserIdentifier(
    long? UserId,
    string? Username,
    string DisplayValue);

public static class JoinUserDestination
{
    internal const string StoredPrefix = "user:";
    internal const string RequiredErrorKey =
        "Validation.JoinUser.Required";
    internal const string TooLongErrorKey =
        "Validation.JoinUser.TooLong";
    internal const string OfficialProfileOnlyErrorKey =
        "Validation.JoinUser.OfficialProfileOnly";
    internal const string NotProfileUrlErrorKey =
        "Validation.JoinUser.NotProfileUrl";
    internal const string ExactUsernameErrorKey =
        "Validation.JoinUser.ExactUsername";
    internal const string NotSavedErrorKey =
        "Validation.JoinUser.NotSaved";
    private const int MaximumInputLength = 512;
    private static readonly Regex UsernamePattern = new(
        "^[A-Za-z0-9_]{3,20}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ProfilePathPattern = new(
        "^/users/(?<id>[0-9]+)/profile/?$",
        RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase |
        RegexOptions.Compiled);

    public static bool TryParseInput(
        string input,
        out JoinUserIdentifier? identifier,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(input);
        identifier = null;
        error = RequiredErrorKey;

        var value = input.Trim();
        if (value.Length == 0)
            return false;
        if (value.Length > MaximumInputLength)
        {
            error = TooLongErrorKey;
            return false;
        }

        if (TryParseUserId(value, out var numericUserId))
        {
            identifier = new JoinUserIdentifier(
                numericUserId,
                null,
                numericUserId.ToString(CultureInfo.InvariantCulture));
            error = string.Empty;
            return true;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme != Uri.UriSchemeHttps ||
                !IsRobloxHost(uri.Host) ||
                !uri.IsDefaultPort ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment))
            {
                error = OfficialProfileOnlyErrorKey;
                return false;
            }

            var match = ProfilePathPattern.Match(uri.AbsolutePath);
            if (!match.Success ||
                !TryParseUserId(match.Groups["id"].Value, out var profileUserId))
            {
                error = NotProfileUrlErrorKey;
                return false;
            }

            identifier = new JoinUserIdentifier(
                profileUserId,
                null,
                profileUserId.ToString(CultureInfo.InvariantCulture));
            error = string.Empty;
            return true;
        }

        var username = value.StartsWith('@') ? value[1..] : value;
        if (!UsernamePattern.IsMatch(username))
        {
            error = ExactUsernameErrorKey;
            return false;
        }

        identifier = new JoinUserIdentifier(
            null,
            username,
            $"@{username}");
        error = string.Empty;
        return true;
    }

    public static bool TryParseStored(
        string? destination,
        out JoinUserIdentifier? identifier,
        out string error)
    {
        identifier = null;
        error = NotSavedErrorKey;
        var value = destination?.Trim();
        return value is not null &&
               value.StartsWith(StoredPrefix, StringComparison.OrdinalIgnoreCase) &&
               TryParseInput(value[StoredPrefix.Length..], out identifier, out error);
    }

    public static string CreateStoredValue(JoinUserIdentifier identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return StoredPrefix + identifier.DisplayValue;
    }

    public static bool IsStoredValue(string? destination) =>
        destination?.Trim().StartsWith(
            StoredPrefix,
            StringComparison.OrdinalIgnoreCase) == true;

    private static bool TryParseUserId(string value, out long userId) =>
        long.TryParse(value, out userId) && userId > 0;

    private static bool IsRobloxHost(string host) =>
        host.Equals("roblox.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase);
}
