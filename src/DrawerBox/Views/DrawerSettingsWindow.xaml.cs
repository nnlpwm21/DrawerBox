using Microsoft.UI.Xaml;
using DeskBoxWhite.Helpers;
using DeskBoxWhite.Platform;
using DeskBoxWhite.Services;
using WinRT.Interop;

namespace DeskBoxWhite.Views;

/// <summary>
/// Minimal DrawerBox settings surface: the copied drawer management section
/// plus an exit action, because the standalone build has no other window to
/// host a tray menu.
/// </summary>
public sealed partial class DrawerSettingsWindow : Window
{
    private readonly LocalizationService _localizationService;

    public DrawerSettingsWindow(LocalizationService localizationService)
    {
        _localizationService = localizationService;
        InitializeComponent();
        TitleText.Text = _localizationService.T("DrawerBox.Settings.Title");
        ExitButton.Content = _localizationService.T("DrawerBox.Tray.Exit");
        ExitButton.Click += (_, _) => App.Current?.RequestExit();

        nint hwnd = WindowNative.GetWindowHandle(this);
        Section.Configure(_localizationService, hwnd);
        Section.RefreshFromSettings();

        try
        {
            Microsoft.UI.Windowing.AppWindow? appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(
                Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
            AppBranding.ApplyWindowIcon(appWindow);
            appWindow.Resize(new Windows.Graphics.SizeInt32(760, 880));
            appWindow.Move(new Windows.Graphics.PointInt32(
                (Win32Helper.GetSystemMetrics(0) - 760) / 2,
                (Win32Helper.GetSystemMetrics(1) - 880) / 2));
        }
        catch (Exception ex)
        {
            App.Log($"[DrawerBox] Settings window sizing failed: {ex.Message}");
        }
    }
}
