using DeskBoxWhite.Platform;
using DrawingPoint = System.Drawing.Point;

namespace DeskBoxWhite;

/// <content>
/// Anchor logic for the tray context menu. The flyout is presented through
/// H.NotifyIcon's hidden second window; positioning it at the raw cursor is
/// unreliable (DPI scaling and the reuse of prewarmed windows shift the
/// popup). This pins the menu to the notify icon's true screen rect and the
/// nearest taskbar edge, kept in lockstep with the main app's implementation.
/// </content>
public sealed partial class App
{
    private const int TrayContextMenuFallbackOffsetPixels = 24;
    private const int TrayContextMenuEstimatedWidth = 192;

    /// <content>
    /// Logical (96-dpi) menu geometry used to lift the popup above the
    /// taskbar. The flyout opens downward/rightward from the anchor point,
    /// so without compensation a bottom taskbar's menu spills onto a monitor
    /// below it. Physical size = row estimate × monitor DPI scale.
    /// </content>
    internal static class TrayMenuDpRows
    {
        public const int MenuFlyoutItemRowHeightDp = 34;
        public const int MenuFlyoutSeparatorHeightDp = 17;
        public const int MenuFlyoutChromeHeightDp = 12;
        public const int MenuFlyoutSafetyMarginDp = 16;

        /// <summary>Extra run-out room for the shell menu border/shadow.</summary>
        public const int MenuFlyoutSafetyWidthDp = 8;
    }

    private enum TaskbarEdge
    {
        Bottom,
        Top,
        Right,
        Left,
        Unknown
    }

    private DrawingPoint GetTrayContextMenuAnchorPoint(DrawingPoint fallbackPoint, int estimatedMenuHeightDp)
    {
        if (TryGetTrayIconIdentity(out var trayIconWindowHandle, out var trayIconId) &&
            Win32Helper.TryGetNotifyIconRect(trayIconWindowHandle, trayIconId, out var iconRect) &&
            IsUsableTrayIconRect(iconRect))
        {
            return GetTrayContextMenuAnchorPointFromIconRect(iconRect, fallbackPoint, estimatedMenuHeightDp);
        }

        return GetFallbackTrayContextMenuAnchorPoint(fallbackPoint, estimatedMenuHeightDp);
    }

    private bool TryGetTrayIconIdentity(out IntPtr windowHandle, out Guid id)
    {
        windowHandle = IntPtr.Zero;
        id = Guid.Empty;

        if (_trayIcon is null)
        {
            return false;
        }

        H.NotifyIcon.Core.TrayIcon trayIcon = _trayIcon.TrayIcon;
        windowHandle = trayIcon.WindowHandle;
        id = trayIcon.Id;

        return windowHandle != IntPtr.Zero && id != Guid.Empty;
    }

    private static DrawingPoint GetTrayContextMenuAnchorPointFromIconRect(
        Win32Helper.RECT iconRect,
        DrawingPoint fallbackPoint,
        int estimatedMenuHeightDp)
    {
        int centerX = iconRect.Left + ((iconRect.Right - iconRect.Left) / 2);
        int centerY = iconRect.Top + ((iconRect.Bottom - iconRect.Top) / 2);
        var anchor = new DrawingPoint(centerX - (TrayContextMenuEstimatedWidth / 2), centerY);

        if (!Win32Helper.TryGetMonitorWorkArea(centerX, centerY, out var monitor, out var workArea))
        {
            return anchor;
        }

        double scale = Win32Helper.GetDpiScaleForPoint(centerX, centerY);
        int menuHeightPhysical = MenuPhysicalSize(estimatedMenuHeightDp, scale);
        int menuWidthPhysical = MenuPhysicalSize(
            TrayContextMenuEstimatedWidth + TrayMenuDpRows.MenuFlyoutSafetyWidthDp,
            scale);

        var edge = GetNearestTaskbarEdge(iconRect, monitor, workArea);
        anchor = edge switch
        {
            // The flyout opens downward from the anchor, so on a bottom
            // taskbar the anchor must sit a whole menu height above the
            // work area edge — otherwise it spills onto a monitor below.
            TaskbarEdge.Bottom => new DrawingPoint(anchor.X, workArea.Bottom - 1 - menuHeightPhysical),
            TaskbarEdge.Top => new DrawingPoint(anchor.X, workArea.Top),
            TaskbarEdge.Right => new DrawingPoint(workArea.Right - 1 - menuWidthPhysical, centerY),
            TaskbarEdge.Left => new DrawingPoint(workArea.Left, centerY),
            _ => GetFallbackTrayContextMenuAnchorPoint(fallbackPoint, estimatedMenuHeightDp)
        };

        return ClampPointToRect(anchor, monitor);
    }

