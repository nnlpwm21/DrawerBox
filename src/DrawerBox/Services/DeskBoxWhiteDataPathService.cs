using System.Security.Cryptography;
using System.Text;

namespace DeskBoxWhite.Services;

public sealed class DeskBoxWhiteDataPathService
{
    public const string DevelopmentRootEnvironmentVariable = "DESKBOXWHITE_DEV_DATA_ROOT";
    public const string AotPreviewRootEnvironmentVariable = "DESKBOXWHITE_AOT_PREVIEW_DATA_ROOT";
    private const string ProductionInstanceScope = "7F3A9B2E";

    public static DeskBoxWhiteDataPathService Current { get; } = new(ResolveConfiguredRoot());

    public DeskBoxWhiteDataPathService(string? rootPath = null)
    {
        string productionRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeskBoxWhite"));
        RootPath = string.IsNullOrWhiteSpace(rootPath)
            ? productionRoot
            : Path.GetFullPath(rootPath.Trim());
        IsDevelopmentRoot = !string.Equals(
            RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            productionRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
        InstanceScope = IsDevelopmentRoot
            ? CreateInstanceScope(RootPath)
            : ProductionInstanceScope;
    }

    public string RootPath { get; }
    public bool IsDevelopmentRoot { get; }
    public string InstanceScope { get; }
    public string ActivationEventName => $"DeskBoxWhite_Activate_Event_{InstanceScope}";
    public string SingleInstanceMutexName => $"DeskBoxWhite_SingleInstance_Mutex_{InstanceScope}";
    public string DataDirectory => Path.Combine(RootPath, "data");
    public string UpdatesDirectory => Path.Combine(RootPath, "updates");
    // Recovery snapshots intentionally live beside, rather than inside, the
    // app-data root. A normal uninstall can therefore never erase the only
    // automatic recovery copy together with settings and widget layouts.
    public string RecoveryDirectory => IsDevelopmentRoot
        ? $"{RootPath}-Recovery"
        : Path.Combine(
            Path.GetDirectoryName(RootPath) ?? RootPath,
            "DeskBoxWhite-Recovery");
    public string LogFilePath => Path.Combine(RootPath, "DeskBoxWhite.log");

    private static string? ResolveConfiguredRoot()
    {
#if DEBUG
        return Environment.GetEnvironmentVariable(DevelopmentRootEnvironmentVariable);
#elif DESKBOXWHITE_NATIVE_AOT
        return Environment.GetEnvironmentVariable(AotPreviewRootEnvironmentVariable);
#else
        return null;
#endif
    }

    private static string CreateInstanceScope(string rootPath)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(rootPath.ToUpperInvariant()));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }
}
