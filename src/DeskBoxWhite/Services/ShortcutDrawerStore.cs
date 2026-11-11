using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBoxWhite.Helpers;
using DeskBoxWhite.Models;

namespace DeskBoxWhite.Services;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(
    typeof(ShortcutDrawerData),
    TypeInfoPropertyName = "StoreData")]
[JsonSerializable(
    typeof(ShortcutDrawerDefinition),
    TypeInfoPropertyName = "Definition")]
[JsonSerializable(
    typeof(ShortcutDrawerItem),
    TypeInfoPropertyName = "Item")]
internal sealed partial class ShortcutDrawerJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Resilient store for shortcut drawers.
/// </summary>
public sealed class ShortcutDrawerStore
{
    private const int CurrentVersion = 2;
    private const string DefaultDrawerName = "Default";
    private const string DefaultHotkeyChar = "D";
    private const string StoreFileName = "shortcut-drawer.json";

    /// <summary>Columns a drawer flows into when the caller has no width context.</summary>
    private const int FallbackColumns = 3;

    /// <summary>Smallest shell icon the drawer can show on a shortcut tile.</summary>
    public const int MinIconSize = 16;

    /// <summary>Largest shell icon the drawer can show on a shortcut tile.</summary>
    public const int MaxIconSize = 64;

    /// <summary>Smallest gap the drawer grid can use between cells.</summary>
    public const int MinSpacing = 0;

    /// <summary>Widest gap the drawer grid can use between cells.</summary>
    public const int MaxSpacing = 40;

    // ─── Drawer-wide defaults with per-drawer overrides ────────────────────

    /// <summary>
    /// Icon edge length for a drawer: the drawer's own value when it set one,
    /// otherwise the drawer-wide value. Mirrors the global hotkey fallback so a
    /// drawer added later still renders with a sane size.
    /// </summary>
    public static int ResolveIconSize(ShortcutDrawerData? data, ShortcutDrawerDefinition? drawer)
    {
        int shared = Math.Clamp(data?.IconSize ?? 32, MinIconSize, MaxIconSize);
        return drawer?.IconSize is null
            ? shared
            : Math.Clamp(drawer.IconSize.Value, MinIconSize, MaxIconSize);
    }

    /// <summary>Row gap for a drawer, with per-drawer override.</summary>
    public static int ResolveRowSpacing(ShortcutDrawerData? data, ShortcutDrawerDefinition? drawer)
    {
        int shared = Math.Clamp(data?.RowSpacing ?? 6, MinSpacing, MaxSpacing);
        return drawer?.RowSpacing is null
            ? shared
            : Math.Clamp(drawer.RowSpacing.Value, MinSpacing, MaxSpacing);
    }

    /// <summary>Column gap for a drawer, with per-drawer override.</summary>
    public static int ResolveColumnSpacing(ShortcutDrawerData? data, ShortcutDrawerDefinition? drawer)
    {
        int shared = Math.Clamp(data?.ColumnSpacing ?? 6, MinSpacing, MaxSpacing);
        return drawer?.ColumnSpacing is null
            ? shared
            : Math.Clamp(drawer.ColumnSpacing.Value, MinSpacing, MaxSpacing);
    }

    /// <summary>
    /// The popup frame a drawer remembers: its own when it has one, otherwise
    /// the drawer-wide one. Each drawer keeps its own position, so opening
    /// several at once never stacks them on the same coordinates.
    /// </summary>
    public static (int Left, int Top, int Width, int Height) ResolvePopupFrame(
        ShortcutDrawerData? data,
        ShortcutDrawerDefinition? drawer)
    {
        int left = drawer?.PopupLeft > 0 ? drawer.PopupLeft : data?.PopupLeft ?? 0;
        int top = drawer?.PopupTop > 0 ? drawer.PopupTop : data?.PopupTop ?? 0;
        int width = drawer?.PopupWidth > 0 ? drawer.PopupWidth : data?.PopupWidth ?? 0;
        int height = drawer?.PopupHeight > 0 ? drawer.PopupHeight : data?.PopupHeight ?? 0;
        return (
            Math.Max(left, 0),
            Math.Max(top, 0),
            Math.Max(width, 0),
            Math.Max(height, 0));
    }

