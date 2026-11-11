using System.Runtime.InteropServices;
using DeskBoxWhite.Helpers;
using DeskBoxWhite.Models;
using DeskBoxWhite.Platform;
using Windows.System;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;

namespace DeskBoxWhite.Services;

/// <summary>
/// Manages the global hotkey that slides the shortcut drawer in from the
/// top-center of the screen. Uses the standard Win32 RegisterHotKey +
/// WM_HOTKEY mechanism like the main and search hotkeys' ordinary gestures.
/// </summary>
public sealed class ShortcutDrawerHotkeyService : IDisposable
{
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private static readonly UIntPtr SubclassIdBase = new(0x4446);

    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly Func<Guid, Task> _invokeAsync;
    private readonly Func<GlobalHotkeyGesture?>? _mainGestureProvider;
    private readonly Func<GlobalHotkeyGesture?>? _searchGestureProvider;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Win32Helper.SubclassProc _subclassProc;
    private IntPtr _windowHandle;
    private bool _isSubclassInstalled;
    private bool _isInvoking;
    private long _receivedSequence;
    private long _invocationSequence;
    private long _dispatchFailureSequence;

    private readonly Dictionary<int, Guid> _hotkeyToDrawerId = new();
    private readonly Dictionary<Guid, int> _drawerToHotkeyId = new();
    private readonly Dictionary<Guid, GlobalHotkeyGesture> _drawerGestures = new();
    private readonly HashSet<int> _registeredHotkeyIds = new();
    private int _nextHotkeyId;

    public ShortcutDrawerHotkeyService(
        SettingsService settingsService,
        LocalizationService localizationService,
        Func<Guid, Task> invokeAsync,
        Func<GlobalHotkeyGesture?>? mainGestureProvider,
        Func<GlobalHotkeyGesture?>? searchGestureProvider,
        DispatcherQueue dispatcherQueue)
    {
        _settingsService = settingsService;
        _localizationService = localizationService;
        _invokeAsync = invokeAsync;
        _mainGestureProvider = mainGestureProvider;
        _searchGestureProvider = searchGestureProvider;
        _dispatcherQueue = dispatcherQueue;
        _subclassProc = WindowSubclassProc;
    }

    public bool IsRegistered => _registeredHotkeyIds.Count > 0;
    public long ReceivedCount => Interlocked.Read(ref _receivedSequence);
    public long InvocationCount => Interlocked.Read(ref _invocationSequence);
    public long DispatchFailureCount => Interlocked.Read(ref _dispatchFailureSequence);

    public GlobalHotkeyGesture GetGesture(ShortcutDrawerDefinition drawer) => GlobalHotkeyService.NormalizeGesture(
        drawer?.HotkeyModifiers ?? ((int)(HotkeyModifierKeys.Control | HotkeyModifierKeys.Alt)),
        drawer?.HotkeyKey ?? 0x44);

    /// <summary>
    /// The drawer the drawer-wide hotkey opens: the drawer after the one last
    /// opened, wrapping around. Every drawer is reachable with the single global
    /// gesture, and a drawer added or deleted later still resolves correctly.
    /// </summary>
    public static Guid ResolveGlobalTargetDrawerId(ShortcutDrawerData? data, Guid? lastOpenedId = null)
    {
        if (data?.Drawers is null || data.Drawers.Count == 0)
        {
            return Guid.Empty;
        }

        var drawers = data.Drawers
            .Where(drawer => drawer?.Id is { } id && id != Guid.Empty)
            .ToList();
        if (drawers.Count == 0)
        {
            return Guid.Empty;
        }

        // Cycle: open the drawer after the one last opened, wrapping around, so
        // every drawer is reachable with the single drawer-wide hotkey.
        if (lastOpenedId is not { } last || last == Guid.Empty)
        {
            return drawers[0].Id;
        }

        int index = drawers.FindIndex(drawer => drawer.Id == last);
        return drawers[(index + 1) % drawers.Count].Id;
    }

    public void Attach(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        Detach();
        _windowHandle = windowHandle;
    }

    public void Detach()
    {
        UnregisterAll();
        RemoveSubclass();
        _windowHandle = IntPtr.Zero;
    }

