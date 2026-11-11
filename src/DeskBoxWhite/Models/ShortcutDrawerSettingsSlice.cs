namespace DeskBoxWhite.Models;

/// <summary>
/// Shortcut drawer preferences: the global hotkey that slides the drawer
/// panel in from the top-center of the screen. The column/item content itself
/// lives in the dedicated <see cref="DeskBoxWhite.Services.ShortcutDrawerStore"/>.
/// </summary>
public sealed class ShortcutDrawerSettingsSlice
{
    /// <summary>Whether the shortcut drawer global hotkey is enabled.</summary>
    public bool ShortcutDrawerHotkeyEnabled { get; set; } = true;

    /// <summary>Shortcut drawer hotkey modifier bit flags.</summary>
    public int ShortcutDrawerHotkeyModifiers { get; set; } =
        (int)(HotkeyModifierKeys.Control | HotkeyModifierKeys.Alt);

    /// <summary>
    /// Shortcut drawer hotkey virtual key code. Default: D (0x44).
    /// </summary>
    public int ShortcutDrawerHotkeyKey { get; set; } = 0x44;
}