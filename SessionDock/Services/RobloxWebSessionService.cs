using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SessionDock.Models;

namespace SessionDock.Services;

public sealed class RobloxWebSessionService : IDisposable
{
    private static readonly TimeSpan AccountTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LocaleTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MacroPlaybackSuspensionTimeout =
        TimeSpan.FromSeconds(1);
    private const int MaximumWebMessageCharacters = 64 * 1024;
    private const int MaximumAuthenticationTicketCharacters = 8 * 1024;
    private static readonly Regex PrivateServerCodePattern = new(
        "^[A-Za-z0-9_-]{6,200}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private WebView2? _browser;
    private int _generation;
    private bool _isReady;
    private WebSessionToken? _currentToken;
    private int _failedGeneration = -1;
    private long _browserWorkGeneration;
    private readonly SemaphoreSlim _macroSuspensionGate = new(1, 1);
    private PendingWebSessionSuspensionLease? _pendingMacroSuspension;
    private TaskCompletionSource<WebSessionUnavailableReason> _sessionEnded =
        CreateSessionEndedSignal();

    internal event EventHandler<WebSessionEventArgs>? RobloxPageLoaded;
    internal event EventHandler<WebSessionUnavailableEventArgs>? SessionUnavailable;

    public bool IsReady => _currentToken is { } token && IsUsable(token);

    internal bool IsUsable(WebSessionToken token) =>
        _isReady && CanContinue(
            _currentToken,
            _generation,
            token,
            isReady: true,
            _browser?.CoreWebView2 is not null);

    internal async Task<IDisposable?> TrySuspendForMacroPlaybackAsync(
        WebSessionToken token,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await _macroSuspensionGate.WaitAsync(
                TimeSpan.Zero,
                cancellationToken))
        {
            // A prior timed-out suspension still owns the WebView operation.
            // Its observer will resume the browser (if needed) and release the
            // gate when that operation eventually settles.
            return null;
        }

        var releaseGate = true;
        CoreWebView2? ownedSuspendedCore = null;
        try
        {
            EnsureUsable(token);
            var browser = _browser!;
            var core = browser.CoreWebView2;
            if (core.IsSuspended)
                return null;
            var browserWorkGeneration = Volatile.Read(
                ref _browserWorkGeneration);

            var suspensionTask = core.TrySuspendAsync();
            bool suspended;
            try
            {
                suspended = await suspensionTask.WaitAsync(
                    MacroPlaybackSuspensionTimeout,
                    cancellationToken);
            }
            catch (TimeoutException)
            {
                releaseGate = false;
                var pendingLease = CreatePendingSuspensionLease(
                    suspensionTask,
                    core,
                    browser.Dispatcher,
                    token,
                    browser,
                    browserWorkGeneration);
                System.Diagnostics.Trace.WriteLine(
                    "WebView2 performance suspension exceeded its start budget; playback continued while suspension completed in the background.");
                return pendingLease;
            }
            catch (OperationCanceledException)
            {
                releaseGate = false;
                var pendingLease = CreatePendingSuspensionLease(
                    suspensionTask,
                    core,
                    browser.Dispatcher,
                    token,
                    browser,
                    browserWorkGeneration);
                pendingLease.Dispose();
                throw;
            }

            if (!suspended)
                return null;
            ownedSuspendedCore = core;
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsCurrent(token) ||
                !ReferenceEquals(browser, _browser) ||
                Volatile.Read(ref _browserWorkGeneration) !=
                    browserWorkGeneration)
            {
                return null;
            }
            var lease = new WebSessionSuspensionLease(
                core,
                _macroSuspensionGate);
            ownedSuspendedCore = null;
            releaseGate = false;
            return lease;
        }
        catch (Exception exception) when (
            IsExpectedSuspensionFailure(exception))
        {
            System.Diagnostics.Trace.WriteLine(
                $"WebView2 performance suspension was unavailable: {exception.GetType().Name}.");
            return null;
        }
        finally
        {
            if (releaseGate)
            {
                if (ownedSuspendedCore is not null)
                    ResumeSafely(ownedSuspendedCore);
                _macroSuspensionGate.Release();
            }
        }
    }

    internal static bool CanContinue(
        WebSessionToken? currentToken,
        int generation,
        WebSessionToken candidate,
        bool isReady,
        bool browserHasCore) =>
        currentToken == candidate &&
        candidate.Generation == generation &&
        isReady &&
        browserHasCore;

    internal static async Task<bool> WaitForSessionWorkAsync(
        Task work,
        Task<WebSessionUnavailableReason> sessionEnded,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(sessionEnded);
        var completed = await Task.WhenAny(work, sessionEnded)
            .WaitAsync(timeout, cancellationToken);
        return completed == work;
    }

    internal WebSessionBrowser BeginBrowserReplacement(string accountKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);
        ReleaseBrowser();
        _sessionEnded = CreateSessionEndedSignal();
        _browser = new WebView2 { AllowExternalDrop = false };
        var token = new WebSessionToken(_generation, accountKey);
        _currentToken = token;
        return new WebSessionBrowser(_browser, token);
    }

    public void ReleaseBrowser()
    {
        Interlocked.Increment(ref _browserWorkGeneration);
        RevokePendingMacroSuspension();
        var browser = _browser;
        _sessionEnded.TrySetResult(WebSessionUnavailableReason.Closed);
        _generation++;
        _isReady = false;
        _currentToken = null;
        _browser = null;
        try
        {
            browser?.Dispose();
        }
        catch (Exception exception) when (
            IsExpectedRuntimeTeardownFailure(exception))
        {
            System.Diagnostics.Trace.WriteLine(
                $"WebView2 teardown failed safely: {exception.GetType().Name}.");
        }
    }

    internal async Task<bool> InitializeAsync(
        WebSessionBrowser session,
        string userDataDirectory,
        bool showLogin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataDirectory);
        var browser = session.Browser;
        var token = session.Token;
        cancellationToken.ThrowIfCancellationRequested();

        CoreWebView2Environment environment;
        try
        {
            environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: userDataDirectory);
        }
        catch (WebView2RuntimeNotFoundException exception)
        {
            throw CreateInitializationUnavailableException(
                exception,
                token,
                WebSessionUnavailableReason.MissingRuntime);
        }
        catch (COMException exception) when (
            IsExpectedInitializationHResult(exception.HResult))
        {
            throw CreateInitializationUnavailableException(
                exception,
                token,
                GetInitializationFailureReason(exception.HResult));
        }
        if (!IsCurrent(session) || cancellationToken.IsCancellationRequested)
            return false;

        try
        {
            await browser.EnsureCoreWebView2Async(environment);
        }
        catch (COMException exception) when (
            IsExpectedInitializationHResult(exception.HResult))
        {
            throw CreateInitializationUnavailableException(
                exception,
                token,
                GetInitializationFailureReason(exception.HResult));
        }
        catch (Exception exception) when (
            IsCorrelatedRuntimeFailure(
                exception,
                token,
                exactRuntimeCall: true))
        {
            throw CreateRuntimeUnavailableException(exception, token);
        }
        if (!IsCurrent(session) || cancellationToken.IsCancellationRequested)
            return false;

        try
        {
            Configure(browser.CoreWebView2);
            _isReady = true;
            _failedGeneration = -1;
            browser.CoreWebView2.Navigate(showLogin
                ? "https://www.roblox.com/login"
                : "https://www.roblox.com/home");
        }
        catch (Exception exception) when (
            IsCorrelatedRuntimeFailure(
                exception,
                token,
                exactRuntimeCall: true))
        {
            _isReady = false;
            throw CreateRuntimeUnavailableException(exception, token);
        }
        return IsUsable(token);
    }

    internal void NavigateToLogin(WebSessionToken token)
    {
        try
        {
            GetCore(token).Navigate("https://www.roblox.com/login");
        }
        catch (Exception exception) when (
            IsCorrelatedRuntimeFailure(exception, token, exactRuntimeCall: true))
        {
            throw CreateRuntimeUnavailableException(exception, token);
        }
    }

    internal async Task<RobloxUser?> GetAuthenticatedUserAsync(
        WebSessionToken token,
        CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var message = await RunMessageScriptAsync(
            requestId,
            RobloxWebScripts.GetAuthenticatedUser(requestId),
            AccountTimeout,
            token,
            cancellationToken);
        if (message is null ||
            !message.Value.TryGetProperty("user", out var user) ||
            user.ValueKind != JsonValueKind.Object ||
            !user.TryGetProperty("id", out var idElement) ||
            !idElement.TryGetInt64(out var id) ||
            id <= 0 ||
            !user.TryGetProperty("name", out var nameElement))
        {
            return null;
        }

        var name = nameElement.GetString();
        if (!IsBoundedDisplayText(name, 50))
            return null;
        var safeName = name!;
        var displayName = user.TryGetProperty("displayName", out var displayNameElement)
            ? displayNameElement.GetString() ?? safeName
            : safeName;
        if (!IsBoundedDisplayText(displayName, 200))
            displayName = safeName;
        return new RobloxUser(id, safeName, displayName!);
    }

    internal async Task<LaunchTarget?> ResolvePrivateServerAsync(
        string shareCode,
        WebSessionToken token,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shareCode);
        var requestId = Guid.NewGuid().ToString("N");
        var message = await RunMessageScriptAsync(
            requestId,
            RobloxWebScripts.ResolvePrivateServer(requestId, shareCode),
            ApiTimeout,
            token,
            cancellationToken);
        if (message is null ||
            !message.Value.TryGetProperty("placeId", out var placeIdElement) ||
            !placeIdElement.TryGetInt64(out var placeId) ||
            placeId <= 0 ||
            !message.Value.TryGetProperty("linkCode", out var linkCodeElement))
        {
            return null;
        }

        var linkCode = linkCodeElement.GetString();
        return linkCode is null || !PrivateServerCodePattern.IsMatch(linkCode)
            ? null
            : new LaunchTarget(placeId, linkCode, null);
    }

    internal async Task<string?> GetAuthenticationTicketAsync(
        WebSessionToken token,
        CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var message = await RunMessageScriptAsync(
            requestId,
            RobloxWebScripts.GetAuthenticationTicket(requestId),
            ApiTimeout,
            token,
            cancellationToken);
        if (message is null ||
            !message.Value.TryGetProperty("ticket", out var ticketElement))
        {
            return null;
        }

        var ticket = ticketElement.GetString();
        return ticket is { Length: > 0 and <= MaximumAuthenticationTicketCharacters } &&
               !ticket.Any(char.IsControl)
            ? ticket
            : null;
    }

    internal async Task<string?> GetUserLocaleAsync(
        WebSessionToken token,
        CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var message = await RunMessageScriptAsync(
            requestId,
            RobloxWebScripts.GetUserLocale(requestId),
            LocaleTimeout,
            token,
            cancellationToken);
        if (message is null ||
            !message.Value.TryGetProperty("locale", out var localeElement))
        {
            return null;
        }

        var locale = localeElement.GetString();
        return locale is { Length: > 0 and <= 32 } &&
               !locale.Any(char.IsControl)
            ? locale
            : null;
    }

    internal async Task<string?> GetExperienceNameAsync(
        long placeId,
        WebSessionToken token,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(placeId);
        var requestId = Guid.NewGuid().ToString("N");
        var message = await RunMessageScriptAsync(
            requestId,
            RobloxWebScripts.GetExperienceName(requestId, placeId),
            ApiTimeout,
            token,
            cancellationToken);
        if (message is null ||
            !message.Value.TryGetProperty("name", out var nameElement))
        {
            return null;
        }

        var name = nameElement.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(name) || name.Length > 200 ? null : name;
    }

    internal async Task<JoinUserLookupResult> ResolveJoinUserAsync(
        JoinUserIdentifier identifier,
        WebSessionToken token,
        CancellationToken cancellationToken = default)
    {
        var identity = await ResolveJoinUserIdentityAsync(
            identifier,
            token,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (identity.Identity is null)
            return MapJoinUserIdentityFailure(identity);

        return await GetJoinUserPresenceAsync(
            identity.Identity,
            token,
            cancellationToken);
    }

    internal async Task<JoinUserIdentityLookupResult> ResolveJoinUserIdentityAsync(
        JoinUserIdentifier identifier,
        WebSessionToken token,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        var requestId = Guid.NewGuid().ToString("N");
        var message = await RunMessageScriptAsync(
            requestId,
            RobloxWebScripts.ResolveJoinUserIdentity(requestId, identifier),
            ApiTimeout,
            token,
            cancellationToken);
        return ParseJoinUserIdentityResponse(message, identifier);
    }

    internal async Task<JoinUserLookupResult> GetJoinUserPresenceAsync(
        JoinUserIdentity identity,
        WebSessionToken token,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var requestId = Guid.NewGuid().ToString("N");
        var message = await RunMessageScriptAsync(
            requestId,
            RobloxWebScripts.GetJoinUserPresence(
                requestId,
                identity.UserId),
            ApiTimeout,
            token,
            cancellationToken);
        return ParseJoinUserPresenceResponse(message, identity);
    }

    internal static JoinUserIdentityLookupResult ParseJoinUserIdentityResponse(
        JsonElement? message,
        JoinUserIdentifier identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        if (message is null ||
            !message.Value.TryGetProperty("status", out var statusElement) ||
            statusElement.ValueKind != JsonValueKind.String)
        {
            return JoinUserIdentityLookupResult.Unavailable(
                JoinUserIdentityAvailability.ServiceUnavailable);
        }

        var status = statusElement.GetString();
        var unavailable = status switch
        {
            "available" => JoinUserIdentityAvailability.Available,
            "user-not-found" => JoinUserIdentityAvailability.UserNotFound,
            "rate-limited" => JoinUserIdentityAvailability.RateLimited,
            "session-unavailable" =>
                JoinUserIdentityAvailability.SessionUnavailable,
            _ => JoinUserIdentityAvailability.ServiceUnavailable
        };
        if (unavailable != JoinUserIdentityAvailability.Available)
        {
            return JoinUserIdentityLookupResult.Unavailable(
                unavailable,
                ParseRetryAfter(message.Value));
        }

        if (!message.Value.TryGetProperty("user", out var user) ||
            user.ValueKind != JsonValueKind.Object ||
            !user.TryGetProperty("id", out var userIdElement) ||
            !userIdElement.TryGetInt64(out var userId) ||
            userId <= 0 ||
            !user.TryGetProperty("name", out var usernameElement) ||
            usernameElement.ValueKind != JsonValueKind.String)
        {
            return JoinUserIdentityLookupResult.Unavailable(
                JoinUserIdentityAvailability.ServiceUnavailable);
        }

        var username = usernameElement.GetString();
        var displayName = user.TryGetProperty("displayName", out var displayNameElement) &&
                          displayNameElement.ValueKind == JsonValueKind.String
            ? displayNameElement.GetString()
            : username;
        if (!IsBoundedDisplayText(username, 50) ||
            !IsBoundedDisplayText(displayName, 200) ||
            identifier.UserId is long requestedUserId &&
            requestedUserId != userId ||
            identifier.Username is { } requestedUsername &&
            !string.Equals(
                requestedUsername,
                username,
                StringComparison.OrdinalIgnoreCase))
        {
            return JoinUserIdentityLookupResult.Unavailable(
                JoinUserIdentityAvailability.ServiceUnavailable);
        }

        return new JoinUserIdentityLookupResult(
            JoinUserIdentityAvailability.Available,
            new JoinUserIdentity(
                userId,
                username!,
                displayName!));
    }

    internal static JoinUserLookupResult ParseJoinUserPresenceResponse(
        JsonElement? message,
        JoinUserIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (message is null ||
            !message.Value.TryGetProperty("status", out var statusElement) ||
            statusElement.ValueKind != JsonValueKind.String)
        {
            return JoinUserLookupResult.Unavailable(
                JoinUserAvailability.ServiceUnavailable);
        }

        var status = statusElement.GetString();
        var unavailable = status switch
        {
            "available" => JoinUserAvailability.Available,
            "offline" => JoinUserAvailability.Offline,
            "not-in-experience" => JoinUserAvailability.NotInExperience,
            "not-joinable" => JoinUserAvailability.NotJoinable,
            "rate-limited" => JoinUserAvailability.RateLimited,
            "session-unavailable" => JoinUserAvailability.SessionUnavailable,
            _ => JoinUserAvailability.ServiceUnavailable
        };
        if (unavailable != JoinUserAvailability.Available)
        {
            return JoinUserLookupResult.Unavailable(
                unavailable,
                ParseRetryAfter(message.Value));
        }

        if (!message.Value.TryGetProperty("userId", out var userIdElement) ||
            !userIdElement.TryGetInt64(out var userId) ||
            userId != identity.UserId ||
            !message.Value.TryGetProperty("placeId", out var placeIdElement) ||
            !placeIdElement.TryGetInt64(out var placeId) ||
            placeId <= 0 ||
            !message.Value.TryGetProperty("gameId", out var gameIdElement) ||
            gameIdElement.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(gameIdElement.GetString(), out var parsedGameId))
        {
            return JoinUserLookupResult.Unavailable(
                JoinUserAvailability.ServiceUnavailable);
        }

        return new JoinUserLookupResult(
            JoinUserAvailability.Available,
            new JoinUserResolution(
                identity.UserId,
                identity.Username,
                identity.DisplayName,
                placeId,
                parsedGameId.ToString("D")));
    }

    internal static JoinUserLookupResult MapJoinUserIdentityFailure(
        JoinUserIdentityLookupResult result)
    {
        var availability = result.Availability switch
        {
            JoinUserIdentityAvailability.UserNotFound =>
                JoinUserAvailability.UserNotFound,
            JoinUserIdentityAvailability.RateLimited =>
                JoinUserAvailability.RateLimited,
            JoinUserIdentityAvailability.SessionUnavailable =>
                JoinUserAvailability.SessionUnavailable,
            _ => JoinUserAvailability.ServiceUnavailable
        };
        return JoinUserLookupResult.Unavailable(
            availability,
            result.RetryAfter);
    }

    private static TimeSpan? ParseRetryAfter(JsonElement message)
    {
        if (!message.TryGetProperty(
                "retryAfterSeconds",
                out var retryAfterElement) ||
            retryAfterElement.ValueKind != JsonValueKind.Number ||
            !retryAfterElement.TryGetDouble(out var seconds) ||
            !double.IsFinite(seconds) ||
            seconds <= 0)
        {
            return null;
        }

        return TimeSpan.FromSeconds(Math.Clamp(seconds, 15, 300));
    }

    internal async Task<bool> ClearProfileAsync(
        WebSessionToken token,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureCurrent(token);
        if (!IsReady)
            return false;

        try
        {
            await GetCore(token).Profile.ClearBrowsingDataAsync(
                    CoreWebView2BrowsingDataKinds.AllProfile)
                .WaitAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            IsCorrelatedRuntimeFailure(exception, token, exactRuntimeCall: true))
        {
            return false;
        }
    }

    public void Dispose()
    {
        ReleaseBrowser();
    }

    private void Configure(CoreWebView2 core)
    {
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Profile.IsPasswordAutosaveEnabled = false;
        core.Profile.IsGeneralAutofillEnabled = false;
        core.Profile.PreferredTrackingPreventionLevel =
            CoreWebView2TrackingPreventionLevel.Balanced;
        core.NewWindowRequested += Core_NewWindowRequested;
        core.NavigationStarting += Core_NavigationStarting;
        core.NavigationCompleted += Core_NavigationCompleted;
        core.ProcessFailed += Core_ProcessFailed;
        core.LaunchingExternalUriScheme += (_, args) => args.Cancel = true;
        core.DownloadStarting += (_, args) =>
        {
            args.Cancel = true;
            args.Handled = true;
        };
        core.PermissionRequested += (_, args) =>
        {
            args.State = CoreWebView2PermissionState.Deny;
            args.SavesInProfile = false;
        };
    }

    private async Task<JsonElement?> RunMessageScriptAsync(
        string requestId,
        string script,
        TimeSpan timeout,
        WebSessionToken token,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var core = GetCore(token);
        var sessionEnded = GetSessionEndedTask(token);
        var completion = new TaskCompletionSource<JsonElement?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        EventHandler<CoreWebView2WebMessageReceivedEventArgs>? handler = null;
        handler = (_, args) =>
        {
            try
            {
                if (!IsTrustedScriptOrigin(args.Source))
                    return;
                var message = args.WebMessageAsJson;
                if (message.Length > MaximumWebMessageCharacters)
                    return;
                using var document = JsonDocument.Parse(
                    message,
                    new JsonDocumentOptions { MaxDepth = 8 });
                var root = document.RootElement;
                if (!root.TryGetProperty("requestId", out var idElement) ||
                    idElement.GetString() != requestId)
                {
                    return;
                }

                completion.TrySetResult(root.Clone());
            }
            catch (Exception exception) when (
                exception is JsonException or ArgumentException or
                    InvalidOperationException ||
                IsCorrelatedRuntimeFailure(
                    exception,
                    token,
                    exactRuntimeCall: true))
            {
                // Ignore unrelated or malformed browser messages.
            }
        };

        core.WebMessageReceived += handler;
        try
        {
            try
            {
                await core.ExecuteScriptAsync(script);
            }
            catch (Exception exception) when (
                IsCorrelatedRuntimeFailure(
                    exception,
                    token,
                    exactRuntimeCall: true))
            {
                throw CreateRuntimeUnavailableException(exception, token);
            }
            EnsureUsable(token);
            var delay = Task.Delay(timeout, cancellationToken);
            var finished = await Task.WhenAny(
                completion.Task,
                delay,
                sessionEnded);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureUsable(token);
            return finished == completion.Task
                ? await completion.Task
                : null;
        }
        finally
        {
            try
            {
                core.WebMessageReceived -= handler;
            }
            catch (Exception exception) when (
                IsCorrelatedRuntimeFailure(
                    exception,
                    token,
                    exactRuntimeCall: true))
            {
                System.Diagnostics.Trace.WriteLine(
                    $"Superseded WebView2 handler cleanup failed safely: {exception.GetType().Name}.");
            }
        }
    }

    private void Core_NewWindowRequested(
        object? sender,
        CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (sender is CoreWebView2 core && IsTrustedBrowserLocation(args.Uri))
        {
            try
            {
                core.Navigate(args.Uri);
            }
            catch (Exception exception) when (
                _currentToken is { } token &&
                IsCorrelatedRuntimeFailure(
                    exception,
                    token,
                    exactRuntimeCall: true))
            {
                System.Diagnostics.Trace.WriteLine(
                    $"Superseded WebView2 navigation failed safely: {exception.GetType().Name}.");
            }
        }
    }

    private static void Core_NavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs args)
    {
        if (!IsTrustedBrowserLocation(args.Uri))
            args.Cancel = true;
    }

    private void Core_NavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs args)
    {
        var browser = _browser;
        var token = _currentToken;
        if (browser is null || token is null)
            return;

        try
        {
            if (!args.IsSuccess ||
                sender != browser.CoreWebView2 ||
                browser.Source is not { } source ||
                !IsTrustedScriptHost(source.Host))
            {
                return;
            }
        }
        catch (Exception exception) when (
            IsCurrent(token.Value) &&
            IsCorrelatedRuntimeFailure(
                exception,
                token.Value,
                exactRuntimeCall: true))
        {
            MarkSessionUnavailable(token.Value);
            return;
        }

        if (IsUsable(token.Value))
            RobloxPageLoaded?.Invoke(this, new WebSessionEventArgs(token.Value));
    }

    private void Core_ProcessFailed(
        object? sender,
        CoreWebView2ProcessFailedEventArgs args)
    {
        if (_currentToken is not { } token ||
            sender is not CoreWebView2 core ||
            !ReferenceEquals(core, _browser?.CoreWebView2))
        {
            return;
        }

        if (args.ProcessFailedKind is not (
                CoreWebView2ProcessFailedKind.BrowserProcessExited or
                CoreWebView2ProcessFailedKind.RenderProcessExited or
                CoreWebView2ProcessFailedKind.RenderProcessUnresponsive))
        {
            System.Diagnostics.Trace.WriteLine(
                $"WebView2 subprocess reported {args.ProcessFailedKind} and was left to runtime recovery.");
            return;
        }

        MarkSessionUnavailable(token);
    }

    private void MarkSessionUnavailable(WebSessionToken token)
    {
        if (!IsCurrent(token) ||
            !_isReady && _failedGeneration == token.Generation)
        {
            return;
        }

        _isReady = false;
        _failedGeneration = token.Generation;
        _sessionEnded.TrySetResult(
            WebSessionUnavailableReason.ProcessExited);
        SessionUnavailable?.Invoke(
            this,
            new WebSessionUnavailableEventArgs(
                token,
                WebSessionUnavailableReason.ProcessExited));
    }

    private bool IsCurrent(WebSessionBrowser session) =>
        IsCurrent(session.Token) && ReferenceEquals(session.Browser, _browser);

    internal bool IsCurrent(WebSessionToken token) =>
        _currentToken == token && token.Generation == _generation;

    internal Task<WebSessionUnavailableReason> GetSessionEndedTask(
        WebSessionToken token)
    {
        EnsureCurrent(token);
        return _sessionEnded.Task;
    }

    private CoreWebView2 GetCore(WebSessionToken token)
    {
        EnsureUsable(token);
        Interlocked.Increment(ref _browserWorkGeneration);
        RevokePendingMacroSuspension();
        var core = _browser!.CoreWebView2;
        // Account and batch operations always take priority over the optional
        // playback optimization. If any work reaches the browser, resume it
        // before executing scripts or navigating.
        if (core.IsSuspended)
            core.Resume();
        return core;
    }

    private static bool IsExpectedSuspensionFailure(Exception exception) =>
        exception is COMException or InvalidOperationException or
            ObjectDisposedException or WebSessionUnavailableException;

    private static void ResumeSafely(CoreWebView2 core)
    {
        try
        {
            if (core.IsSuspended)
                core.Resume();
        }
        catch (Exception exception) when (
            IsExpectedSuspensionFailure(exception))
        {
            System.Diagnostics.Trace.WriteLine(
                $"WebView2 performance resume was unavailable: {exception.GetType().Name}.");
        }
    }

    private PendingWebSessionSuspensionLease CreatePendingSuspensionLease(
        Task<bool> suspensionTask,
        CoreWebView2 core,
        Dispatcher dispatcher,
        WebSessionToken token,
        WebView2 browser,
        long browserWorkGeneration)
    {
        var pending = new PendingWebSessionSuspensionLease(
            suspensionTask,
            () => ResumeOnDispatcherAsync(core, dispatcher),
            _macroSuspensionGate,
            exception => System.Diagnostics.Trace.WriteLine(
                $"Late WebView2 performance suspension settled safely: {exception.GetType().Name}."),
            () => IsCurrent(token) &&
                ReferenceEquals(browser, _browser) &&
                Volatile.Read(ref _browserWorkGeneration) ==
                    browserWorkGeneration);
        var superseded = Interlocked.Exchange(
            ref _pendingMacroSuspension,
            pending);
        superseded?.Dispose();
        _ = ClearPendingSuspensionWhenCompleteAsync(pending);
        return pending;
    }

    private async Task ClearPendingSuspensionWhenCompleteAsync(
        PendingWebSessionSuspensionLease pending)
    {
        await pending.Completion.ConfigureAwait(false);
        _ = Interlocked.CompareExchange(
            ref _pendingMacroSuspension,
            null,
            pending);
    }

    private void RevokePendingMacroSuspension()
    {
        var pending = Interlocked.Exchange(
            ref _pendingMacroSuspension,
            null);
        pending?.Dispose();
    }

    private static Task ResumeOnDispatcherAsync(
        CoreWebView2 core,
        Dispatcher dispatcher)
    {
        if (dispatcher.CheckAccess())
        {
            ResumeSafely(core);
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(
            () => ResumeSafely(core),
            DispatcherPriority.Send).Task;
    }

    private sealed class WebSessionSuspensionLease(
        CoreWebView2 core,
        SemaphoreSlim suspensionGate) : IDisposable
    {
        private SuspensionLeaseState? _state = new(core, suspensionGate);

        public void Dispose()
        {
            var state = Interlocked.Exchange(ref _state, null);
            if (state is null)
                return;

            try
            {
                ResumeSafely(state.Core);
            }
            finally
            {
                state.SuspensionGate.Release();
            }
        }

        private sealed record SuspensionLeaseState(
            CoreWebView2 Core,
            SemaphoreSlim SuspensionGate);
    }

    private void EnsureUsable(WebSessionToken token)
    {
        EnsureCurrent(token);
        if (!IsUsable(token))
        {
            throw new WebSessionUnavailableException(
                _failedGeneration == token.Generation
                    ? WebSessionUnavailableReason.ProcessExited
                    : WebSessionUnavailableReason.Closed,
                "The Roblox web session is no longer available.");
        }
    }

    private void EnsureCurrent(WebSessionToken token)
    {
        if (!IsCurrent(token))
        {
            throw new WebSessionUnavailableException(
                WebSessionUnavailableReason.Superseded,
                "A newer Roblox account session replaced this operation.");
        }
    }

    private bool IsCorrelatedRuntimeFailure(
        Exception exception,
        WebSessionToken token,
        bool exactRuntimeCall = false)
    {
        if (exception is WebSessionUnavailableException)
            return true;
        var correlated = exactRuntimeCall ||
            !IsCurrent(token) ||
            _failedGeneration == token.Generation;
        return correlated &&
            (exception is ObjectDisposedException or InvalidOperationException ||
             exception is COMException comException &&
             IsClosedRuntimeHResult(comException.HResult));
    }

    private WebSessionUnavailableException CreateRuntimeUnavailableException(
        Exception exception,
        WebSessionToken token)
    {
        if (exception is WebSessionUnavailableException unavailable)
            return unavailable;

        var isCurrent = IsCurrent(token);
        if (isCurrent)
            MarkSessionUnavailable(token);
        return new WebSessionUnavailableException(
            isCurrent
                ? WebSessionUnavailableReason.ProcessExited
                : WebSessionUnavailableReason.Superseded,
            isCurrent
                ? "The Roblox web session process exited. Reconnect this account and try again."
                : "A newer Roblox account session replaced this operation.",
            exception);
    }

    private WebSessionUnavailableException CreateInitializationUnavailableException(
        Exception exception,
        WebSessionToken token,
        WebSessionUnavailableReason reason)
    {
        EnsureCurrent(token);
        var message = reason == WebSessionUnavailableReason.MissingRuntime
            ? "Microsoft Edge WebView2 is missing or damaged. Install or repair WebView2, restart Windows, then reopen SessionDock. Your saved accounts and isolated profiles were left unchanged."
            : "Microsoft Edge WebView2 could not start. Repair or reinstall WebView2, restart Windows, then reopen SessionDock. Your saved accounts and isolated profiles were left unchanged.";
        var unavailable = new WebSessionUnavailableException(
            reason,
            message,
            exception);
        ReleaseBrowser();
        return unavailable;
    }

    internal static WebSessionUnavailableReason GetInitializationFailureReason(
        int hResult) =>
        hResult == unchecked((int)0x80040154)
            ? WebSessionUnavailableReason.MissingRuntime
            : WebSessionUnavailableReason.RuntimeStartFailed;

    internal static bool IsExpectedInitializationHResult(int hResult) =>
        hResult is
            unchecked((int)0x80070032) or
            unchecked((int)0x8007139F) or
            unchecked((int)0x80070578) or
            unchecked((int)0x80070070) or
            unchecked((int)0x8007064E) or
            unchecked((int)0x80070002) or
            unchecked((int)0x80070050) or
            unchecked((int)0x80070005) or
            unchecked((int)0x80004005) or
            unchecked((int)0x80004004) or
            unchecked((int)0x80040154);

    private static bool IsClosedRuntimeHResult(int hResult) =>
        hResult is
            unchecked((int)0x80010108) or
            unchecked((int)0x800401FD) or
            unchecked((int)0x80000013);

    private static bool IsExpectedRuntimeTeardownFailure(
        Exception exception) =>
        exception is ObjectDisposedException or InvalidOperationException ||
        exception is COMException comException &&
        (IsClosedRuntimeHResult(comException.HResult) ||
         IsExpectedInitializationHResult(comException.HResult));

    private static TaskCompletionSource<WebSessionUnavailableReason>
        CreateSessionEndedSignal() => new(
            TaskCreationOptions.RunContinuationsAsynchronously);

    private static bool IsTrustedBrowserLocation(string location)
    {
        if (location.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
            return true;
        return Uri.TryCreate(location, UriKind.Absolute, out var uri) &&
               uri.Scheme == Uri.UriSchemeHttps &&
               uri.IsDefaultPort &&
               string.IsNullOrEmpty(uri.UserInfo) &&
               IsTrustedScriptHost(uri.Host);
    }

    private static bool IsTrustedScriptOrigin(string location) =>
        Uri.TryCreate(location, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        IsTrustedScriptHost(uri.Host);

    private static bool IsTrustedScriptHost(string host) =>
        host.Equals("roblox.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("www.roblox.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsBoundedDisplayText(string? value, int maximumLength) =>
        value is { Length: > 0 } &&
        value.Length <= maximumLength &&
        !string.IsNullOrWhiteSpace(value) &&
        !value.Any(char.IsControl);
}
