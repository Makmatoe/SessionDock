using System.Runtime.InteropServices;
using SessionDock.ExactWheel;
using SessionDock.ExactWheel.Windows;

namespace SessionDock.Tests;

public sealed class ExactWheelInputInjectorTests
{
    [Fact]
    public void NativeInput_Win64Layout_IsSendInputCompatible()
    {
        Assert.Equal(40, Marshal.SizeOf<ExactWheelNativeMethods.NativeInput>());
    }

    [Fact]
    public void BuildBatch_VerticalWheel_PreservesSignedDeltaAndAnchorsPointer()
    {
        var topology = ExactWheelTestData.Display();
        var inputEvent = new ExactWheelInputEvent(
            1,
            1,
            ExactWheelInputEventType.VerticalWheel,
            -1_920,
            1_079,
            -240,
            0);

        var batch = ExactWheelInputInjector.BuildBatch(inputEvent, topology);

        Assert.Equal(2, batch.Length);
        Assert.Equal(ExactWheelNativeMethods.InputMouse, batch[0].Type);
        Assert.Equal(0, batch[0].Data.Mouse.X);
        Assert.Equal(65_535, batch[0].Data.Mouse.Y);
        Assert.Equal(
            ExactWheelNativeMethods.MouseEventMove |
            ExactWheelNativeMethods.MouseEventAbsolute |
            ExactWheelNativeMethods.MouseEventVirtualDesktop |
            ExactWheelNativeMethods.MouseEventMoveNoCoalesce,
            batch[0].Data.Mouse.Flags);
        Assert.Equal(ExactWheelNativeMethods.MouseEventWheel, batch[1].Data.Mouse.Flags);
        Assert.Equal(unchecked((uint)-240), batch[1].Data.Mouse.MouseData);
        Assert.All(batch, item => Assert.Equal(
            unchecked((nuint)ExactWheelLimits.PrivateInputMarker),
            item.Data.Mouse.ExtraInfo));
    }

    [Fact]
    public void BuildBatch_HorizontalWheel_UsesHorizontalFlag()
    {
        var batch = ExactWheelInputInjector.BuildBatch(
            new ExactWheelInputEvent(
                1,
                1,
                ExactWheelInputEventType.HorizontalWheel,
                0,
                0,
                30,
                0),
            ExactWheelTestData.Display());

        Assert.Equal(
            ExactWheelNativeMethods.MouseEventHorizontalWheel,
            batch[1].Data.Mouse.Flags);
        Assert.Equal(30U, batch[1].Data.Mouse.MouseData);
    }

    [Theory]
    [InlineData(ExactWheelInputEventType.KeyDown, 0x0009U)]
    [InlineData(ExactWheelInputEventType.KeyUp, 0x000BU)]
    public void BuildBatch_KeyboardScanCodeAndExtendedFlags_ArePreserved(
        ExactWheelInputEventType type,
        uint expectedFlags)
    {
        var batch = ExactWheelInputInjector.BuildBatch(
            new ExactWheelInputEvent(
                1,
                1,
                type,
                0,
                0,
                0x41,
                0x1E,
                ExactWheelKeyboardFlags.Extended),
            ExactWheelTestData.Display());

        Assert.Single(batch);
        Assert.Equal(ExactWheelNativeMethods.InputKeyboard, batch[0].Type);
        Assert.Equal((ushort)0, batch[0].Data.Keyboard.VirtualKey);
        Assert.Equal((ushort)0x1E, batch[0].Data.Keyboard.ScanCode);
        Assert.Equal(expectedFlags, batch[0].Data.Keyboard.Flags);
        Assert.Equal(
            unchecked((nuint)ExactWheelLimits.PrivateInputMarker),
            batch[0].Data.Keyboard.ExtraInfo);
    }

