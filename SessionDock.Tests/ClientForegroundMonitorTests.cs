using System.ComponentModel;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class ClientForegroundMonitorTests
{
    [Fact]
    public void ForegroundLoss_LatchesAcrossReturnAndNotifiesOnce()
    {
        var native = new FakeNative
        {
            ForegroundWindow = (nint)101
        };
        using var monitor = new ClientForegroundMonitor(native, (nint)101);
        var notifications = 0;
        monitor.ForegroundLost += (_, _) => notifications++;

        native.RaiseForegroundChanged((nint)101);
        Assert.False(monitor.HasLostForeground);

        native.ForegroundWindow = (nint)202;
        native.RaiseForegroundChanged((nint)202);
        native.ForegroundWindow = (nint)101;
        native.RaiseForegroundChanged((nint)101);
        native.RaiseForegroundChanged((nint)303);

        Assert.True(monitor.HasLostForeground);
        Assert.Equal(1, notifications);
        Assert.True(monitor.Complete());
        Assert.Equal(1, native.RemoveCalls);
    }

    [Fact]
    public void KeyboardOnlyForegroundChange_IsDetectedWithoutInputEvents()
    {
        var native = new FakeNative
        {
            ForegroundWindow = (nint)41
        };
        using var monitor = new ClientForegroundMonitor(native, (nint)41);

        // A WinEvent is sufficient; the monitor deliberately has no mouse or
        // keyboard-event dependency.
        native.ForegroundWindow = (nint)42;
        native.RaiseForegroundChanged((nint)42);

        Assert.True(monitor.HasLostForeground);
    }

    [Fact]
    public void Start_SamplesForegroundAfterHookInstallation()
    {
        var native = new FakeNative
        {
            ForegroundWindow = (nint)77
        };
        native.AfterInstall = () => native.ForegroundWindow = (nint)88;

        using var monitor = new ClientForegroundMonitor(native, (nint)77);

        Assert.True(monitor.HasLostForeground);
        Assert.True(native.InstallCalled);
    }

    [Fact]
    public void Complete_FinalSampleClosesUndeliveredEventGap()
    {
        var native = new FakeNative
        {
            ForegroundWindow = (nint)501
        };
        using var monitor = new ClientForegroundMonitor(native, (nint)501);
        native.ForegroundWindow = (nint)502;

        Assert.True(monitor.Complete());
        Assert.True(monitor.HasLostForeground);
        Assert.Equal(1, native.RemoveCalls);
    }

    [Fact]
    public void InstallFailure_IsFailClosed()
    {
        var native = new FakeNative
        {
            ForegroundWindow = (nint)1,
            InstallResult = nint.Zero,
            LastError = 5
        };

        var exception = Assert.Throws<Win32Exception>(() =>
            new ClientForegroundMonitor(native, (nint)1));

        Assert.Equal(5, exception.NativeErrorCode);
    }

    private sealed class FakeNative : IClientForegroundMonitorNative
    {
        private Action<nint>? _foregroundChanged;

        public nint InstallResult { get; set; } = (nint)900;

        public nint ForegroundWindow { get; set; }

        public int LastError { get; set; }

        public bool InstallCalled { get; private set; }

        public int RemoveCalls { get; private set; }

        public Action? AfterInstall { get; set; }

        public nint InstallForegroundChangedHook(
            Action<nint> foregroundChanged)
        {
            InstallCalled = true;
            _foregroundChanged = foregroundChanged;
            AfterInstall?.Invoke();
            return InstallResult;
        }

        public nint GetForegroundWindow() => ForegroundWindow;

        public bool RemoveForegroundChangedHook(nint hook)
        {
            Assert.Equal(InstallResult, hook);
            RemoveCalls++;
            return true;
        }

        public void RaiseForegroundChanged(nint foregroundWindow) =>
            _foregroundChanged?.Invoke(foregroundWindow);
    }
}