    private static DrawingPoint GetFallbackTrayContextMenuAnchorPoint(DrawingPoint point, int estimatedMenuHeightDp)
    {
        if (!Win32Helper.TryGetMonitorWorkArea(point.X, point.Y, out var monitor, out var workArea))
        {
            return new DrawingPoint(
                point.X - (TrayContextMenuEstimatedWidth / 2),
                point.Y - TrayContextMenuFallbackOffsetPixels);
        }

        double scale = Win32Helper.GetDpiScaleForPoint(point.X, point.Y);
        int menuHeightPhysical = MenuPhysicalSize(estimatedMenuHeightDp, scale);
        int menuWidthPhysical = MenuPhysicalSize(
            TrayContextMenuEstimatedWidth + TrayMenuDpRows.MenuFlyoutSafetyWidthDp,
            scale);

        int x = point.X - (TrayContextMenuEstimatedWidth / 2);
        int y = point.Y;
        bool moved = false;

        if (workArea.Bottom < monitor.Bottom && y >= workArea.Bottom)
        {
            y = workArea.Bottom - 1 - menuHeightPhysical;
            moved = true;
        }
        else if (workArea.Top > monitor.Top && y <= workArea.Top)
        {
            y = workArea.Top;
            moved = true;
        }

        if (workArea.Right < monitor.Right && x >= workArea.Right)
        {
            x = workArea.Right - 1 - menuWidthPhysical;
            moved = true;
        }
        else if (workArea.Left > monitor.Left && x <= workArea.Left)
        {
            x = workArea.Left;
            moved = true;
        }

        if (!moved)
        {
            int distanceToBottom = Math.Abs(monitor.Bottom - y);
            int distanceToTop = Math.Abs(y - monitor.Top);
            int distanceToRight = Math.Abs(monitor.Right - x);
            int distanceToLeft = Math.Abs(x - monitor.Left);
            int nearestDistance = Math.Min(
                Math.Min(distanceToBottom, distanceToTop),
                Math.Min(distanceToRight, distanceToLeft));

            if (nearestDistance == distanceToBottom)
            {
                y -= Math.Max(menuHeightPhysical, TrayContextMenuFallbackOffsetPixels);
            }
            else if (nearestDistance == distanceToTop)
            {
                y += TrayContextMenuFallbackOffsetPixels;
            }
            else if (nearestDistance == distanceToRight)
            {
                x -= Math.Max(menuWidthPhysical, TrayContextMenuFallbackOffsetPixels);
            }
            else
            {
                x += TrayContextMenuFallbackOffsetPixels;
            }
        }

        return ClampPointToRect(new DrawingPoint(x, y), monitor);
    }

    private static int MenuPhysicalSize(int estimatedDp, double scale)
    {
        return (int)Math.Ceiling(Math.Max(0, estimatedDp) * Math.Max(scale, 1.0));
    }

    private static bool IsUsableTrayIconRect(Win32Helper.RECT rect)
    {
        return rect.Right > rect.Left && rect.Bottom > rect.Top;
    }

    private static TaskbarEdge GetNearestTaskbarEdge(
        Win32Helper.RECT iconRect,
        Win32Helper.RECT monitor,
        Win32Helper.RECT workArea)
    {
        if (workArea.Bottom < monitor.Bottom &&
            iconRect.Top >= workArea.Bottom)
        {
            return TaskbarEdge.Bottom;
        }

        if (workArea.Top > monitor.Top &&
            iconRect.Bottom <= workArea.Top)
        {
            return TaskbarEdge.Top;
        }

        if (workArea.Right < monitor.Right &&
            iconRect.Left >= workArea.Right)
        {
            return TaskbarEdge.Right;
        }

        if (workArea.Left > monitor.Left &&
            iconRect.Right <= workArea.Left)
        {
            return TaskbarEdge.Left;
        }

        int distanceToBottom = Math.Abs(monitor.Bottom - iconRect.Bottom);
        int distanceToTop = Math.Abs(iconRect.Top - monitor.Top);
        int distanceToRight = Math.Abs(monitor.Right - iconRect.Right);
        int distanceToLeft = Math.Abs(iconRect.Left - monitor.Left);
        int nearestDistance = Math.Min(
            Math.Min(distanceToBottom, distanceToTop),
            Math.Min(distanceToRight, distanceToLeft));

        if (nearestDistance == distanceToBottom)
        {
            return TaskbarEdge.Bottom;
        }

        if (nearestDistance == distanceToTop)
        {
            return TaskbarEdge.Top;
        }

        return nearestDistance == distanceToRight
            ? TaskbarEdge.Right
            : TaskbarEdge.Left;
    }

    private static DrawingPoint ClampPointToRect(DrawingPoint point, Win32Helper.RECT rect)
    {
        return new DrawingPoint(
            Math.Clamp(point.X, rect.Left, rect.Right - 1),
            Math.Clamp(point.Y, rect.Top, rect.Bottom - 1));
    }
}
