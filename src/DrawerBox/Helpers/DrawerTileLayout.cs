// Copyright (c) DeskBoxWhite. All rights reserved.

namespace DeskBoxWhite.Helpers;

/// <summary>
/// A tile's grid footprint: anchor cell (top-left, 1-based) plus the number of
/// cells it spans. Pure value logic — no UI or storage dependencies — so the
/// placement rules can be unit-tested without a window.
/// </summary>
public sealed record DrawerTileRect(int Column, int Row, int Width = 1, int Height = 1)
{
    public static readonly DrawerTileRect Empty = new(0, 0, 0, 0);

    public bool Overlaps(DrawerTileRect other)
    {
        if (other is null || Width <= 0 || Height <= 0 || other.Width <= 0 || other.Height <= 0)
        {
            return false;
        }
        return Column < other.Column + other.Width
            && other.Column < Column + Width
            && Row < other.Row + other.Height
            && other.Row < Row + Height;
    }
}

/// <summary>
/// Placement rules for the free-layout drawer: tiles are dropped anywhere, but
/// snap to the cell grid and never overlap — a drop that lands on another tile
/// displaces that tile to the nearest free spot (like the Windows 11 start
/// menu). Searched cells are bounded so pathological layouts cannot lock the
/// algorithm into unbounded growth.
/// </summary>
public static class DrawerTileLayout
{
    /// <summary>
    /// Extra rows and columns the search explores beyond the layout's current
    /// extent, so a full drawer can still absorb one more tile.
    /// </summary>
    public const int SearchPadding = 4;

    /// <summary>
    /// Places the dropped tile as close to the anchor cell as possible and, when
    /// the desired spot is occupied, shifts the overlapped tiles to their own
    /// nearest free spots. Returns the final position of the dropped tile and
    /// every tile that had to move (keyed by 0-based index into <paramref name="others"/>).
    /// Displacement is best-effort: when a spot for an overlapped tile cannot
    /// be found, the dropped tile itself falls back to the nearest free spot
    /// and nothing moves so the received layout always stays collision-free.
    /// </summary>
    public static (DrawerTileRect Dropped, Dictionary<int, DrawerTileRect> Displaced) PlaceWithDisplacement(
        DrawerTileRect desired,
        IReadOnlyList<DrawerTileRect> others,
        int maxColumns,
        int maxRows,
        int searchPadding = SearchPadding)
    {
        var displaced = new Dictionary<int, DrawerTileRect>();
        if (desired is null || desired.Width <= 0 || desired.Height <= 0 || others is null || others.Count == 0)
        {
            return (desired ?? DrawerTileRect.Empty, displaced);
        }

        int boundaryColumns = Math.Max(BoundaryColumn(others), desired.Column + desired.Width - 1) + searchPadding;
        int boundaryRows = Math.Max(BoundaryRow(others), desired.Row + desired.Height - 1) + searchPadding;
        boundaryColumns = Math.Max(1, Math.Min(boundaryColumns, Math.Max(1, maxColumns + searchPadding)));
        boundaryRows = Math.Max(1, Math.Min(boundaryRows, Math.Max(1, maxRows + searchPadding)));

        // Occupied set of every tile that participates; the dropped one is
        // excluded so moving it never blocks its own placement.
        DrawerTileRect ClampAnchor(DrawerTileRect rect)
        {
            int column = Math.Clamp(Math.Max(1, rect.Column), 1, Math.Max(1, boundaryColumns - rect.Width + 1));
            int row = Math.Clamp(Math.Max(1, rect.Row), 1, Math.Max(1, boundaryRows - rect.Height + 1));
            return new DrawerTileRect(column, row, rect.Width, rect.Height);
        }

        DrawerTileRect desiredClamped = ClampAnchor(desired);

        // Try to shift every tile the dropped tile lands on first; if any of
        // them fails, the whole displacement round is abandoned in favor of a
        // fallback spot for the dropped tile itself — a partially resolved drop
        // would leave overlaps behind, which the algorithm must never produce.
        var tentative = new Dictionary<int, DrawerTileRect>();
        for (int i = 0; i < others.Count; i++)
        {
            if (others[i] is not null && desiredClamped.Overlaps(others[i]))
            {
                // Occupancy for this search excludes the moving tile itself;
                // FindNearestFree only knows how to exclude excludeSelf.
                var residents = new List<DrawerTileRect>();
                for (int k = 0; k < others.Count; k++)
                {
                    if (k != i && others[k] is not null)
                    {
                        residents.Add(others[k]);
                    }
                }
                DrawerTileRect? spot = FindNearestFree(desiredClamped, others[i], residents, tentative, boundaryColumns, boundaryRows);
                if (spot is null)
                {
                    tentative.Clear();
                    break;
                }
                tentative[i] = spot;
            }
        }

        if (tentative.Count > 0)
        {
            return (desiredClamped, new Dictionary<int, DrawerTileRect>(tentative));
        }

        // Fallback: leave all resident tiles untouched and move only the
        // dropped tile to the nearest free spot. The second attempt grows the
        // row bound — a drawer can always stack one row deeper, so the
        // "no room at all" case resolves instead of returning an overlap.
        DrawerTileRect? fallback = FindNearestFree(null, desiredClamped, others, null, boundaryColumns, boundaryRows)
            ?? FindNearestFree(null, desiredClamped, others, null, boundaryColumns, boundaryRows + 12);
        return (fallback ?? desiredClamped, displaced);
    }

