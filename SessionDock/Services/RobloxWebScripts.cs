using System.Text.Json;

namespace SessionDock.Services;

internal static class RobloxWebScripts
{
    public static string GetAuthenticatedUser(string requestId)
    {
        var id = JsonSerializer.Serialize(requestId);
        return $$"""
            (() => {
                fetch('https://users.roblox.com/v1/users/authenticated', {
                    credentials: 'include'
                })
                .then(async response => {
                    const user = response.ok ? await response.json() : null;
                    window.chrome.webview.postMessage({ requestId: {{id}}, user });
                })
                .catch(() => window.chrome.webview.postMessage({
                    requestId: {{id}},
                    user: null
                }));
            })();
            """;
    }

    public static string ResolvePrivateServer(string requestId, string shareCode)
    {
        var id = JsonSerializer.Serialize(requestId);
        var code = JsonSerializer.Serialize(shareCode);
        return $$"""
            (async () => {
                let placeId = 0;
                let linkCode = null;
                try {
                    const resolve = csrfToken => fetch(
                        'https://apis.roblox.com/sharelinks/v1/resolve-link',
                        {
                            method: 'POST',
                            credentials: 'include',
                            headers: {
                                'Content-Type': 'application/json',
                                ...(csrfToken ? { 'x-csrf-token': csrfToken } : {})
                            },
                            body: JSON.stringify({
                                linkId: {{code}},
                                linkType: 'Server'
                            })
                        });

                    let response = await resolve(null);
                    if (response.status === 403) {
                        const csrfToken = response.headers.get('x-csrf-token');
                        if (csrfToken)
                            response = await resolve(csrfToken);
                    }

                    if (response.ok) {
                        const resolution = await response.json();
                        const invite = resolution?.privateServerInviteData;
                        if (invite?.status === 'Valid' && invite?.universeId && invite?.linkCode) {
                            const gameResponse = await fetch(
                                `https://games.roblox.com/v1/games?universeIds=${invite.universeId}`,
                                { credentials: 'include' });
                            if (gameResponse.ok) {
                                const games = await gameResponse.json();
                                placeId = Number(games?.data?.[0]?.rootPlaceId ?? 0);
                                linkCode = invite.linkCode;
                            }
                        }
                    }
                } catch {}

                window.chrome.webview.postMessage({
                    requestId: {{id}},
                    placeId,
                    linkCode
                });
            })();
            """;
    }

    public static string GetAuthenticationTicket(string requestId)
    {
        var id = JsonSerializer.Serialize(requestId);
        return $$"""
            (async () => {
                let ticket = null;
                try {
                    let assertion = {};
                    try {
                        const assertionResponse = await fetch(
                            'https://auth.roblox.com/v1/client-assertion/',
                            { credentials: 'include' });
                        if (assertionResponse.ok)
                            assertion = await assertionResponse.json();
                    } catch {}

                    const requestTicket = csrfToken => fetch(
                        'https://auth.roblox.com/v1/authentication-ticket/',
                        {
                            method: 'POST',
                            credentials: 'include',
                            headers: {
                                'Content-Type': 'application/json',
                                ...(csrfToken ? { 'x-csrf-token': csrfToken } : {})
                            },
                            body: JSON.stringify(assertion ?? {})
                        });

                    let response = await requestTicket(null);
                    if (response.status === 403) {
                        const csrfToken = response.headers.get('x-csrf-token');
                        if (csrfToken)
                            response = await requestTicket(csrfToken);
                    }

                    if (response.ok)
                        ticket = response.headers.get('rbx-authentication-ticket');
                } catch {}

                window.chrome.webview.postMessage({
                    requestId: {{id}},
                    ticket
                });
            })();
            """;
    }

    public static string GetUserLocale(string requestId)
    {
        var id = JsonSerializer.Serialize(requestId);
        return $$"""
            (async () => {
                let locale = null;
                try {
                    const response = await fetch(
                        'https://locale.roblox.com/v1/locales/user-locale',
                        { credentials: 'include' });
                    if (response.ok) {
                        const value = await response.json();
                        locale = value?.supportedLocale?.locale ?? null;
                    }
                } catch {}

                window.chrome.webview.postMessage({
                    requestId: {{id}},
                    locale
                });
            })();
            """;
    }

    public static string GetExperienceName(string requestId, long placeId)
    {
        var id = JsonSerializer.Serialize(requestId);
        return $$"""
            (async () => {
                let name = null;
                try {
                    const response = await fetch(
                        'https://games.roblox.com/v1/games/multiget-place-details?placeIds={{placeId}}',
                        { credentials: 'include' });
                    if (response.ok) {
                        const places = await response.json();
                        name = places?.[0]?.name ?? null;
                    }
                } catch {}

                window.chrome.webview.postMessage({
                    requestId: {{id}},
                    name
                });
            })();
            """;
    }

