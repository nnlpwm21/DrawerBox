using DeskBoxWhite.Helpers;
using Xunit;

namespace DeskBoxWhite.Tests;

public sealed class DrawerTileLayoutTests
{
    private static DrawerTileRect T(int col, int row, int w = 1, int h = 1) => new(col, row, w, h);

    [Fact]
    public void Overlap_DetectsIntersectingFootprints()
    {
        Assert.True(T(1, 1, 2, 2).Overlaps(T(2, 2, 2, 2)));
        Assert.True(T(1, 1).Overlaps(T(1, 1)));
        Assert.False(T(1, 1).Overlaps(T(2, 1)));   // side by side
        Assert.False(T(1, 1).Overlaps(T(1, 2)));   // stacked
        Assert.False(T(1, 1, 2, 2).Overlaps(T(3, 3)));
    }

    [Fact]
    public void Place_FreeTargetStaysAtTheAnchor()
    {
        var desired = T(5, 2);
        var others = new[] { T(1, 1), T(2, 1) };

        (DrawerTileRect dropped, var displaced) = DrawerTileLayout.PlaceWithDisplacement(
            desired, others, maxColumns: 6, maxRows: 12);

        Assert.Equal(desired, dropped);
        Assert.Empty(displaced);
    }

    [Fact]
    public void Place_OccupiedTargetDisplacesTheResidentToTheNearestFreeSpot()
    {
        // Two residents; the drop lands on the one at (2,1). The nearest free
        // cell for that resident is (3,1).
        var resident = T(2, 1);
        var others = new[] { T(1, 1), resident };

        (DrawerTileRect dropped, var displaced) = DrawerTileLayout.PlaceWithDisplacement(
            T(2, 1), others, maxColumns: 6, maxRows: 12);

        Assert.Equal(T(2, 1), dropped);
        Assert.Single(displaced);
        Assert.Equal(T(3, 1), displaced[1]); // index 1 = the resident tile
    }

    [Fact]
    public void Place_NeverReturnsAnOverlappingLayout()
    {
        var others = new List<DrawerTileRect>
        {
            T(1, 1), T(2, 1), T(3, 1), T(4, 1),
            T(1, 2), T(2, 2), T(3, 2), T(4, 2)
        };

        (DrawerTileRect dropped, var displaced) = DrawerTileLayout.PlaceWithDisplacement(
            T(2, 1), others, maxColumns: 4, maxRows: 12);

        var occupied = new List<DrawerTileRect>(others);
        for (int i = 0; i < occupied.Count; i++)
        {
            if (displaced.TryGetValue(i, out DrawerTileRect? moved))
            {
                occupied[i] = moved;
            }
        }
        occupied.Add(dropped);
        for (int i = 0; i < occupied.Count; i++)
        {
            for (int j = i + 1; j < occupied.Count; j++)
            {
                Assert.False(occupied[i].Overlaps(occupied[j]),
                    $"Tiles {i} and {j} overlap after placement");
            }
        }
    }

    [Fact]
    public void Place_FullRowFallsBackToTheNearestFreeSpot()
    {
        // Six 1x1 tiles fill a 3x2 block completely; dropping onto the middle
        // tile of row 1 must resolve without leaving any overlap behind. The
        // padding gives residents room to slide out, so displacement succeeds
        // — assert the invariant (no overlap), not the exact path.
        var others = new List<DrawerTileRect> { T(1, 1), T(2, 1), T(3, 1), T(1, 2), T(2, 2), T(3, 2) };

        (DrawerTileRect dropped, var displaced) = DrawerTileLayout.PlaceWithDisplacement(
            T(2, 1), others, maxColumns: 3, maxRows: 12, searchPadding: 2);

        var occupied = new List<DrawerTileRect>(others);
        for (int i = 0; i < occupied.Count; i++)
        {
            if (displaced.TryGetValue(i, out DrawerTileRect? moved))
            {
                occupied[i] = moved;
            }
        }
        occupied.Add(dropped);
        for (int i = 0; i < occupied.Count; i++)
        {
            for (int j = i + 1; j < occupied.Count; j++)
            {
                Assert.False(occupied[i].Overlaps(occupied[j]),
                    $"Tiles {i} and {j} overlap after placement");
            }
        }
    }

    [Fact]
    public void Place_TightBoundsLeaveNoRoomForDisplacement_FallsBack()
    {
        // Padding 0 and a fully packed 3x3 block: the resident cannot go
        // anywhere inside the bounds, so no resident may shift and the dropped
        // tile itself moves to the nearest free cell below the block.
        var others = new List<DrawerTileRect>
        {
            T(1, 1), T(2, 1), T(3, 1),
            T(1, 2), T(2, 2), T(3, 2),
            T(1, 3), T(2, 3), T(3, 3)
        };

        (DrawerTileRect dropped, var displaced) = DrawerTileLayout.PlaceWithDisplacement(
            T(2, 1), others, maxColumns: 3, maxRows: 3, searchPadding: 0);

        Assert.Empty(displaced);
        Assert.False(dropped.Overlaps(T(2, 1)));
        for (int i = 0; i < others.Count; i++)
        {
            Assert.False(dropped.Overlaps(others[i]), $"Dropped tile overlaps resident {i}");
        }
    }

    [Fact]
    public void Place_LargeTileFitsAcrossFreeCells()
    {
        var others = new List<DrawerTileRect> { T(1, 1), T(2, 1) };

        (DrawerTileRect dropped, var displaced) = DrawerTileLayout.PlaceWithDisplacement(
            T(1, 2, 2, 2), others, maxColumns: 4, maxRows: 12);

        Assert.Equal(T(1, 2, 2, 2), dropped);
        Assert.Empty(displaced);
    }

    [Fact]
    public void Place_LargeTileDisplacesEveryoneItCovers()
    {
        var others = new List<DrawerTileRect> { T(1, 1), T(2, 1), T(1, 2), T(2, 2) };

        (DrawerTileRect dropped, var displaced) = DrawerTileLayout.PlaceWithDisplacement(
            T(1, 1, 2, 2), others, maxColumns: 4, maxRows: 12);

        Assert.Equal(T(1, 1, 2, 2), dropped);
        Assert.Equal(4, displaced.Count);
    }

    [Fact]
    public void FindNearestFree_RespectsTheBoundary()
    {
        var occupied = new[] { T(1, 1) };
        DrawerTileRect? spot = DrawerTileLayout.FindNearestFree(
            null, T(1, 1), occupied, extraOccupied: null,
            boundaryColumns: 1, boundaryRows: 3);

        // Column count is 1 and column 1 is occupied on row 1: the only option
        // is downward.
        Assert.Equal(T(1, 2), spot);
    }
}
