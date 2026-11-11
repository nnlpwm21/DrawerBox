using Windows.Graphics;

namespace DeskBoxWhite.Services;

/// <summary>
/// Edge snapping for drawer popups. While a drawer is moved or resized, the edge
/// (or the centre line) the user is actually dragging snaps to the matching
/// guide of a neighbouring drawer or of the work area, so windows line up
/// instead of being eyeballed into place.
/// </summary>
internal static class DrawerSnapCalculator
{
    /// <summary>How close an edge has to be to a guide to snap, in pixels.</summary>
    public const int DefaultTolerance = 8;

    /// <summary>
    /// Snaps <paramref name="desired"/> to the nearest guide. Only edges that
    /// actually moved relative to <paramref name="origin"/> take part, so a
    /// resize drag snaps the dragged edge and a move snaps the whole window.
    /// </summary>
    public static RectInt32 Apply(
        RectInt32 desired,
        RectInt32 origin,
        RectInt32 workArea,
        IReadOnlyList<RectInt32> neighbours,
        int tolerance = DefaultTolerance)
    {
        if (tolerance < 1 || (desired == origin))
        {
            return desired;
        }

        bool leftMoved = desired.X != origin.X;
        bool rightMoved = desired.X + desired.Width != origin.X + origin.Width;
        bool topMoved = desired.Y != origin.Y;
        bool bottomMoved = desired.Y + desired.Height != origin.Y + origin.Height;

        if (!leftMoved && !rightMoved && !topMoved && !bottomMoved)
        {
            return desired;
        }

        // A resize must not move the edge the user is not dragging, so the
        // centre line only takes part while the whole window is being moved.
        bool isMove = leftMoved && rightMoved && topMoved && bottomMoved;

        int left = desired.X;
        int top = desired.Y;
        int width = desired.Width;
        int height = desired.Height;

        // A shift for the leading edge moves the frame; a shift for the trailing
        // edge resizes it. Applying it to the wrong side is what made resizes
        // drag the opposite edge along.
        int? shiftX = FindSmallestShift(
            [
                (desired.X, leftMoved),
                (desired.X + desired.Width, rightMoved),
                (desired.X + (desired.Width / 2), isMove),
            ],
            VerticalGuides(workArea, neighbours),
            tolerance);
        if (shiftX is { } dx)
        {
            if (leftMoved)
            {
                left += dx;
            }
            else
            {
                width += dx;
            }
        }

        int? shiftY = FindSmallestShift(
            [
                (desired.Y, topMoved),
                (desired.Y + desired.Height, bottomMoved),
                (desired.Y + (desired.Height / 2), isMove),
            ],
            HorizontalGuides(workArea, neighbours),
            tolerance);
        if (shiftY is { } dy)
        {
            if (topMoved)
            {
                top += dy;
            }
            else
            {
                height += dy;
            }
        }

        return new RectInt32(
            left,
            top,
            Math.Max(1, width),
            Math.Max(1, height));
    }

    private static int? FindSmallestShift(
        IReadOnlyList<(int Edge, bool Moved)> edges,
        IReadOnlyList<int> guides,
        int tolerance)
    {
        int? best = null;
        int bestDistance = tolerance;

        foreach ((int edge, bool moved) in edges)
        {
            if (!moved)
            {
                continue;
            }

            foreach (int guide in guides)
            {
                int delta = guide - edge;
                int distance = Math.Abs(delta);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = delta;
                }
            }
        }

        return best;
    }

    private static List<int> VerticalGuides(RectInt32 workArea, IReadOnlyList<RectInt32> neighbours)
    {
        var guides = new List<int>();
        if (workArea.Width > 0 && workArea.Height > 0)
        {
            guides.Add(workArea.X);
            guides.Add(workArea.X + workArea.Width);
            guides.Add(workArea.X + (workArea.Width / 2));
        }

        foreach (RectInt32 neighbour in neighbours)
        {
            guides.Add(neighbour.X);
            guides.Add(neighbour.X + neighbour.Width);
            guides.Add(neighbour.X + (neighbour.Width / 2));
        }

        return guides;
    }

    private static List<int> HorizontalGuides(RectInt32 workArea, IReadOnlyList<RectInt32> neighbours)
    {
        var guides = new List<int>();
        if (workArea.Width > 0 && workArea.Height > 0)
        {
            guides.Add(workArea.Y);
            guides.Add(workArea.Y + workArea.Height);
            guides.Add(workArea.Y + (workArea.Height / 2));
        }

        foreach (RectInt32 neighbour in neighbours)
        {
            guides.Add(neighbour.Y);
            guides.Add(neighbour.Y + neighbour.Height);
            guides.Add(neighbour.Y + (neighbour.Height / 2));
        }

        return guides;
    }
}
