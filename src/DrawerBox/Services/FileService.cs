using DeskBoxWhite.Helpers;
using DeskBoxWhite.Models;
using DeskBoxWhite.Platform;

namespace DeskBoxWhite.Services;

/// <summary>
/// DrawerBox-only subset of the full FileService: launching a drawer item
/// (shortcut, folder or file) off the UI thread, plus the .lnk target read used
/// by the drawer's "open target folder" menu item.
/// </summary>
public sealed partial class FileService
{
    private static readonly BoundedStaOperationRunner s_openItemRunner =
        new(maxConcurrency: 2, maxQueued: 6, queueTimeout: TimeSpan.FromSeconds(2));

    public enum OpenItemResult
    {
        OpenedOrHandled,

        /// <summary>
        /// The shortcut's stored target is missing, so the link was handed to
        /// Windows instead of being launched. Nothing was opened here, which is
        /// why this is not folded into <see cref="OpenedOrHandled"/>.
        /// </summary>
        ShortcutTargetMissing,
        ShortcutDeleted,
        Busy,
        RequiresOpenWithPicker,
        Failed
    }

    /// <summary>
    /// Opens an item without running filesystem or Shell work on the caller's
    /// UI thread. Mirrors the full app's dispatch pipeline for the kinds the
    /// drawer holds: shell-dispatch by default, the folder default verb for
    /// directory shortcuts, and the shell link-resolution UI for broken links.
    /// </summary>
    public static async Task<OpenItemResult> OpenItemAsync(
        WidgetItem item,
        IntPtr ownerHwnd,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        StaOperationResult<OpenItemResult> operation = await s_openItemRunner.RunAsync(
            () => OpenItemCore(item.Path, item.TargetPath, item.IsShortcut, ownerHwnd),
            cancellationToken).ConfigureAwait(false);
        if (!operation.Started)
        {
            return OpenItemResult.Busy;
        }

        return operation.Value;
    }

    private static OpenItemResult OpenItemCore(
        string itemPath,
        string targetPath,
        bool isShortcut,
        IntPtr ownerHwnd)
    {
        try
        {
            bool shellLink = ShortcutHelper.IsShellLinkPath(itemPath);
            if (shellLink && string.IsNullOrWhiteSpace(targetPath))
            {
                targetPath = ShortcutHelper.ReadStoredMetadata(itemPath)?.TargetPath ??
                    string.Empty;
            }

            ShortcutTargetProbeResult shortcutProbe = shellLink
                ? ShortcutTargetProbe.Probe(itemPath, targetPath)
                : default;
            if (shellLink && shortcutProbe.IsBroken)
            {
                // Windows resolves links by tracking the file, so a shortcut
                // whose stored path moved can still open from Explorer. Hand
                // the link to the shell; report the launch separately.
                BrokenShortcutResolution resolution =
                    ShortcutHelper.ResolveBrokenShortcutWithShellUi(itemPath, ownerHwnd);
                return resolution == BrokenShortcutResolution.ShortcutDeleted
                    ? OpenItemResult.ShortcutDeleted
                    : OpenItemResult.ShortcutTargetMissing;
            }

            string pathToOpen = isShortcut ? itemPath : targetPath;
            if (string.IsNullOrEmpty(pathToOpen))
            {
                return OpenItemResult.Failed;
            }

            if (!Win32Helper.HasShellOpenAssociation(pathToOpen))
            {
                // Both Explorer's dispatch and a local ShellExecuteEx report a
                // dismissed picker as a silent success. The drawer keeps the
                // pipeline result without a picker (the full app's picker is a
                // file-widget surface).
                return OpenItemResult.RequiresOpenWithPicker;
            }

            // Folder shortcuts use the Shell default verb so third-party file
            // managers register as the Folder handler (#459). Everything else
            // keeps the explorer-first dispatch.
            bool folderShortcutDefaultVerb =
                isShortcut &&
                shellLink &&
                shortcutProbe.Kind == ShortcutTargetKind.LocalFileSystem &&
                shortcutProbe.TargetIsDirectory;
            bool launched = folderShortcutDefaultVerb
                ? Win32Helper.OpenWithDefaultVerbLocally(
                    pathToOpen,
                    Win32Helper.ResolveShellLaunchDirectory(pathToOpen))
                : Win32Helper.OpenFile(ownerHwnd, pathToOpen);
            return launched
                ? OpenItemResult.OpenedOrHandled
                : OpenItemResult.Failed;
        }
        catch (Exception ex)
        {
            App.Log($"[DrawerBox] Open item failed path='{itemPath}': {ex.Message}");
            return OpenItemResult.Failed;
        }
    }

    /// <summary>
    /// Reads the stored target of a .lnk file. A thin direct call into the
    /// shortcut helper; the drawer only waits for this single fast COM read.
    /// </summary>
    public async Task<string> GetStoredShortcutTargetAsync(string shortcutPath)
    {
        try
        {
            return await Task.Run(
                () => ShortcutHelper.ReadStoredMetadata(shortcutPath)?.TargetPath ??
                    string.Empty).ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }
}
