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
    public void ReleaseHeld_CtrlCChordReleasesInReversePressOrder()
    {
        var backend = new FakeInputBackend((inputs, _) =>
            (checked((uint)inputs.Length), 0));
        var injector = new ExactWheelInputInjector(backend);
        var topology = ExactWheelTestData.Display();
        Assert.True(injector.Inject(
            Key(ExactWheelInputEventType.KeyDown, 0x11),
            topology).Succeeded);
        Assert.True(injector.Inject(
            Key(ExactWheelInputEventType.KeyDown, 0x43),
            topology).Succeeded);
        Assert.True(injector.HasHeldInputs);

        var release = injector.ReleaseHeld();

        Assert.True(release.Succeeded);
        Assert.Equal(2U, release.Submitted);
        Assert.False(injector.HasHeldInputs);
        var releases = backend.Batches[2];
        Assert.Equal(2, releases.Length);
        Assert.Equal((ushort)0x43, releases[0].Data.Keyboard.VirtualKey);
        Assert.Equal(
            ExactWheelNativeMethods.KeyboardEventKeyUp,
            releases[0].Data.Keyboard.Flags);
        Assert.Equal((ushort)0x11, releases[1].Data.Keyboard.VirtualKey);
        Assert.Equal(
            ExactWheelNativeMethods.KeyboardEventKeyUp,
            releases[1].Data.Keyboard.Flags);
        injector.Dispose();
    }

    [Fact]
    public void ReleaseHeld_MixedChordReleasesInStrictReversePressOrder()
    {
        var backend = new FakeInputBackend((inputs, _) =>
            (checked((uint)inputs.Length), 0));
        var injector = new ExactWheelInputInjector(backend);
        var topology = ExactWheelTestData.Display();
        Assert.True(injector.Inject(
            Key(ExactWheelInputEventType.KeyDown, 0x11),
            topology).Succeeded);
        Assert.True(injector.Inject(
            MouseButton(
                ExactWheelInputEventType.MouseButtonDown,
                ExactWheelMouseButton.Left),
            topology).Succeeded);
        Assert.True(injector.Inject(
            Key(ExactWheelInputEventType.KeyDown, 0x43),
            topology).Succeeded);
        Assert.True(injector.Inject(
            MouseButton(
                ExactWheelInputEventType.MouseButtonDown,
                ExactWheelMouseButton.X2),
            topology).Succeeded);

        var release = injector.ReleaseHeld();

        Assert.True(release.Succeeded);
        Assert.False(injector.HasHeldInputs);
        var releases = backend.Batches[4];
        Assert.Equal(4, releases.Length);
        Assert.Equal(
            ExactWheelNativeMethods.MouseEventXUp,
            releases[0].Data.Mouse.Flags);
        Assert.Equal(
            ExactWheelNativeMethods.XButton2,
            releases[0].Data.Mouse.MouseData);
        Assert.Equal((ushort)0x43, releases[1].Data.Keyboard.VirtualKey);
        Assert.Equal(
            ExactWheelNativeMethods.KeyboardEventKeyUp,
            releases[1].Data.Keyboard.Flags);
        Assert.Equal(
            ExactWheelNativeMethods.MouseEventLeftUp,
            releases[2].Data.Mouse.Flags);
        Assert.Equal((ushort)0x11, releases[3].Data.Keyboard.VirtualKey);
        Assert.Equal(
            ExactWheelNativeMethods.KeyboardEventKeyUp,
            releases[3].Data.Keyboard.Flags);
        injector.Dispose();
    }

    [Fact]
    public void ReleaseHeld_PartialReverseReleaseLeavesPrefixCleanable()
    {
        var responses = new Queue<(uint Submitted, int Error)>(
        [
            (1, 0),
            (2, 0),
            (1, 0),
            (2, 5),
            (1, 0)
        ]);
        var backend = new FakeInputBackend((_, _) => responses.Dequeue());
        var injector = new ExactWheelInputInjector(backend);
        var topology = ExactWheelTestData.Display();
        Assert.True(injector.Inject(
            Key(ExactWheelInputEventType.KeyDown, 0x11),
            topology).Succeeded);
        Assert.True(injector.Inject(
            MouseButton(
                ExactWheelInputEventType.MouseButtonDown,
                ExactWheelMouseButton.Left),
            topology).Succeeded);
        Assert.True(injector.Inject(
            Key(ExactWheelInputEventType.KeyDown, 0x43),
            topology).Succeeded);

        var firstCleanup = injector.ReleaseHeld();

        Assert.False(firstCleanup.Succeeded);
        Assert.Equal(2U, firstCleanup.Submitted);
        Assert.Equal(3U, firstCleanup.Expected);
        Assert.True(injector.HasHeldInputs);
        var firstReleases = backend.Batches[3];
        Assert.Equal((ushort)0x43, firstReleases[0].Data.Keyboard.VirtualKey);
        Assert.Equal(
            ExactWheelNativeMethods.MouseEventLeftUp,
            firstReleases[1].Data.Mouse.Flags);
        Assert.Equal((ushort)0x11, firstReleases[2].Data.Keyboard.VirtualKey);

        var secondCleanup = injector.ReleaseHeld();

        Assert.True(secondCleanup.Succeeded);
        Assert.False(injector.HasHeldInputs);
        Assert.Single(backend.Batches[4]);
        Assert.Equal(
            (ushort)0x11,
            backend.Batches[4][0].Data.Keyboard.VirtualKey);
        Assert.Equal(
            ExactWheelNativeMethods.KeyboardEventKeyUp,
            backend.Batches[4][0].Data.Keyboard.Flags);
        injector.Dispose();
    }

    [Fact]
    public void SuspendHeld_NoHeldInputs_IsAllocationFree()
    {
        var backend = new NonRetainingInputBackend();
        var injector = new ExactWheelInputInjector(backend);
        InjectionAttempt attempt = default;
        ExactWheelHeldInputSuspension? suspension = null;
        for (var index = 0; index < 1_000; index++)
            attempt = injector.SuspendHeld(out suspension);

        const int allocationAttempts = 3;
        var allocated = AllocationMeasurement.MinimumAllocatedBytes(() =>
        {
            for (var index = 0; index < 100_000; index++)
                attempt = injector.SuspendHeld(out suspension);
        }, allocationAttempts);

        Assert.True(attempt.Succeeded);
        Assert.Null(suspension);
        Assert.Equal(0, backend.SendCount);
        Assert.InRange(allocated, 0, 256);
        injector.Dispose();
    }

    [Fact]
    public void SuspendHeld_SnapshotPreservesOriginalPressOrderAndKeyboardRepeat()
    {
        var backend = new FakeInputBackend((inputs, _) =>
            (checked((uint)inputs.Length), 0));
        var injector = new ExactWheelInputInjector(backend);
        var topology = ExactWheelTestData.Display();
        var controlDown = new ExactWheelInputEvent(
            1,
            1,
            ExactWheelInputEventType.KeyDown,
            0,
            0,
            0x11,
            0);
        var mouseDown = new ExactWheelInputEvent(
            2,
            2,
            ExactWheelInputEventType.MouseButtonDown,
            777,
            444,
            (int)ExactWheelMouseButton.X2,
            0);
        var cDown = new ExactWheelInputEvent(
            3,
            3,
            ExactWheelInputEventType.KeyDown,
            0,
            0,
            0x43,
            0);
        var controlRepeat = controlDown with
        {
            TimestampMicroseconds = 4,
            Sequence = 4
        };
        Assert.True(injector.Inject(controlDown, topology).Succeeded);
        Assert.True(injector.Inject(mouseDown, topology).Succeeded);
        Assert.True(injector.Inject(cDown, topology).Succeeded);
        Assert.True(injector.Inject(controlRepeat, topology).Succeeded);

        var suspended = injector.SuspendHeld(out var suspension);

        Assert.True(suspended.Succeeded);
        Assert.Equal(3U, suspended.Submitted);
        Assert.Equal(3U, suspended.Expected);
        Assert.NotNull(suspension);
        Assert.False(injector.HasHeldInputs);
        Assert.Collection(
            suspension.HeldInputs,
            item => Assert.Equal(controlDown, item),
            item => Assert.Equal(mouseDown, item),
            item => Assert.Equal(cDown, item));
        var readOnlyInputs = Assert.IsAssignableFrom<
            IList<ExactWheelInputEvent>>(suspension.HeldInputs);
        Assert.True(readOnlyInputs.IsReadOnly);
        Assert.Throws<NotSupportedException>(() =>
            readOnlyInputs[0] = default);
        var suspensionReleases = backend.Batches[4];
        Assert.Equal(3, suspensionReleases.Length);
        Assert.Equal(
            (ushort)0x43,
            suspensionReleases[0].Data.Keyboard.VirtualKey);
        Assert.Equal(
            ExactWheelNativeMethods.MouseEventXUp,
            suspensionReleases[1].Data.Mouse.Flags);
        Assert.Equal(
            ExactWheelNativeMethods.XButton2,
            suspensionReleases[1].Data.Mouse.MouseData);
        Assert.Equal(
            (ushort)0x11,
            suspensionReleases[2].Data.Keyboard.VirtualKey);
        injector.Dispose();
    }

    [Fact]
    public void SuspendHeld_PartialReleasePublishesNoSnapshot()
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
        var topology = ExactWheelTestData.Display();
        Assert.True(injector.Inject(
            new ExactWheelInputEvent(
                1,
                1,
                ExactWheelInputEventType.KeyDown,
                0,
                0,
                0x41,
                0),
            topology).Succeeded);
        Assert.True(injector.Inject(
            MouseButton(
                ExactWheelInputEventType.MouseButtonDown,
                ExactWheelMouseButton.Left),
            topology).Succeeded);

        var suspended = injector.SuspendHeld(out var suspension);

        Assert.False(suspended.Succeeded);
        Assert.Equal(1U, suspended.Submitted);
        Assert.Equal(2U, suspended.Expected);
        Assert.Equal(5, suspended.Win32Error);
        Assert.Null(suspension);
        Assert.True(injector.HasHeldInputs);
        Assert.True(injector.ReleaseHeld().Succeeded);
        Assert.False(injector.HasHeldInputs);
        Assert.Single(backend.Batches[3]);
        Assert.Equal(
            (ushort)0x41,
            backend.Batches[3][0].Data.Keyboard.VirtualKey);
        Assert.Equal(
            ExactWheelNativeMethods.KeyboardEventKeyUp,
            backend.Batches[3][0].Data.Keyboard.Flags);
        injector.Dispose();
    }

    [Fact]
    public void Inject_AllowsAtMost256DistinctHeldKeyboardIdentities()
    {
        var backend = new FakeInputBackend((inputs, _) =>
            (checked((uint)inputs.Length), 0));
        var injector = new ExactWheelInputInjector(backend);
        var topology = ExactWheelTestData.Display();
        for (var scanCode = 1; scanCode <= 256; scanCode++)
        {
            Assert.True(injector.Inject(
                new ExactWheelInputEvent(
                    checked((ulong)scanCode),
                    checked((ulong)scanCode),
                    ExactWheelInputEventType.KeyDown,
                    0,
                    0,
                    0,
                    scanCode),
                topology).Succeeded);
        }

        var overflow = injector.Inject(
            new ExactWheelInputEvent(
                257,
                257,
                ExactWheelInputEventType.KeyDown,
                0,
                0,
                0,
                257),
            topology);

        Assert.False(overflow.Succeeded);
        Assert.Equal(0U, overflow.Submitted);
        Assert.Equal(1U, overflow.Expected);
        Assert.Equal(56, overflow.Win32Error);
        Assert.Equal(256, backend.Batches.Count);
        Assert.True(injector.ReleaseHeld().Succeeded);
        Assert.False(injector.HasHeldInputs);
        Assert.Equal(
            256,
            backend.Batches[256].Length);
        Assert.Equal(
            (ushort)256,
            backend.Batches[256][0].Data.Keyboard.ScanCode);
        Assert.Equal(
            (ushort)1,
            backend.Batches[256][^1].Data.Keyboard.ScanCode);
        injector.Dispose();
    }

    private static ExactWheelInputEvent Key(
        ExactWheelInputEventType type,
        int virtualKey) =>
        new(
            1,
            1,
            type,
            0,
            0,
            virtualKey,
            0);

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