    public void RefreshAllRegistrations(ShortcutDrawerData? data)
    {
        UnregisterAll();

        if (_windowHandle == IntPtr.Zero)
        {
            RemoveSubclass();
            return;
        }

        if (!_isSubclassInstalled)
        {
            _isSubclassInstalled = Win32Helper.SetWindowSubclass(
                _windowHandle,
                _subclassProc,
                SubclassIdBase,
                UIntPtr.Zero);
            if (!_isSubclassInstalled)
            {
                App.Log("[ShortcutDrawer] Failed to install tray window subclass");
                return;
            }
        }

        if (data is null)
        {
            return;
        }

        var ownedGestures = new HashSet<GlobalHotkeyGesture>();
        foreach (var drawer in data.Drawers)
        {
            if (drawer is null || !drawer.HotkeyEnabled)
            {
                continue;
            }
            var gesture = GetGesture(drawer);
            if (gesture.Modifiers.HasFlag(HotkeyModifierKeys.Windows) ||
                !GlobalHotkeyService.IsValidGesture(gesture) ||
                GlobalHotkeyService.IsReservedSystemGesture(gesture))
            {
                continue;
            }
            if (IsGestureOwnedByMainHotkey(gesture) || IsGestureOwnedBySearchHotkey(gesture))
            {
                continue;
            }

            // Two drawers may not share one gesture; the first one keeps it.
            if (!ownedGestures.Add(gesture))
            {
                continue;
            }

            int id = AllocateHotkeyId();
            if (Register(_windowHandle, id, gesture))
            {
                _hotkeyToDrawerId[id] = drawer.Id;
                _drawerToHotkeyId[drawer.Id] = id;
                _drawerGestures[drawer.Id] = gesture;
                _registeredHotkeyIds.Add(id);
                App.Log($"[ShortcutDrawer] Registered drawer {drawer.Id} gesture={FormatGesture(gesture)}");
            }
        }

        RegisterGlobalHotkey(data, ownedGestures);
    }

    /// <summary>
    /// Registers the drawer-wide hotkey. It maps to <see cref="Guid.Empty"/> and
    /// is resolved at invocation time, so a drawer added or deleted later still
    /// resolves to the current first drawer without its own hotkey.
    /// </summary>
    private void RegisterGlobalHotkey(ShortcutDrawerData data, HashSet<GlobalHotkeyGesture> ownedGestures)
    {
        if (!data.GlobalHotkeyEnabled)
        {
            return;
        }

        var gesture = GlobalHotkeyService.NormalizeGesture(
            data.GlobalHotkeyModifiers,
            data.GlobalHotkeyKey);
        if (gesture.Modifiers.HasFlag(HotkeyModifierKeys.Windows) ||
            !GlobalHotkeyService.IsValidGesture(gesture) ||
            GlobalHotkeyService.IsReservedSystemGesture(gesture))
        {
            App.Log("[ShortcutDrawer] Global drawer hotkey gesture is not registrable");
            return;
        }

        if (IsGestureOwnedByMainHotkey(gesture) || IsGestureOwnedBySearchHotkey(gesture))
        {
            App.Log("[ShortcutDrawer] Global drawer hotkey conflicts with the main or search hotkey");
            return;
        }

        if (ownedGestures.Contains(gesture))
        {
            App.Log("[ShortcutDrawer] Global drawer hotkey is already owned by a drawer hotkey");
            return;
        }

        int id = AllocateHotkeyId();
        if (Register(_windowHandle, id, gesture))
        {
            _hotkeyToDrawerId[id] = Guid.Empty;
            _registeredHotkeyIds.Add(id);
            App.Log($"[ShortcutDrawer] Registered global drawer hotkey gesture={FormatGesture(gesture)}");
        }
    }

    private int AllocateHotkeyId()
    {
        _nextHotkeyId = Math.Max(_nextHotkeyId, 1);
        return _nextHotkeyId++;
    }

    public bool IsGestureOwnedByMainHotkey(GlobalHotkeyGesture gesture)
    {
        return _mainGestureProvider?.Invoke()?.Equals(gesture) == true;
    }

    public bool IsGestureOwnedBySearchHotkey(GlobalHotkeyGesture gesture)
    {
        return _searchGestureProvider?.Invoke()?.Equals(gesture) == true;
    }