    [Fact]
    public void Inject_PartialSend_IsNotRetriedOrTracked()
    {
        var backend = new FakeInputBackend((inputs, _) =>
            (checked((uint)inputs.Length - 1U), 5));
        var injector = new ExactWheelInputInjector(backend);
        var inputEvent = MouseButton(
            ExactWheelInputEventType.MouseButtonDown,
            ExactWheelMouseButton.Left);

        var attempt = injector.Inject(inputEvent, ExactWheelTestData.Display());

        Assert.False(attempt.Succeeded);
        Assert.Equal(1U, attempt.Submitted);
        Assert.Equal(2U, attempt.Expected);
        Assert.Equal(5, attempt.Win32Error);
        Assert.Single(backend.Batches);
        Assert.False(injector.HasHeldInputs);
        injector.Dispose();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65_536)]
    public void Inject_InvalidKeyboardCode_FailsWithoutThrowing(int code)
    {
        var backend = new NonRetainingInputBackend();
        var injector = new ExactWheelInputInjector(backend);
        var inputEvent = new ExactWheelInputEvent(
            1,
            1,
            ExactWheelInputEventType.KeyDown,
            0,
            0,
            code,
            0);

        var attempt = injector.Inject(
            inputEvent,
            ExactWheelTestData.Display());

        Assert.False(attempt.Succeeded);
        Assert.Equal(0U, attempt.Submitted);
        Assert.Equal(0U, attempt.Expected);
        Assert.Equal(13, attempt.Win32Error);
        Assert.Equal(0, backend.SendCount);
        injector.Dispose();
    }

    [Fact]
    public void Inject_MouseMoveAndCleanCompletion_AreAllocationFree()
    {
        var backend = new NonRetainingInputBackend();
        var injector = new ExactWheelInputInjector(backend);
        var topology = ExactWheelTestData.Display();
        var inputEvent = new ExactWheelInputEvent(
            1,
            1,
            ExactWheelInputEventType.MouseMove,
            100,
            80,
            0,
            0);
        InjectionAttempt injection = default;
        InjectionAttempt cleanup = default;
        for (var index = 0; index < 1_000; index++)
        {
            injection = injector.Inject(inputEvent, topology);
            cleanup = injector.ReleaseHeld();
        }
        const int allocationAttempts = 3;
        var allocated = AllocationMeasurement.MinimumAllocatedBytes(() =>
        {
            for (var index = 0; index < 100_000; index++)
            {
                injection = injector.Inject(inputEvent, topology);
                cleanup = injector.ReleaseHeld();
            }
        }, allocationAttempts);
        Assert.True(injection.Succeeded);
        Assert.True(cleanup.Succeeded);
        Assert.Equal(
            1_000 + ((allocationAttempts + 1) * 100_000),
            backend.SendCount);
        Assert.InRange(allocated, 0, 256);
        injector.Dispose();
    }

    [Fact]
    public void ReleaseHeld_ReleasesOnlySuccessfullyInjectedHeldInputs()
    {
        var responses = new Queue<(uint Submitted, int Error)>(
        [
            (1, 0),
            (2, 0),
            (2, 0)
        ]);
        var backend = new FakeInputBackend((_, _) => responses.Dequeue());
        var injector = new ExactWheelInputInjector(backend);
        var keyDown = new ExactWheelInputEvent(
            1,
            1,
            ExactWheelInputEventType.KeyDown,
            0,
            0,
            0x41,
            0x1E,
            ExactWheelKeyboardFlags.Extended);

        Assert.True(injector.Inject(keyDown, ExactWheelTestData.Display()).Succeeded);
        Assert.True(injector.Inject(
            MouseButton(
                ExactWheelInputEventType.MouseButtonDown,
                ExactWheelMouseButton.X2),
            ExactWheelTestData.Display()).Succeeded);
        Assert.True(injector.HasHeldInputs);

        var release = injector.ReleaseHeld();

        Assert.True(release.Succeeded);
        Assert.Equal(2U, release.Submitted);
        Assert.False(injector.HasHeldInputs);
        var releases = backend.Batches[2];
        Assert.Equal(2, releases.Length);
        Assert.Equal(
            ExactWheelNativeMethods.KeyboardEventKeyUp |
            ExactWheelNativeMethods.KeyboardEventScanCode |
            ExactWheelNativeMethods.KeyboardEventExtendedKey,
            releases[0].Data.Keyboard.Flags);
        Assert.Equal(ExactWheelNativeMethods.MouseEventXUp, releases[1].Data.Mouse.Flags);
        Assert.Equal(ExactWheelNativeMethods.XButton2, releases[1].Data.Mouse.MouseData);
        injector.Dispose();
    }

    [Fact]
    public void ReleaseHeld_PartialCleanupRetainsOnlyUnacceptedTransitions()
    {
        var responses = new Queue<(uint Submitted, int Error)>(
        [
            (1, 0),
            (2, 0),
            (1, 5),
            (1, 0)
        ]);
        var backend = new FakeInputBackend((_, _) => responses.Dequeue());
        var injector = new ExactWheelInputInjector(backend);
        _ = injector.Inject(
            new ExactWheelInputEvent(
                1,
                1,
                ExactWheelInputEventType.KeyDown,
                0,
                0,
                0x41,
                0,
                ExactWheelKeyboardFlags.None),
            ExactWheelTestData.Display());
        _ = injector.Inject(
            MouseButton(
                ExactWheelInputEventType.MouseButtonDown,
                ExactWheelMouseButton.Left),
            ExactWheelTestData.Display());

        var firstCleanup = injector.ReleaseHeld();
        Assert.False(firstCleanup.Succeeded);
        Assert.True(injector.HasHeldInputs);

        var secondCleanup = injector.ReleaseHeld();

        Assert.True(secondCleanup.Succeeded);
        Assert.False(injector.HasHeldInputs);
        Assert.Single(backend.Batches[3]);
        Assert.Equal(
            ExactWheelNativeMethods.MouseEventLeftUp,
            backend.Batches[3][0].Data.Mouse.Flags);
        injector.Dispose();
    }

    private static ExactWheelInputEvent MouseButton(
        ExactWheelInputEventType type,
        ExactWheelMouseButton button) =>
        new(
            1,
            1,
            type,
            100,
            80,
            (int)button,
            0);

    private sealed class FakeInputBackend(
        Func<ExactWheelNativeMethods.NativeInput[], int, (uint Submitted, int Error)> send)
        : IExactWheelInputBackend
    {
        internal List<ExactWheelNativeMethods.NativeInput[]> Batches { get; } = [];

        public uint Send(
            ExactWheelNativeMethods.NativeInput[] inputs,
            out int win32Error)
        {
            Batches.Add(inputs.ToArray());
            var result = send(inputs, Batches.Count - 1);
            win32Error = result.Error;
            return result.Submitted;
        }
    }

    private sealed class NonRetainingInputBackend : IExactWheelInputBackend
    {
        internal int SendCount { get; private set; }

        public uint Send(
            ExactWheelNativeMethods.NativeInput[] inputs,
            out int win32Error)
        {
            SendCount++;
            win32Error = 0;
            return checked((uint)inputs.Length);
        }
    }
}