    /// <summary>
    /// Nearest free anchor cell to <paramref name="target"/> where a tile of the
    /// same size fits without overlapping any occupied rect. Ring search by
    /// Chebyshev distance keeps ties broken toward the top-left, which feels
    /// deterministic to a user watching tiles slide.
    /// </summary>
    public static DrawerTileRect? FindNearestFree(
        DrawerTileRect? excludeSelf,
        DrawerTileRect target,
        IReadOnlyList<DrawerTileRect> occupied,
        IReadOnlyDictionary<int, DrawerTileRect>? extraOccupied,
        int boundaryColumns,
        int boundaryRows)
    {
        if (target is null || target.Width <= 0 || target.Height <= 0)
        {
            return null;
        }
        int width = target.Width;
        int height = target.Height;
        int startColumn = excludeSelf?.Column ?? target.Column;
        int startRow = excludeSelf?.Row ?? target.Row;

        bool FitsAt(int column, int row)
        {
            if (column + width - 1 > boundaryColumns || row + height - 1 > boundaryRows)
            {
                return false;
            }
            var candidate = new DrawerTileRect(column, row, width, height);
            // excludeSelf anchors the ring search but is also the dropped
            // tile's guaranteed footprint — every candidate must avoid it, or
            // a displacement would land a resident right under the drop.
            if (excludeSelf is not null && candidate.Overlaps(excludeSelf))
            {
                return false;
            }
            for (int i = 0; i < occupied.Count; i++)
            {
                if (occupied[i] is not null && candidate.Overlaps(occupied[i]))
                {
                    return false;
                }
            }
            if (extraOccupied is not null)
            {
                foreach (var rect in extraOccupied.Values)
                {
                    if (candidate.Overlaps(rect))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        int maxColumn = Math.Max(1, boundaryColumns - width + 1);
        int maxRow = Math.Max(1, boundaryRows - height + 1);
        for (int ring = 0; ring <= Math.Max(maxColumn, maxRow); ring++)
        {
            int minRow = Math.Max(1, startRow - ring);
            int maxRowRing = Math.Min(maxRow, startRow + ring);
            for (int candidateRow = minRow; candidateRow <= maxRowRing; candidateRow++)
            {
                int minColumn = Math.Max(1, startColumn - ring);
                int maxColumnRing = Math.Min(maxColumn, startColumn + ring);
                for (int candidateColumn = minColumn; candidateColumn <= maxColumnRing; candidateColumn++)
                {
                    // Only the ring's edge is new on later passes; visiting the
                    // whole square again would double-check without harm but
                    // keep it exact to preserve first-wins ordering.
                    bool onRingEdge = ring == 0
                        || candidateRow == minRow
                        || candidateRow == maxRowRing
                        || candidateColumn == minColumn
                        || candidateColumn == maxColumnRing;
                    if (!onRingEdge)
                    {
                        continue;
                    }
                    if (FitsAt(candidateColumn, candidateRow))
                    {
                        return new DrawerTileRect(candidateColumn, candidateRow, width, height);
                    }
                }
            }
        }
        return null;
    }

    private static int BoundaryColumn(IReadOnlyList<DrawerTileRect> rects)
    {
        int max = 1;
        foreach (var rect in rects)
        {
            if (rect is not null)
            {
                max = Math.Max(max, rect.Column + rect.Width - 1);
            }
        }
        return max;
    }

    private static int BoundaryRow(IReadOnlyList<DrawerTileRect> rects)
    {
        int max = 1;
        foreach (var rect in rects)
        {
            if (rect is not null)
            {
                max = Math.Max(max, rect.Row + rect.Height - 1);
            }
        }
        return max;
    }
}