    private readonly string _storePath;

    /// <summary>
    /// Root the drawer data lives in: the file-grid managed storage root plus the
    /// "shortcut-drawer" folder, so drawer shortcuts sit beside the user's files
    /// instead of inside the app-data folder. A caller that supplies its own
    /// directory (tests, migration) keeps using it.
    /// </summary>
    private readonly string _drawersRootPath;

    /// <summary>
    /// Uses the default location: the file-grid managed storage root plus the
    /// "shortcut-drawer" folder, with a legacy app-data folder folded in.
    /// </summary>
    public ShortcutDrawerStore()
        : this(GetDefaultStoreDirectory())
    {
    }

    /// <summary>
    /// The managed-storage root the drawer data lives under. Callers that have
    /// an app shell pass it in; everything else falls back to the documented
    /// default, so this store never reaches into the app object.
    /// </summary>
    public static string GetDefaultStoreDirectory(string? managedStorageRoot = null) =>
        Path.Combine(
            SettingsService.NormalizeManagedStorageRootPath(managedStorageRoot),
            "shortcut-drawer");

    /// <summary>
    /// One-shot move of the app-data drawer folder into the managed storage
    /// root. The contents are moved rather than the directory itself, because a
    /// whole-directory move is refused when the target's parent is protected or
    /// the source is still held open. A failure leaves the legacy data intact.
    /// </summary>
    private static void MigrateLegacyStoreDirectory(string legacy, string managed)
    {
        try
        {
            if (!Directory.Exists(legacy) ||
                (Directory.Exists(managed) && Directory.EnumerateFileSystemEntries(managed).Any()))
            {
                return;
            }

            Directory.CreateDirectory(managed);
            MoveDirectoryContents(legacy, managed);
            TryDeleteDirectory(legacy);
            App.Log($"[ShortcutDrawer] Moved drawer data from '{legacy}' to '{managed}'");
        }
        catch (Exception ex)
        {
            App.Log($"[ShortcutDrawer] Drawer data migration skipped: {ex.Message}");
        }
    }

