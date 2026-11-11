using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
using WinRT;
using WinRT.DeskBoxWhiteVtableClasses;
using WinRT.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.System;
using Windows.UI.ViewManagement;

namespace DeskBoxWhite.Views;

[WinRTRuntimeClassName("Microsoft.UI.Xaml.Markup.IComponentConnector")]

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

	private Border? _tileDragOwner;

	private Point _tileDragStart;

	private bool _isTileDragging;

	private bool _isWindowDragging;

	private PointInt32 _dragOrigin;

	private PointInt32 _dragWindowOrigin;

	/// <summary>
	/// Window bounds at the start of the current move-or-resize gesture. The
	/// snap calculator compares these against the desired rectangle, so only
	/// the edges the user is actually dragging take part in the snap.
	/// </summary>
	private RectInt32 _dragStartBounds;

	private Win32Helper.SubclassProc? _snapSubclassProc;

	private bool _isSnapSubclassInstalled;

	private bool _isResizeSessionActive;

	private const uint WmEnterSizeMode = 0x0231;

	private const uint WmExitSizeMode = 0x0232;

	private const uint WmSizing = 0x0214;

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
		App.ShortcutDrawerLayoutChanged += OnShortcutDrawerLayoutChanged;
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
		DrawerHideStoryboard.Stop();
		DrawerHideStoryboard.Completed -= OnDrawerHideCompleted;
		_isHiding = false;
		await LoadDrawerAsync(drawerId);
		RootGrid.Opacity = ((!AreSystemAnimationsEnabled()) ? 1 : 0);
		DrawerTranslateTransform.Y = (AreSystemAnimationsEnabled() ? (-28) : 0);
		_appWindow?.Show();
		IsPopupVisible = true;
		_shownAtUtc = DateTime.UtcNow;
		_focusWatchTimer.Start();
		DrawerShown?.Invoke(this, EventArgs.Empty);
		App.Current?.WidgetManager?.BringAuxiliaryWindowToFront(_hwnd, "shortcut-drawer-shown");
		Activate();
		Win32Helper.SetForegroundWindow(_hwnd);
		if (AreSystemAnimationsEnabled())
		{
			DrawerShowStoryboard.Begin();
		}
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
			DrawerShowStoryboard.Stop();
			if (AreSystemAnimationsEnabled())
			{
				DrawerHideStoryboard.Completed += OnDrawerHideCompleted;
				DrawerHideStoryboard.Begin();
			}
			else
			{
				CompleteHide();
			}
		}
	}

	private void OnDrawerHideCompleted(object? sender, object e)
	{
		DrawerHideStoryboard.Completed -= OnDrawerHideCompleted;
		CompleteHide();
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
		RemoveSnapSubclass();
		App.ShortcutDrawerLayoutChanged -= OnShortcutDrawerLayoutChanged;
		DrawerHideStoryboard.Completed -= OnDrawerHideCompleted;
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
		await LoadDrawerAsync(_drawer?.Id ?? Guid.Empty);
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
					return new IntPtr(1);

				case WmExitSizeMode:
					_isResizeSessionActive = false;
					_dragStartBounds = default;
					break;
			}
		}
		catch (Exception ex)
		{
			App.Log("[ShortcutDrawer] Snap sizing failed: " + ex.Message);
		}

		return Win32Helper.DefSubclassProc(hwnd, message, wParam, lParam);
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
			num = Math.Max(num, item.Column);
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
				num = Math.Max(num, item.Row);
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
			Win32Helper.ApplyFullWindowFrame(_hwnd);
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
		RootGrid.RequestedTheme = elementTheme;
	}

	private async Task LoadDrawerAsync(Guid drawerId)
	{
		_data = await _store.LoadAsync();
		_drawer = ((drawerId == Guid.Empty) ? _data.Drawers.FirstOrDefault() : (_data.Drawers.FirstOrDefault((ShortcutDrawerDefinition d) => d.Id == drawerId) ?? _data.Drawers.FirstOrDefault()));
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
		bool showBadges = _drawer.ShowPositionBadges ?? _data.ShowPositionBadges;
		List<ShortcutDrawerItem> list = _drawer.Items.ToList();
		EmptyHintText.Visibility = ((list.Count != 0) ? Visibility.Collapsed : Visibility.Visible);
		int columnCount = GetColumnCount();
		int num = 1;
		foreach (ShortcutDrawerItem item in list)
		{
			num = Math.Max(num, item.Row);
		}
		int num2 = num + 2;
		int num3 = Math.Max(columnCount, (list.Count != 0) ? 1 : columnCount) + 2;
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
				Border border = CreateCell(col, row);
				Grid.SetColumn(border, col - 1);
				Grid.SetRow(border, row - 1);
				CellsGrid.Children.Add(border);
				ShortcutDrawerItem shortcutDrawerItem = list.FirstOrDefault((ShortcutDrawerItem shortcutDrawerItem2) => shortcutDrawerItem2.Column == col && shortcutDrawerItem2.Row == row);
				if (shortcutDrawerItem != null)
				{
					try
					{
						FrameworkElement frameworkElement = CreateItemTile(shortcutDrawerItem, drawerFolderPath, showBadges, col, row);
						Grid.SetColumn(frameworkElement, col - 1);
						Grid.SetRow(frameworkElement, row - 1);
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

	private Border CreateCell(int column, int row)
	{
		Border border = new Border
		{
			Width = _tileSize - 4,
			Height = _tileSize - 4,
			CornerRadius = new CornerRadius(8.0),
			Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
			BorderThickness = new Thickness(1.0),
			BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
			Tag = (column, row)
		};
		if (_isEditMode)
		{
			border.Tapped += Cell_Tapped;
			border.AddHandler(UIElement.ContextRequestedEvent, new TypedEventHandler<UIElement, ContextRequestedEventArgs>(Cell_ContextRequested), handledEventsToo: true);
		}
		return border;
	}

	private void Cell_Tapped(object sender, TappedRoutedEventArgs e)
	{
		if (_isEditMode && _selectedItem != null && _drawer != null && _data != null)
		{
			(int, int) tuple = default;
			int num;
			if (sender is Border { Tag: var tag } && tag is ValueTuple<int, int>)
			{
				tuple = ((int, int))tag;
				num = 1;
			}
			else
			{
				num = 0;
			}
			if (num != 0)
			{
				MoveItem(_selectedItem, tuple.Item1, tuple.Item2);
				_selectedItem = null;
				RenderDrawer();
				PersistDrawerAsync();
			}
		}
	}

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

	private FrameworkElement CreateItemTile(ShortcutDrawerItem item, string folder, bool showBadges, int column, int row)
	{
		ShortcutDrawerItemViewModel shortcutDrawerItemViewModel = new ShortcutDrawerItemViewModel
		{
			FileName = item.FileName,
			DisplayName = GetDisplayName(item.FileName),
			Column = column,
			Row = row,
			FullPath = Path.Combine(folder, item.FileName)
		};
		Grid grid = new Grid
		{
			Width = _tileSize - 4,
			Height = _tileSize - 4
		};
		StackPanel stackPanel = new StackPanel
		{
			Orientation = Orientation.Vertical,
			Spacing = 4.0,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		Image image = new Image
		{
			Width = _iconSize,
			Height = _iconSize,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		stackPanel.Children.Add(image);
		stackPanel.Children.Add(new TextBlock
		{
			Text = shortcutDrawerItemViewModel.DisplayName,
			MaxWidth = _tileSize - 12,
			TextAlignment = TextAlignment.Center,
			TextTrimming = TextTrimming.CharacterEllipsis,
			TextWrapping = TextWrapping.NoWrap,
			FontSize = 12.0,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		});
		grid.Children.Add(stackPanel);
		if (showBadges)
		{
			grid.Children.Add(new Border
			{
				Margin = new Thickness(3.0),
				MinWidth = 26.0,
				HorizontalAlignment = HorizontalAlignment.Left,
				VerticalAlignment = VerticalAlignment.Top,
				IsHitTestVisible = false,
				Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
				CornerRadius = new CornerRadius(4.0),
				Padding = new Thickness(4.0, 0.0, 4.0, 0.0),
				Child = new TextBlock
				{
					Text = $"{column}.{row}",
					FontSize = 10.0,
					TextAlignment = TextAlignment.Center,
					Foreground = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"]
				}
			});
		}
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
			Width = _tileSize - 4,
			Height = _tileSize - 4,
			Padding = new Thickness(4.0),
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
			Width = _tileSize - 4,
			Height = _tileSize - 4,
			CornerRadius = new CornerRadius(8.0),
			Padding = new Thickness(4.0),
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

		}
		_dragTileTransform.X = num;
		_dragTileTransform.Y = num2;
		e.Handled = true;
	}

	private void Tile_PointerReleased(object sender, PointerRoutedEventArgs e)
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
				if (true)
				{
					MoveItemToReleaseAsync(pendingItem, position);
				}
			}
			_selectedItem = null;
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
			int column = Math.Max(1, (int)Math.Floor(releasePoint.X / stepX) + 1);
			int row = Math.Max(1, (int)Math.Floor(releasePoint.Y / stepY) + 1);
			MoveItem(item, column, row);
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
		double num = _tileSize + _columnSpacing;
		double num2 = _tileSize + _rowSpacing;
		if (num <= 0.0 || num2 <= 0.0)
		{
			return new List<string>();
		}
		int firstColumn = (int)Math.Floor((double)bounds.X / num) + 1;
		int lastColumn = (int)Math.Ceiling((double)(bounds.X + bounds.Width) / num) + 1;
		int firstRow = (int)Math.Floor((double)bounds.Y / num2) + 1;
		int lastRow = (int)Math.Ceiling((double)(bounds.Y + bounds.Height) / num2) + 1;
		return (from item in _drawer.Items
			where item.Column >= Math.Max(1, firstColumn) && item.Column <= lastColumn && item.Row >= Math.Max(1, firstRow) && item.Row <= lastRow
			select item.FileName).ToList();
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
			_tileSize = Math.Clamp(_iconSize + 40, 72, 160);
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
		if (shortcutDrawerItem != null)
		{
			ShortcutDrawerItem shortcutDrawerItem2 = _drawer.Items.FirstOrDefault((ShortcutDrawerItem i) => i.Column == targetColumn && i.Row == targetRow);
			if (shortcutDrawerItem2 != null && shortcutDrawerItem2 != shortcutDrawerItem)
			{
				int column = shortcutDrawerItem.Column;
				int row = shortcutDrawerItem.Row;
				shortcutDrawerItem.Column = targetColumn;
				shortcutDrawerItem.Row = targetRow;
				shortcutDrawerItem2.Column = column;
				shortcutDrawerItem2.Row = row;
			}
			else
			{
				shortcutDrawerItem.Column = targetColumn;
				shortcutDrawerItem.Row = targetRow;
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
		BitmapImage icon = await IconHelper.GetIconAsync(item.FullPath, hideShortcutArrowOverlay: true, showImageFilesAsIcons: true, GetIconSize(), "shortcut-drawer");
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
