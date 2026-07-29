using System.Globalization;
using System.Text.RegularExpressions;
using SessionDock.Models;

namespace SessionDock.Services;

internal sealed record ExternalRobloxLink(
    string Destination,
    LaunchTarget Target,
    string PreviewTitle,
    string PreviewDetail)
{
    internal bool IsPrivateServer => Target.IsPrivateServer;
}

internal static class ExternalRobloxLinkPolicy
{
    internal const string HandlerScheme = "sessiondock-roblox";
    internal const int MaximumInputLength = 4096;

    private static readonly HashSet<string> AllowedQueryKeys = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "code",
        "type",
        "privateServerLinkCode",
        "linkCode"
    };
    private static readonly Regex ExternalGamePathPattern = new(
        @"^/games/\d+(?:/[^/]*)?/?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant |
            RegexOptions.Compiled);

    private static readonly string[] SensitiveQueryFragments =
    [
        "auth",
        "cookie",
        "gameinfo",
        "ticket",
        "token",
        "browsertracker",
        "jobid",
        "gameinstance"
    ];

    internal static bool TryParse(
        string input,
        out ExternalRobloxLink? link,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(input);
        link = null;
        error = "Choose an official Roblox experience or private-server link.";
        if (input.Length == 0 || input.Length > MaximumInputLength ||
            input.Any(character => char.IsControl(character)))
        {
            error = "The external link is empty, too long, or contains unsafe characters.";
            return false;
        }

        var value = input.Trim();
        if (value.StartsWith(HandlerScheme + ":", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryUnwrapHandlerLink(value, out value, out error))
                return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            error = "Only complete official Roblox links can be opened externally.";
            return false;
        }

        var trustedHttps = uri.Scheme == Uri.UriSchemeHttps &&
                           IsRobloxHost(uri.Host);
        var trustedRobloxProtocol = uri.Scheme.Equals(
            "roblox",
            StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrEmpty(uri.Host) || IsRobloxHost(uri.Host));
        if ((!trustedHttps && !trustedRobloxProtocol) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "Only unambiguous official roblox.com HTTPS links and safe roblox: links are accepted.";
            return false;
        }

        if (!TryValidateQuery(uri.Query, out var queryKeys, out error))
            return false;

        LaunchTarget? target;
        try
        {
            if (!DestinationParser.TryParse(value, out target, out error))
                return false;
        }
        catch (UriFormatException)
        {
            error = "The external link contains invalid escaping.";
            return false;
        }

        if (target!.ShareCode is not null)
        {
            if (!uri.AbsolutePath.Equals("/share", StringComparison.OrdinalIgnoreCase) &&
                !uri.AbsolutePath.Equals("/share/", StringComparison.OrdinalIgnoreCase))
            {
                error = "A private share code is accepted only in an official Roblox share link.";
                return false;
            }
        }
        else if (!ExternalGamePathPattern.IsMatch(uri.AbsolutePath))
        {
            error = "An external experience link must use the official /games/PlaceId path.";
            return false;
        }

        var destinationKeyCount = queryKeys.Count(key =>
            key.Equals("code", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("privateServerLinkCode", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("linkCode", StringComparison.OrdinalIgnoreCase));
        if (destinationKeyCount > 1 ||
            queryKeys.Contains("type") && !queryKeys.Contains("code"))
        {
            error = "The external link contains conflicting destination parameters.";
            return false;
        }

        var destination = CreateCanonicalDestination(target!);
        if (!LaunchInputResolver.TryResolve(
                destination,
                Array.Empty<RecentExperience>(),
                out var resolved,
                out error))
        {
            return false;
        }

        target = resolved!.Target;
        var isPrivate = target.IsPrivateServer;
        var placeDetail = target.PlaceId > 0
            ? $"Experience {target.PlaceId.ToString(CultureInfo.InvariantCulture)}"
            : "The experience will be resolved after account confirmation";
        link = new ExternalRobloxLink(
            destination,
            target,
            isPrivate ? "Private Roblox server" : "Roblox experience",
            isPrivate
                ? $"{placeDetail}. The private code is intentionally hidden and will not be saved."
                : placeDetail);
        return true;
    }

    internal static string WrapForHandler(string trustedLink) =>
        $"{HandlerScheme}:{Uri.EscapeDataString(trustedLink)}";

    internal static bool ShouldSaveToHistory(ExternalRobloxLink? link) =>
        link?.IsPrivateServer != true;

    private static bool TryUnwrapHandlerLink(
        string input,
        out string unwrapped,
        out string error)
    {
        unwrapped = string.Empty;
        error = "The SessionDock link wrapper is invalid.";
        var payload = input[(HandlerScheme.Length + 1)..];
        if (payload.Length == 0 ||
            payload.Contains('?') ||
            payload.Contains('#') ||
            payload.Contains('/') ||
            payload.Contains(':'))
        {
            return false;
        }

        try
        {
            unwrapped = Uri.UnescapeDataString(payload);
        }
        catch (UriFormatException)
        {
            return false;
        }

        return unwrapped.Length > 0 &&
               unwrapped.Length <= MaximumInputLength &&
               !unwrapped.StartsWith(
                   HandlerScheme + ":",
                   StringComparison.OrdinalIgnoreCase) &&
               !unwrapped.Any(character => char.IsControl(character));
    }

    private static bool TryValidateQuery(
        string query,
        out HashSet<string> queryKeys,
        out string error)
    {
        error = string.Empty;
        queryKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in query.TrimStart('?').Split(
                     '&',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('=', 2);
            string key;
            string value;
            try
            {
                key = Uri.UnescapeDataString(pair[0].Replace("+", " "));
                value = pair.Length > 1
                    ? Uri.UnescapeDataString(pair[1].Replace("+", " "))
                    : string.Empty;
            }
            catch (UriFormatException)
            {
                error = "The external link contains invalid escaping.";
                return false;
            }

            if (SensitiveQueryFragments.Any(fragment =>
                    key.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            {
                error = "Authentication tickets, cookies, tokens, and server JobIds are never accepted from external links.";
                return false;
            }
            if (!AllowedQueryKeys.Contains(key))
            {
                error = "The external link contains unsupported or ambiguous launch parameters.";
                return false;
            }
            if (!queryKeys.Add(key))
            {
                error = "The external link repeats a launch parameter and was refused.";
                return false;
            }
            if (key.Equals("type", StringComparison.OrdinalIgnoreCase))
            {
                if (!value.Equals("Server", StringComparison.OrdinalIgnoreCase))
                {
                    error = "Only Roblox server share links can be opened externally.";
                    return false;
                }
            }
            else if (string.IsNullOrWhiteSpace(value))
            {
                error = "The external link contains an empty launch parameter.";
                return false;
            }
        }

        return true;
    }

    private static string CreateCanonicalDestination(LaunchTarget target)
    {
        if (target.ShareCode is not null)
            return "code=" + target.ShareCode;
        if (target.LinkCode is not null)
        {
            return $"https://www.roblox.com/games/{target.PlaceId.ToString(CultureInfo.InvariantCulture)}" +
                   $"?privateServerLinkCode={Uri.EscapeDataString(target.LinkCode)}";
        }

        return target.PlaceId.ToString(CultureInfo.InvariantCulture);
    }

    private static bool IsRobloxHost(string host) =>
        host.Equals("roblox.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase);
}

internal static class ExternalLaunchCommandLine
{
    internal const string OpenLinkOption = "--open-roblox-link";

    internal static bool TryParse(
        IReadOnlyList<string> arguments,
        out string? externalLink,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        externalLink = null;
        error = string.Empty;
        var optionIndexes = arguments
            .Select((argument, index) => (argument, index))
            .Where(item => item.argument.Equals(
                OpenLinkOption,
                StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .ToArray();
        if (optionIndexes.Length == 0)
            return true;
        if (optionIndexes.Length != 1 || arguments.Count != 2 ||
            optionIndexes[0] != 0)
        {
            error = "The external link request used an invalid or ambiguous command line.";
            return false;
        }

        if (!ExternalRobloxLinkPolicy.TryParse(
                arguments[1],
                out _,
                out error))
        {
            return false;
        }

        externalLink = arguments[1];
        return true;
    }
}