    private static void MoveDirectoryContents(string source, string target)
    {
        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            string destination = Path.Combine(target, Path.GetFileName(directory));
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                string moved = Path.Combine(destination, Path.GetFileName(file));
                if (!File.Exists(moved))
                {
                    File.Move(file, moved);
                }
            }
        }

        foreach (string file in Directory.EnumerateFiles(source))
        {
            string destination = Path.Combine(target, Path.GetFileName(file));
            if (!File.Exists(destination))
            {
                File.Move(file, destination);
            }
        }
    }
    internal ShortcutDrawerStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _storePath = Path.Combine(dataDir, StoreFileName);
        _drawersRootPath = dataDir;

        // A legacy app-data drawer folder is folded into the store root once.
        string legacy = Path.Combine(
            DeskBoxWhiteDataPathService.Current.DataDirectory,
            "shortcut-drawer");
        MigrateLegacyStoreDirectory(legacy, dataDir);
    }

    internal string StorePath => _storePath;
    internal string DrawersRootPath => _drawersRootPath;

    public async Task<ShortcutDrawerData> LoadAsync()
    {
        var data = await ResilientJsonStore.LoadAsync(
            _storePath,
            json => NormalizeInRoot(this, JsonSerializer.Deserialize(
                json,
                ShortcutDrawerJsonContext.Default.StoreData)),
            () => NormalizeInRoot(this, null),
            nameof(ShortcutDrawerStore));
        await RecoverIdNamedDrawerFoldersAsync(data);
        return data;
    }

    /// <summary>
    /// Older drawer folders were named after the drawer id and lived in a nested
    /// "drawers" folder. Any drawer whose links are still there gets them pulled
    /// into its folder in the store root, so a rename or a path change can never
    /// strand the shortcuts.
    /// </summary>
    private async Task RecoverIdNamedDrawerFoldersAsync(ShortcutDrawerData data)
    {
        if (data?.Drawers is null || data.Drawers.Count == 0)
        {
            return;
        }

        string legacyRoot = Path.Combine(_drawersRootPath, "drawers");
        if (!Directory.Exists(legacyRoot))
        {
            return;
        }

        bool changed = false;
        foreach (var drawer in data.Drawers)
        {
            if (drawer is null)
            {
                continue;
            }

            string legacy = Path.Combine(legacyRoot, drawer.Id.ToString("N"));
            if (!Directory.Exists(legacy))
            {
                continue;
            }

            string target = GetDrawerFolderPath(drawer);
            foreach (string file in Directory.EnumerateFiles(legacy, "*.lnk"))
            {
                string destination = Path.Combine(target, Path.GetFileName(file));
                if (!File.Exists(destination))
                {
                    File.Move(file, destination);
                    changed = true;
                }
            }

            TryDeleteDirectory(legacy);
        }

        if (!Directory.EnumerateFileSystemEntries(legacyRoot).Any())
        {
            TryDeleteDirectory(legacyRoot);
        }

        if (!changed)
        {
            return;
        }

        await SaveAsync(data);
        App.Log("[ShortcutDrawer] Recovered shortcuts from the legacy id-named folders");
    }

    public Task SaveAsync(ShortcutDrawerData data)
    {
        data = NormalizeInRoot(this, data);
        return ResilientJsonStore.SaveAsync(
            _storePath,
            async tempPath =>
            {
                await using var stream = new FileStream(
                    tempPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None);
                await JsonSerializer.SerializeAsync(
                    stream,
                    data,
                    ShortcutDrawerJsonContext.Default.StoreData);
            });
    }

    /// <summary>
    /// Filesystem-safe form of a drawer name, so a drawer folder always reads
    /// like the drawer it belongs to instead of a GUID.
    /// </summary>
    public static string SanitizeFolderName(string? name)
    {
        string candidate = (name ?? string.Empty).Trim();
        var builder = new System.Text.StringBuilder(candidate.Length);
        foreach (char character in candidate)
        {
            if (character is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ||
                char.IsControl(character))
            {
                continue;
            }

            builder.Append(character);
        }

        string cleaned = builder.ToString().Trim().TrimEnd('.');
        if (cleaned.Length > 40)
        {
            cleaned = cleaned[..40].Trim().TrimEnd('.');
        }

        return cleaned.Length == 0 ? DefaultDrawerName : cleaned;
    }

    /// <summary>
    /// Renames a drawer and moves its folder to the new name, links included.
    /// </summary>
    public async Task<bool> RenameDrawerAsync(
        ShortcutDrawerData data,
        ShortcutDrawerDefinition drawer,
        string drawerName)
    {
        string renamed = (drawerName ?? string.Empty).Trim();
        if (data is null || drawer is null || renamed.Length == 0)
        {
            return false;
        }

        string source = GetDrawerFolderPath(drawer);
        string target = Path.Combine(_drawersRootPath, SanitizeFolderName(renamed));
        bool moved = false;
        if (!string.Equals(
                Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase) &&
            !Directory.Exists(target))
        {
            try
            {
                Directory.Move(source, target);
                moved = true;
            }
            catch (Exception ex)
            {
                App.Log($"[ShortcutDrawer] Renaming the drawer folder to '{target}' failed: {ex.Message}");
            }
        }

        drawer.Name = renamed;
        await SaveAsync(data);
        return moved;
    }

    /// <summary>
    /// A drawer's folder inside the store root, always named after the drawer.
    /// Drawers no longer carry their own directory: one predictable place.
    /// </summary>
    public string GetDrawerFolderPath(ShortcutDrawerDefinition drawer)
    {
        if (drawer is null)
        {
            throw new ArgumentNullException(nameof(drawer));
        }

        string path = Path.Combine(_drawersRootPath, SanitizeFolderName(drawer.Name));
        Directory.CreateDirectory(path);
        return path;
    }

    public async Task SyncDrawerFolderAsync(ShortcutDrawerDefinition drawer, int columns = 0)
    {
        int columnCount = Math.Max(1, columns == 0 ? FallbackColumns : columns);
        if (drawer is null)
        {
            return;
        }

        string folder = GetDrawerFolderPath(drawer);
        drawer.Items ??= [];
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in drawer.Items.ToArray())
        {
            if (string.IsNullOrWhiteSpace(item.FileName))
            {
                drawer.Items.Remove(item);
                continue;
            }
            string full = Path.Combine(folder, item.FileName.Trim());
            if (!File.Exists(full))
            {
                drawer.Items.Remove(item);
                continue;
            }
            existing.Add(Path.GetFileName(full));
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*.lnk", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(file);
            if (!existing.Contains(name))
            {
                AllocateNextPosition(drawer, columnCount, out int col, out int row);
                drawer.Items.Add(new ShortcutDrawerItem { FileName = name, Column = col, Row = row });
            }
        }
    }

    /// <summary>
    /// First free grid cell in row-major order, bounded by the drawer's column
    /// count. A single-column drawer therefore keeps stacking downwards, while a
    /// wider drawer fills left to right first.
    /// </summary>
    public void AllocateNextPosition(ShortcutDrawerDefinition drawer, int columns, out int column, out int row)
    {
        column = 1;
        row = 1;
        if (drawer?.Items is null || drawer.Items.Count == 0)
        {
            return;
        }

        int columnCount = Math.Max(1, columns == 0 ? FallbackColumns : columns);
        var occupied = new HashSet<(int, int)>();
        int maxRow = 1;
        foreach (var item in drawer.Items)
        {
            occupied.Add((item.Column, item.Row));
            maxRow = Math.Max(maxRow, item.Row);
        }

        for (int candidateRow = 1; candidateRow <= maxRow + 1; candidateRow++)
        {
            for (int candidateColumn = 1; candidateColumn <= columnCount; candidateColumn++)
            {
                if (!occupied.Contains((candidateColumn, candidateRow)))
                {
                    column = candidateColumn;
                    row = candidateRow;
                    return;
                }
            }
        }
    }

    public async Task CreateShortcutFromPathAsync(
        ShortcutDrawerDefinition drawer,
        string sourcePath,
        int columns = 0)
    {
        if (drawer is null || string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath) && !Directory.Exists(sourcePath))
        {
            return;
        }

        string folder = GetDrawerFolderPath(drawer);
        string name = Path.GetFileName(sourcePath);
        string ext = Path.GetExtension(name);
        if (!ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            name = Path.GetFileNameWithoutExtension(name) + ".lnk";
        }
        string shortcutPath = Path.Combine(folder, name);

        int i = 1;
        while (File.Exists(shortcutPath))
        {
            name = $"{Path.GetFileNameWithoutExtension(name)}_{i}.lnk";
            shortcutPath = Path.Combine(folder, name);
            i++;
        }

        ShortcutHelper.CreateOrUpdateShortcut(shortcutPath, sourcePath, name);
        await SyncDrawerFolderAsync(drawer, columns);
    }

    /// <summary>
    /// Repacks a drawer's shortcuts into a compact grid, in their current
    /// reading order. This is the automatic layout: positions follow the files,
    /// so a fresh drawer needs no manual placement, and a drawer that grew messy
    /// can be tidied in one step.
    /// </summary>
    public async Task AutoArrangeDrawerAsync(
        ShortcutDrawerData data,
        ShortcutDrawerDefinition drawer,
        int columns)
    {
        if (data is null || drawer?.Items is null || drawer.Items.Count == 0)
        {
            return;
        }

        int columnCount = Math.Max(1, columns == 0 ? FallbackColumns : columns);
        var ordered = drawer.Items
            .OrderBy(item => item.Row)
            .ThenBy(item => item.Column)
            .ToList();

        for (int index = 0; index < ordered.Count; index++)
        {
            ordered[index].Column = (index % columnCount) + 1;
            ordered[index].Row = (index / columnCount) + 1;
        }

        await SaveAsync(data);
    }

    /// <summary>
    /// Deletes a drawer's shortcut: removes the .lnk from the drawer folder and
    /// drops the matching entry. The link file carries no user data beyond its
    /// target path, so removing it is the drawer's own undoable surface.
    /// </summary>
    public async Task<bool> RemoveShortcutAsync(
        ShortcutDrawerData data,
        ShortcutDrawerDefinition drawer,
        string fileName)
    {
        if (data is null || drawer is null || string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        string name = Path.GetFileName(fileName.Trim());
        string folder = GetDrawerFolderPath(drawer);
        string target = Path.Combine(folder, name);
        if (!IsInsideFolder(target, folder))
        {
            return false;
        }

        bool removedFromList = drawer.Items.RemoveAll(item =>
            string.Equals(item.FileName, name, StringComparison.OrdinalIgnoreCase)) > 0;

        if (File.Exists(target))
        {
            File.Delete(target);
        }

        await SaveAsync(data);
        return removedFromList;
    }

    /// <summary>Removes several shortcuts at once (marquee selection).</summary>
    public async Task<int> RemoveShortcutsAsync(
        ShortcutDrawerData data,
        ShortcutDrawerDefinition drawer,
        IEnumerable<string> fileNames)
    {
        int removed = 0;
        foreach (string fileName in fileNames.ToArray())
        {
            if (await RemoveShortcutAsync(data, drawer, fileName))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// Moves a drawer's shortcuts into a folder the user picks, taking the links
    /// out of the drawer. Moving is the point: the shortcuts live in one place.
    /// </summary>
    public async Task<int> MoveShortcutsAsync(
        ShortcutDrawerData data,
        ShortcutDrawerDefinition drawer,
        IEnumerable<string> fileNames,
        string targetFolder)
    {
        if (data is null || drawer is null || string.IsNullOrWhiteSpace(targetFolder))
        {
            return 0;
        }

        string folder = GetDrawerFolderPath(drawer);
        Directory.CreateDirectory(targetFolder);
        int moved = 0;
        foreach (string rawName in fileNames.ToArray())
        {
            string name = Path.GetFileName(rawName.Trim());
            string source = Path.Combine(folder, name);
            string destination = Path.Combine(targetFolder, name);
            if (!File.Exists(source) || !IsInsideFolder(source, folder) ||
                !IsInsideFolder(destination, targetFolder))
            {
                continue;
            }

            try
            {
                File.Move(source, destination, overwrite: false);
                moved++;
            }
            catch (Exception ex)
            {
                App.Log($"[ShortcutDrawer] Moving '{source}' into '{targetFolder}' failed: {ex.Message}");
            }
        }

        if (moved > 0)
        {
            await SyncDrawerFolderAsync(drawer, columns: 0);
            await SaveAsync(data);
        }

        return moved;
    }
    /// <summary>
    /// Moves shortcuts from one drawer into another, links included. The links
    /// are relocated into the target drawer's folder, so a shortcut belongs to
    /// exactly one drawer at a time.
    /// </summary>
    public async Task<int> MoveShortcutsToDrawerAsync(
        ShortcutDrawerData data,
        ShortcutDrawerDefinition source,
        ShortcutDrawerDefinition target,
        IEnumerable<string> fileNames,
        int columns = 0)
    {
        if (data is null || source is null || target is null || ReferenceEquals(source, target))
        {
            return 0;
        }

        string sourceFolder = GetDrawerFolderPath(source);
        string targetFolder = GetDrawerFolderPath(target);
        int moved = 0;

        foreach (string rawName in fileNames.ToArray())
        {
            string name = Path.GetFileName(rawName.Trim());
            string from = Path.Combine(sourceFolder, name);
            if (!File.Exists(from) || !IsInsideFolder(from, sourceFolder))
            {
                continue;
            }

            string destination = Path.Combine(targetFolder, name);
            int suffix = 1;
            while (File.Exists(destination))
            {
                destination = Path.Combine(
                    targetFolder,
                    $"{Path.GetFileNameWithoutExtension(name)}_{suffix}{Path.GetExtension(name)}");
                suffix++;
            }

            try
            {
                File.Move(from, destination);
                moved++;
            }
            catch (Exception ex)
            {
                App.Log($"[ShortcutDrawer] Moving '{from}' into drawer '{target.Name}' failed: {ex.Message}");
            }
        }

        if (moved == 0)
        {
            return 0;
        }

        await SyncDrawerFolderAsync(source, columns);
        await SyncDrawerFolderAsync(target, columns);
        await SaveAsync(data);
        return moved;
    }

    private static bool IsInsideFolder(string candidate, string folder)
    {
        string normalized = Path.GetFullPath(folder)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFullPath(candidate).StartsWith(
            normalized + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pulls the links out of a folder a drawer used to own into its folder in
    /// the store root, so nothing is left behind in the old location.
    /// </summary>
    private void MigrateLegacyDrawerFolder(ShortcutDrawerDefinition drawer)
    {
        try
        {
            string legacy = drawer.FolderPath.Trim();
            string target = Path.Combine(_drawersRootPath, SanitizeFolderName(drawer.Name));
            if (!Directory.Exists(legacy) ||
                string.Equals(
                    Path.GetFullPath(legacy).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Directory.CreateDirectory(target);
            foreach (string file in Directory.EnumerateFiles(legacy, "*.lnk"))
            {
                string destination = Path.Combine(target, Path.GetFileName(file));
                if (!File.Exists(destination))
                {
                    File.Move(file, destination);
                }
            }

            App.Log($"[ShortcutDrawer] Merged '{legacy}' into '{target}'");
        }
        catch (Exception ex)
        {
            App.Log($"[ShortcutDrawer] Legacy drawer folder merge skipped: {ex.Message}");
        }
    }

    public async Task PackDrawersAsync(string outputZipPath)
    {
        var data = await LoadAsync();
        string tempDir = Path.Combine(Path.GetTempPath(), "DeskBoxWhite-drawer-pack-" + Guid.NewGuid().ToString("N"));
        try
        {
            var tempStore = new ShortcutDrawerStore(tempDir);
            await tempStore.SaveAsync(data);

            await using var zip = new ZipArchive(
                File.Create(outputZipPath),
                ZipArchiveMode.Create);
            zip.CreateEntryFromFile(tempStore.StorePath, StoreFileName);

            foreach (var drawer in data.Drawers)
            {
                string folder = GetDrawerFolderPath(drawer);
                if (!Directory.Exists(folder))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(folder, "*.lnk"))
                {
                    zip.CreateEntryFromFile(
                        file,
                        Path.Combine(drawer.Id.ToString("N"), Path.GetFileName(file)));
                }
            }
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    public async Task ImportPackAsync(string zipPath)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "DeskBoxWhite-drawer-import-" + Guid.NewGuid().ToString("N"));
        ShortcutDrawerData? data;
        try
        {
            var tempStore = new ShortcutDrawerStore(tempDir);
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var dataEntry = zip.GetEntry(StoreFileName);
                if (dataEntry is null)
                {
                    return;
                }

                dataEntry.ExtractToFile(tempStore.StorePath, overwrite: true);
            }

            data = await tempStore.LoadAsync();
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }

        if (data is null)
        {
            return;
        }

        await SaveAsync(data);

        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var drawer in data.Drawers)
            {
                string folder = GetDrawerFolderPath(drawer);
                Directory.CreateDirectory(folder);
                string prefix = drawer.Id.ToString("N");
                foreach (var entry in zip.Entries.Where(e =>
                    e.FullName.StartsWith(prefix + "/", StringComparison.Ordinal) ||
                    e.FullName.StartsWith(prefix + "\\", StringComparison.Ordinal)))
                {
                    string dest = Path.Combine(folder, Path.GetFileName(entry.FullName));
                    entry.ExtractToFile(dest, overwrite: true);
                }
            }
        }

        foreach (var drawer in data.Drawers)
        {
            await SyncDrawerFolderAsync(drawer);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[ShortcutDrawer] Failed to delete temp directory '{path}': {ex.Message}");
        }
    }

    private static ShortcutDrawerData NormalizeInRoot(ShortcutDrawerStore store, ShortcutDrawerData? data)
    {
        data ??= new ShortcutDrawerData();
        data.Version = CurrentVersion;
        data.Drawers ??= [];
        if (data.GlobalHotkeyKey < 0)
        {
            data.GlobalHotkeyKey = 0x44;
        }
        data.PopupWidth = data.PopupWidth < 0 ? 0 : data.PopupWidth;
        data.PopupHeight = data.PopupHeight < 0 ? 0 : data.PopupHeight;
        if (data.LastOpenedDrawerId == Guid.Empty)
        {
            // The cycle starts at the first drawer.
            data.LastOpenedDrawerId = data.Drawers.FirstOrDefault()?.Id ?? Guid.Empty;
        }
        data.IconSize = Math.Clamp(data.IconSize, MinIconSize, MaxIconSize);
        data.RowSpacing = Math.Clamp(data.RowSpacing, MinSpacing, MaxSpacing);
        data.ColumnSpacing = Math.Clamp(data.ColumnSpacing, MinSpacing, MaxSpacing);

        var list = new List<ShortcutDrawerDefinition>();
        foreach (var drawer in data.Drawers)
        {
            if (drawer is null)
            {
                continue;
            }

            drawer.Id = drawer.Id == Guid.Empty ? Guid.NewGuid() : drawer.Id;
            drawer.Name = (drawer.Name ?? string.Empty).Trim();
            if (drawer.Name.Length == 0)
            {
                drawer.Name = DefaultDrawerName;
            }

            // A drawer used to carry its own folder. Every drawer now lives in
            // the store root under its name, so any old path is dropped.
            if (!string.IsNullOrWhiteSpace(drawer.FolderPath))
            {
                store.MigrateLegacyDrawerFolder(drawer);
                drawer.FolderPath = string.Empty;
            }

            drawer.Items ??= [];

            var items = new List<ShortcutDrawerItem>();
            foreach (var item in drawer.Items)
            {
                if (item is null || string.IsNullOrWhiteSpace(item.FileName))
                {
                    continue;
                }
                items.Add(new ShortcutDrawerItem
                {
                    FileName = item.FileName.Trim(),
                    Column = item.Column < 1 ? 1 : item.Column,
                    Row = item.Row < 1 ? 1 : item.Row,
                });
            }

            // Deduplicate same file name
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dedup = new List<ShortcutDrawerItem>();
            foreach (var item in items)
            {
                if (seen.Add(item.FileName))
                {
                    dedup.Add(item);
                }
            }
            drawer.Items = dedup;

            drawer.IconSize = drawer.IconSize is null
                ? null
                : Math.Clamp(drawer.IconSize.Value, MinIconSize, MaxIconSize);
            drawer.RowSpacing = drawer.RowSpacing is null
                ? null
                : Math.Clamp(drawer.RowSpacing.Value, MinSpacing, MaxSpacing);
            drawer.ColumnSpacing = drawer.ColumnSpacing is null
                ? null
                : Math.Clamp(drawer.ColumnSpacing.Value, MinSpacing, MaxSpacing);
            drawer.PopupWidth = drawer.PopupWidth < 0 ? 0 : drawer.PopupWidth;
            drawer.PopupHeight = drawer.PopupHeight < 0 ? 0 : drawer.PopupHeight;
            drawer.PopupLeft = drawer.PopupLeft < 0 ? 0 : drawer.PopupLeft;
            drawer.PopupTop = drawer.PopupTop < 0 ? 0 : drawer.PopupTop;
            list.Add(drawer);
        }

        if (list.Count == 0)
        {
            list.Add(new ShortcutDrawerDefinition
            {
                Id = Guid.NewGuid(),
                Name = DefaultDrawerName,
                FolderPath = string.Empty,
                HotkeyEnabled = false,
                HotkeyModifiers = (int)(HotkeyModifierKeys.Control | HotkeyModifierKeys.Alt),
                HotkeyKey = 0x44,
                ShowPositionBadges = null,
                Items = new(),
            });
        }

        data.Drawers = list;
        return data;
    }
}
