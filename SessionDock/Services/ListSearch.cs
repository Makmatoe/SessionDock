using System.Globalization;
using SessionDock.Models;

namespace SessionDock.Services;

internal sealed class SearchQueryState
{
    internal const int MaximumLength = 256;

    private string _query = string.Empty;

    internal string Query => _query;

    internal bool IsActive => !string.IsNullOrWhiteSpace(_query);

    internal bool Update(string? query)
    {
        var nextQuery = query ?? string.Empty;
        if (nextQuery.Length > MaximumLength)
            nextQuery = nextQuery[..MaximumLength];

        if (string.Equals(_query, nextQuery, StringComparison.Ordinal))
            return false;

        _query = nextQuery;
        return true;
    }

    internal bool Clear() => Update(null);

    internal bool MatchesAccount(
        AccountProfile account,
        string? group = null) =>
        ListSearchMatcher.MatchesAccount(account, _query, group);

    internal bool MatchesRecent(RecentExperience recent) =>
        ListSearchMatcher.MatchesRecent(recent, _query);
}

internal static class ListSearchMatcher
{
    internal static bool MatchesAccount(
        AccountProfile account,
        string? query,
        string? group = null)
    {
        ArgumentNullException.ThrowIfNull(account);

        return MatchesAllTerms(
            query,
            account.Label,
            account.Username,
            account.UserId.ToString(CultureInfo.InvariantCulture),
            group,
            account.Destination);
    }

    internal static bool MatchesRecent(
        RecentExperience recent,
        string? query)
    {
        ArgumentNullException.ThrowIfNull(recent);

        return MatchesAllTerms(
            query,
            recent.CustomName,
            recent.Name,
            recent.PlaceId.ToString(CultureInfo.InvariantCulture),
            recent.AccountUsername,
            recent.Destination,
            recent.ServerJobId);
    }

    private static bool MatchesAllTerms(
        string? query,
        params string?[] searchableValues)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        var terms = query.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
        return terms.All(term => searchableValues.Any(value =>
            value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true));
    }
}
