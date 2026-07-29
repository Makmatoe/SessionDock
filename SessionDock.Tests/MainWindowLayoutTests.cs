namespace SessionDock.Tests;

public sealed class MainWindowLayoutTests
{
    [Theory]
    [InlineData(false, 2, true)]
    [InlineData(true, 2, false)]
    [InlineData(false, 1, false)]
    public void IsAccountReorderingAvailable_DisablesFilteredOrTrivialLists(
        bool searchActive,
        int accountCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            MainWindow.IsAccountReorderingAvailable(
                operationBusy: false,
                reorderInProgress: false,
                shuttingDown: false,
                hasPendingProfile: false,
                searchActive,
                accountCount));
    }

    [Theory]
    [InlineData(true, false, true, true, true)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, true, true, true, false)]
    [InlineData(false, false, true, true, false)]
    public void AreActiveAccountControlsAvailable_RequiresAVisibleTarget(
        bool controlsEnabled,
        bool hasPendingProfile,
        bool hasActiveProfile,
        bool activeProfileVisible,
        bool expected)
    {
        Assert.Equal(
            expected,
            MainWindow.AreActiveAccountControlsAvailable(
                controlsEnabled,
                hasPendingProfile,
                hasActiveProfile,
                activeProfileVisible));
    }

    [Theory]
    [InlineData(-20, 0)]
    [InlineData(49, 0)]
    [InlineData(50, 1)]
    [InlineData(149, 1)]
    [InlineData(150, 2)]
    [InlineData(500, 3)]
    public void CalculateAccountDropInsertionIndex_UsesRenderedMidpoints(
        double pointerX,
        int expectedIndex)
    {
        Assert.Equal(
            expectedIndex,
            MainWindow.CalculateAccountDropInsertionIndex(
                pointerX,
                [50, 150, 250]));
    }

    [Theory]
    [InlineData(5, 1, true)]
    [InlineData(3, 1, false)]
    [InlineData(5, 6, false)]
    [InlineData(-5, -1, true)]
    public void ShouldStartHorizontalAccountDrag_RequiresHorizontalIntent(
        double deltaX,
        double deltaY,
        bool expected)
    {
        Assert.Equal(
            expected,
            MainWindow.ShouldStartHorizontalAccountDrag(
                deltaX,
                deltaY,
                minimumHorizontalDistance: 4));
    }

    [Theory]
    [InlineData(5, 300, 100, 500, 76)]
    [InlineData(295, 300, 100, 500, 124)]
    [InlineData(150, 300, 100, 500, 100)]
    [InlineData(5, 300, 0, 500, 0)]
    [InlineData(295, 300, 500, 500, 500)]
    public void CalculateAccountDragScrollOffset_MovesNearEdgesAndClamps(
        double pointerX,
        double viewportWidth,
        double currentOffset,
        double scrollableWidth,
        double expectedOffset)
    {
        Assert.Equal(
            expectedOffset,
            MainWindow.CalculateAccountDragScrollOffset(
                pointerX,
                viewportWidth,
                currentOffset,
                scrollableWidth));
    }

    [Theory]
    [InlineData(120, 200, 120, 80)]
    [InlineData(80, 200, -120, 120)]
    public void CalculateHorizontalWheelOffset_MovesInWheelDirection(
        double currentOffset,
        double scrollableWidth,
        int wheelDelta,
        double expectedOffset)
    {
        Assert.Equal(
            expectedOffset,
            MainWindow.CalculateHorizontalWheelOffset(
                currentOffset,
                scrollableWidth,
                wheelDelta));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void CalculateHorizontalWheelOffset_PreservesPrecisionWheelMovement(
        int wheelDelta)
    {
        const double currentOffset = 100;

        var result = MainWindow.CalculateHorizontalWheelOffset(
            currentOffset,
            scrollableWidth: 200,
            wheelDelta);

        Assert.NotEqual(currentOffset, result);
        Assert.Equal(
            Math.Abs(wheelDelta / 3d),
            Math.Abs(result - currentOffset),
            precision: 10);
    }

    [Theory]
    [InlineData(10, 200, 120, 0)]
    [InlineData(190, 200, -120, 200)]
    [InlineData(50, 0, -120, 0)]
    public void CalculateHorizontalWheelOffset_ClampsToAvailableRange(
        double currentOffset,
        double scrollableWidth,
        int wheelDelta,
        double expectedOffset)
    {
        Assert.Equal(
            expectedOffset,
            MainWindow.CalculateHorizontalWheelOffset(
                currentOffset,
                scrollableWidth,
                wheelDelta));
    }
}
