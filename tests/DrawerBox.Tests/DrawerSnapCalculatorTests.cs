using DeskBoxWhite.Services;
using Windows.Graphics;
using Xunit;

namespace DeskBoxWhite.Tests;

public sealed class DrawerSnapCalculatorTests
{
    private static readonly RectInt32 WorkArea = new(0, 0, 1920, 1040);

    [Fact]
    public void Move_SnapsTheLeftEdgeToTheNeighbourLeftEdge()
    {
        var neighbour = new RectInt32(400, 100, 300, 400);
        var desired = new RectInt32(405, 100, 300, 400);
        var origin = new RectInt32(0, 100, 300, 400);

        RectInt32 result = DrawerSnapCalculator.Apply(
            desired, origin, WorkArea, [neighbour], tolerance: 8);

        // 405 is five pixels off the neighbour's left edge: snapped to 400.
        Assert.Equal(400, result.X);
    }

    [Fact]
    public void Move_KeepsThePositionWhenNoGuideIsClose()
    {
        var neighbour = new RectInt32(900, 100, 300, 400);
        var desired = new RectInt32(450, 120, 300, 400);
        var origin = new RectInt32(0, 120, 300, 400);

        RectInt32 result = DrawerSnapCalculator.Apply(
            desired, origin, WorkArea, [neighbour], tolerance: 8);

        Assert.Equal(desired, result);
    }

    [Fact]
    public void Move_SnapsTheCentreLineToTheNeighbourCentreLine()
    {
        var neighbour = new RectInt32(600, 100, 300, 400);   // centre X = 750
        var desired = new RectInt32(650, 500, 200, 400);      // centre X = 750
        var origin = new RectInt32(0, 500, 200, 400);

        RectInt32 result = DrawerSnapCalculator.Apply(
            desired, origin, WorkArea, [neighbour], tolerance: 8);

        Assert.Equal(750, result.X + (result.Width / 2));
    }

    [Fact]
    public void Move_SnapsToTheWorkAreaEdge()
    {
        var desired = new RectInt32(3, 120, 300, 400);
        var origin = new RectInt32(200, 120, 300, 400);

        RectInt32 result = DrawerSnapCalculator.Apply(
            desired, origin, WorkArea, [], tolerance: 8);

        Assert.Equal(0, result.X);
    }

    [Fact]
    public void Resize_SnapsOnlyTheDraggedRightEdge()
    {
        var neighbour = new RectInt32(400, 100, 300, 400);
        // Left stays put at 20; the right edge is dragged to 396 (4px off 400).
        var desired = new RectInt32(20, 100, 376, 400);
        var origin = new RectInt32(20, 100, 300, 400);

        RectInt32 result = DrawerSnapCalculator.Apply(
            desired, origin, WorkArea, [neighbour], tolerance: 8);

        // The dragged right edge aligns to the neighbour, the left edge stays.
        Assert.Equal(20, result.X);
        Assert.Equal(400, result.X + result.Width);
    }

    [Fact]
    public void Resize_DoesNotMoveTheEdgeTheUserDidNotDrag()
    {
        var neighbour = new RectInt32(400, 100, 300, 400);
        // Right edge dragged, so the left edge must not be snapped over to 400.
        var desired = new RectInt32(20, 100, 380, 400);
        var origin = new RectInt32(20, 100, 300, 400);

        RectInt32 result = DrawerSnapCalculator.Apply(
            desired, origin, WorkArea, [neighbour], tolerance: 8);

        Assert.Equal(20, result.X);
        Assert.Equal(380, result.Width);
    }

    [Fact]
    public void Resize_SnapsTheBottomEdgeToTheNeighbourBottomEdge()
    {
        var neighbour = new RectInt32(400, 100, 300, 600);   // bottom = 700
        var desired = new RectInt32(20, 100, 300, 578);        // bottom = 678
        var origin = new RectInt32(20, 100, 300, 400);

        RectInt32 result = DrawerSnapCalculator.Apply(
            desired, origin, WorkArea, [neighbour], tolerance: 30);

        Assert.Equal(100, result.Y);
        Assert.Equal(700, result.Y + result.Height);
    }

    [Fact]
    public void NoGuides_LeavesTheFrameAlone()
    {
        var desired = new RectInt32(31, 57, 300, 400);

        RectInt32 result = DrawerSnapCalculator.Apply(
            desired, desired, new RectInt32(0, 0, 0, 0), [], tolerance: 8);

        Assert.Equal(desired, result);
    }

    [Fact]
    public void Move_SnapsToTheClosestGuide()
    {
        var near = new RectInt32(500, 100, 100, 100);
        var far = new RectInt32(505, 300, 100, 100);
        // 505 is only one pixel away, so it wins over 500.
        var desired = new RectInt32(504, 100, 100, 100);
        var origin = new RectInt32(0, 100, 100, 100);

        RectInt32 result = DrawerSnapCalculator.Apply(
            desired, origin, WorkArea, [near, far], tolerance: 8);

        Assert.Equal(505, result.X);
    }
}
