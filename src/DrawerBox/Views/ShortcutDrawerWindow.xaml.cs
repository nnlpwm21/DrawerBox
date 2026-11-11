using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using DeskBoxWhite.Helpers;
using DeskBoxWhite.Models;
using DeskBoxWhite.Platform;
using DeskBoxWhite.Services;
using DeskBoxWhite.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.System;
using Windows.UI.ViewManagement;

namespace DeskBoxWhite.Views;

public sealed partial class ShortcutDrawerWindow : Window
{
	private const int MinDrawerWidth = 420;

	private const int MinDrawerHeight = 240;

	private const int MaxDrawerWidth = 760;

	private const int MinTileSize = 72;

	private const int MaxTileSize = 160;

	private const int TileChromeHeight = 40;

	private const int GridChromeWidth = 46;

	private const int GridChromeHeight = 104;

	private const int ExtraGridRows = 2;

	private const int ExtraGridColumns = 2;

	private const double MaxWorkAreaHeightShare = 0.7;

	private const int CascadeOffset = 28;

	private static readonly TimeSpan FocusWatchInterval = TimeSpan.FromMilliseconds(400L);

	private static readonly TimeSpan RightTapMenuSuppression = TimeSpan.FromMilliseconds(300L);

	private const double DragStartThreshold = 2.0;

	private readonly LocalizationService _localizationService;

	private readonly ShortcutDrawerStore _store;

	private readonly ThemeService? _themeService;

	private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;

	private AppWindow? _appWindow;

	private nint _hwnd;

	private bool _isClosed;

	private bool _isHiding;

	/// <summary>Editing is the default: every open starts unlocked so tiles are
	/// draggable immediately; the header toggle locks the drawer.</summary>
	private bool _isEditMode = true;

	private bool _isSyncingEditToggle;

	private ShortcutDrawerData? _data;

	private ShortcutDrawerDefinition? _drawer;

	private ShortcutDrawerItemViewModel? _selectedItem;

	private ShortcutDrawerItemViewModel? _menuTarget;

	private DateTime _shownAtUtc;

	private DateTime _contextMenuShownAt;

	private readonly DispatcherTimer _focusWatchTimer;

	private readonly HashSet<string> _selectedItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	private Point _marqueeStart;

	private bool _isMarqueeActive;

	private ShortcutDrawerItemViewModel? _pendingItem;

	private TranslateTransform? _dragTileTransform;

	// Multi-select group drag: ghosts move together, members land with their
	// relative offsets preserved (MoveGroupToReleaseAsync).
	private List<Border> _dragGroupOwners = new List<Border>();

	private List<TranslateTransform> _dragGroupTransforms = new List<TranslateTransform>();

	private List<ShortcutDrawerItemViewModel> _dragGroupItems = new List<ShortcutDrawerItemViewModel>();

	private Border? _tileDragOwner;

	private Point _tileDragStart;

	private bool _isTileDragging;

	private bool _isWindowDragging;
	private bool _customDrawerSurfaceActive;

	private PointInt32 _dragOrigin;

	private PointInt32 _dragWindowOrigin;

	private bool _isResizeSessionActive;

	/// <summary>
	/// Window bounds at the start of the current move-or-resize gesture. The
	/// snap calculator compares these against the desired rectangle, so only
	/// the edges the user is actually dragging take part in the snap.
	/// </summary>
	private RectInt32 _dragStartBounds;

	private Win32Helper.SubclassProc? _snapSubclassProc;

	private bool _isSnapSubclassInstalled;

	// The XAML content island covers the whole client area after frame
	// absorption; resize borders are declared via InputNonClientPointerSource
	// (ApplyNonClientRegions).
	private bool _topLevelHitDebugLogged;

	// GDI brush for the surface fill that keeps resize-frame lag invisible:
	// the window surface is painted with the drawer's settled base color on
	// every sizing step (native WmEraseBkgndClassColor replaced).
	private IntPtr _surfaceFillBrush;

	private IntPtr _contentBridgeHwnd;

	private bool _bridgeSurfaceDebugLogged;

	private Win32Helper.SubclassProc? _bridgeSubclassProc;

	private bool _bridgeSubclassInstalled;

	private IntPtr _subclassedBridgeHwnd;

	private bool _bridgeEraseDebugLogged;

	private bool _darkSheetLogged;

	private static readonly UIntPtr BridgeSubclassId = new(0x4449);

	// Ghost resize preview: while the modal sizing edge is dragged, the real
	// window keeps its rect and a color-keyed border follows the cursor; the
	// window is resized once on release, so the XAML relayout never exposes
	// the white composition backdrop mid-drag.
	private bool _ghostActive;

	private IntPtr _ghostFrame;

	private Win32Helper.RECT _ghostStartRect;

	private const int GhostBorderPx = 3;

	private const uint GhostKeyColor = 0x00FE00FF; // RGB(255,0,254) as COLORREF

	private const uint WmEnterSizeMode = 0x0231;
	private const uint WmExitSizeMode = 0x0232;

	private const uint WmSizing = 0x0214;

	private const uint WmEraseBkgnd = 0x0014;

	// Frame absorption: NCCALCSIZE collapses the non-client band into the
	// client area (the XAML island then covers the whole window rect, so no
	// white DWM sheet can show), and NCHITTEST lends the outer band back to
	// the system so edge resize keeps working.
	private const uint WmNcCalcSize = 0x0083;

	private const uint WmNcHitTest = 0x0084;

	private const int HtLeft = 10;

	private const int HtRight = 11;

	private const int HtTop = 12;

	private const int HtTopLeft = 13;

	private const int HtTopRight = 14;

	private const int HtBottom = 15;

	private const int HtBottomLeft = 16;

	private const int HtBottomRight = 17;

	private static readonly UIntPtr SnapSubclassId = new(0x4447);

	private int _tileSize = 96;

	private int _iconSize = 32;

	private int _rowSpacing = 6;

	private int _columnSpacing = 6;

	private static readonly TimeSpan GroupOpenGracePeriod = TimeSpan.FromSeconds(1.5);










