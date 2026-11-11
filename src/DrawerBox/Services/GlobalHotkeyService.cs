using System.Globalization;
using DeskBoxWhite.Models;
using Windows.System;

namespace DeskBoxWhite.Services;

/// <summary>
/// DrawerBox-only static helper subset of the full GlobalHotkeyService: gesture
/// normalization, validation, and formatting used by the drawer settings UI.
/// </summary>
public static class GlobalHotkeyService
{
    public const uint WmHotkey = 0x0312;

    // Hardware Copilot keys deliver Left Shift + Win + F23 on most keyboards,
    // so the preset registers that chord instead of adding a new activation kind.
    public static GlobalHotkeyGesture CopilotKeyGesture { get; } = new(
        HotkeyModifierKeys.Windows | HotkeyModifierKeys.Shift,
        (int)VirtualKey.F23);

    public static GlobalHotkeyGesture NormalizeGesture(int modifiers, int virtualKey)
    {
        var normalizedModifiers = (HotkeyModifierKeys)modifiers &
            (HotkeyModifierKeys.Alt |
             HotkeyModifierKeys.Control |
             HotkeyModifierKeys.Shift |
             HotkeyModifierKeys.Windows);
        return new GlobalHotkeyGesture(normalizedModifiers, virtualKey);
    }

    public static bool IsValidGesture(GlobalHotkeyGesture gesture)
    {
        if (gesture.VirtualKey <= 0)
        {
            return false;
        }

        if (gesture.Modifiers == HotkeyModifierKeys.None)
        {
            return IsFunctionKey(gesture.VirtualKey);
        }

        return IsAllowedPrimaryKey(gesture.VirtualKey);
    }

    public static bool IsReservedSystemGesture(GlobalHotkeyGesture gesture)
    {
        return gesture.VirtualKey == (int)VirtualKey.Space &&
               gesture.Modifiers is
                   HotkeyModifierKeys.Windows or
                   HotkeyModifierKeys.Alt;
    }

    public static bool IsRiskyGesture(GlobalHotkeyGesture gesture)
    {
        return IsReservedSystemGesture(gesture) ||
               gesture.Modifiers == HotkeyModifierKeys.None ||
               gesture.VirtualKey is
                   (int)VirtualKey.F1 or
                   (int)VirtualKey.F2 or
                   (int)VirtualKey.F5 or
                   (int)VirtualKey.F11 or
                   (int)VirtualKey.F12;
    }

    private static bool IsFunctionKey(int virtualKey)
    {
        return virtualKey is >= (int)VirtualKey.F1 and <= (int)VirtualKey.F24;
    }

    private static bool IsAllowedPrimaryKey(int virtualKey)
    {
        return virtualKey is
            >= (int)VirtualKey.A and <= (int)VirtualKey.Z or
            >= (int)VirtualKey.Number0 and <= (int)VirtualKey.Number9 or
            >= (int)VirtualKey.NumberPad0 and <= (int)VirtualKey.NumberPad9 or
            >= (int)VirtualKey.F1 and <= (int)VirtualKey.F24;
    }

    public static string FormatGesture(GlobalHotkeyGesture gesture, LocalizationService localization)
    {
        if (gesture.VirtualKey <= 0)
        {
            return localization.T("Settings.GlobalHotkey.NotSet");
        }

        if (gesture.Equals(CopilotKeyGesture))
        {
            return localization.T("Settings.GlobalHotkey.Preset.CopilotKey");
        }

        var parts = new List<string>();
        if (gesture.Modifiers.HasFlag(HotkeyModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        if (gesture.Modifiers.HasFlag(HotkeyModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (gesture.Modifiers.HasFlag(HotkeyModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (gesture.Modifiers.HasFlag(HotkeyModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        parts.Add(FormatVirtualKey(gesture.VirtualKey));
        return string.Join(" + ", parts);
    }

    private static string FormatVirtualKey(int virtualKey)
    {
        if (virtualKey is >= (int)VirtualKey.A and <= (int)VirtualKey.Z)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= (int)VirtualKey.Number0 and <= (int)VirtualKey.Number9)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= (int)VirtualKey.NumberPad0 and <= (int)VirtualKey.NumberPad9)
        {
            return $"Num {virtualKey - (int)VirtualKey.NumberPad0}";
        }

        if (virtualKey is >= (int)VirtualKey.F1 and <= (int)VirtualKey.F24)
        {
            return $"F{virtualKey - (int)VirtualKey.F1 + 1}";
        }

        return ((VirtualKey)virtualKey) switch
        {
            VirtualKey.Space => "Space",
            VirtualKey.Tab => "Tab",
            VirtualKey.Insert => "Insert",
            VirtualKey.Delete => "Delete",
            VirtualKey.Home => "Home",
            VirtualKey.End => "End",
            VirtualKey.PageUp => "Page Up",
            VirtualKey.PageDown => "Page Down",
            _ => ((VirtualKey)virtualKey).ToString()
        };
    }
}
