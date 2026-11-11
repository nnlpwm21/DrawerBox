using System.Collections.Concurrent;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DeskBoxWhite.Helpers;
using DeskBoxWhite.Models;
using DeskBoxWhite.Platform;
using DeskBoxWhite.Services;
using DeskBoxWhite.Views;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using WinRT.Interop;
using Windows.Graphics;
using DrawingPoint = System.Drawing.Point;

namespace DeskBoxWhite;

/// <summary>
/// DrawerBox-only application shell. Everything the copied drawer feature
/// reaches through <see cref="App"/> lives here: the log, the language and
/// storage-root service, the hotkey service host, the drawer window registry,
/// and a minimal single-instance settings window.
/// </summary>
public sealed partial class App : Application
{
    public static App? Current { get; private set; }

    private readonly Dictionary<Guid, ShortcutDrawerWindow> _shortcutDrawerWindows = new();
    private ShortcutDrawerHotkeyService? _shortcutDrawerHotkeyService;
    private SettingsService? _settingsService;
    private LocalizationService? _localizationService;
    private FileService? _fileService;
    private WidgetManager? _widgetManager;
    private TaskbarIcon? _trayIcon;
    private MenuFlyout? _trayContextMenu;
    private string? _trayMenuSignature;
    private int _trayMenuEstimatedHeightDp;
    private Window? _hotkeyWindow;
    private nint _hotkeyWindowHandle;
    private DrawerSettingsWindow? _settingsWindow;
    private bool _isExiting;
    private DispatcherQueue _dispatcherQueue = null!;

    public static DispatcherQueue UiDispatcherQueue => Current is App app
        ? app._dispatcherQueue
        : throw new InvalidOperationException("The app shell is not started");

    public SettingsService SettingsService => _settingsService ?? throw new InvalidOperationException("Settings not started");

    public LocalizationService LocalizationService => _localizationService ?? throw new InvalidOperationException("Localization not started");

    public FileService FileService => _fileService ?? throw new InvalidOperationException("File service not started");

    public ThemeService? ThemeService => null;

    public WidgetManager? WidgetManager => _widgetManager ??= new WidgetManager();

    public ShortcutDrawerHotkeyService? ShortcutDrawerHotkeyService => _shortcutDrawerHotkeyService;

    public App()
    {
        Current = this;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _settingsService = new SettingsService();
        _localizationService = new LocalizationService(_settingsService);
        _fileService = new FileService();
        StartLogWriter();
        AttachUnhandledExceptionLogging();
        Log("[DrawerBox] Started");

        try
        {
            // Hidden message window the global hotkeys dispatch through.
            _hotkeyWindow = new Window();
            _hotkeyWindowHandle = WindowNative.GetWindowHandle(_hotkeyWindow);
        }
        catch (Exception ex)
        {
            Log($"[DrawerBox] Hidden hotkey window failed: {ex.Message}");
        }

        EnsureShortcutDrawerServices();
        CreateTrayIcon();
    }