	[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2608")]




	public nint WindowHandle => _hwnd;

	public bool IsPopupVisible { get; private set; }

	public bool IsClosed => _isClosed;

	public Guid? CurrentDrawerId => _drawer?.Id;

	/// <summary>Column budget of this drawer's grid (for cross-window placement).</summary>
	public int TileColumnCount => GetColumnCount();

	/// <summary>
	/// Maps a screen point to a cell in this drawer's grid, using this
	/// window's own tile metrics; used by sibling drawers to land a
	/// cross-window tile drop where the pointer is.
	/// </summary>
	public (int Column, int Row)? GetGridCellFromScreenPoint(Windows.Graphics.PointInt32 screenPoint)
	{
		double stepX = _tileSize + _columnSpacing;
		double stepY = _tileSize + _rowSpacing;
		if (_hwnd == IntPtr.Zero || stepX <= 0 || stepY <= 0)
		{
			return null;
		}
		if (!Win32Helper.GetWindowRect(_hwnd, out var rect))
		{
			return null;
		}
		double scale = RootGrid?.XamlRoot?.RasterizationScale ?? 1.0;
		if (scale <= 0)
		{
			scale = 1.0;
		}
		double clientX = (screenPoint.X - rect.Left) / scale;
		double clientY = (screenPoint.Y - rect.Top) / scale;
		try
		{
			Point gridOrigin = CellsGrid.TransformToVisual(RootGrid).TransformPoint(new Point(0, 0));
			int column = (int)Math.Floor((clientX - gridOrigin.X) / stepX) + 1;
			int row = (int)Math.Floor((clientY - gridOrigin.Y) / stepY) + 1;
			return (Math.Max(1, column), Math.Max(1, row));
		}
		catch (Exception ex)
		{
			App.Log("[ShortcutDrawer] Drop cell lookup failed: " + ex.Message);
			return null;
		}
	}

	public event EventHandler<ShortcutDrawerItemViewModel>? ItemActivated;

	public event EventHandler? DrawerShown;

	public event EventHandler? DrawerHidden;

	public ShortcutDrawerWindow(LocalizationService localizationService)
	{
		_localizationService = localizationService;
		_store = new ShortcutDrawerStore(ShortcutDrawerStore.GetDefaultStoreDirectory(App.Current?.SettingsService?.Settings?.FileWidget?.DefaultManagedStorageRootPath));
		_dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
		_themeService = App.Current?.ThemeService;
		_focusWatchTimer = new DispatcherTimer
		{
			Interval = FocusWatchInterval
		};
		_focusWatchTimer.Tick += FocusWatchTimer_Tick;
		InitializeComponent();
		_hwnd = WindowNative.GetWindowHandle(this);
		WindowId windowIdFromWindow = Win32Interop.GetWindowIdFromWindow(_hwnd);
		_appWindow = AppWindow.GetFromWindowId(windowIdFromWindow);
		ConfigureWindow();
		InstallSnapSubclass();
		ApplyTheme();
		CloseDrawerButton.Content = "\ue711";
		ToolTipService.SetToolTip(CloseDrawerButton, _localizationService.T("ShortcutDrawer.Close"));
		EmptyHintText.Text = _localizationService.T("ShortcutDrawer.Empty");
		SyncEditModeToggle();
		Activated += OnWindowActivated;
		Closed += OnWindowClosed;
		CellsGrid.Background = new SolidColorBrush(Colors.Transparent);
		CellsGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Grid_PointerPressed), handledEventsToo: true);
		CellsGrid.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Grid_PointerMoved), handledEventsToo: true);
		CellsGrid.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Grid_PointerReleased), handledEventsToo: true);
		CellsGrid.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(Grid_PointerCaptureLost), handledEventsToo: true);
		// Empty-area right click serves the multi-select menu now that the
		// per-cell borders (its previous host) are gone. Tile taps handle
		// ContextRequested themselves, so this only fires on empty space.
		CellsGrid.ContextRequested += Cell_ContextRequested;
		App.ShortcutDrawerLayoutChanged += OnShortcutDrawerLayoutChanged;
		App.DrawerAppearanceChanged += OnDrawerAppearanceChanged;
		ApplyDrawerAppearance();
		if (_themeService != null)
		{
			_themeService.AppearanceChanged += OnThemeServiceAppearanceChanged;
		}
	}

	public async void ShowPopup(Guid drawerId = default(Guid))
	{
		if (_isClosed)
		{
			return;
		}
		int num;
		if (IsPopupVisible)
		{
			ShortcutDrawerDefinition? drawer = _drawer;
			if (drawer != null && drawer.Id == drawerId)
			{
				num = ((!_isEditMode) ? 1 : 0);
				goto IL_0069;
			}
		}
		num = 0;
		goto IL_0069;
		IL_0069:
		if (num != 0)
		{
			Activate();
			Win32Helper.SetForegroundWindow(_hwnd);
			return;
		}
		_isHiding = false;
		await LoadDrawerAsync(drawerId);
		// The content island bridge may exist by now; push the dark DWM
		// sheet attributes again right before the first presented frame.
		ApplyDarkSheetAttributes();
		// Instant, fully-painted show. The native backdrop is a white glass
		// sheet; a content-side opacity fade would expose it during the
		// first frames (white → gray flash), so the popup appears directly
		// in its final appearance.
		RootGrid.Opacity = 1;
		_appWindow?.Show();
		IsPopupVisible = true;
		_shownAtUtc = DateTime.UtcNow;
		_focusWatchTimer.Start();
		DrawerShown?.Invoke(this, EventArgs.Empty);
		App.Current?.WidgetManager?.BringAuxiliaryWindowToFront(_hwnd, "shortcut-drawer-shown");
		Activate();
		Win32Helper.SetForegroundWindow(_hwnd);
	}

	public void HidePopup()
	{
		if (!_isClosed && IsPopupVisible && !_isHiding)
		{
			_isHiding = true;
			_selectedItem = null;
			_pendingItem = null;
			_tileDragOwner = null;
			_dragTileTransform = null;
			_isTileDragging = false;
			_isWindowDragging = false;
			// Keep the user's lock state across hide/show: locking persists
			// until they unlock it (edit mode is only the *initial* default).
			SyncEditModeToggle();
			// Instant hide, matching the instant show: a fade-out would
			// expose the white native backdrop in its final frames.
			CompleteHide();
		}
	}

	private void CompleteHide()
	{
		CapturePopupFrame();
		_appWindow?.Hide();
		IsPopupVisible = false;
		_isHiding = false;
		_focusWatchTimer.Stop();
		SavePopupFrameAsync();
		DrawerHidden?.Invoke(this, EventArgs.Empty);
	}

	private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
	{
		if (args.WindowActivationState == WindowActivationState.Deactivated && IsPopupVisible && !_isEditMode && DateTime.UtcNow - _shownAtUtc >= GroupOpenGracePeriod)
		{
			HidePopup();
		}
	}

	private void FocusWatchTimer_Tick(object? sender, object e)
	{
		if (_isClosed || !IsPopupVisible)
		{
			_focusWatchTimer.Stop();
			return;
		}
		nint foregroundWindow = Win32Helper.GetForegroundWindow();
		if (foregroundWindow != IntPtr.Zero && foregroundWindow != _hwnd)
		{
			Win32Helper.GetWindowThreadProcessId(foregroundWindow, out var processId);
			if (processId != Environment.ProcessId)
			{
				HidePopup();
			}
		}
	}

	private void CloseDrawerButton_Click(object sender, RoutedEventArgs e)
	{
		HidePopup();
	}

	private void EditModeToggle_Toggled(object sender, RoutedEventArgs e)
	{
		if (!_isSyncingEditToggle)
		{
			// Switch ON = locked; OFF = editing.
			_isEditMode = !EditModeToggle.IsOn;
			_selectedItem = null;
			if (_drawer != null)
			{
				RenderDrawer();
			}
		}
	}

	private void ExitEditMode()
	{
		if (_isEditMode)
		{
			_isEditMode = false;
			_selectedItem = null;
			SyncEditModeToggle();
			if (_drawer != null)
			{
				RenderDrawer();
			}
		}
	}

	private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (e.Key == VirtualKey.Escape && IsPopupVisible)
		{
			if (_isEditMode)
			{
				ExitEditMode();
			}
			else
			{
				HidePopup();
			}
			e.Handled = true;
		}
	}

	private void DrawerItem_Click(object sender, RoutedEventArgs e)
	{
		if (!(sender is Button { Tag: ShortcutDrawerItemViewModel tag }))
		{
			return;
		}
		// Open only when locked. In edit mode the pointer-release path opens
		// plain clicks, so this handler must not (re)select the tile.
		if (!_isEditMode && tag.FullPath != null)
		{
			ItemActivated?.Invoke(this, tag);
		}
	}

	private async void PersistDrawerAsync()
	{
		try
		{
			await MergeDrawerIntoStoreAsync();
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			App.Log("[ShortcutDrawer] Drawer layout not saved: " + ex2.Message);
		}
	}

	private void OnWindowClosed(object sender, WindowEventArgs args)
	{
		_isClosed = true;
		SetSurfaceFillColor(null);
		RemoveBridgeSubclass();
		RemoveSnapSubclass();
		App.ShortcutDrawerLayoutChanged -= OnShortcutDrawerLayoutChanged;
		App.DrawerAppearanceChanged -= OnDrawerAppearanceChanged;
		if (_themeService != null)
		{
			_themeService.AppearanceChanged -= OnThemeServiceAppearanceChanged;
		}
	}

	private async void OnShortcutDrawerLayoutChanged(object? sender, EventArgs e)
	{
		if (_isClosed)
		{
			return;
		}
		if (!_dispatcherQueue.HasThreadAccess)
		{
			_dispatcherQueue.TryEnqueue(() =>
			{
				OnShortcutDrawerLayoutChanged(sender, e);
			});
			return;
		}
		// Preserve the on-screen geometry across the reload: drag/resize may
		// not have been persisted yet, and the fresh store data would
		// otherwise snap the window back to a stale or default rect.
		(int Left, int Top, int Width, int Height)? preservedFrame = null;
		if (_drawer != null && (object)_appWindow != null && Win32Helper.GetWindowRect(_hwnd, out var currentRect))
		{
			preservedFrame = (currentRect.Left, currentRect.Top, currentRect.Right - currentRect.Left, currentRect.Bottom - currentRect.Top);
		}
		await LoadDrawerAsync(_drawer?.Id ?? Guid.Empty, preservedFrame);
		if (IsPopupVisible)
		{
			ApplyPopupFrame();
		}
	}

	private void HeaderStrip_PointerPressed(object sender, PointerRoutedEventArgs e)
	{
		if (sender is FrameworkElement frameworkElement && e.GetCurrentPoint(frameworkElement).Properties.IsLeftButtonPressed && (object)_appWindow != null && Win32Helper.GetCursorPos(out var lpPoint) && Win32Helper.GetWindowRect(_hwnd, out var lpRect))
		{
			_dragOrigin = new PointInt32(lpPoint.X, lpPoint.Y);
			_dragWindowOrigin = new PointInt32(lpRect.Left, lpRect.Top);
			_dragStartBounds = new RectInt32(
				lpRect.Left,
				lpRect.Top,
				lpRect.Right - lpRect.Left,
				lpRect.Bottom - lpRect.Top);
			_isWindowDragging = true;
			frameworkElement.CapturePointer(e.Pointer);
			e.Handled = true;
		}
	}

	private void HeaderStrip_PointerMoved(object sender, PointerRoutedEventArgs e)
	{
		if (_isWindowDragging && (object)_appWindow != null && Win32Helper.GetCursorPos(out var lpPoint))
		{
			try
			{
				var desired = new RectInt32(
					_dragWindowOrigin.X + lpPoint.X - _dragOrigin.X,
					_dragWindowOrigin.Y + lpPoint.Y - _dragOrigin.Y,
					_dragStartBounds.Width,
					_dragStartBounds.Height);
				var snapped = ApplySnap(desired, _dragStartBounds);
				_appWindow.Move(new PointInt32(snapped.X, snapped.Y));
			}
			catch (Exception ex)
			{
				App.Log("[ShortcutDrawer] Popup move failed: " + ex.Message);
			}
			e.Handled = true;
		}
	}

	private void HeaderStrip_PointerReleased(object sender, PointerRoutedEventArgs e)
	{
		if (_isWindowDragging && sender is FrameworkElement frameworkElement)
		{
			_isWindowDragging = false;
			_dragStartBounds = default;
			frameworkElement.ReleasePointerCapture(e.Pointer);
			// Persist the dragged position immediately; the fresh geometry
			// must survive any later data reload that reapplies frames.
			_ = SavePopupFrameAsync();
			e.Handled = true;
		}
	}

	// ─── Edge snapping while the popup is moved or resized ──────────

	private RectInt32 ApplySnap(RectInt32 desired, RectInt32 origin)
	{
		List<RectInt32> neighbours = App.Current?.GetOpenDrawerBoundsExcept(
			_drawer?.Id ?? Guid.Empty) ?? new List<RectInt32>();
		return DrawerSnapCalculator.Apply(
			desired,
			origin,
			GetWorkArea(),
			neighbours,
			DrawerSnapCalculator.DefaultTolerance);
	}

	/// <summary>
	/// Installs the comctl32 subclass that routes the native resize loop
	/// (WS_THICKFRAME drag) through the snap calculator. Size dragging never
	/// raises WinUI pointer events, so the window procedure is the only place
	/// the proposed rectangle can be adjusted per message.
	/// </summary>
	private void InstallSnapSubclass()
	{
		if (_hwnd == IntPtr.Zero || _isSnapSubclassInstalled)
		{
			return;
		}

		try
		{
			_snapSubclassProc = SnapWindowSubclassProc;
			_isSnapSubclassInstalled = Win32Helper.SetWindowSubclass(
				_hwnd,
				_snapSubclassProc,
				SnapSubclassId,
				UIntPtr.Zero);
			if (!_isSnapSubclassInstalled)
			{
				App.Log("[ShortcutDrawer] Sizing snap subclass install failed");
			}
		}
		catch (Exception ex)
		{
			App.Log("[ShortcutDrawer] Sizing snap subclass failed: " + ex.Message);
		}
	}

	private void RemoveSnapSubclass()
	{
		if (!_isSnapSubclassInstalled || _snapSubclassProc is not { } proc)
		{
			return;
		}

		_isSnapSubclassInstalled = false;
		try
		{
			Win32Helper.RemoveWindowSubclass(_hwnd, proc, SnapSubclassId);
		}
		catch
		{
		}
	}

	/// <summary>
	/// Ghost resize: captures the mouse, tracks the dragged edge in the
	/// original window rect, draws a preview frame on a click-through layered
	/// window and applies the final rect once on release. The real window and
	/// its XAML island never resize mid-drag, so nothing can flash white.
	/// </summary>
	private void RunGhostResizeLoop(IntPtr hwnd, uint hitCode)
	{
		if (_ghostActive || !Win32Helper.GetWindowRect(hwnd, out var startRect))
		{
			return;
		}
		if (!Win32Helper.GetCursorPos(out var grabPoint))
		{
			return;
		}
		_isResizeSessionActive = true;
		_dragStartBounds = new RectInt32(
			startRect.Left,
			startRect.Top,
			startRect.Right - startRect.Left,
			startRect.Bottom - startRect.Top);
		_ghostStartRect = startRect;

		IntPtr keyBrush = Win32Helper.CreateSolidBrush(GhostKeyColor);
		bool borderOwned = _surfaceFillBrush == IntPtr.Zero;
		IntPtr borderBrush = borderOwned
			? Win32Helper.CreateSolidBrush(0x00787878)
			: _surfaceFillBrush;
		IntPtr ghost = borderBrush != IntPtr.Zero && keyBrush != IntPtr.Zero
			? Win32Helper.CreateGhostWindow(GhostKeyColor)
			: IntPtr.Zero;
		bool done = false;
		bool cancelled = false;
		try
		{
			if (ghost != IntPtr.Zero)
			{
				_ghostActive = true;
				_ghostFrame = ghost;
				Win32Helper.SetCursor(ResizeCursorFor(hitCode));
				UpdateGhostFrame(ghost, ApplySnap(
					GhostDesiredRect(hitCode, grabPoint),
					_dragStartBounds),
					keyBrush,
					borderBrush);
			}
			Win32Helper.SetCapture(hwnd);
			Win32Helper.SetCursor(ResizeCursorFor(hitCode));
			bool mouseLogged = false;
			while (!done)
			{
				if (Win32Helper.GetMessage(out var msg, IntPtr.Zero, 0, 0) <= 0)
				{
					break;
				}
				switch (msg.message)
				{
					case Win32Helper.WM_LBUTTONUP:
						done = true;
						break;
					case Win32Helper.WM_CAPTURECHANGED when msg.lParam == IntPtr.Zero:
						done = true;
						break;
					case Win32Helper.WM_KEYDOWN when (int)msg.wParam == 0x1B:
						cancelled = true;
						done = true;
						break;
					case Win32Helper.WM_MOUSEMOVE:
						Win32Helper.GetCursorPos(out var cursorPos);
						if (!mouseLogged)
						{
							mouseLogged = true;
							App.Log("[ShortcutDrawer] Ghost resize tracking");
						}
						UpdateGhostFrame(ghost, ApplySnap(
							GhostDesiredRect(hitCode, cursorPos),
							_dragStartBounds),
							keyBrush,
							borderBrush);
						break;
					default:
						_ = Win32Helper.TranslateMessage(in msg);
						_ = Win32Helper.DispatchMessage(in msg);
						break;
				}
			}
		}
		catch (Exception ex)
		{
			App.Log("[ShortcutDrawer] Ghost resize loop failed: " + ex.Message);
		}
		finally
		{
			_ = Win32Helper.ReleaseCapture();
			Win32Helper.SetCursor(Win32Helper.LoadCursor(IntPtr.Zero, 0));
			if (ghost != IntPtr.Zero)
			{
				_ = Win32Helper.DestroyWindow(ghost);
				_ghostFrame = IntPtr.Zero;
				_ghostActive = false;
			}
			if (keyBrush != IntPtr.Zero)
			{
				_ = Win32Helper.DeleteObject(keyBrush);
			}
			if (borderOwned && borderBrush != IntPtr.Zero)
			{
				_ = Win32Helper.DeleteObject(borderBrush);
			}
		}
		bool applied = false;
		if (!cancelled && done)
		{
			Win32Helper.GetCursorPos(out var finalCursor);
			RectInt32 finalBounds = ApplySnap(GhostDesiredRect(hitCode, finalCursor), _dragStartBounds);
			if (finalBounds != _dragStartBounds)
			{
				Win32Helper.SetWindowPos(
					hwnd,
					IntPtr.Zero,
					finalBounds.X,
					finalBounds.Y,
					finalBounds.Width,
					finalBounds.Height,
					Win32Helper.SWP_NOZORDER | Win32Helper.SWP_NOACTIVATE);
				applied = true;
			}
		}
		_isResizeSessionActive = false;
		_dragStartBounds = default;
		if (applied)
		{
			ApplyNonClientRegions();
			_ = SavePopupFrameAsync();
		}
		App.Log("[ShortcutDrawer] Ghost resize ended applied=" + applied + " cancelled=" + cancelled);
	}

	/// <summary>The tracked rect when the dragged edges ride the cursor.</summary>
	private RectInt32 GhostDesiredRect(uint hitCode, Win32Helper.POINT point)
	{
		int left = _ghostStartRect.Left;
		int top = _ghostStartRect.Top;
		int right = _ghostStartRect.Right;
		int bottom = _ghostStartRect.Bottom;
		if (hitCode is HtLeft or HtTopLeft or HtBottomLeft)
		{
			left = point.X;
		}
		if (hitCode is HtRight or HtTopRight or HtBottomRight)
		{
			right = point.X;
		}
		if (hitCode is HtTop or HtTopLeft or HtTopRight)
		{
			top = point.Y;
		}
		if (hitCode is HtBottom or HtBottomLeft or HtBottomRight)
		{
			bottom = point.Y;
		}
		return new RectInt32(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
	}

	private static IntPtr ResizeCursorFor(uint hitCode)
	{
		int id = hitCode is HtLeft or HtRight
			? Win32Helper.IDC_SIZEWE
			: hitCode is HtTop or HtBottom
				? Win32Helper.IDC_SIZENS
				: hitCode is HtTopLeft or HtBottomRight
					? Win32Helper.IDC_SIZENWSE
					: Win32Helper.IDC_SIZENESW;
		return Win32Helper.LoadCursor(IntPtr.Zero, id);
	}

	/// <summary>Moves the preview frame to the tracked rect and repaints it.</summary>
	private void UpdateGhostFrame(IntPtr ghost, RectInt32 bounds, IntPtr keyBrush, IntPtr borderBrush)
	{
		try
		{
			if (ghost == IntPtr.Zero
				|| !Win32Helper.SetWindowPos(
					ghost,
					IntPtr.Zero,
					bounds.X,
					bounds.Y,
					bounds.Width,
					bounds.Height,
					Win32Helper.SWP_NOZORDER | Win32Helper.SWP_NOACTIVATE))
			{
				return;
			}
			int width = bounds.Width;
			int height = bounds.Height;
			int b = GhostBorderPx;
			var full = new Win32Helper.RECT { Left = 0, Top = 0, Right = width, Bottom = height };
			IntPtr hdc = Win32Helper.GetDC(ghost);
			if (hdc == IntPtr.Zero)
			{
				return;
			}
			try
			{
				// Keyed interior first (invisible), then opaque edges on top.
				_ = Win32Helper.FillRect(hdc, ref full, keyBrush);
				var strips = new List<Win32Helper.RECT>
				{
					new() { Left = 0, Top = 0, Right = width, Bottom = b },
					new() { Left = 0, Top = height - b, Right = width, Bottom = height },
					new() { Left = 0, Top = 0, Right = b, Bottom = height },
					new() { Left = width - b, Top = 0, Right = width, Bottom = height }
				};
				for (int i = 0; i < strips.Count; i++)
				{
					Win32Helper.RECT strip = strips[i];
					_ = Win32Helper.FillRect(hdc, ref strip, borderBrush);
				}
			}
			finally
			{
				_ = Win32Helper.ReleaseDC(ghost, hdc);
			}
		}
		catch (Exception ex)
		{
			App.Log("[ShortcutDrawer] Ghost frame paint failed: " + ex.Message);
		}
	}

	/// <summary>
	/// The frame absorption removes the native non-client band, and content
	/// islands route pointer input through their own pipeline (the classic
	/// WM_NCHITTEST never reaches the window), so resize borders are declared
	/// with the public InputNonClientPointerSource API instead: the outer
	/// band rect is marked as LeftBorder/RightBorder/TopBorder/BottomBorder
	/// and Windows then serves native resize from there.
	/// </summary>
	private void ApplyNonClientRegions()
	{
		if (_appWindow == null || _hwnd == IntPtr.Zero)
		{
			return;
		}
		try
		{
			Microsoft.UI.Input.InputNonClientPointerSource source =
				Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(_appWindow.Id);
			double scale = Win32Helper.GetDpiScaleForWindow(_hwnd, RootGrid?.XamlRoot);
			int border = Math.Max(4, (int)Math.Round(8.0 * scale));
			int width = _appWindow.ClientSize.Width;
			int height = _appWindow.ClientSize.Height;
			if (width <= 0 || height <= 0)
			{
				return;
			}
			source.SetRegionRects(
				Microsoft.UI.Input.NonClientRegionKind.LeftBorder,
				new Windows.Graphics.RectInt32[] { new Windows.Graphics.RectInt32 { X = 0, Y = 0, Width = border, Height = height } });
			source.SetRegionRects(
				Microsoft.UI.Input.NonClientRegionKind.RightBorder,
				new Windows.Graphics.RectInt32[] { new Windows.Graphics.RectInt32 { X = width - border, Y = 0, Width = border, Height = height } });
			source.SetRegionRects(
				Microsoft.UI.Input.NonClientRegionKind.TopBorder,
				new Windows.Graphics.RectInt32[] { new Windows.Graphics.RectInt32 { X = 0, Y = 0, Width = width, Height = border } });
			source.SetRegionRects(
				Microsoft.UI.Input.NonClientRegionKind.BottomBorder,
				new Windows.Graphics.RectInt32[] { new Windows.Graphics.RectInt32 { X = 0, Y = height - border, Width = width, Height = border } });
		}
		catch (Exception ex)
		{
			App.Log("[ShortcutDrawer] Non-client regions not applied: " + ex.Message);
		}
	}

	private IntPtr SnapWindowSubclassProc(
		IntPtr hwnd,
		uint message,
		UIntPtr wParam,
		IntPtr lParam,
		UIntPtr uIdSubclass,
		UIntPtr dwRefData)
	{
		try
		{
			switch (message)
			{
				case WmEraseBkgnd when _surfaceFillBrush != IntPtr.Zero:
					// Replace the white class-erase with the settled base
					// color so exposed gaps never flash white.
					PaintSurfaceFill();
					return new IntPtr(1);

				case WmEnterSizeMode:
					_isResizeSessionActive = true;
					if (Win32Helper.GetWindowRect(_hwnd, out var startRect))
					{
						_dragStartBounds = new RectInt32(
							startRect.Left,
							startRect.Top,
							startRect.Right - startRect.Left,
							startRect.Bottom - startRect.Top);
					}
					break;

				case WmSizing when _isResizeSessionActive:
					var proposed = Marshal.PtrToStructure<Win32Helper.RECT>(lParam);
					var proposedBounds = new RectInt32(
						proposed.Left,
						proposed.Top,
						Math.Max(1, proposed.Right - proposed.Left),
						Math.Max(1, proposed.Bottom - proposed.Top));
					var snapped = ApplySnap(proposedBounds, _dragStartBounds);
					var resized = new Win32Helper.RECT
					{
						Left = snapped.X,
						Top = snapped.Y,
						Right = snapped.X + snapped.Width,
						Bottom = snapped.Y + snapped.Height
					};
					Marshal.StructureToPtr(resized, lParam, fDeleteOld: false);
					// Handled: the default window procedure must not re-run its
					// own sizing pass over the snapped rectangle.
					ApplyNonClientRegions();
					// The island relayout lags the native frame update by a
					// beat; keep the exposed gap in the settled base color.
					PaintSurfaceFill();
					return new IntPtr(1);

				case WmExitSizeMode:
					_isResizeSessionActive = false;
					_dragStartBounds = default;
					// Persist the resized geometry immediately so a later
					// reload cannot revert the window to a stale rect.
					_ = SavePopupFrameAsync();
					ApplyNonClientRegions();
					break;

				case WmNcCalcSize when wParam != 0:
					// Absorb the frame: the client area spans the whole window
					// rect, so the XAML island covers what used to be the
					// 7px white DWM band at every edge.
					return new IntPtr(1);

				case WmNcHitTest:
					{
						int edge = ComputeResizeHitTest(hwnd, lParam);
						if (edge != 0)
						{
							if (!_topLevelHitDebugLogged)
							{
								_topLevelHitDebugLogged = true;
								App.Log("[ShortcutDrawer] Top-level resize HT=" + edge);
							}
							return new IntPtr(edge);
						}
						break;
					}
			}
		}
		catch (Exception ex)
		{
			App.Log("[ShortcutDrawer] Snap sizing failed: " + ex.Message);
		}

		return Win32Helper.DefSubclassProc(hwnd, message, wParam, lParam);
	}

	/// <summary>
	/// The content island now covers the whole window, so the system no
	/// longer knows where the resize borders are. Re-lend the outer band to
	/// Windows here; returns 0 when the point is interior.
	/// </summary>
	private int ComputeResizeHitTest(IntPtr hwnd, IntPtr lParam)
	{
		long packed = lParam.ToInt64();
		var point = new Win32Helper.POINT
		{
			X = (short)(packed & 0xFFFF),
			Y = (short)((packed >> 16) & 0xFFFF)
		};
		return ResolveEdgeHit(hwnd, point);
	}

	/// <summary>
	/// Maps a screen point to a border-band hit code (HtLeft/HtRight/...);
	/// returns 0 when the point is interior.
	/// </summary>
	private int ResolveEdgeHit(IntPtr hwnd, Win32Helper.POINT point)
	{
		if (!Win32Helper.GetWindowRect(hwnd, out var rect) || rect.Right - rect.Left <= 0 || rect.Bottom - rect.Top <= 0)
		{
			return 0;
		}
		double scale = Win32Helper.GetDpiScaleForWindow(hwnd, RootGrid?.XamlRoot);
		int border = Math.Max(4, (int)Math.Round(8.0 * scale));
		bool isLeft = point.X >= rect.Left && point.X < rect.Left + border;
		bool isRight = point.X >= rect.Right - border && point.X < rect.Right;
		bool isTop = point.Y >= rect.Top && point.Y < rect.Top + border;
		bool isBottom = point.Y >= rect.Bottom - border && point.Y < rect.Bottom;
		if (isTop && isLeft)
		{
			return HtTopLeft;
		}
		if (isTop && isRight)
		{
			return HtTopRight;
		}
		if (isBottom && isLeft)
		{
			return HtBottomLeft;
		}
		if (isBottom && isRight)
		{
			return HtBottomRight;
		}
		if (isLeft)
		{
			return HtLeft;
		}
		if (isRight)
		{
			return HtRight;
		}
		if (isTop)
		{
			return HtTop;
		}
		if (isBottom)
		{
			return HtBottom;
		}
		return 0;
	}

	private int GetColumnCount()
	{
		int columnsThatFit = GetColumnsThatFit();
		if (_drawer == null)
		{
			return Math.Max(1, columnsThatFit);
		}
		int num = columnsThatFit;
		foreach (ShortcutDrawerItem item in _drawer.Items)
		{
			num = Math.Max(num, item.Column + Math.Max(1, item.SizeW) - 1);
		}
		return Math.Max(1, num);
	}

	private int GetColumnsThatFit()
	{
		double num = _tileSize + _columnSpacing;
		if (num <= 0.0)
		{
			return 1;
		}
		int num2 = GetClientWidth() - 46;
		return Math.Max(1, (int)((double)num2 / num));
	}

	private int GetClientWidth()
	{
		try
		{
			AppWindow appWindow = _appWindow;
			if ((object)appWindow != null && appWindow.ClientSize.Width > 0)
			{
				return appWindow.ClientSize.Width;
			}
		}
		catch (Exception ex)
		{
			App.Log("[ShortcutDrawer] Client width lookup failed: " + ex.Message);
		}
		return 420;
	}

	private void ApplyPopupFrame()
	{
		if (_data == null)
		{
			return;
		}
		(int Left, int Top, int Width, int Height) tuple = ShortcutDrawerStore.ResolvePopupFrame(_data, _drawer);
		int item = tuple.Left;
		int item2 = tuple.Top;
		int item3 = tuple.Width;
		int item4 = tuple.Height;
		RectInt32 rect = ((item3 > 0 && item4 > 0) ? ClampFrameToWorkArea(new RectInt32(item, item2, item3, item4)) : BuildFittedFrame());
		try
		{
			_appWindow?.MoveAndResize(rect);
			// Window geometry changed: keep the declared resize borders in sync.
			ApplyNonClientRegions();
			App.Log($"[ShortcutDrawer] Popup frame applied rect={rect.X},{rect.Y},{rect.Width},{rect.Height}");
		}
		catch (Exception ex)
		{
			App.Log("[ShortcutDrawer] Popup frame not applied: " + ex.Message);
		}
	}

	private RectInt32 BuildFittedFrame()
	{
		int columnCount = GetColumnCount();
		int num = 1;
		if (_drawer != null)
		{
			foreach (ShortcutDrawerItem item in _drawer.Items)
			{
				num = Math.Max(num, item.Row + Math.Max(1, item.SizeH) - 1);
			}
		}
		RectInt32 workArea = GetWorkArea();
		int maximum = ((workArea.Height > 0) ? ((int)((double)workArea.Height * 0.7)) : 720);
		int num2 = ClampTo(columnCount * _tileSize + 46, 420, 760);
		int num3 = ClampTo(num * _tileSize + 104, 240, maximum);
		SizeInt32 windowFrameDelta = GetWindowFrameDelta();
		int num4 = num2 + windowFrameDelta.Width;
		int height = num3 + windowFrameDelta.Height;
		int num5 = Math.Max(0, GetDrawerOrder());
		int value = workArea.X + (workArea.Width - num4) / 2 + num5 * 28;
		int y = workArea.Y + 8 + num5 * 28;
		value = ((workArea.Width > num4) ? Math.Clamp(value, workArea.X, workArea.X + workArea.Width - num4) : workArea.X);
		return new RectInt32(value, y, num4, height);
	}

	private int GetDrawerOrder()
	{
		if (_drawer == null || _data?.Drawers == null)
		{
			return 0;
		}
		int num = _data.Drawers.FindIndex((ShortcutDrawerDefinition drawer) => drawer != null && drawer.Id == _drawer.Id);
		return (num >= 0) ? num : 0;
	}

	private RectInt32 ClampFrameToWorkArea(RectInt32 frame)
	{
		RectInt32 workArea = GetWorkArea();
		if (workArea.Width <= 0 || workArea.Height <= 0)
		{
			return frame;
		}
		int val = workArea.X + workArea.Width - frame.Width;
		int val2 = workArea.Y + workArea.Height - frame.Height;
		int x = ((frame.Width > workArea.Width) ? workArea.X : Math.Clamp(frame.X, workArea.X, Math.Max(workArea.X, val)));
		int y = ((frame.Height > workArea.Height) ? workArea.Y : Math.Clamp(frame.Y, workArea.Y, Math.Max(workArea.Y, val2)));
		return new RectInt32(x, y, frame.Width, frame.Height);
	}

	private RectInt32 GetWorkArea()
	{
		try
		{
			DisplayArea fromWindowId = DisplayArea.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd), DisplayAreaFallback.Primary);
			RectInt32 workArea = fromWindowId.WorkArea;
			return new RectInt32(workArea.X, workArea.Y, workArea.Width, workArea.Height);
		}
		catch (Exception ex)
		{
			App.Log("[ShortcutDrawer] Work area lookup failed: " + ex.Message);
			return new RectInt32(0, 0, 0, 0);
		}
	}

	private SizeInt32 GetWindowFrameDelta()
	{
		try
		{
			if (Win32Helper.GetWindowRect(_hwnd, out var lpRect))
			{
				AppWindow appWindow = _appWindow;
				if ((object)appWindow != null)
				{
					SizeInt32 clientSize = appWindow.ClientSize;
					if (clientSize.Width > 0 && clientSize.Height > 0)
					{
						int num = lpRect.Right - lpRect.Left - clientSize.Width;
						int num2 = lpRect.Bottom - lpRect.Top - clientSize.Height;
						if (num >= 0 && num2 >= 0)
						{
							return new SizeInt32(num, num2);
						}
					}
				}
			}
		}
		catch (Exception ex)
		{
			App.Log("[ShortcutDrawer] Frame delta lookup failed: " + ex.Message);
		}
		return new SizeInt32(16, 8);
	}

	private static int ClampTo(int value, int minimum, int maximum)
	{
		return Math.Clamp(value, minimum, Math.Max(minimum, maximum));
	}

	private void CapturePopupFrame()
	{
		if ((object)_appWindow != null && _drawer != null && Win32Helper.GetWindowRect(_hwnd, out var lpRect))
		{
			_drawer.PopupLeft = lpRect.Left;
			_drawer.PopupTop = lpRect.Top;
			_drawer.PopupWidth = lpRect.Right - lpRect.Left;
			_drawer.PopupHeight = lpRect.Bottom - lpRect.Top;
		}
	}

	private async Task SavePopupFrameAsync()
	{
		if ((object)_appWindow == null || _drawer == null)
		{
			return;
		}
		try
		{
			CapturePopupFrame();
			await MergeDrawerIntoStoreAsync();
			App.Log($"[ShortcutDrawer] Popup frame saved drawer={_drawer.Name} rect={_drawer.PopupLeft},{_drawer.PopupTop},{_drawer.PopupWidth},{_drawer.PopupHeight}");
		}
		catch (Exception ex)
		{
			App.Log("[ShortcutDrawer] Popup frame not saved: " + ex.Message);
		}
	}

	private async Task MergeDrawerIntoStoreAsync()
	{
		if (_data != null && _drawer != null)
		{
			ShortcutDrawerData fresh = await _store.LoadAsync();
			ShortcutDrawerDefinition target = fresh.Drawers.FirstOrDefault((ShortcutDrawerDefinition drawer) => drawer.Id == _drawer.Id);
			if (target != null)
			{
				target.Name = _drawer.Name;
				target.Items = _drawer.Items;
				target.PopupLeft = _drawer.PopupLeft;
				target.PopupTop = _drawer.PopupTop;
				target.PopupWidth = _drawer.PopupWidth;
				target.PopupHeight = _drawer.PopupHeight;
				target.IconSize = _drawer.IconSize;
				target.RowSpacing = _drawer.RowSpacing;
				target.ColumnSpacing = _drawer.ColumnSpacing;
				target.ShowPositionBadges = _drawer.ShowPositionBadges;
				await _store.SaveAsync(fresh);
			}
		}
	}

	private void ConfigureWindow()
	{
		if ((object)_appWindow != null)
		{
			WindowShellState.TryApplyBorderlessOverlappedPresenter(_appWindow);
			if (_appWindow.Presenter is OverlappedPresenter overlappedPresenter)
			{
				overlappedPresenter.IsResizable = true;
			}
			_appWindow.Resize(new SizeInt32(420, 240));
			int windowLong = Win32Helper.GetWindowLong(_hwnd, -20);
			windowLong |= 0x80;
			windowLong &= -134217729;
			Win32Helper.SetWindowLongPtr(_hwnd, -20, new IntPtr(windowLong));
			int windowLong2 = Win32Helper.GetWindowLong(_hwnd, -16);
			windowLong2 &= -12582913;
			windowLong2 |= 0x40000;
			Win32Helper.SetWindowLong(_hwnd, -16, windowLong2);
			Win32Helper.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, 51u);
			Win32Helper.SetWindowBorderColor(_hwnd, -2);
			// DIAG C: no DwmExtendFrameIntoClientArea — the glass sheet it
			// lays over the surface flashes white at activation relayout;
			// with every appearance mode opaque now, the sheet is dead
			// weight and GDI fills can hold the base tone instead.
			ApplyNonClientRegions();
		}
	}

	private void OnThemeServiceAppearanceChanged()
	{
		if (DispatcherQueue.HasThreadAccess)
		{
			ApplyTheme();
		}
		else
		{
			DispatcherQueue.TryEnqueue(ApplyTheme);
		}
	}

	private void ApplyTheme()
	{
		ElementTheme elementTheme = _themeService?.CurrentTheme ?? ElementTheme.Default;
		if (elementTheme == ElementTheme.Default)
		{
			elementTheme = ((!Win32Helper.IsSystemDarkMode()) ? ElementTheme.Light : ElementTheme.Dark);
		}

		// Custom surfaces (user color / picture) are designed on a dark base,
		// so the whole content follows the dark palette to stay readable.
		if (_customDrawerSurfaceActive)
		{
			elementTheme = ElementTheme.Dark;
		}
		RootGrid.RequestedTheme = elementTheme;
	}

	private void OnDrawerAppearanceChanged(object? sender, EventArgs args)
	{
		if (_dispatcherQueue.HasThreadAccess)
		{
			ApplyDrawerAppearance();
			return;
		}
		_dispatcherQueue.TryEnqueue(ApplyDrawerAppearance);
	}

	/// <summary>
	/// Surface mode: the theme card (default), a user color with alpha, or a
	/// local picture lightly veiled for label readability. Every failure falls
	/// back to the card so a broken setting can never blank the drawer.
	/// </summary>
	private async void ApplyDrawerAppearance()
	{
		Brush cardBrush = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
		Brush cardStrokeBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
		// Theme-card mode keeps the native white sheet (it matches the light
		// palette and shows no flash); the surface fill is only set by the
		// custom modes below.
		RootGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
			Microsoft.UI.Colors.Transparent);
		DrawerBorder.Background = cardBrush;
		DrawerBorder.BorderBrush = cardStrokeBrush;
		BackgroundOverlayBorder.Visibility = Visibility.Collapsed;
		SetSurfaceFillColor(null);
		ApplyDarkSheetAttributes();
		bool customSurface = false;
		SettingsService? settingsService = (App.Current as App)?.SettingsService;
		Services.DrawerAppearanceSlice appearance = settingsService?.Settings?.DrawerAppearance;
		if (appearance == null)
		{
			return;
		}
		try
		{
			string mode = appearance.Mode?.Trim() ?? string.Empty;
			if (mode.Equals("Color", StringComparison.OrdinalIgnoreCase))
			{
				if (TryParseHexColor(appearance.Color, out Windows.UI.Color color))
				{
					// Opaque, premul-equalized fill: the same final tone the
					// translucent color used to blend to over the white sheet,
					// but painted directly — the native sheet no longer takes
					// part in the composition at all, so resize-frame lag
					// cannot flash white through translucent pixels.
					Windows.UI.Color settled = BlendOverWhite(color);
					RootGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(settled);
					DrawerBorder.Background = null;
					DrawerBorder.BorderBrush = null;
					SetSurfaceFillColor(settled.R, settled.G, settled.B);
					customSurface = true;
				}
			}
			else if (mode.Equals("Image", StringComparison.OrdinalIgnoreCase))
			{
				string path = appearance.ImagePath ?? string.Empty;
				if (File.Exists(path))
				{
					// Opaque dark base keeps the window sheet out of the image.
					RootGrid.Background = new SolidColorBrush(new Windows.UI.Color
					{
						A = 255,
						R = 16,
						G = 16,
						B = 16
					});
					SetSurfaceFillColor(16, 16, 16);
					DrawerBorder.BorderBrush = new SolidColorBrush(new Windows.UI.Color
					{
						A = 255,
						R = 48,
						G = 48,
						B = 48
					});
					BitmapImage image = new BitmapImage();
					await using (System.IO.FileStream stream = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read))
					{
						await image.SetSourceAsync(stream.AsRandomAccessStream());
					}
					DrawerBorder.Background = new ImageBrush
					{
						ImageSource = image,
						Stretch = Stretch.UniformToFill
					};
				// The veil keeps the small white labels readable on any picture.
					BackgroundOverlayBorder.Visibility = Visibility.Visible;
					customSurface = true;
				}
			}
		}
		catch (Exception ex)
		{
			App.Log("[ShortcutDrawer] Custom background failed, falling back to card: " + ex.Message);
			RootGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
				Microsoft.UI.Colors.Transparent);
			DrawerBorder.Background = cardBrush;
			BackgroundOverlayBorder.Visibility = Visibility.Collapsed;
			SetSurfaceFillColor(null);
			customSurface = false;
		}

		if (customSurface)
		{
			_customDrawerSurfaceActive = true;
		}
		else
		{
			_customDrawerSurfaceActive = false;
		}
		ApplyTheme();
	}

	/// <summary>
	/// Pushes the dark DWM sheet + "no system backdrop" attributes onto the
	/// top-level window and the content island bridge, so the base that
	/// shows through before/behind XAML frames reads dark instead of white.
	/// The bridge appears late, so this is re-run whenever it is found.
	/// </summary>
	private void ApplyDarkSheetAttributes()
	{
		if (_hwnd == IntPtr.Zero)
		{
			return;
		}
		bool top = false;
		IntPtr bridge = IntPtr.Zero;
		try
		{
			top = Win32Helper.ApplyDarkSheet(_hwnd);
			bridge = _contentBridgeHwnd;
			if (bridge == IntPtr.Zero || !Win32Helper.IsWindow(bridge))
			{
				bridge = Win32Helper.FindWindowEx(_hwnd, IntPtr.Zero, "Microsoft.UI.Content.DesktopChildSiteBridge", null);
				if (bridge != IntPtr.Zero)
				{
					_contentBridgeHwnd = bridge;
				}
			}
			if (bridge != IntPtr.Zero)
			{
				Win32Helper.ApplyDarkSheet(bridge);
			}
		}
		catch (Exception ex)
		{
			App.Log("[ShortcutDrawer] Dark sheet apply failed: " + ex.Message);
			return;
		}
		if (!_darkSheetLogged)
		{
			_darkSheetLogged = true;
			App.Log("[ShortcutDrawer] Dark sheet applied top=" + top + " bridge=" + (bridge != IntPtr.Zero));
		}
	}

	/// <summary>
	/// The earlier translucent drawer color blended over the native white
	/// sheet; this returns the exact same final tone as an opaque color, so
	/// the appearance is pixel-identical while the sheet drops out of the
	/// composition entirely.
	/// </summary>
	private static Windows.UI.Color BlendOverWhite(Windows.UI.Color color)
	{
		double alpha = color.A / 255.0;
		return new Windows.UI.Color
		{
			A = 255,
			R = (byte)Math.Round(color.R * alpha + 255.0 * (1.0 - alpha)),
			G = (byte)Math.Round(color.G * alpha + 255.0 * (1.0 - alpha)),
			B = (byte)Math.Round(color.B * alpha + 255.0 * (1.0 - alpha))
		};
	}

	/// <summary>
	/// Builds (or clears) the GDI brush used to paint the window surface in
	/// the settled base color during resize steps. RGB order is COLORREF:
	/// 0x00BBGGRR.
	/// </summary>
	private void SetSurfaceFillColor(byte? r, byte? g = null, byte? b = null)
	{
		try
		{
			if (_surfaceFillBrush != IntPtr.Zero)
			{
				Win32Helper.DeleteObject(_surfaceFillBrush);
				_surfaceFillBrush = IntPtr.Zero;
			}
			if (_hwnd == IntPtr.Zero)
			{
				return;
			}
			// One-shot diagnostic: DRAWERBOX_DIAG_RED=1 paints the surface and
			// erase-fill red, proving at a glance whether the GDI surface fill
			// actually reaches the composition. Leave 0 for normal colors.
			if (!r.HasValue)
			{
				try
				{
					if (Environment.GetEnvironmentVariable("DRAWERBOX_DIAG_RED") == "1")
					{
						r = 255;
					}
				}
				catch
				{
				}
			}
			if (r.HasValue)
			{
				_surfaceFillBrush = Win32Helper.CreateSolidBrush(
					(uint)(r.Value | (g!.Value << 8) | (b!.Value << 16)));
			}
		}
		catch (Exception ex)
		{
			App.Log("[ShortcutDrawer] Surface fill brush failed: " + ex.Message);
		}
	}

	/// <summary>
	/// Fills the window's whole client rect with the surface brush. Called
	/// on each sizing step so the one-half-beat gap between the native frame
	/// update and the island's relayout never flashes white. Client
	/// coordinates only: GetDC's origin is the client top-left, so a screen
	/// rect from GetWindowRect would land off-window and paint nothing.
	/// </summary>
	private void PaintSurfaceFill()
	{
		if (_surfaceFillBrush == IntPtr.Zero || _hwnd == IntPtr.Zero)
		{
			return;
		}
		try
		{
			if (Win32Helper.GetClientRect(_hwnd, out Win32Helper.RECT rect))
			{
				IntPtr hdc = Win32Helper.GetDC(_hwnd);
				if (hdc != IntPtr.Zero)
				{
					Win32Helper.FillRect(hdc, ref rect, _surfaceFillBrush);
					Win32Helper.ReleaseDC(_hwnd, hdc);
				}
			}
		}
		catch (Exception ex)
		{
			App.Log("[ShortcutDrawer] Surface fill failed: " + ex.Message);
		}
		PaintBridgeSurfaceFill();
	}

	/// <summary>
	/// The content island child window owns its own composition surface, and
	/// its fresh regions erase white before XAML draws the new frame — that
	/// is the visible resize flash. Paint its surface with the same base
	/// color on every sizing step.
	/// </summary>
	private void PaintBridgeSurfaceFill()
	{
		if (_surfaceFillBrush == IntPtr.Zero || _hwnd == IntPtr.Zero)
		{
			return;
		}
		IntPtr bridge = _contentBridgeHwnd;
		if (bridge == IntPtr.Zero || !Win32Helper.IsWindow(bridge))
		{
			bridge = Win32Helper.FindWindowEx(_hwnd, IntPtr.Zero, "Microsoft.UI.Content.DesktopChildSiteBridge", null);
			if (bridge == IntPtr.Zero)
			{
				return;
			}
			_contentBridgeHwnd = bridge;
			ApplyDarkSheetAttributes();
		}
		// The bridge's own erase paints freshly grown bands white — the
		// grow-direction flash. Override its erase through a subclass.
		InstallBridgeSubclass(bridge);
		try
		{
			if (Win32Helper.GetClientRect(bridge, out Win32Helper.RECT rect))
			{
				IntPtr hdc = Win32Helper.GetDC(bridge);
				if (hdc != IntPtr.Zero)
				{
					Win32Helper.FillRect(hdc, ref rect, _surfaceFillBrush);
					Win32Helper.ReleaseDC(bridge, hdc);
					if (!_bridgeSurfaceDebugLogged)
					{
						_bridgeSurfaceDebugLogged = true;
						App.Log("[ShortcutDrawer] Bridge surface fill active");
					}
				}
			}
		}
		catch
		{
			_contentBridgeHwnd = IntPtr.Zero;
		}
	}

	/// <summary>
	/// Subclasses the content island bridge so its own WM_ERASEBKGND fills
	/// with the settled base color instead of white. Recreates on bridge
	/// change; removed with the window on close.
	/// </summary>
	private void InstallBridgeSubclass(IntPtr bridge)
	{
		if (_bridgeSubclassInstalled && bridge == _subclassedBridgeHwnd)
		{
			return;
		}
		if (_bridgeSubclassInstalled && _subclassedBridgeHwnd != IntPtr.Zero
			&& _subclassedBridgeHwnd != bridge && _bridgeSubclassProc is { } oldProc
			&& Win32Helper.IsWindow(_subclassedBridgeHwnd))
		{
			try
			{
				Win32Helper.RemoveWindowSubclass(_subclassedBridgeHwnd, oldProc, BridgeSubclassId);
			}
			catch
			{
			}
		}
		_bridgeSubclassProc ??= BridgeSubclassProc;
		try
		{
			_bridgeSubclassInstalled = Win32Helper.SetWindowSubclass(
				bridge,
				_bridgeSubclassProc,
				BridgeSubclassId,
				UIntPtr.Zero);
			if (_bridgeSubclassInstalled)
			{
				_subclassedBridgeHwnd = bridge;
				App.Log("[ShortcutDrawer] Bridge erase subclass installed hwnd=" + bridge);
			}
		}
		catch (Exception ex)
		{
			App.Log("[ShortcutDrawer] Bridge erase subclass failed: " + ex.Message);
		}
	}

	private void RemoveBridgeSubclass()
	{
		if (!_bridgeSubclassInstalled || _bridgeSubclassProc is not { } proc)
		{
			return;
		}
		_bridgeSubclassInstalled = false;
		try
		{
			if (Win32Helper.IsWindow(_subclassedBridgeHwnd))
			{
				Win32Helper.RemoveWindowSubclass(_subclassedBridgeHwnd, proc, BridgeSubclassId);
			}
		}
		catch
		{
		}
		_subclassedBridgeHwnd = IntPtr.Zero;
	}

	/// <summary>Runs on the content island bridge; erase fills with the base color.</summary>
	private IntPtr BridgeSubclassProc(
		IntPtr hwnd,
		uint message,
		UIntPtr wParam,
		IntPtr lParam,
		UIntPtr uIdSubclass,
		UIntPtr dwRefData)
	{
		try
		{
			if (message == WmEraseBkgnd && _surfaceFillBrush != IntPtr.Zero)
			{
				if (Win32Helper.GetClientRect(hwnd, out Win32Helper.RECT rect))
				{
					IntPtr hdc = Win32Helper.GetDC(hwnd);
					if (hdc != IntPtr.Zero)
					{
						Win32Helper.FillRect(hdc, ref rect, _surfaceFillBrush);
						Win32Helper.ReleaseDC(hwnd, hdc);
					}
					if (!_bridgeEraseDebugLogged)
					{
						_bridgeEraseDebugLogged = true;
						App.Log("[ShortcutDrawer] Bridge erase filled with base color");
					}
				}
				return new IntPtr(1);
			}
		}
		catch (Exception ex)
		{
			App.Log("[ShortcutDrawer] Bridge erase fill failed: " + ex.Message);
		}
		return Win32Helper.DefSubclassProc(hwnd, message, wParam, lParam);
	}

	/// <summary>Parses "#AARRGGBB" or "#RRGGBB"; false when malformed.</summary>
	private static bool TryParseHexColor(string? text, out Windows.UI.Color color)
	{
		color = new Windows.UI.Color
		{
			A = 255,
			R = 255,
			G = 255,
			B = 255
		};
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}
		string hex = text.Trim();
		if (hex.StartsWith('#'))
		{
			hex = hex.Substring(1);
		}
		if (hex.Length != 6 && hex.Length != 8)
		{
			return false;
		}
		foreach (char c in hex)
		{
			if (!Uri.IsHexDigit(c))
			{
				return false;
			}
		}
		byte alpha = (byte)(hex.Length == 8 ? Convert.ToInt32(hex.Substring(0, 2), 16) : 255);
		int valuePart = hex.Length - 6;
		color = new Windows.UI.Color
		{
			A = alpha,
			R = Convert.ToByte(hex.Substring(valuePart, 2), 16),
			G = Convert.ToByte(hex.Substring(valuePart + 2, 2), 16),
			B = Convert.ToByte(hex.Substring(valuePart + 4, 2), 16)
		};
		return true;
	}

	private async Task LoadDrawerAsync(Guid drawerId, (int Left, int Top, int Width, int Height)? preservedFrame = null)
	{
		_data = await _store.LoadAsync();
		_drawer = ((drawerId == Guid.Empty) ? _data.Drawers.FirstOrDefault() : (_data.Drawers.FirstOrDefault((ShortcutDrawerDefinition d) => d.Id == drawerId) ?? _data.Drawers.FirstOrDefault()));
		if (_drawer != null && preservedFrame.HasValue)
		{
			// Carry the on-screen geometry across the reload, over the fresh
			// store values which may be stale (frame changes while open are
			// only persisted on hide). The SaveAsync below persists it too.
			_drawer.PopupLeft = preservedFrame.Value.Left;
			_drawer.PopupTop = preservedFrame.Value.Top;
			_drawer.PopupWidth = preservedFrame.Value.Width;
			_drawer.PopupHeight = preservedFrame.Value.Height;
		}
		ApplyPopupFrame();
		SyncEditModeToggle();
		await ApplyLayoutFromStoreAsync();
		if (_drawer == null)
		{
			CellsGrid.Children.Clear();
			EmptyHintText.Visibility = Visibility.Visible;
			TitleText.Text = _localizationService.T("ShortcutDrawer.Title");
			EditHintText.Visibility = Visibility.Collapsed;
		}
		else
		{
			await _store.SyncDrawerFolderAsync(_drawer, GetColumnCount());
			await _store.SaveAsync(_data);
			TitleText.Text = (string.IsNullOrWhiteSpace(_drawer.Name) ? _localizationService.T("ShortcutDrawer.Title") : _drawer.Name);
			RenderDrawer();
		}
	}

	private void SyncEditHint()
	{
		if (_isEditMode && _selectedItem != null && _drawer != null)
		{
			TitleText.Text = _localizationService.T("ShortcutDrawer.Edit");
			EditHintText.Text = _localizationService.T("ShortcutDrawer.MoveHint");
			EditHintText.Visibility = Visibility.Visible;
			return;
		}
		EditHintText.Visibility = Visibility.Collapsed;
		EditHintText.Text = string.Empty;
		if (_drawer != null)
		{
			TitleText.Text = (string.IsNullOrWhiteSpace(_drawer.Name) ? _localizationService.T("ShortcutDrawer.Title") : _drawer.Name);
		}
	}

	private void RenderDrawer()
	{
		SyncEditHint();
		CellsGrid.Children.Clear();
		CellsGrid.RowDefinitions.Clear();
		CellsGrid.ColumnDefinitions.Clear();
		if (_drawer == null || _data == null)
		{
			EmptyHintText.Visibility = Visibility.Visible;
			return;
		}
		string drawerFolderPath = _store.GetDrawerFolderPath(_drawer);
		List<ShortcutDrawerItem> list = _drawer.Items.ToList();
		EmptyHintText.Visibility = ((list.Count != 0) ? Visibility.Collapsed : Visibility.Visible);
		// Extent math must include spans: a 2x2 tile at column 10 occupies
		// column 11 too, and the grid must exist underneath it.
		int columnCount = GetColumnCount();
		int num = 1;
		int maxColumn = columnCount;
		foreach (ShortcutDrawerItem item in list)
		{
			int height = Math.Max(1, item.SizeH);
			int width = Math.Max(1, item.SizeW);
			num = Math.Max(num, item.Row + height - 1);
			maxColumn = Math.Max(maxColumn, item.Column + width - 1);
		}
		int num2 = num + 2;
		int num3 = Math.Max(maxColumn, (list.Count != 0) ? 1 : columnCount) + 2;
		CellsGrid.RowSpacing = _rowSpacing;
		CellsGrid.ColumnSpacing = _columnSpacing;
		for (int i = 0; i < num3; i++)
		{
			CellsGrid.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = new GridLength(_tileSize)
			});
		}
		for (int j = 0; j < num2; j++)
		{
			CellsGrid.RowDefinitions.Add(new RowDefinition
			{
				Height = new GridLength(_tileSize)
			});
		}
		for (int row = 1; row <= num2; row++)
		{
			int col;
			for (col = 1; col <= num3; col++)
			{
				ShortcutDrawerItem shortcutDrawerItem = list.FirstOrDefault((ShortcutDrawerItem shortcutDrawerItem2) => shortcutDrawerItem2.Column == col && shortcutDrawerItem2.Row == row);
				if (shortcutDrawerItem != null)
				{
					try
					{
						int width = Math.Clamp(Math.Max(1, shortcutDrawerItem.SizeW), 1, 3);
						int height = Math.Clamp(Math.Max(1, shortcutDrawerItem.SizeH), 1, 3);
						FrameworkElement frameworkElement = CreateItemTile(shortcutDrawerItem, drawerFolderPath, col, row, width, height);
						Grid.SetColumn(frameworkElement, col - 1);
						Grid.SetRow(frameworkElement, row - 1);
						if (width > 1)
						{
							Grid.SetColumnSpan(frameworkElement, width);
						}
						if (height > 1)
						{
							Grid.SetRowSpan(frameworkElement, height);
						}
						CellsGrid.Children.Add(frameworkElement);
					}
					catch (Exception ex)
					{
						App.Log("[ShortcutDrawer] Tile '" + shortcutDrawerItem.FileName + "' not rendered: " + ex.Message);
					}
				}
			}
		}
	}

	// Grid cell backgrounds are gone: tiles are free-positioned now, and the
	// per-cell visuals (with their own pointer handlers) used to sit above
	// every tile that spanned into a later row/column, swallowing drags.

	private void MoveItemAsync(ShortcutDrawerItemViewModel item, Point releasePoint)
	{
		if (_drawer != null && _data != null)
		{
			double num = _tileSize + _columnSpacing;
			double num2 = _tileSize + _rowSpacing;
			if (!(num <= 0.0) && !(num2 <= 0.0))
			{
				int targetColumn = Math.Max(1, (int)Math.Floor(releasePoint.X / num) + 1);
				int targetRow = Math.Max(1, (int)Math.Floor(releasePoint.Y / num2) + 1);
				MoveItem(item, targetColumn, targetRow);
				RenderDrawer();
				PersistDrawerAsync();
			}
		}
	}

	/// <summary>
	/// Rendered width of a tile spanning <paramref name="cells"/> columns: cells
	/// plus the inter-column gaps, minus the same 4px inset a 1x1 tile carries.
	/// </summary>
	private double TilePixelWidth(int cells) => (cells <= 1) ? _tileSize - 4 : (cells * _tileSize + (cells - 1) * _columnSpacing - 4);

	/// <summary>Rendered height of a tile spanning <paramref name="cells"/> rows.</summary>
	private double TilePixelHeight(int cells) => (cells <= 1) ? _tileSize - 4 : (cells * _tileSize + (cells - 1) * _rowSpacing - 4);

	/// <summary>
	/// Larger tiers get proportionally larger artwork, which is the entire
	/// memory benefit of the free layout: big tiles read as important landmarks.
	/// Scaled by area (sqrt of the cell count) and capped so the label stays legible.
	/// </summary>
	private double TileIconEdge(int width, int height)
	{
		double tileWidth = TilePixelWidth(width);
		double tileHeight = TilePixelHeight(height);
		double edge = _iconSize * Math.Sqrt((double)Math.Max(1, width) * Math.Max(1, height));
		return Math.Floor(Math.Min(edge, Math.Min(tileWidth, tileHeight) * 0.60));
	}

	private FrameworkElement CreateItemTile(ShortcutDrawerItem item, string folder, int column, int row, int sizeW, int sizeH)
	{
		double tileWidth = TilePixelWidth(sizeW);
		double tileHeight = TilePixelHeight(sizeH);
		double iconEdge = TileIconEdge(sizeW, sizeH);
		ShortcutDrawerItemViewModel shortcutDrawerItemViewModel = new ShortcutDrawerItemViewModel
		{
			FileName = item.FileName,
			DisplayName = GetDisplayName(item.FileName),
			Column = column,
			Row = row,
			SizeW = sizeW,
			SizeH = sizeH,
			FullPath = Path.Combine(folder, item.FileName)
		};
		Grid grid = new Grid
		{
			Width = tileWidth,
			Height = tileHeight,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		// Top-anchored composition: the icon hangs near the top edge (the
		// tile reads like a phone-desktop launcher), the label follows right
		// beneath it, and both stay horizontally centered. The padding
		// shrinks rather than clipping the label on the tightest 1x1 tiles.
		// Two-line label slot: line height ≈ 1.33×font + wrap leading, x2 lines.
		double spacing = Math.Max(4.0, (iconEdge - _iconSize) * 0.35);
		double labelFont = (sizeW > 1 || sizeH > 1) ? 13.0 : 12.0;
		double labelSlot = labelFont * 2.75;
		double topPad = Math.Max(4.0, tileHeight * 0.06);
		double freeHeight = tileHeight - iconEdge - spacing - labelSlot;
		if (topPad > freeHeight - 2.0)
		{
			topPad = Math.Max(2.0, freeHeight - 2.0);
		}
		StackPanel stackPanel = new StackPanel
		{
			Orientation = Orientation.Vertical,
			Spacing = spacing,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Top,
			Margin = new Thickness(0.0, topPad, 0.0, 0.0)
		};
		Image image = new Image
		{
			Width = iconEdge,
			Height = iconEdge,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		stackPanel.Children.Add(image);
		stackPanel.Children.Add(new TextBlock
		{
			Text = shortcutDrawerItemViewModel.DisplayName,
			MaxWidth = tileWidth - 16,
			TextAlignment = TextAlignment.Center,
			TextTrimming = TextTrimming.CharacterEllipsis,
			TextWrapping = TextWrapping.Wrap,
			MaxLines = 2,
			FontSize = labelFont,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		});
		grid.Children.Add(stackPanel);
		if (_selectedItem == shortcutDrawerItemViewModel)
		{
			grid.Children.Add(new Border
			{
				CornerRadius = new CornerRadius(8.0),
				BorderThickness = new Thickness(2.0),
				BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
				IsHitTestVisible = false
			});
		}
		FrameworkElement result = (_isEditMode ? ((FrameworkElement)CreateEditTile(shortcutDrawerItemViewModel, grid)) : ((FrameworkElement)CreateOpenTile(shortcutDrawerItemViewModel, grid)));
		ResolveIconAsync(shortcutDrawerItemViewModel, image);
		return result;
	}

	private Button CreateOpenTile(ShortcutDrawerItemViewModel viewModel, Grid root)
	{
		Button button = new Button
		{
			// Zero padding/border so the content slot is exactly the tile
			// size: an oversized fixed-size child is left-anchored by WinUI,
			// which shifted the whole tile content to the right.
			Width = root.Width,
			Height = root.Height,
			Padding = new Thickness(0.0),
			BorderThickness = new Thickness(0.0),
			HorizontalContentAlignment = HorizontalAlignment.Stretch,
			VerticalContentAlignment = VerticalAlignment.Stretch,
			Tag = viewModel,
			CommandParameter = viewModel,
			Content = root
		};
		ToolTipService.SetToolTip(button, viewModel.DisplayName);
		button.Click += DrawerItem_Click;
		button.AddHandler(UIElement.ContextRequestedEvent, new TypedEventHandler<UIElement, ContextRequestedEventArgs>(Tile_ContextRequested), handledEventsToo: true);
		button.RightTapped += Tile_RightTapped;
		return button;
	}

	private Border CreateEditTile(ShortcutDrawerItemViewModel viewModel, Grid root)
	{
		Border border = new Border
		{
			Width = root.Width,
			Height = root.Height,
			CornerRadius = new CornerRadius(8.0),
			// Padding 0: the 65px content grid must not be squeezed into a
			// smaller slot (an oversized fixed child gets left-anchored,
			// shoving the whole tile right). The border stays for the
			// selection ring; the child centers inside the remaining slot.
			Padding = new Thickness(0.0),
			BorderThickness = new Thickness(1.0),
			Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
			BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
			Tag = viewModel,
			Child = root,
			RenderTransform = new TranslateTransform()
		};
		ApplyTileSelectionVisual(border, viewModel);
		border.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Tile_PointerPressed), handledEventsToo: true);
		border.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Tile_PointerMoved), handledEventsToo: true);
		border.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Tile_PointerReleased), handledEventsToo: true);
		border.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(Tile_PointerCaptureLost), handledEventsToo: true);
		border.RightTapped += Tile_RightTapped;
		border.AddHandler(UIElement.ContextRequestedEvent, new TypedEventHandler<UIElement, ContextRequestedEventArgs>(Tile_ContextRequested), handledEventsToo: true);
		ToolTipService.SetToolTip(border, viewModel.DisplayName);
		return border;
	}

	private void Tile_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
	{

		if (!(sender is FrameworkElement { Tag: ShortcutDrawerItemViewModel tag } frameworkElement))
		{

			return;
		}

		_menuTarget = tag;
		_contextMenuShownAt = DateTime.UtcNow;
		args.Handled = true;
		try
		{
			ShowTileMenu(frameworkElement, tag, args.TryGetPosition(frameworkElement, out var point) ? new Point?(point) : ((Point?)null));
		}
		catch (Exception value)
		{
			App.Log($"[ShortcutDrawer] Tile menu failed: {value}");
		}
	}

	private void Tile_RightTapped(object sender, RightTappedRoutedEventArgs e)
	{

		if (!(sender is FrameworkElement { Tag: ShortcutDrawerItemViewModel tag } frameworkElement))
		{

			return;
		}

		_menuTarget = tag;
		if (DateTime.UtcNow - _contextMenuShownAt < RightTapMenuSuppression)
		{
			return;
		}
		if (!_selectedItems.Contains(tag.FileName))
		{
			_selectedItem = tag;
			_selectedItems.Clear();
		}
		try
		{
			ShowTileMenu(frameworkElement, tag, e.GetPosition(frameworkElement));
		}
		catch (Exception value)
		{
			App.Log($"[ShortcutDrawer] Tile menu failed: {value}");
		}
	}

	private void ShowTileMenu(FrameworkElement tile, ShortcutDrawerItemViewModel item, Point? position)
	{
		MenuFlyout menuFlyout = BuildTileContextMenu(item);
		if (position.HasValue)
		{
			Point valueOrDefault = position.GetValueOrDefault();
			if (true)
			{
				menuFlyout.ShowAt(tile, valueOrDefault);
				return;
			}
		}
		menuFlyout.ShowAt(tile);
	}

	private MenuFlyout BuildTileContextMenu(ShortcutDrawerItemViewModel viewModel)
	{
		MenuFlyout menuFlyout = new MenuFlyout();
		menuFlyout.XamlRoot = ((Content is FrameworkElement frameworkElement) ? frameworkElement.XamlRoot : null);
		MenuFlyoutItem menuFlyoutItem = new MenuFlyoutItem
		{
			Text = _localizationService.T("ShortcutDrawer.Menu.Delete"),
			Icon = new FontIcon
			{
				Glyph = "\ue74d"
			}
		};
		menuFlyoutItem.Click += async (object _, RoutedEventArgs _) =>
		{
			await DeleteTileTargetsAsync(_menuTarget ?? viewModel);
		};
		menuFlyout.Items.Add(menuFlyoutItem);
		MenuFlyoutSubItem menuFlyoutSubItem = new MenuFlyoutSubItem
		{
			Text = _localizationService.T("ShortcutDrawer.Menu.Move"),
			Icon = new FontIcon
			{
				Glyph = "\ue8de"
			}
		};
		foreach (ShortcutDrawerDefinition item in OtherDrawers())
		{
			MenuFlyoutItem menuFlyoutItem2 = new MenuFlyoutItem
			{
				Text = item.Name
			};
			ShortcutDrawerDefinition target = item;
			menuFlyoutItem2.Click += async (object _, RoutedEventArgs _) =>
			{
				await MoveTileTargetsToDrawerAsync(_menuTarget ?? viewModel, target);
			};
			menuFlyoutSubItem.Items.Add(menuFlyoutItem2);
		}
		menuFlyoutSubItem.IsEnabled = menuFlyoutSubItem.Items.Count > 0;
		menuFlyout.Items.Add(menuFlyoutSubItem);
		menuFlyout.Items.Add(BuildTileSizeMenu(viewModel));
		MenuFlyoutItem menuFlyoutItem3 = new MenuFlyoutItem
		{
			Text = _localizationService.T("ShortcutDrawer.Menu.OpenTargetFolder"),
			Icon = new FontIcon
			{
				Glyph = "\ue8da"
			}
		};
		menuFlyoutItem3.Click += async (object _, RoutedEventArgs _) =>
		{
			await OpenShortcutTargetFolderAsync(_menuTarget ?? viewModel);
		};
		menuFlyout.Items.Add(menuFlyoutItem3);
		return menuFlyout;
	}

	/// <summary>
	/// Size tiers for the tile the menu was opened on. Applied to that one tile
	/// only — multi-select resizing would move many tiles for one gesture.
	/// </summary>
	private MenuFlyoutSubItem BuildTileSizeMenu(ShortcutDrawerItemViewModel viewModel)
	{
		MenuFlyoutSubItem submenu = new MenuFlyoutSubItem
		{
			Text = _localizationService.T("ShortcutDrawer.Menu.Size"),
			Icon = new FontIcon
			{
				Glyph = "\ue8a9"
			}
		};
		(int W, int H, string Key)[] tiers =
		{
			(1, 1, "ShortcutDrawer.Menu.Size.Small"),
			(2, 1, "ShortcutDrawer.Menu.Size.Wide"),
			(2, 2, "ShortcutDrawer.Menu.Size.Large")
		};
		foreach ((int w, int h, string key) in tiers)
		{
			ToggleMenuFlyoutItem option = new ToggleMenuFlyoutItem
			{
				Text = _localizationService.T(key),
				IsChecked = viewModel.SizeW == w && viewModel.SizeH == h
			};
			option.Click += (_, _) => ApplyTileSize(viewModel, w, h);
			submenu.Items.Add(option);
		}
		return submenu;
	}

	/// <summary>
	/// Changes a tile's size tiers in place. The anchor cell is kept when the
	/// new footprint fits there; otherwise the dropped rect is placed by the
	/// same displacement rule as a drag, so nothing ever overlaps.
	/// </summary>
	private void ApplyTileSize(ShortcutDrawerItemViewModel viewModel, int width, int height)
	{
		if (_drawer == null || _data == null)
		{
			return;
		}
		ShortcutDrawerItem entry = _drawer.Items.FirstOrDefault((ShortcutDrawerItem i) => string.Equals(i.FileName, viewModel.FileName, StringComparison.OrdinalIgnoreCase));
		if (entry == null)
		{
			return;
		}
		width = Math.Clamp(width, 1, 3);
		height = Math.Clamp(height, 1, 3);
		entry.SizeW = width;
		entry.SizeH = height;
		var others = new List<DrawerTileRect>();
		var map = new List<ShortcutDrawerItem>();
		foreach (ShortcutDrawerItem other in _drawer.Items)
		{
			if (other != entry)
			{
				others.Add(new DrawerTileRect(other.Column, other.Row, Math.Max(1, other.SizeW), Math.Max(1, other.SizeH)));
				map.Add(other);
			}
		}
		(DrawerTileRect dropped, Dictionary<int, DrawerTileRect> displaced) = DrawerTileLayout.PlaceWithDisplacement(
			new DrawerTileRect(entry.Column, entry.Row, width, height),
			others,
			GetColumnCount(),
			Math.Max(8, map.Count * 4 + 8));
		entry.Column = dropped.Column;
		entry.Row = dropped.Row;
		foreach (KeyValuePair<int, DrawerTileRect> pair in displaced)
		{
			if (map[pair.Key] is ShortcutDrawerItem resident)
			{
				resident.Column = pair.Value.Column;
				resident.Row = pair.Value.Row;
			}
		}
		RenderDrawer();
		PersistDrawerAsync();
	}

	private MenuFlyout BuildGridContextMenu()
	{
		MenuFlyout menuFlyout = new MenuFlyout();
		menuFlyout.XamlRoot = ((Content is FrameworkElement frameworkElement) ? frameworkElement.XamlRoot : null);
		ShortcutDrawerItemViewModel first = _drawer?.Items.Select((ShortcutDrawerItem item) => new ShortcutDrawerItemViewModel
		{
			FileName = item.FileName,
			DisplayName = GetDisplayName(item.FileName),
			Column = item.Column,
			Row = item.Row
		}).FirstOrDefault((ShortcutDrawerItemViewModel candidate) => _selectedItems.Contains(candidate.FileName));
		MenuFlyoutItem menuFlyoutItem = new MenuFlyoutItem
		{
			Text = _localizationService.T("ShortcutDrawer.Menu.Delete"),
			Icon = new FontIcon
			{
				Glyph = "\ue74d"
			},
			IsEnabled = (first != null)
		};
		menuFlyoutItem.Click += async (object _, RoutedEventArgs _) =>
		{
			await DeleteTileTargetsAsync(first);
		};
		menuFlyout.Items.Add(menuFlyoutItem);
		MenuFlyoutSubItem menuFlyoutSubItem = new MenuFlyoutSubItem
		{
			Text = _localizationService.T("ShortcutDrawer.Menu.Move"),
			Icon = new FontIcon
			{
				Glyph = "\ue8de"
			},
			IsEnabled = (first != null)
		};
		foreach (ShortcutDrawerDefinition item in OtherDrawers())
		{
			MenuFlyoutItem menuFlyoutItem2 = new MenuFlyoutItem
			{
				Text = item.Name
			};
			ShortcutDrawerDefinition target = item;
			menuFlyoutItem2.Click += async (object _, RoutedEventArgs _) =>
			{
				await MoveTileTargetsToDrawerAsync(first, target);
			};
			menuFlyoutSubItem.Items.Add(menuFlyoutItem2);
		}
		menuFlyoutSubItem.IsEnabled = first != null && menuFlyoutSubItem.Items.Count > 0;
		menuFlyout.Items.Add(menuFlyoutSubItem);
		return menuFlyout;
	}

	private List<ShortcutDrawerDefinition> OtherDrawers()
	{
		if (_drawer == null || _data?.Drawers == null)
		{
			return new List<ShortcutDrawerDefinition>();
		}
		return _data.Drawers.Where((ShortcutDrawerDefinition drawer) => drawer != null && drawer.Id != _drawer.Id).ToList();
	}

	private void Cell_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
	{
		if (_selectedItems.Count == 0)
		{
			return;
		}
		args.Handled = true;
		_contextMenuShownAt = DateTime.UtcNow;
		try
		{
			MenuFlyout menuFlyout = BuildGridContextMenu();
			FrameworkElement frameworkElement = (sender as FrameworkElement) ?? CellsGrid;
			if (args.TryGetPosition(frameworkElement, out var point))
			{
				menuFlyout.ShowAt(frameworkElement, point);
			}
			else
			{
				menuFlyout.ShowAt(frameworkElement);
			}
		}
		catch (Exception value)
		{
			App.Log($"[ShortcutDrawer] Grid menu failed: {value}");
		}
	}

	private void ApplyTileSelectionVisual(Border tile, ShortcutDrawerItemViewModel viewModel)
	{
		bool flag = _selectedItem == viewModel || _selectedItems.Contains(viewModel.FileName);
		tile.BorderThickness = (flag ? new Thickness(2.0) : new Thickness(1.0));
		tile.BorderBrush = (flag ? ((Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]) : ((Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"]));
	}

	private List<string> GetMenuTargetFileNames(ShortcutDrawerItemViewModel? tile)
	{
		if (tile != null && _selectedItems.Contains(tile.FileName))
		{
			return _selectedItems.ToList();
		}
		if (tile == null)
		{
			return (_selectedItems.Count > 0) ? _selectedItems.ToList() : new List<string>();
		}
		int num = 1;
		List<string> list = new List<string>(num);
		CollectionsMarshal.SetCount(list, num);
		CollectionsMarshal.AsSpan(list)[0] = tile.FileName;
		return list;
	}

	private void Tile_PointerPressed(object sender, PointerRoutedEventArgs e)
	{
		if (sender is Border { Tag: ShortcutDrawerItemViewModel tag } border && _isEditMode)
		{
			PointerPoint currentPoint = e.GetCurrentPoint(border);
			if (currentPoint.Properties.IsLeftButtonPressed)
			{
				_pendingItem = tag;
				_tileDragStart = e.GetCurrentPoint(CellsGrid).Position;
				_tileDragOwner = border;
				_dragTileTransform = border.RenderTransform as TranslateTransform;
				border.CapturePointer(e.Pointer);
				HighlightTile(border);
				// A press on a multi-selected tile drags the whole selection.
				_dragGroupItems.Clear();
				_dragGroupOwners.Clear();
				_dragGroupTransforms.Clear();
				if (_selectedItems.Contains(tag.FileName) && _selectedItems.Count > 1)
				{
					var members = new List<KeyValuePair<ShortcutDrawerItemViewModel, Border>>();
					foreach (UIElement child in CellsGrid.Children)
					{
						if (child is Border { Tag: ShortcutDrawerItemViewModel vm } owner
							&& vm.FileName != null && _selectedItems.Contains(vm.FileName))
						{
							members.Add(new KeyValuePair<ShortcutDrawerItemViewModel, Border>(vm, owner));
						}
					}
					foreach (KeyValuePair<ShortcutDrawerItemViewModel, Border> pair in members.OrderBy(p => p.Key.Row * 4096 + p.Key.Column))
					{
						_dragGroupItems.Add(pair.Key);
						_dragGroupOwners.Add(pair.Value);
						_dragGroupTransforms.Add(pair.Value.RenderTransform as TranslateTransform ?? new TranslateTransform());
					}
				}
				e.Handled = true;
			}
		}
	}

	private void Tile_PointerMoved(object sender, PointerRoutedEventArgs e)
	{
		if (!_isEditMode || (object)_tileDragOwner == null || (object)_dragTileTransform == null)
		{
			return;
		}
		Point position = e.GetCurrentPoint(CellsGrid).Position;
		double num = position.X - _tileDragStart.X;
		double num2 = position.Y - _tileDragStart.Y;
		if (!_isTileDragging)
		{
			if (Math.Abs(num) < 2.0 && Math.Abs(num2) < 2.0)
			{
				return;
			}
			_isTileDragging = true;
			_tileDragOwner.Opacity = 0.6;
			// Ghost the whole selection the same beat as the anchor tile.
			foreach (Border owner in _dragGroupOwners)
			{
				owner.Opacity = 0.6;
			}
		}
		_dragTileTransform.X = num;
		_dragTileTransform.Y = num2;
		for (int i = 0; i < _dragGroupOwners.Count; i++)
		{
			if (_dragGroupTransforms[i] != null)
			{
				_dragGroupTransforms[i].X = num;
				_dragGroupTransforms[i].Y = num2;
			}
		}
		e.Handled = true;
	}

	private async void Tile_PointerReleased(object sender, PointerRoutedEventArgs e)
	{
		Border tileDragOwner = _tileDragOwner;
		TranslateTransform dragTileTransform = _dragTileTransform;
		ShortcutDrawerItemViewModel pendingItem = _pendingItem;
		bool isTileDragging = _isTileDragging;
		// _pendingItem is only ever set by a left-button press, so it being
		// non-null is the left-press check. Re-reading IsLeftButtonPressed here
		// would always be false on a release event, which is why drops were
		// silently discarded.
		bool isLeftButtonPressed = pendingItem != null;
		_tileDragOwner = null;
		_dragTileTransform = null;
		_pendingItem = null;
		_isTileDragging = false;
		if (pendingItem == null || !isLeftButtonPressed)
		{
			if ((object)dragTileTransform != null)
			{
				dragTileTransform.X = 0.0;
				dragTileTransform.Y = 0.0;
			}
			if ((object)tileDragOwner != null)
			{
				tileDragOwner.Opacity = 1.0;
			}
			return;
		}
		tileDragOwner?.ReleasePointerCapture(e.Pointer);
		RestoreGroupVisuals();
		if ((object)dragTileTransform != null)
		{
			dragTileTransform.X = 0.0;
			dragTileTransform.Y = 0.0;
		}
		if ((object)tileDragOwner != null)
		{
			tileDragOwner.Opacity = 1.0;
		}
		if (isTileDragging)
		{
			if (pendingItem != null)
			{
				Point position = e.GetCurrentPoint(CellsGrid).Position;
				// A release over another open drawer transfers the whole
				// dragged selection there; otherwise the tiles land on this
				// window's grid as usual.
				bool transferred = await TryTransferDraggedTilesToDrawerUnderCursorAsync(pendingItem);
				if (!transferred)
				{
					MoveItemToReleaseAsync(pendingItem, position);
				}
			}
			_selectedItem = null;
			_dragGroupItems.Clear();
			_dragGroupOwners.Clear();
			_dragGroupTransforms.Clear();
			RenderDrawer();
		}
		else if (pendingItem == null)
		{
			RenderDrawer();
		}
		else if (IsCtrlPressed())
		{
			if (!_selectedItems.Add(pendingItem.FileName))
			{
				_selectedItems.Remove(pendingItem.FileName);
			}
			_selectedItem = null;
			RenderDrawer();
		}
		else if (_selectedItem == null)
		{
			// A plain click opens the tile in every mode: reordering is drag
			// only, and multi-select stays on Ctrl+click.
			_selectedItem = null;
			_selectedItems.Clear();
			if (pendingItem.FullPath != null)
			{
				ItemActivated?.Invoke(this, pendingItem);
			}
		}
		else
		{
			_selectedItem = null;
			_selectedItems.Clear();
		}
	}

	private void Tile_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
	{
		if ((object)_tileDragOwner != null)
		{
			TranslateTransform dragTileTransform = _dragTileTransform;
			Border tileDragOwner = _tileDragOwner;
			_tileDragOwner = null;
			_dragTileTransform = null;
			_pendingItem = null;
			_isTileDragging = false;
			RestoreGroupVisuals();
			_dragGroupItems.Clear();
			_dragGroupOwners.Clear();
			_dragGroupTransforms.Clear();
			if ((object)dragTileTransform != null)
			{
				dragTileTransform.X = 0.0;
				dragTileTransform.Y = 0.0;
			}
			if ((object)tileDragOwner != null)
			{
				tileDragOwner.Opacity = 1.0;
			}
		}
	}

	/// <summary>Zeros every group ghost transform and restores its opacity.</summary>
	private void RestoreGroupVisuals()
	{
		for (int i = 0; i < _dragGroupOwners.Count; i++)
		{
			_dragGroupOwners[i].Opacity = 1.0;
			if (i < _dragGroupTransforms.Count && _dragGroupTransforms[i] != null)
			{
				_dragGroupTransforms[i].X = 0.0;
				_dragGroupTransforms[i].Y = 0.0;
			}
		}
	}

	private void HighlightTile(Border tile)
	{
		tile.BorderThickness = new Thickness(2.0);
		tile.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
	}

	private async Task MoveItemToReleaseAsync(ShortcutDrawerItemViewModel item, Point releasePoint)
	{
		double stepX = _tileSize + _columnSpacing;
		double stepY = _tileSize + _rowSpacing;
		if (!(stepX <= 0.0) && !(stepY <= 0.0) && _drawer != null && _data != null)
		{
			// WYSIWYG drop: the ghost preview shows the tile at its original
			// cell plus the pointer delta, so the release cell derives from
			// that translation — not from where the pointer happens to sit
			// inside the tile.
			int column = Math.Max(1, item.Column + (int)Math.Round((releasePoint.X - _tileDragStart.X) / stepX));
			int row = Math.Max(1, item.Row + (int)Math.Round((releasePoint.Y - _tileDragStart.Y) / stepY));
			if (_dragGroupItems.Count > 1 && _selectedItems.Contains(item.FileName))
			{
				MoveGroupItems(item, column, row);
			}
			else
			{
				MoveItem(item, column, row);
			}
		}
	}

	/// <summary>
	/// When a tile drag ends with the pointer over another open drawer, the
	/// whole dragged selection transfers into that drawer instead of landing
	/// on this window's grid. Returns false when the release is not over a
	/// sibling drawer, so the normal in-drawer move proceeds.
	/// </summary>
	private async Task<bool> TryTransferDraggedTilesToDrawerUnderCursorAsync(ShortcutDrawerItemViewModel anchor)
	{
		if (_drawer == null || _data == null
			|| string.IsNullOrEmpty(anchor.FileName)
			|| !Win32Helper.GetCursorPos(out Win32Helper.POINT cursor))
		{
			return false;
		}
		ShortcutDrawerWindow? targetWindow = (App.Current as App)?.GetOpenDrawerWindowAt(
			new Windows.Graphics.PointInt32(cursor.X, cursor.Y), _drawer.Id);
		if (targetWindow?.CurrentDrawerId is not Guid targetId || targetId == Guid.Empty)
		{
			return false;
		}
		ShortcutDrawerDefinition? target = _data.Drawers.FirstOrDefault(
			(ShortcutDrawerDefinition d) => d.Id == targetId);
		if (target == null)
		{
			return false;
		}
		List<string> names = _dragGroupItems.Count > 1 && _selectedItems.Contains(anchor.FileName)
			? _dragGroupItems.Select((ShortcutDrawerItemViewModel vm) => vm.FileName).ToList()
			: new List<string> { anchor.FileName };
		names.RemoveAll(string.IsNullOrEmpty);
		if (names.Count == 0)
		{
			return false;
		}
		// Source cells before the move: the group keeps its shape relative to
		// them once the anchor lands at the pointer cell in the target grid.
		var sourcePositions = new Dictionary<string, (int Column, int Row)>(StringComparer.OrdinalIgnoreCase);
		foreach (ShortcutDrawerItem sourceItem in _drawer.Items)
		{
			if (sourceItem.FileName != null && names.Contains(sourceItem.FileName))
			{
				sourcePositions[sourceItem.FileName] = (sourceItem.Column, sourceItem.Row);
			}
		}
		(int Column, int Row)? dropCell = targetWindow.GetGridCellFromScreenPoint(
			new Windows.Graphics.PointInt32(cursor.X, cursor.Y));
		int moved = await _store.MoveShortcutsToDrawerAsync(_data, _drawer, target, names, GetColumnCount());
		if (moved > 0)
		{
			if (dropCell.HasValue)
			{
				PlaceTransferredGroupInDrawer(target, sourcePositions, anchor.FileName, dropCell.Value);
			}
			await _store.SaveAsync(_data);
			App.Log("[ShortcutDrawer] Drag transferred " + moved + " tile(s) to another drawer");
			App.NotifyShortcutDrawerLayoutChanged();
		}
		return moved > 0;
	}

	/// <summary>
	/// Repositions just-transferred tiles inside the target drawer: the anchor
	/// lands at the pointer cell and the rest keep their original relative
	/// offsets through the same collision/displacement rule as in-drawer
	/// group moves.
	/// </summary>
	private void PlaceTransferredGroupInDrawer(
		ShortcutDrawerDefinition target,
		Dictionary<string, (int Column, int Row)> sourcePositions,
		string anchorName,
		(int Column, int Row) anchorCell)
	{
		var movedItems = target.Items
			.Where((ShortcutDrawerItem i) => i.FileName != null && sourcePositions.ContainsKey(i.FileName))
			.ToList();
		if (movedItems.Count == 0)
		{
			return;
		}
		ShortcutDrawerItem anchorItem = movedItems.FirstOrDefault(
			(ShortcutDrawerItem i) => string.Equals(i.FileName, anchorName, StringComparison.OrdinalIgnoreCase))
			?? movedItems[0];
		var occupancy = new List<DrawerTileRect>();
		var map = new List<ShortcutDrawerItem>();
		foreach (ShortcutDrawerItem other in target.Items)
		{
			if (movedItems.Contains(other))
			{
				continue;
			}
			occupancy.Add(new DrawerTileRect(other.Column, other.Row, Math.Max(1, other.SizeW), Math.Max(1, other.SizeH)));
			map.Add(other);
		}
		int columns = TileColumnCount;
		int maxIterations = Math.Max(8, map.Count * 4 + 8 + movedItems.Count * 2);
		(int anchorOriginalColumn, int anchorOriginalRow) = sourcePositions[anchorItem.FileName];
		int anchorFinalColumn = anchorCell.Column;
		int anchorFinalRow = anchorCell.Row;
		var ordered = new[] { anchorItem }
			.Concat(movedItems.Where((ShortcutDrawerItem m) => m != anchorItem)
				.OrderBy((ShortcutDrawerItem m) =>
					sourcePositions[m.FileName].Row * 4096 + sourcePositions[m.FileName].Column));
		foreach (ShortcutDrawerItem member in ordered)
		{
			bool isAnchor = member == anchorItem;
			(int sourceColumn, int sourceRow) = sourcePositions[member.FileName];
			int width = Math.Clamp(member.SizeW <= 0 ? 1 : member.SizeW, 1, 3);
			int height = Math.Clamp(member.SizeH <= 0 ? 1 : member.SizeH, 1, 3);
			int targetColumn = Math.Max(1, anchorFinalColumn + sourceColumn - anchorOriginalColumn);
			int targetRow = Math.Max(1, anchorFinalRow + sourceRow - anchorOriginalRow);
			(DrawerTileRect dropped, Dictionary<int, DrawerTileRect> displaced) = DrawerTileLayout.PlaceWithDisplacement(
				new DrawerTileRect(targetColumn, targetRow, width, height),
				occupancy,
				columns,
				maxIterations);
			member.Column = dropped.Column;
			member.Row = dropped.Row;
			if (isAnchor)
			{
				anchorFinalColumn = dropped.Column;
				anchorFinalRow = dropped.Row;
			}
			foreach (KeyValuePair<int, DrawerTileRect> pair in displaced)
			{
				if (map[pair.Key] is ShortcutDrawerItem resident)
				{
					resident.Column = pair.Value.Column;
					resident.Row = pair.Value.Row;
					occupancy[pair.Key] = new DrawerTileRect(pair.Value.Column, pair.Value.Row, Math.Max(1, resident.SizeW), Math.Max(1, resident.SizeH));
				}
			}
			occupancy.Add(dropped);
		}
	}

	/// <summary>
	/// Lands every member of the drag group with the anchor at the release
	/// cell and the rest at their original relative offsets, placed in reading
	/// order through the same collision/displacement rule as single moves.
	/// </summary>
	private void MoveGroupItems(ShortcutDrawerItemViewModel anchor, int anchorColumn, int anchorRow)
	{
		if (_drawer == null || _data == null)
		{
			return;
		}
		ShortcutDrawerItem anchorData = _drawer.Items.FirstOrDefault(
			(ShortcutDrawerItem i) => string.Equals(i.FileName, anchor.FileName, StringComparison.OrdinalIgnoreCase));
		if (anchorData == null)
		{
			return;
		}
		var groupData = new List<ShortcutDrawerItem>();
		foreach (ShortcutDrawerItemViewModel viewModel in _dragGroupItems)
		{
			ShortcutDrawerItem data = _drawer.Items.FirstOrDefault(
				(ShortcutDrawerItem i) => string.Equals(i.FileName, viewModel.FileName, StringComparison.OrdinalIgnoreCase));
			if (data != null)
			{
				groupData.Add(data);
			}
		}
		// Occupancy excludes the whole group so members never block each other.
		var occupancy = new List<DrawerTileRect>();
		var map = new List<ShortcutDrawerItem>();
		foreach (ShortcutDrawerItem other in _drawer.Items)
		{
			if (groupData.Contains(other))
			{
				continue;
			}
			occupancy.Add(new DrawerTileRect(other.Column, other.Row, Math.Max(1, other.SizeW), Math.Max(1, other.SizeH)));
			map.Add(other);
		}
		int columns = GetColumnCount();
		int maxIterations = Math.Max(8, map.Count * 4 + 8 + groupData.Count * 2);
		// The anchor lands first; every other member bases its target on the
		// anchor's final cell, so the group keeps its original relative shape
		// even when the anchor's own placement falls back to a free spot.
		// anchorData is the same object the loop mutates, so its ORIGINAL
		// cell must be captured before any placement rewrites it.
		int anchorOriginalColumn = anchorData.Column;
		int anchorOriginalRow = anchorData.Row;
		int anchorFinalColumn = anchorColumn;
		int anchorFinalRow = anchorRow;
		var ordered = new[] { anchorData }
			.Concat(groupData.Where((ShortcutDrawerItem m) => m != anchorData)
				.OrderBy((ShortcutDrawerItem data) => data.Row * 4096 + data.Column));
		foreach (ShortcutDrawerItem member in ordered)
		{
			bool isAnchor = member == anchorData;
			int width = Math.Clamp(member.SizeW <= 0 ? 1 : member.SizeW, 1, 3);
			int height = Math.Clamp(member.SizeH <= 0 ? 1 : member.SizeH, 1, 3);
			int targetColumn = Math.Max(1, anchorFinalColumn + member.Column - anchorOriginalColumn);
			int targetRow = Math.Max(1, anchorFinalRow + member.Row - anchorOriginalRow);
			(DrawerTileRect dropped, Dictionary<int, DrawerTileRect> displaced) = DrawerTileLayout.PlaceWithDisplacement(
				new DrawerTileRect(targetColumn, targetRow, width, height),
				occupancy,
				columns,
				maxIterations);
			int originalColumn = member.Column;
			int originalRow = member.Row;
			member.Column = dropped.Column;
			member.Row = dropped.Row;
			if (isAnchor)
			{
				anchorFinalColumn = dropped.Column;
				anchorFinalRow = dropped.Row;
			}
			foreach (KeyValuePair<int, DrawerTileRect> pair in displaced)
			{
				if (map[pair.Key] is ShortcutDrawerItem resident)
				{
					resident.Column = pair.Value.Column;
					resident.Row = pair.Value.Row;
					occupancy[pair.Key] = new DrawerTileRect(pair.Value.Column, pair.Value.Row, Math.Max(1, resident.SizeW), Math.Max(1, resident.SizeH));
				}
			}
			occupancy.Add(dropped);
		}
	}

	private async Task DeleteTileTargetsAsync(ShortcutDrawerItemViewModel tile)
	{
		if (_drawer != null && _data != null)
		{
			List<string> targets = GetMenuTargetFileNames(tile);
			await _store.RemoveShortcutsAsync(_data, _drawer, targets);
			_selectedItem = null;
			_selectedItems.ExceptWith(targets);
			RenderDrawer();
		}
	}

	private async Task MoveTileTargetsToDrawerAsync(ShortcutDrawerItemViewModel? tile, ShortcutDrawerDefinition destination)
	{
		if (_drawer == null || _data == null)
		{
			return;
		}
		List<string> targets = GetMenuTargetFileNames(tile);
		if (targets.Count != 0)
		{
			int moved = await _store.MoveShortcutsToDrawerAsync(_data, _drawer, destination, targets, GetColumnCount());
			if (moved > 0)
			{
				App.Log($"[ShortcutDrawer] Moved {moved} shortcut(s) into drawer '{destination.Name}'");
			}
			_selectedItem = null;
			_selectedItems.Clear();
			RenderDrawer();
			App.NotifyShortcutDrawerLayoutChanged();
		}
	}

	private async Task OpenShortcutTargetFolderAsync(ShortcutDrawerItemViewModel tile)
	{
		string shortcutPath = Path.Combine(path2: GetMenuTargetFileNames(tile)[0], path1: GetDrawerFolder());
		try
		{
			string resolved = await (App.Current?.FileService.GetStoredShortcutTargetAsync(shortcutPath));
			string targetPath = (string.IsNullOrWhiteSpace(resolved) ? null : resolved);
			if (targetPath != null && File.Exists(targetPath))
			{
				targetPath = Path.GetDirectoryName(targetPath);
			}
			else if (targetPath == null || !Directory.Exists(targetPath))
			{
				targetPath = null;
			}
			if (string.IsNullOrWhiteSpace(targetPath) || !Directory.Exists(targetPath))
			{
				App.Log("[ShortcutDrawer] Shortcut target folder not available for '" + shortcutPath + "'");
			}
			else
			{
				OpenFolder(targetPath);
			}
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			App.Log("[ShortcutDrawer] Opening the shortcut target folder failed: " + ex2.Message);
		}
	}

	private string GetDrawerFolder()
	{
		return (_drawer == null) ? string.Empty : _store.GetDrawerFolderPath(_drawer);
	}

	private static void OpenFolder(string folder)
	{
		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = folder,
				UseShellExecute = true
			});
		}
		catch (Exception ex)
		{
			App.Log("[ShortcutDrawer] Failed to open '" + folder + "': " + ex.Message);
		}
	}

	private void Grid_PointerPressed(object sender, PointerRoutedEventArgs e)
	{
		if (_isEditMode && _drawer != null && _data != null && IsOverEditableSurface(e))
		{
			PointerPoint currentPoint = e.GetCurrentPoint(CellsGrid);
			if (currentPoint.Properties.IsLeftButtonPressed)
			{
				_marqueeStart = currentPoint.Position;
				_isMarqueeActive = true;
				CellsGrid.CapturePointer(e.Pointer);
				e.Handled = true;
			}
		}
	}

	private bool IsOverEditableSurface(PointerRoutedEventArgs e)
	{
		return FindTileAncestor(e.OriginalSource as DependencyObject) == null;
	}

	private static ShortcutDrawerItemViewModel? FindTileAncestor(DependencyObject? node)
	{
		while ((object)node != null)
		{
			if (node is FrameworkElement { Tag: ShortcutDrawerItemViewModel tag })
			{
				return tag;
			}
			node = VisualTreeHelper.GetParent(node);
		}
		return null;
	}

	private void Grid_PointerMoved(object sender, PointerRoutedEventArgs e)
	{
		if (_isMarqueeActive && _isEditMode)
		{
			Point position = e.GetCurrentPoint(CellsGrid).Position;
			double num = Math.Min(_marqueeStart.X, position.X);
			double num2 = Math.Min(_marqueeStart.Y, position.Y);
			double width = Math.Max(Math.Abs(position.X - _marqueeStart.X), 1.0);
			double height = Math.Max(Math.Abs(position.Y - _marqueeStart.Y), 1.0);
			FrameworkElement frameworkElement = (MarqueeBorder.Parent as FrameworkElement) ?? CellsGrid;
			Point point = frameworkElement.TransformToVisual(CellsGrid).TransformPoint(new Point(0f, 0f));
			MarqueeBorder.Margin = new Thickness(point.X + num, point.Y + num2, 0.0, 0.0);
			MarqueeBorder.Width = width;
			MarqueeBorder.Height = height;
			MarqueeBorder.Visibility = Visibility.Visible;
			e.Handled = true;
		}
	}

	private void Grid_PointerReleased(object sender, PointerRoutedEventArgs e)
	{
		if (!_isMarqueeActive)
		{
			return;
		}
		_isMarqueeActive = false;
		CellsGrid.ReleasePointerCapture(e.Pointer);
		e.Handled = true;
		if (MarqueeBorder.Visibility != Visibility.Visible)
		{
			// Plain click on empty space (no marquee was drawn): deselect
			// everything. Ctrl keeps the selection, matching the additive
			// Ctrl-marquee semantics.
			if (!IsCtrlPressed())
			{
				_selectedItem = null;
				_selectedItems.Clear();
				RenderDrawer();
			}
			return;
		}
		RectInt32 bounds = new RectInt32((int)MarqueeBorder.Margin.Left, (int)MarqueeBorder.Margin.Top, (int)MarqueeBorder.Width, (int)MarqueeBorder.Height);
		MarqueeBorder.Visibility = Visibility.Collapsed;
		if (!IsCtrlPressed())
		{
			_selectedItems.Clear();
		}
		foreach (string item in HitTestSelection(bounds))
		{
			_selectedItems.Add(item);
		}
		RenderDrawer();
	}

	private void Grid_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
	{
		if (_isMarqueeActive)
		{
			_isMarqueeActive = false;
			MarqueeBorder.Visibility = Visibility.Collapsed;
		}
	}

	private List<string> HitTestSelection(RectInt32 bounds)
	{
		if (_drawer == null || _drawer.Items.Count == 0)
		{
			return new List<string>();
		}
		double stepX = _tileSize + _columnSpacing;
		double stepY = _tileSize + _rowSpacing;
		if (stepX <= 0.0 || stepY <= 0.0)
		{
			return new List<string>();
		}
		// Pixel-space intersection against each tile's real footprint: an
		// oversized tile must be selectable by any part of its body, and the
		// old anchor-cell range math (with a ceiling overshoot) pulled whole
		// adjacent rows into the selection.
		double marqueeRight = bounds.X + bounds.Width;
		double marqueeBottom = bounds.Y + bounds.Height;
		List<string> hits = new List<string>();
		foreach (ShortcutDrawerItem item in _drawer.Items)
		{
			int widthCells = Math.Clamp(Math.Max(1, item.SizeW), 1, 3);
			int heightCells = Math.Clamp(Math.Max(1, item.SizeH), 1, 3);
			double tileLeft = (item.Column - 1) * stepX + 2;
			double tileTop = (item.Row - 1) * stepY + 2;
			double tileWidth = widthCells * stepX - _columnSpacing - 4;
			double tileHeight = heightCells * stepY - _rowSpacing - 4;
			bool intersects =
				tileLeft < marqueeRight
				&& tileLeft + tileWidth > bounds.X
				&& tileTop < marqueeBottom
				&& tileTop + tileHeight > bounds.Y;
			if (intersects)
			{
				hits.Add(item.FileName);
			}
		}
		return hits;
	}

	private static bool IsCtrlPressed()
	{
		return Win32Helper.IsKeyPressed(VirtualKey.Control);
	}

	private async Task ApplyLayoutFromStoreAsync()
	{
		if (_drawer != null && _data != null)
		{
			_iconSize = ShortcutDrawerStore.ResolveIconSize(_data, _drawer);
			_rowSpacing = ShortcutDrawerStore.ResolveRowSpacing(_data, _drawer);
			_columnSpacing = ShortcutDrawerStore.ResolveColumnSpacing(_data, _drawer);
			_tileSize = Math.Clamp(_iconSize + 52, 84, 208);
			CellsGrid.ColumnSpacing = _columnSpacing;
			CellsGrid.RowSpacing = _rowSpacing;
			await Task.CompletedTask;
		}
	}

	private int GetIconSize()
	{
		return _iconSize;
	}

	private void SyncEditModeToggle()
	{
		_isSyncingEditToggle = true;
		try
		{
			// The header switch is a lock: ON = tiles locked (not draggable);
			// the drawer always opens unlocked (edit mode).
			EditModeToggle.IsOn = !_isEditMode;
			// Editing shows the lock action; locked shows the edit action.
			EditGlyph.Text = _isEditMode ? "\uE72E" : "\uE70F";
			ToolTipService.SetToolTip(EditModeToggle, _localizationService.T(
				_isEditMode ? "ShortcutDrawer.LockHint" : "ShortcutDrawer.EditHint"));
		}
		finally
		{
			_isSyncingEditToggle = false;
		}
	}

	private void MoveItem(ShortcutDrawerItemViewModel item, int targetColumn, int targetRow)
	{
		if (_drawer == null || _data == null)
		{
			return;
		}
		ShortcutDrawerItem shortcutDrawerItem = _drawer.Items.FirstOrDefault((ShortcutDrawerItem i) => string.Equals(i.FileName, item.FileName, StringComparison.OrdinalIgnoreCase));
		if (shortcutDrawerItem == null)
		{
			return;
		}
		int width = Math.Clamp(shortcutDrawerItem.SizeW <= 0 ? 1 : shortcutDrawerItem.SizeW, 1, 3);
		int height = Math.Clamp(shortcutDrawerItem.SizeH <= 0 ? 1 : shortcutDrawerItem.SizeH, 1, 3);
		// Resident occupancy for the placement search excludes the tile being
		// moved, so its old footprint never blocks the drop.
		var others = new List<DrawerTileRect>();
		var map = new List<ShortcutDrawerItem>();
		foreach (ShortcutDrawerItem other in _drawer.Items)
		{
			if (other != shortcutDrawerItem)
			{
				others.Add(new DrawerTileRect(other.Column, other.Row, Math.Max(1, other.SizeW), Math.Max(1, other.SizeH)));
				map.Add(other);
			}
		}
		(DrawerTileRect dropped, Dictionary<int, DrawerTileRect> displaced) = DrawerTileLayout.PlaceWithDisplacement(
			new DrawerTileRect(targetColumn, targetRow, width, height),
			others,
			GetColumnCount(),
			Math.Max(8, map.Count * 4 + 8));
		shortcutDrawerItem.Column = dropped.Column;
		shortcutDrawerItem.Row = dropped.Row;
		foreach (KeyValuePair<int, DrawerTileRect> pair in displaced)
		{
			if (map[pair.Key] is ShortcutDrawerItem resident)
			{
				resident.Column = pair.Value.Column;
				resident.Row = pair.Value.Row;
			}
		}
	}

	private void RootGrid_DragOver(object sender, DragEventArgs e)
	{
		if (e.DataView.Contains(StandardDataFormats.StorageItems))
		{
			e.AcceptedOperation = DataPackageOperation.Copy;
		}
	}

	private async void RootGrid_Drop(object sender, DragEventArgs e)
	{
		if (!e.DataView.Contains(StandardDataFormats.StorageItems) || _drawer == null || _data == null)
		{
			return;
		}
		foreach (IStorageItem storageItem in await e.DataView.GetStorageItemsAsync())
		{
			if (!string.IsNullOrWhiteSpace(storageItem.Path))
			{
				await _store.CreateShortcutFromPathAsync(_drawer, storageItem.Path, GetColumnCount());
			}
		}
		await _store.SaveAsync(_data);
		RenderDrawer();
	}

	private static string GetDisplayName(string fileName)
	{
		string result = fileName;
		if (Path.GetExtension(fileName).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
		{
			result = Path.GetFileNameWithoutExtension(fileName);
		}
		return result;
	}

	public async Task ResolveIconAsync(ShortcutDrawerItemViewModel? item, Image? target)
	{
		if (item == null || (object)target == null || string.IsNullOrWhiteSpace(item.FullPath) || (object)target.Source != null)
		{
			return;
		}
		// Larger tiers need higher-resolution artwork: decode at the rendered
		// edge so a 2x2 landmark tile is crisp instead of a stretched small icon.
		int requestedEdge = Math.Max(GetIconSize(), (int)Math.Ceiling(target.Width));
		BitmapImage icon = await IconHelper.GetIconAsync(item.FullPath, hideShortcutArrowOverlay: true, showImageFilesAsIcons: true, requestedEdge, "shortcut-drawer");
		if ((object)icon == null)
		{
			return;
		}
		item.Icon = icon;
		if (_dispatcherQueue.HasThreadAccess)
		{
			target.Source = icon;
			return;
		}
		_dispatcherQueue.TryEnqueue(() =>
		{
			target.Source = icon;
		});
	}

	public Task OpenItemAsync(ShortcutDrawerItemViewModel item)
	{
		string path = item.FullPath ?? item.FileName;
		WidgetItem item2 = new WidgetItem
		{
			Path = path,
			IsFolder = Directory.Exists(path),
			IsShortcut = ShortcutHelper.IsShellLinkPath(path),
			TargetPath = string.Empty,
			Name = item.DisplayName
		};
		return FileService.OpenItemAsync(item2, _hwnd);
	}

	private static bool AreSystemAnimationsEnabled()
	{
		UISettings uISettings = new UISettings();
		return uISettings.AnimationsEnabled;
	}
}
