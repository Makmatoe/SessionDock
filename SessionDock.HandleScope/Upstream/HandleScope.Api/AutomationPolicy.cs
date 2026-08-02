using System.Globalization;
using HandleScope.Models;
using HandleScope.Services;

namespace HandleScope.Api;

public sealed record AutomationRequestAuthorization(
    bool IsAllowed,
    int RequiredSessionId,
    string CanonicalKey,
    string ErrorCode,
    string ExpectedProcessName,
    string ExpectedHandleType,
    uint ExpectedHandleAccess)
{
    public static AutomationRequestAuthorization Denied(string errorCode) =>
        new(false, 0, string.Empty, errorCode, string.Empty, string.Empty, 0);
}

public sealed record AutomationProcessAuthorization(
    bool IsAllowed,
    ProcessIdentity? Identity,
    string ErrorCode)
{
    public static AutomationProcessAuthorization Denied(string errorCode) =>
        new(false, null, errorCode);
}

public interface IHandleAutomationPolicy
{
    string PolicyId { get; }

    int MaximumProcessCount { get; }

    AutomationRequestAuthorization AuthorizeRequest(CloseHandlesRequest request);

    AutomationProcessAuthorization AuthorizeProcess(
        int processId,
        AutomationRequestAuthorization request);
}

public sealed class RobloxSingletonAutomationPolicy : IHandleAutomationPolicy
{
    public const string Id = "roblox-singleton-event-v1";
    public const string ProcessName = RobloxAutomationRecipe.ProcessName;
    public const string HandleType = RobloxAutomationRecipe.HandleType;
    public const uint HandleAccess = RobloxAutomationRecipe.HandleAccess;
    private readonly Func<int, ProcessIdentity> _getIdentity;
    private readonly IRobloxExecutableVerifier _executableVerifier;
    private readonly ProcessIdentity _currentIdentity;

    public RobloxSingletonAutomationPolicy(
        ProcessIdentityService identityService,
        IRobloxExecutableVerifier executableVerifier)
    {
        ArgumentNullException.ThrowIfNull(identityService);
        ArgumentNullException.ThrowIfNull(executableVerifier);
        _getIdentity = identityService.GetIdentity;
        _executableVerifier = executableVerifier;
        _currentIdentity = _getIdentity(Environment.ProcessId);
    }

    public RobloxSingletonAutomationPolicy(
        Func<int, ProcessIdentity> getIdentity,
        IRobloxExecutableVerifier executableVerifier)
    {
        ArgumentNullException.ThrowIfNull(getIdentity);
        ArgumentNullException.ThrowIfNull(executableVerifier);
        _getIdentity = getIdentity;
        _executableVerifier = executableVerifier;
        _currentIdentity = getIdentity(Environment.ProcessId);
    }

    public string PolicyId => Id;

    public int MaximumProcessCount => 32;

    public AutomationRequestAuthorization AuthorizeRequest(
        CloseHandlesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Process is null ||
            request.Handle is null ||
            request.DryRun is null ||
            (request.DryRun == true && request.PlanId is not null) ||
            (request.DryRun == false &&
             !DryRunPlanStore.IsCanonicalPlanId(request.PlanId)) ||
            request.CloseAll ||
            !string.IsNullOrEmpty(request.Handle.Handle) ||
            !string.Equals(request.Handle.Match, "exact", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(request.Handle.Type, HandleType, StringComparison.OrdinalIgnoreCase) ||
            !TryParseUnsigned(request.Handle.Access, out var access) ||
            access != HandleAccess ||
            !RobloxAutomationRecipe.TryGetSessionId(
                request.Handle.Name,
                out var handleSession) ||
            handleSession != _currentIdentity.WindowsSessionId ||
            !HasAllowedProcessSelector(request.Process, request.AllProcesses))
        {
            return AutomationRequestAuthorization.Denied("policy_denied");
        }

        var target = request.Process.Pid is int pid
            ? $"pid:{pid.ToString(CultureInfo.InvariantCulture)}"
            : $"name:{ProcessName.ToUpperInvariant()}";
        var canonicalKey = string.Join(
            '|',
            target,
            $"session:{handleSession.ToString(CultureInfo.InvariantCulture)}",
            $"all:{request.AllProcesses}",
            $"name:{request.Handle.Name}",
            $"type:{HandleType}",
            $"access:{HandleAccess:X8}");
        return new AutomationRequestAuthorization(
            true,
            checked((int)handleSession),
            canonicalKey,
            string.Empty,
            ProcessName,
            HandleType,
            HandleAccess);
    }

    public AutomationProcessAuthorization AuthorizeProcess(
        int processId,
        AutomationRequestAuthorization request)
    {
        if (!request.IsAllowed || processId <= 0)
        {
            return AutomationProcessAuthorization.Denied("policy_denied");
        }

        try
        {
            var identity = _getIdentity(processId);
            if (!string.Equals(
                    identity.ProcessName,
                    ProcessName,
                    StringComparison.OrdinalIgnoreCase) ||
                identity.WindowsSessionId != (uint)request.RequiredSessionId ||
                identity.WindowsSessionId != _currentIdentity.WindowsSessionId ||
                !string.Equals(
                    identity.OwnerSid,
                    _currentIdentity.OwnerSid,
                    StringComparison.Ordinal) ||
                identity.IsElevated ||
                !_executableVerifier.IsTrusted(identity.ImagePath))
            {
                return AutomationProcessAuthorization.Denied("policy_denied");
            }

            return new AutomationProcessAuthorization(true, identity, string.Empty);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            ArgumentException or
            System.ComponentModel.Win32Exception)
        {
            return AutomationProcessAuthorization.Denied("policy_denied");
        }
    }

    private static bool HasAllowedProcessSelector(
        ProcessSelector selector,
        bool allProcesses)
    {
        if (allProcesses)
        {
            return selector.Pid is null &&
                   string.Equals(
                       NormalizeProcessName(selector.Name),
                       ProcessName,
                       StringComparison.OrdinalIgnoreCase);
        }

        if (selector.Pid is not int pid || pid <= 0)
        {
            return false;
        }

        return string.IsNullOrEmpty(selector.Name) ||
               string.Equals(
                   NormalizeProcessName(selector.Name),
                   ProcessName,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseUnsigned(string? value, out uint result)
    {
        result = 0;
        if (string.IsNullOrEmpty(value) || value != value.Trim())
        {
            return false;
        }

        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(
                value[2..],
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out result)
            : uint.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out result);
    }

    private static string? NormalizeProcessName(string? value)
    {
        if (string.IsNullOrEmpty(value) || value != value.Trim())
        {
            return null;
        }

        return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? value[..^4]
            : value;
    }
}
