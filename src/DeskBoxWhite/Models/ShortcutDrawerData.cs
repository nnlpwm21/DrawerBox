namespace DeskBoxWhite.Models;

/// <summary>
/// Persistent shortcut drawer content: multiple drawers, each bound to a local folder.
/// </summary>
public sealed class ShortcutDrawerData
{
    /// <summary>Store schema version for future migrations.</summary>
    public int Version { get; set; } = 2;

    /// <summary>Global toggle for showing position badges on all drawers.</summary>
    public bool ShowPositionBadges { get; set; } = true;

    /// <summary>Whether the drawer-wide global hotkey is enabled.</summary>
    public bool GlobalHotkeyEnabled { get; set; } = true;

    /// <summary>Global hotkey modifiers (Windows.System.HotkeyModifierKeys as int).</summary>
    public int GlobalHotkeyModifiers { get; set; } =
        (int)(HotkeyModifierKeys.Control | HotkeyModifierKeys.Alt);

    /// <summary>Global hotkey virtual key code. Default: D (0x44).</summary>
    public int GlobalHotkeyKey { get; set; } = 0x44;

    /// <summary>
    /// Drawer popup client width in physical pixels; 0 means "size to content".
    /// </summary>
    public int PopupWidth { get; set; }

    /// <summary>
    /// Drawer popup client height in physical pixels; 0 means "size to content".
    /// </summary>
    public int PopupHeight { get; set; }

    /// <summary>Drawer popup left origin in physical pixels; 0 means "default".</summary>
    public int PopupLeft { get; set; }

    /// <summary>Drawer popup top origin in physical pixels; 0 means "default".</summary>
    public int PopupTop { get; set; }

    /// <summary>
    /// Last drawer opened with the drawer-wide hotkey, so the next press cycles
    /// to the next one instead of always reopening the same drawer.
    /// </summary>
    public Guid LastOpenedDrawerId { get; set; }

    /// <summary>Edge length in pixels of the shell icon shown on each shortcut.</summary>
    public int IconSize { get; set; } = 32;

    /// <summary>Gap between drawer grid rows, in pixels. 0 means inherit.</summary>
    public int RowSpacing { get; set; } = 6;

    /// <summary>Gap between drawer grid columns, in pixels. 0 means inherit.</summary>
    public int ColumnSpacing { get; set; } = 6;

    /// <summary>Drawers, in display order in settings.</summary>
    public List<ShortcutDrawerDefinition> Drawers { get; set; } = new();
}

/// <summary>
/// One drawer group bound to a local folder.
/// </summary>
public sealed class ShortcutDrawerDefinition
{
    /// <summary>Unique identifier for the drawer.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Drawer display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Folder path containing .lnk files; empty means auto-create in data dir.</summary>
    public string FolderPath { get; set; } = string.Empty;

    /// <summary>Hotkey enabled for this drawer.</summary>
    public bool HotkeyEnabled { get; set; } = false;

    /// <summary>Hotkey modifiers (Windows.System.HotkeyModifierKeys as int).</summary>
    public int HotkeyModifiers { get; set; } = (int)(HotkeyModifierKeys.Control | HotkeyModifierKeys.Alt);

    /// <summary>Virtual key code for hotkey.</summary>
    public int HotkeyKey { get; set; } = 0x44; // 'D'

    /// <summary>Override icon edge length; null inherits the drawer-wide value.</summary>
    public int? IconSize { get; set; }

    /// <summary>Popup left origin in physical pixels; 0 means "remembered by the window".</summary>
    public int PopupLeft { get; set; }

    /// <summary>Popup top origin in physical pixels; 0 means "remembered by the window".</summary>
    public int PopupTop { get; set; }

    /// <summary>Popup outer width in physical pixels; 0 means "size to content".</summary>
    public int PopupWidth { get; set; }

    /// <summary>Popup outer height in physical pixels; 0 means "size to content".</summary>
    public int PopupHeight { get; set; }

    /// <summary>Override row gap; null inherits the drawer-wide value.</summary>
    public int? RowSpacing { get; set; }

    /// <summary>Override column gap; null inherits the drawer-wide value.</summary>
    public int? ColumnSpacing { get; set; }

    /// <summary>Per-drawer override for position badges; null = inherit global.</summary>
    public bool? ShowPositionBadges { get; set; }

    /// <summary>Items in this drawer with absolute position coords (1-based column/row).</summary>
    public List<ShortcutDrawerItem> Items { get; set; } = new();
}

/// <summary>A single shortcut entry placed in drawer grid.</summary>
public sealed class ShortcutDrawerItem
{
    /// <summary>File name (relative to drawer folder), e.g. "App.lnk".</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Column number (1-based).</summary>
    public int Column { get; set; } = 1;

    /// <summary>Row number (1-based).</summary>
    public int Row { get; set; } = 1;
}