    public static string ResolveJoinUserIdentity(
        string requestId,
        JoinUserIdentifier identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        var id = JsonSerializer.Serialize(requestId);
        var requestedUserId = JsonSerializer.Serialize(
            identifier.UserId?.ToString());
        var requestedUsername = JsonSerializer.Serialize(identifier.Username);
        return $$"""
            (async () => {
                let status = 'service-unavailable';
                let user = null;
                let retryAfterSeconds = null;
                const controller = new AbortController();
                const timeoutId = setTimeout(() => controller.abort(), 10000);
                try {
                    const requestedUserId = {{requestedUserId}};
                    const requestedUsername = {{requestedUsername}};
                    let userResponse;
                    if (requestedUserId) {
                        userResponse = await fetch(
                            `https://users.roblox.com/v1/users/${requestedUserId}`,
                            {
                                credentials: 'include',
                                signal: controller.signal
                            });
                    } else {
                        userResponse = await fetch(
                            'https://users.roblox.com/v1/usernames/users',
                            {
                                method: 'POST',
                                credentials: 'include',
                                headers: { 'Content-Type': 'application/json' },
                                signal: controller.signal,
                                body: JSON.stringify({
                                    usernames: [requestedUsername],
                                    excludeBannedUsers: true
                                })
                            });
                    }

                    if (userResponse.status === 401 || userResponse.status === 403) {
                        status = 'session-unavailable';
                    } else if (userResponse.status === 404) {
                        status = 'user-not-found';
                    } else if (userResponse.status === 429) {
                        status = 'rate-limited';
                        const retryAfter = Number(
                            userResponse.headers.get('Retry-After'));
                        retryAfterSeconds = Number.isFinite(retryAfter) && retryAfter > 0
                            ? Math.min(retryAfter, 300)
                            : null;
                    } else if (userResponse.ok) {
                        if (requestedUserId) {
                            user = await userResponse.json();
                        } else {
                            const users = await userResponse.json();
                            user = users?.data?.[0] ?? null;
                        }
                        status = user?.id ? 'available' : 'user-not-found';
                    }
                } catch {
                    status = 'service-unavailable';
                } finally {
                    clearTimeout(timeoutId);
                }

                window.chrome.webview.postMessage({
                    requestId: {{id}},
                    status,
                    retryAfterSeconds,
                    user: user ? {
                        id: user.id,
                        name: user.name,
                        displayName: user.displayName
                    } : null
                });
            })();
            """;
    }

    public static string GetJoinUserPresence(
        string requestId,
        long userId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        var id = JsonSerializer.Serialize(requestId);
        var requestedUserId = JsonSerializer.Serialize(userId);
        return $$"""
            (async () => {
                let status = 'service-unavailable';
                let placeId = 0;
                let gameId = null;
                let retryAfterSeconds = null;
                const userId = {{requestedUserId}};
                const controller = new AbortController();
                const timeoutId = setTimeout(() => controller.abort(), 10000);
                try {
                    const presenceResponse = await fetch(
                        'https://presence.roblox.com/v1/presence/users',
                        {
                            method: 'POST',
                            credentials: 'include',
                            headers: { 'Content-Type': 'application/json' },
                            signal: controller.signal,
                            body: JSON.stringify({ userIds: [userId] })
                        });
                    if (presenceResponse.status === 401 ||
                        presenceResponse.status === 403) {
                        status = 'session-unavailable';
                    } else if (presenceResponse.status === 429) {
                        status = 'rate-limited';
                        const retryAfter = Number(
                            presenceResponse.headers.get('Retry-After'));
                        retryAfterSeconds = Number.isFinite(retryAfter) && retryAfter > 0
                            ? Math.min(retryAfter, 300)
                            : null;
                    } else if (presenceResponse.ok) {
                        const presences = await presenceResponse.json();
                        const presence = presences?.userPresences?.[0] ?? null;
                        const responseUserId = Number(presence?.userId ?? 0);
                        const presenceType = Number(
                            presence?.userPresenceType ?? 0);
                        if (!presence || responseUserId !== userId) {
                            status = 'service-unavailable';
                        } else if (presenceType === 0) {
                            status = 'offline';
                        } else if (presenceType !== 2) {
                            status = 'not-in-experience';
                        } else {
                            placeId = Number(presence.placeId ?? 0);
                            gameId = typeof presence.gameId === 'string'
                                ? presence.gameId
                                : null;
                            status = placeId > 0 && gameId
                                ? 'available'
                                : 'not-joinable';
                        }
                    }
                } catch {
                    status = 'service-unavailable';
                } finally {
                    clearTimeout(timeoutId);
                }

                window.chrome.webview.postMessage({
                    requestId: {{id}},
                    status,
                    userId,
                    retryAfterSeconds,
                    placeId,
                    gameId
                });
            })();
            """;
    }
}