    public bool TryApplyGesture(Guid drawerId, GlobalHotkeyGesture gesture, out string? error)
    {
        error = null;
        gesture = GlobalHotkeyService.NormalizeGesture((int)gesture.Modifiers, gesture.VirtualKey);
        if (gesture.Modifiers.HasFlag(HotkeyModifierKeys.Windows) ||
            !GlobalHotkeyService.IsValidGesture(gesture))
        {
            error = _localizationService.T("Settings.ShortcutDrawer.Hotkey.Status.Invalid");
            return false;
        }

        if (IsGestureOwnedByMainHotkey(gesture))
        {
            error = _localizationService.T("Settings.ShortcutDrawer.Hotkey.Status.GlobalHotkeyConflict");
            return false;
        }

        if (IsGestureOwnedBySearchHotkey(gesture))
        {
            error = _localizationService.T("Settings.ShortcutDrawer.Hotkey.Status.SearchHotkeyConflict");
            return false;
        }
        return true;
    }

    public void SetEnabled(Guid drawerId, bool enabled)
    {
        // Handled by caller refreshing registrations
    }

    private IntPtr WindowSubclassProc(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
    {
        if (message == GlobalHotkeyService.WmHotkey)
        {
            uint id = wParam.ToUInt32();
            if (_hotkeyToDrawerId.TryGetValue((int)id, out Guid drawerId))
            {
                long receivedId = Interlocked.Increment(ref _receivedSequence);

                // The modifier keys are deliberately left alone here. Releasing
                // them synthetically (ReleaseAllModifiers) would make Windows
                // forget they are held, so holding Ctrl+Alt and tapping the key
                // again could never re-trigger this hotkey. The drawer has no
                // text input that would be hurt by a sticky modifier.

                if (_dispatcherQueue.TryEnqueue(() =>
                {
                    _ = InvokeAsync(drawerId);
                }))
                {
                    return IntPtr.Zero;
                }

                Interlocked.Increment(ref _dispatchFailureSequence);
                App.Log($"[ShortcutDrawer] UI dispatch rejected id={receivedId}");
                return IntPtr.Zero;
            }
        }

        return Win32Helper.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private async Task InvokeAsync(Guid drawerId)
    {
        if (_isInvoking)
        {
            return;
        }

        _isInvoking = true;
        long invocationId = Interlocked.Increment(ref _invocationSequence);
        App.Log($"[ShortcutDrawer] Triggered id={invocationId}");
        try
        {
            await _invokeAsync(drawerId);
        }
        catch (Exception ex)
        {
            App.Log($"[ShortcutDrawer] Invocation failed: {ex}");
        }
        finally
        {
            _isInvoking = false;
        }
    }

    private void UnregisterAll()
    {
        if (_windowHandle != IntPtr.Zero)
        {
            foreach (var id in _registeredHotkeyIds.ToArray())
            {
                Win32Helper.UnregisterHotKey(_windowHandle, id);
            }
        }
        _registeredHotkeyIds.Clear();
        _hotkeyToDrawerId.Clear();
        _drawerToHotkeyId.Clear();
        _drawerGestures.Clear();
        _nextHotkeyId = 0;
    }

    private void RemoveSubclass()
    {
        if (_isSubclassInstalled && _windowHandle != IntPtr.Zero)
        {
            Win32Helper.RemoveWindowSubclass(_windowHandle, _subclassProc, SubclassIdBase);
        }

        _isSubclassInstalled = false;
    }

    private static bool Register(IntPtr windowHandle, int hotkeyId, GlobalHotkeyGesture gesture)
    {
        return Win32Helper.RegisterHotKey(
            windowHandle,
            hotkeyId,
            ToWin32Modifiers(gesture.Modifiers) | ModNoRepeat,
            (uint)gesture.VirtualKey);
    }

    private static uint ToWin32Modifiers(HotkeyModifierKeys modifiers)
    {
        uint value = 0;
        if (modifiers.HasFlag(HotkeyModifierKeys.Alt))
        {
            value |= ModAlt;
        }

        if (modifiers.HasFlag(HotkeyModifierKeys.Control))
        {
            value |= ModControl;
        }

        if (modifiers.HasFlag(HotkeyModifierKeys.Shift))
        {
            value |= ModShift;
        }

        return value;
    }

    private static string FormatGesture(GlobalHotkeyGesture gesture)
    {
        var parts = new List<string>();
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

        parts.Add($"VK:{gesture.VirtualKey:X2}");
        return string.Join("+", parts);
    }

    public void Dispose()
    {
        Detach();
    }
}