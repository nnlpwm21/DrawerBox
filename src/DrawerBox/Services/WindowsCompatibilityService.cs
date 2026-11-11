namespace DeskBoxWhite.Services;

/// <summary>
/// DrawerBox-only subset of the compatibility service: just the OS build probe
/// the copied Win32Helper reads to skip Win11-only DWM attributes on the Win10
/// floor. The material/corner preference surface stays in the full app.
/// </summary>
public static class WindowsCompatibilityService
{
    public const int Windows11Build = 22000;

    private static readonly Lazy<int> s_osBuild = new(GetOsBuild);

    public static int OsBuild => s_osBuild.Value;

    public static bool IsWindows11OrLater => OsBuild >= Windows11Build;

    public static bool SupportsWin11DwmAttributes => IsWindows11OrLater;

    public static bool SupportsNativeWindowCorners => IsWindows11OrLater;

    private static int GetOsBuild()
    {
        try
        {
            return Environment.OSVersion.Version.Build;
        }
        catch
        {
            // Capability detection must fail closed. Assuming Windows 11 here
            // could invoke unsupported DWM/backdrop APIs on the Win10 floor.
            return 0;
        }
    }
}