    private void ShowSettingsWindow()
    {
        if (!_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(ShowSettingsWindow);
            return;
        }

        try
        {
            if (_settingsWindow is null)
            {
                _settingsWindow = new DrawerSettingsWindow(LocalizationService);
                _settingsWindow.Closed += OnSettingsWindowClosed;
            }

            _settingsWindow.Activate();
            Log("[DrawerBox] Settings window shown");
        }
        catch (Exception ex)
        {
            Log($"[DrawerBox] Settings window failed: {ex.Message}");
            for (Exception? inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            {
                Log($"[DrawerBox] Settings window inner: {inner.GetType().Name}: {inner.Message}");
            }
        }
    }

    private void OnSettingsWindowClosed(object sender, WindowEventArgs args)
    {
        Log("[DrawerBox] Settings window closed");
        if (sender is DrawerSettingsWindow window)
        {
            window.Closed -= OnSettingsWindowClosed;
        }

        _settingsWindow = null;
    }

    // Verbose logging mirrors the full app: off unless the environment
    // variable is set, so busy low-level traces stay quiet.
    private const string VerboseLoggingEnvironmentVariable = "DESKBOXWHITE_VERBOSE_LOG";

    private void AttachUnhandledExceptionLogging()
    {
        UnhandledException += (_, e) =>
        {
            Log($"[DrawerBox] Application.UnhandledException: {e.Message}");
            Log(e.Exception?.ToString() ?? "(no exception object)");
            // Keep the tray app alive for the user's own windows.
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log($"[DrawerBox] AppDomain.UnhandledException (terminating={e.IsTerminating}):");
            Log(e.ExceptionObject?.ToString() ?? "(no exception object)");
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log($"[DrawerBox] UnobservedTaskException: {e.Exception?.Message}");
            e.SetObserved();
        };
    }

    private static readonly bool EnableVerboseLogging = IsEnabledEnvironmentValue(
        Environment.GetEnvironmentVariable(VerboseLoggingEnvironmentVariable));

    public static bool IsVerboseLoggingEnabled => EnableVerboseLogging;

    private static bool IsEnabledEnvironmentValue(string? value)
    {
        return value is not null &&
            (value.Length == 0 ||
             (value != "0" && !value.Equals("false", StringComparison.OrdinalIgnoreCase)));
    }

    public static void LogVerbose(string msg)
    {
        if (!EnableVerboseLogging)
        {
            return;
        }

        Log(msg);
    }

    private void CreateTrayIcon()
    {
        try
        {
            var icon = AppBranding.CreateTrayIcon(
                "System",
                Win32Helper.IsSystemDarkMode());
            _trayContextMenu = BuildTrayContextMenu(new ShortcutDrawerData());
            _trayIcon = new TaskbarIcon
            {
                Icon = icon,
                ToolTipText = _localizationService?.T("DrawerBox.Tray.Tooltip") ?? "DrawerBox",
                // Native mode: the system renders a Win32 popup menu near the
                // tray icon and flips it against screen and monitor edges by
                // itself — multi-monitor and taskbar moves need no manual
                // anchor math. (SecondWindow + GetTrayContextMenuAnchorPoint
                // remains available for a one-line revert.)
                ContextMenuMode = ContextMenuMode.PopupMenu,
                MenuActivation = PopupActivationMode.None,
                NoLeftClickDelay = true,
                ContextFlyout = _trayContextMenu,
                LeftClickCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(ToggleAllDrawersFromTray),
                RightClickCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(ShowTrayContextMenuFromTray)
            };

            // H.NotifyIcon registers the shell icon only when the control
            // enters a visual tree, so it must live inside a window — mirror
            // the main app's hidden 1x1 tray host (which doubles here as the
            // hotkey message window). The host is invisible shell-side and
            // unclosable: it is the app's keep-alive, so an accidental close
            // would take the whole tray app down.
            if (_hotkeyWindow.Content is null)
            {
                _hotkeyWindow.Content = new Grid
                {
                    Width = 1,
                    Height = 1,
                    MinWidth = 1,
                    MinHeight = 1,
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        Microsoft.UI.Colors.Transparent)
                };
            }

            try
            {
                WindowShellState.TryApplyBorderlessOverlappedPresenter(_hotkeyWindow.AppWindow);
                WindowShellState.TryHideFromSwitchers(_hotkeyWindow.AppWindow);
                _hotkeyWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(1, 1));
                _hotkeyWindow.AppWindow.Closing += (_, e) =>
                {
                    if (!_isExiting)
                    {
                        e.Cancel = true;
                        Log("[DrawerBox] Host window close intercepted");
                    }
                };
            }
            catch (Exception ex)
            {
                Log($"[DrawerBox] Host window hardening not applied: {ex.Message}");
            }

            if (_hotkeyWindow.Content is Panel trayPanel)
            {
                trayPanel.Children.Add(_trayIcon);
            }

            // Reorder BEFORE activation: the keep-alive window exists for
            // messages and XAML load, not as a surface. Offscreen first, then
            // activate (XAML island loads), then hide — nothing ever shows.
            try
            {
                const uint SwpNOSize = 0x0001;
                const uint SwpNoZOrder = 0x0004;
                const uint SwpNoActivate = 0x0010;
                Win32Helper.SetWindowPos(
                    _hotkeyWindowHandle,
                    IntPtr.Zero,
                    -32000,
                    -32000,
                    0,
                    0,
                    SwpNOSize | SwpNoZOrder | SwpNoActivate);
            }
            catch (Exception ex)
            {
                Log($"[DrawerBox] Host window park failed: {ex.Message}");
            }

            _hotkeyWindow.Activate();
            try
            {
                Win32Helper.ShowWindow(_hotkeyWindowHandle, Win32Helper.SW_HIDE);
            }
            catch (Exception ex)
            {
                Log($"[DrawerBox] Host hide failed: {ex.Message}");
            }

            Log($"[DrawerBox] Tray icon created: iconLoaded={icon is not null}");
        }
        catch (Exception ex)
        {
            Log($"[DrawerBox] Tray icon failed: {ex.Message}");
        }
    }

    // ── Tray menu ─────────────────────────────────────────────────────
    // Left click toggles every drawer, the same call the global hotkey
    // takes. Right click opens the menu at the tray icon: settings, every
    // drawer by name, the icon store folder, and exit in the conventional
    // last position.

    /// <summary>
    /// Builds one fresh flyout with everything already in it. Because the
    /// library's second-window presenter keeps a snapshot of what was last
    /// shown, a shared flyout mutated in place made drawer rows accumulate
    /// on every right click — instead this whole flyout is replaced only
    /// when the drawer list actually changes.
    /// </summary>
    private MenuFlyout BuildTrayContextMenu(ShortcutDrawerData data)
    {
        var localization = LocalizationService;
        var menu = new MenuFlyout { ShouldConstrainToRootBounds = false };
        int estimatedHeightDp = TrayMenuDpRows.MenuFlyoutChromeHeightDp;

        menu.Items.Add(CreateTrayMenuItem(
            localization.T("DrawerBox.Tray.Settings"),
            "\uE713",
            ShowSettingsWindow));
        estimatedHeightDp += TrayMenuDpRows.MenuFlyoutItemRowHeightDp;

        var drawerEntries = data.Drawers
            .Where(drawer => drawer?.Id is { } id && id != Guid.Empty)
            .ToList();
        if (drawerEntries.Count > 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            estimatedHeightDp += TrayMenuDpRows.MenuFlyoutSeparatorHeightDp;
            for (int i = 0; i < drawerEntries.Count; i++)
            {
                Models.ShortcutDrawerDefinition drawer = drawerEntries[i];
                string label = string.IsNullOrWhiteSpace(drawer.Name)
                    ? "#" + (i + 1).ToString()
                    : drawer.Name.Trim();
                menu.Items.Add(CreateTrayDrawerMenuItem(drawer.Id, label));
                estimatedHeightDp += TrayMenuDpRows.MenuFlyoutItemRowHeightDp;
            }

            menu.Items.Add(new MenuFlyoutSeparator());
            estimatedHeightDp += TrayMenuDpRows.MenuFlyoutSeparatorHeightDp;
        }

        menu.Items.Add(CreateTrayMenuItem(
            localization.T("DrawerBox.Tray.OpenFolder"),
            "\uE8B7",
            OpenStoreFolderFromTray));
        estimatedHeightDp += TrayMenuDpRows.MenuFlyoutItemRowHeightDp;
        menu.Items.Add(CreateTrayMenuItem(
            localization.T("DrawerBox.Tray.Exit"),
            "\uE7E8",
            RequestExit));
        estimatedHeightDp += TrayMenuDpRows.MenuFlyoutItemRowHeightDp + TrayMenuDpRows.MenuFlyoutSafetyMarginDp;

        // The anchor logic lifts the popup by this much so it opens above the
        // taskbar instead of spilling onto a monitor below or beside it.
        _trayMenuEstimatedHeightDp = estimatedHeightDp;
        return menu;
    }

    /// <summary>
    /// Both Click and Command fire in the second-window presenter, so one
    /// action is guarded against running twice per menu opening.
    /// </summary>
    private static MenuFlyoutItem CreateTrayMenuItem(string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem
        {
            Text = text,
            Icon = new FontIcon { Glyph = glyph },
            Tag = false
        };

        void Run()
        {
            if (item.Tag is true)
            {
                return;
            }

            item.Tag = true;
            action();
        }

        item.Click += (_, _) => Run();
        item.Command = new CommunityToolkit.Mvvm.Input.RelayCommand(Run);
        return item;
    }

    private MenuFlyoutItem CreateTrayDrawerMenuItem(Guid drawerId, string label)
    {
        var item = new MenuFlyoutItem
        {
            Text = label,
            Tag = false
        };

        void Run()
        {
            if (item.Tag is true)
            {
                return;
            }

            item.Tag = true;
            OpenShortcutDrawer(drawerId);
        }

        item.Click += (_, _) => Run();
        item.Command = new CommunityToolkit.Mvvm.Input.RelayCommand(Run);
        return item;
    }

    private async void ShowTrayContextMenuFromTray()
    {
        if (_trayIcon is null || !Win32Helper.GetCursorPos(out var cursor))
        {
            return;
        }

        try
        {
            var data = await new ShortcutDrawerStore().LoadAsync();
            string signature = string.Join(
                "|",
                data.Drawers
                    .Where(drawer => drawer?.Id is { } id && id != Guid.Empty)
                    .Select(drawer => drawer.Id + ":" + (drawer.Name ?? string.Empty)));
            if (_trayContextMenu is null || !string.Equals(signature, _trayMenuSignature, StringComparison.Ordinal))
            {
                _trayContextMenu = BuildTrayContextMenu(data);
                // Re-assigning makes the library rebuild its hidden second
                // window so the fresh flyout is what gets presented.
                _trayIcon.ContextFlyout = _trayContextMenu;
                _trayMenuSignature = signature;
            }

            // The double-fire guard must not survive between menu openings:
            // a reused flyout keeps its items, so any Tag left true would
            // silently swallow the next click.
            foreach (MenuFlyoutItem? item in _trayContextMenu.Items.OfType<MenuFlyoutItem>())
            {
                item.Tag = false;
            }

            // Native menu mode: TrackPopupMenu opens near this point and the
            // system handles edge flipping and monitor catching itself.
            var point = new DrawingPoint(cursor.X, cursor.Y);
            _trayIcon.ShowContextMenu(point);
        }
        catch (Exception ex)
        {
            Log($"[DrawerBox] Tray context menu failed: {ex.Message}");
        }
    }

    private async void ToggleAllDrawersFromTray()
    {
        try
        {
            await ToggleAllShortcutDrawersAsync();
        }
        catch (Exception ex)
        {
            Log($"[DrawerBox] Tray toggle drawers failed: {ex.Message}");
        }
    }

    private void OpenStoreFolderFromTray()
    {
        try
        {
            string folder = ShortcutDrawerStore.GetDefaultStoreDirectory(
                SettingsService.Settings.FileWidget.DefaultManagedStorageRootPath);
            Directory.CreateDirectory(folder);
            using var _ = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log($"[DrawerBox] Open store folder failed: {ex.Message}");
        }
    }

    private void CloseTrayIcon()
    {
        try
        {
            _trayIcon?.Dispose();
            _trayIcon = null;
        }
        catch
        {
        }
    }

    public void RequestExit()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        DisposeShortcutDrawerServices();
        CloseTrayIcon();
        _settingsService?.Dispose();
        try
        {
            _settingsWindow?.Close();
        }
        catch
        {
        }

        Exit();
    }

    // ─── Shortcut Drawer Services ───────────────────────────────────

    private void EnsureShortcutDrawerServices()
    {
        if (LocalizationService is null || SettingsService is null)
        {
            return;
        }

        try
        {
            if (_shortcutDrawerHotkeyService is null)
            {
                _shortcutDrawerHotkeyService = new ShortcutDrawerHotkeyService(
                    SettingsService,
                    LocalizationService,
                    (drawerId) => ToggleShortcutDrawerAsync(drawerId),
                    mainGestureProvider: null,
                    searchGestureProvider: null,
                    UiDispatcherQueue);
                if (_hotkeyWindowHandle != nint.Zero)
                {
                    _shortcutDrawerHotkeyService.Attach(_hotkeyWindowHandle);
                    Log("[ShortcutDrawer] Hotkey service attached");
                }
            }

            RefreshShortcutDrawerHotkeys();
        }
        catch (Exception ex)
        {
            Log($"[ShortcutDrawer] Hotkey service initialization failed: {ex}");
            _shortcutDrawerHotkeyService?.Dispose();
            _shortcutDrawerHotkeyService = null;
        }
    }

    /// <summary>
    /// Raised when drawer layout settings change (icon size, spacing), so an
    /// open drawer window can refresh without reading the store on a timer.
    /// </summary>
    public static event EventHandler? ShortcutDrawerLayoutChanged;

    /// <summary>Raised when the user changes the drawer surface appearance.</summary>
    public static event EventHandler? DrawerAppearanceChanged;

    /// <summary>
    /// Tells every open drawer window to re-read the appearance settings.
    /// </summary>
    public static void NotifyDrawerAppearanceChanged()
    {
        var handler = DrawerAppearanceChanged;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(null, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log($"[DrawerBox] Appearance notification failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Tells an open drawer window that its layout settings changed.
    /// </summary>
    public static void NotifyShortcutDrawerLayoutChanged()
    {
        var handler = ShortcutDrawerLayoutChanged;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(null, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log($"[ShortcutDrawer] Layout change notification failed: {ex.Message}");
        }
    }

    public void RefreshShortcutDrawerHotkeys()
    {
        var service = _shortcutDrawerHotkeyService;
        if (service is null)
        {
            return;
        }

        _ = RefreshShortcutDrawerHotkeysAsync(service);
    }

    private async Task RefreshShortcutDrawerHotkeysAsync(ShortcutDrawerHotkeyService service)
    {
        try
        {
            ShortcutDrawerData data = await new ShortcutDrawerStore().LoadAsync();
            service.RefreshAllRegistrations(data);
        }
        catch (Exception ex)
        {
            Log($"[ShortcutDrawer] Failed to refresh hotkeys: {ex}");
        }
    }

    private void DisposeShortcutDrawerServices()
    {
        foreach (ShortcutDrawerWindow window in _shortcutDrawerWindows.Values.ToArray())
        {
            try
            {
                window.Close();
            }
            catch (Exception ex)
            {
                Log($"[ShortcutDrawer] Window close failed during cleanup: {ex.Message}");
            }
        }

        _shortcutDrawerWindows.Clear();
        _shortcutDrawerHotkeyService?.Dispose();
        _shortcutDrawerHotkeyService = null;
        Log("[ShortcutDrawer] Services disposed");
    }

    private async Task ToggleShortcutDrawerAsync(Guid drawerId)
    {
        if (!UiDispatcherQueue.HasThreadAccess)
        {
            UiDispatcherQueue.TryEnqueue(() => _ = ToggleShortcutDrawerAsync(drawerId));
            return;
        }

        // An empty id is the drawer-wide hotkey: it opens every drawer, so one
        // gesture brings the whole set up (and a second press takes it down).
        if (drawerId == Guid.Empty)
        {
            await ToggleAllShortcutDrawersAsync();
            return;
        }

        if (TryGetShortcutDrawerWindow(drawerId) is { IsPopupVisible: true } open && !open.IsClosed)
        {
            open.HidePopup();
            return;
        }

        OpenShortcutDrawer(drawerId);
    }

    /// <summary>
    /// Opens every drawer at once, or closes them all when they are already up.
    /// </summary>
    private async Task ToggleAllShortcutDrawersAsync()
    {
        List<Guid> ids;
        try
        {
            var data = await new ShortcutDrawerStore().LoadAsync();
            ids = data.Drawers
                .Where(drawer => drawer?.Id is { } id && id != Guid.Empty)
                .Select(drawer => drawer.Id)
                .ToList();
        }
        catch (Exception ex)
        {
            Log($"[ShortcutDrawer] Failed to read the drawer list: {ex}");
            return;
        }

        if (ids.Count == 0)
        {
            return;
        }

        bool anyVisible = ids
            .Select(TryGetShortcutDrawerWindow)
            .Any(window => window is { IsPopupVisible: true });
        if (anyVisible)
        {
            foreach (Guid id in ids)
            {
                TryGetShortcutDrawerWindow(id)?.HidePopup();
            }

            return;
        }

        foreach (Guid id in ids)
        {
            OpenShortcutDrawer(id);
            await Task.Delay(40);
        }
    }

    /// <summary>
    /// Public entry point used by the tray menu and settings. Unlike the global
    /// hotkey this is idempotent: repeat invocations only focus the drawer.
    /// </summary>
    public void OpenShortcutDrawer(Guid drawerId = default)
    {
        // The drawer has no background file indexing to cancel; the call was
        // only a guard in the full app against a stalled widget cleanup.
        ShortcutDrawerWindow? window = TryGetShortcutDrawerWindow(drawerId);
        if (window is null || window.IsClosed)
        {
            window = CreateShortcutDrawerWindow(drawerId);
        }

        window.ShowPopup(drawerId);
    }

    /// <summary>
    /// Bounds of the other open drawer popups, used for edge snapping while one
    /// of them is dragged or resized.
    /// </summary>
    public List<RectInt32> GetOpenDrawerBoundsExcept(Guid drawerId)
    {
        var bounds = new List<RectInt32>();
        foreach ((Guid id, ShortcutDrawerWindow window) in _shortcutDrawerWindows)
        {
            if (id == drawerId || window.IsClosed)
            {
                continue;
            }

            if (Win32Helper.GetWindowRect(window.WindowHandle, out var rect))
            {
                bounds.Add(new RectInt32(
                    rect.Left,
                    rect.Top,
                    rect.Right - rect.Left,
                    rect.Bottom - rect.Top));
            }
        }

        return bounds;
    }

    /// <summary>
    /// The open drawer popup whose window rect contains the screen point, if
    /// any. Used to land tile drags on another drawer (cross-window move).
    /// </summary>
    public ShortcutDrawerWindow? GetOpenDrawerWindowAt(Windows.Graphics.PointInt32 point, Guid exceptDrawerId)
    {
        foreach ((Guid id, ShortcutDrawerWindow window) in _shortcutDrawerWindows)
        {
            if (id == exceptDrawerId || window.IsClosed)
            {
                continue;
            }

            if (Win32Helper.GetWindowRect(window.WindowHandle, out var rect)
                && point.X >= rect.Left && point.X < rect.Right
                && point.Y >= rect.Top && point.Y < rect.Bottom)
            {
                return window;
            }
        }

        return null;
    }

    private ShortcutDrawerWindow? TryGetShortcutDrawerWindow(Guid drawerId)
    {
        return _shortcutDrawerWindows.TryGetValue(drawerId, out ShortcutDrawerWindow? window) &&
            !window.IsClosed
            ? window
            : null;
    }

    private ShortcutDrawerWindow CreateShortcutDrawerWindow(Guid drawerId)
    {
        var window = new ShortcutDrawerWindow(LocalizationService);
        _shortcutDrawerWindows[drawerId] = window;
        window.ItemActivated += (_, item) =>
        {
            window.HidePopup();
            _ = window.OpenItemAsync(item);
        };
        window.Closed += (_, _) =>
        {
            _shortcutDrawerWindows.Remove(drawerId);
            Log("[ShortcutDrawer] Window shell discarded; next open will recreate it");
        };

        return window;
    }

    // ─── Log ────────────────────────────────────────────────────────

    private static readonly ConcurrentQueue<string> s_logQueue = new();
    private static readonly SemaphoreSlim s_logSignal = new(0, int.MaxValue);
    private static int s_pendingLogLineCount;
    private const int MaxQueuedLogLines = 20_000;

    public static void Log(string msg)
    {
        try
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}";
            if (Interlocked.Increment(ref s_pendingLogLineCount) > MaxQueuedLogLines)
            {
                Interlocked.Decrement(ref s_pendingLogLineCount);
                return;
            }

            s_logQueue.Enqueue(line);
            s_logSignal.Release();
        }
        catch
        {
        }
    }

    private static void StartLogWriter()
    {
        Task.Run(() =>
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DrawerBox");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "DrawerBox.log");
            using var writer = new StreamWriter(
                path,
                append: true,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.AutoFlush = true;
            while (true)
            {
                s_logSignal.Wait();
                while (s_logQueue.TryDequeue(out string? line))
                {
                    Interlocked.Decrement(ref s_pendingLogLineCount);
                    writer.Write(line);
                }
            }
        });
    }
}

/// <summary>Optional theme follower; the drawer build follows the system theme.</summary>
public sealed class ThemeService
{
    public event Action? AppearanceChanged;

    public Microsoft.UI.Xaml.ElementTheme CurrentTheme => Microsoft.UI.Xaml.ElementTheme.Default;
}

/// <summary>
/// DrawerBox-only bring-to-front choke point that mirrors the widget manager's
/// signature so the copied drawer window compiles unchanged.
/// </summary>
public sealed class WidgetManager
{
    public void BringAuxiliaryWindowToFront(nint windowHandle, string reason)
    {
        if (windowHandle != nint.Zero && Win32Helper.IsWindow(windowHandle))
        {
            _ = reason;
            Win32Helper.BringWindowTemporarilyToFront(windowHandle);
        }
    }
}
