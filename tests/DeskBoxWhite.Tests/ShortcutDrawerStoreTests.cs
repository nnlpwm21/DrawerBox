using DeskBoxWhite.Models;
using DeskBoxWhite.Services;
using Xunit;

namespace DeskBoxWhite.Tests;

/// <summary>
/// Local-layer domain contract for <see cref="ShortcutDrawerStore"/>: drawers
/// live in their own JSON file, loads fail-closed on corruption, drawer order is
/// preserved, items are deduplicated by file name, and absolute grid coordinates
/// are clamped to a valid 1-based range.
/// </summary>
public sealed class ShortcutDrawerStoreTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), "deskboxwhite-shortcut-drawer-store-tests", Guid.NewGuid().ToString("N"));

    private string StorePath =>
        Path.Combine(_tempRoot, "shortcut-drawer.json");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task SaveLoad_RoundTripsDrawersItemsAndCoordinates()
    {
        var store = new ShortcutDrawerStore(_tempRoot);
        var data = new ShortcutDrawerData
        {
            Drawers =
            [
                new ShortcutDrawerDefinition
                {
                    Name = "Tools",
                    Items =
                    [
                        new ShortcutDrawerItem { FileName = "b.lnk", Column = 2, Row = 1 },
                        new ShortcutDrawerItem { FileName = "a.lnk", Column = 1, Row = 2 },
                    ],
                },
                new ShortcutDrawerDefinition
                {
                    Name = "Docs",
                    Items = [new ShortcutDrawerItem { FileName = "d.docx", Column = 1, Row = 1 }],
                },
            ],
        };
        await store.SaveAsync(data);

        var reloaded = new ShortcutDrawerStore(_tempRoot);
        ShortcutDrawerData loaded = await reloaded.LoadAsync();

        Assert.Equal(2, loaded.Version);
        Assert.Equal(2, loaded.Drawers.Count);
        Assert.Equal("Tools", loaded.Drawers[0].Name);
        // Drawers no longer carry their own directory.
        Assert.Equal(string.Empty, loaded.Drawers[0].FolderPath);
        Assert.Equal("Docs", loaded.Drawers[1].Name);
        Assert.Equal(
            new[] { "b.lnk", "a.lnk" },
            loaded.Drawers[0].Items.Select(item => item.FileName));
        Assert.Equal(
            new[] { (2, 1), (1, 2) },
            loaded.Drawers[0].Items.Select(item => (item.Column, item.Row)));
    }

    [Fact]
    public async Task LoadAsync_MissingFile_ReturnsDefaultDrawer()
    {
        var store = new ShortcutDrawerStore(_tempRoot);
        ShortcutDrawerData loaded = await store.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Version);
        Assert.True(loaded.ShowPositionBadges);
        var drawer = Assert.Single(loaded.Drawers);
        Assert.Equal("Default", drawer.Name);
        Assert.Empty(drawer.Items);
    }

    [Fact]
    public void StorePath_LivesUnderDataDirectory()
    {
        var store = new ShortcutDrawerStore(_tempRoot);
        Assert.Equal(StorePath, store.StorePath);
    }

    [Fact]
    public async Task LoadAsync_CorruptFile_FailsClosedToDefaultDrawer()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        await File.WriteAllTextAsync(StorePath, "{ nope");

        var store = new ShortcutDrawerStore(_tempRoot);
        ShortcutDrawerData loaded = await store.LoadAsync();

        Assert.Single(loaded.Drawers);
        Assert.Equal("Default", loaded.Drawers[0].Name);
    }

    [Fact]
    public async Task Normalize_DeduplicatesItemsByFileNameClampsCoordsAndTrims()
    {
        var store = new ShortcutDrawerStore(_tempRoot);
        var data = new ShortcutDrawerData
        {
            Drawers =
            [
                new ShortcutDrawerDefinition
                {
                    Name = "  Mixed  ",
                    Items =
                    [
                        new ShortcutDrawerItem { FileName = @"same.txt", Column = 0, Row = -3 },
                        new ShortcutDrawerItem { FileName = @"SAME.txt", Column = 5, Row = 5 },
                        new ShortcutDrawerItem { FileName = "   " },
                        new ShortcutDrawerItem { FileName = @"other.txt", Column = 3, Row = 2 },
                    ],
                },
            ],
        };
        await store.SaveAsync(data);

        var reloaded = new ShortcutDrawerStore(_tempRoot);
        ShortcutDrawerData loaded = await reloaded.LoadAsync();

        // The case-insensitive duplicate collapses to the first occurrence and
        // invalid coordinates are clamped to 1.
        Assert.Equal("Mixed", loaded.Drawers[0].Name);
        Assert.Equal(
            new[] { "same.txt", "other.txt" },
            loaded.Drawers[0].Items.Select(item => item.FileName));
        Assert.Equal(
            new[] { (1, 1), (3, 2) },
            loaded.Drawers[0].Items.Select(item => (item.Column, item.Row)));
    }

    [Fact]
    public async Task Normalize_PreservesDrawerOrderNeverSortsByName()
    {
        var store = new ShortcutDrawerStore(_tempRoot);
        var data = new ShortcutDrawerData
        {
            Drawers =
            [
                new ShortcutDrawerDefinition { Name = "Zebra" },
                new ShortcutDrawerDefinition { Name = "Alpha" },
                new ShortcutDrawerDefinition { Name = "Middle" },
            ],
        };
        await store.SaveAsync(data);

        var reloaded = new ShortcutDrawerStore(_tempRoot);
        ShortcutDrawerData loaded = await reloaded.LoadAsync();

        Assert.Equal(
            new[] { "Zebra", "Alpha", "Middle" },
            loaded.Drawers.Select(drawer => drawer.Name));
    }

    [Fact]
    public async Task Normalize_DropsNullDrawersOnLoad()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        await File.WriteAllTextAsync(StorePath,
            "{\"version\":2,\"drawers\":[{\"name\":\"Keep\",\"items\":[]},null,{\"name\":\"Drop\",\"items\":[]}]}");

        var store = new ShortcutDrawerStore(_tempRoot);
        ShortcutDrawerData loaded = await store.LoadAsync();

        Assert.Equal(
            new[] { "Keep", "Drop" },
            loaded.Drawers.Select(drawer => drawer.Name));
    }

    [Fact]
    public async Task SaveLoad_EmptyDrawerItemList_RoundTripsCleanly()
    {
        var store = new ShortcutDrawerStore(_tempRoot);
        await store.SaveAsync(new ShortcutDrawerData
        {
            Drawers = [new ShortcutDrawerDefinition { Name = "Empty" }],
        });

        var reloaded = new ShortcutDrawerStore(_tempRoot);
        ShortcutDrawerData loaded = await reloaded.LoadAsync();

        var drawer = Assert.Single(loaded.Drawers);
        Assert.Equal("Empty", drawer.Name);
        Assert.Empty(drawer.Items);
    }

    [Fact]
    public async Task SyncDrawerFolder_ImportsExistingLinksAndDropsMissingItems()
    {
        var store = new ShortcutDrawerStore(_tempRoot);
        var drawer = new ShortcutDrawerDefinition { Name = "Sync" };
        string folder = store.GetDrawerFolderPath(drawer);
        File.WriteAllText(Path.Combine(folder, "kept.lnk"), "placeholder");
        File.WriteAllText(Path.Combine(folder, "not-a-shortcut.txt"), "ignored");
        drawer.Items.Add(new ShortcutDrawerItem { FileName = "missing.lnk", Column = 1, Row = 1 });

        await store.SyncDrawerFolderAsync(drawer);

        Assert.Equal(new[] { "kept.lnk" }, drawer.Items.Select(item => item.FileName));
    }

    [Fact]
    public void AllocateNextPosition_FillsTheFirstFreeCellInReadingOrder()
    {
        var store = new ShortcutDrawerStore(_tempRoot);
        var drawer = new ShortcutDrawerDefinition
        {
            Items =
            [
                new ShortcutDrawerItem { FileName = "a.lnk", Column = 1, Row = 1 },
                new ShortcutDrawerItem { FileName = "b.lnk", Column = 1, Row = 2 },
                new ShortcutDrawerItem { FileName = "c.lnk", Column = 2, Row = 1 },
            ],
        };

        // With three columns allowed the gap at 3.1 wins over starting a new row.
        store.AllocateNextPosition(drawer, 3, out int column, out int row);

        Assert.Equal((3, 1), (column, row));
    }

    [Fact]
    public async Task GlobalHotkey_RoundTripsAndKeepsSaneDefaults()
    {
        var store = new ShortcutDrawerStore(_tempRoot);

        ShortcutDrawerData fresh = await store.LoadAsync();
        Assert.True(fresh.GlobalHotkeyEnabled);
        Assert.Equal((int)(HotkeyModifierKeys.Control | HotkeyModifierKeys.Alt), fresh.GlobalHotkeyModifiers);
        Assert.Equal(0x44, fresh.GlobalHotkeyKey);

        fresh.GlobalHotkeyEnabled = false;
        fresh.GlobalHotkeyModifiers = (int)(HotkeyModifierKeys.Control | HotkeyModifierKeys.Shift);
        fresh.GlobalHotkeyKey = 0x4B;
        await store.SaveAsync(fresh);

        ShortcutDrawerData reloaded = await new ShortcutDrawerStore(_tempRoot).LoadAsync();
        Assert.False(reloaded.GlobalHotkeyEnabled);
        Assert.Equal((int)(HotkeyModifierKeys.Control | HotkeyModifierKeys.Shift), reloaded.GlobalHotkeyModifiers);
        Assert.Equal(0x4B, reloaded.GlobalHotkeyKey);
    }

    [Fact]
    public async Task GlobalHotkey_AnInvalidKeyFallsBackToTheDefault()
    {
        var store = new ShortcutDrawerStore(_tempRoot);
        var data = new ShortcutDrawerData { GlobalHotkeyKey = -5 };
        await store.SaveAsync(data);

        ShortcutDrawerData reloaded = await store.LoadAsync();
        Assert.Equal(0x44, reloaded.GlobalHotkeyKey);
    }

    [Fact]
    public async Task AllocateNextPosition_EmptyDrawer_ReturnsFirstCell()
    {
        var store = new ShortcutDrawerStore(_tempRoot);
        store.AllocateNextPosition(new ShortcutDrawerDefinition(), 3, out int column, out int row);
        Assert.Equal((1, 1), (column, row));
    }

    [Fact]
    public async Task CreateShortcutFromPath_WritesLinkAndRegistersItem()
    {
        var store = new ShortcutDrawerStore(_tempRoot);
        var drawer = new ShortcutDrawerDefinition { Name = "Apps" };
        string source = Path.Combine(_tempRoot, "source", "launcher.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "binary");

        await store.CreateShortcutFromPathAsync(drawer, source);

        string folder = store.GetDrawerFolderPath(drawer);
        Assert.True(File.Exists(Path.Combine(folder, "launcher.lnk")));
        var item = Assert.Single(drawer.Items);
        Assert.Equal("launcher.lnk", item.FileName);
        Assert.Equal(1, item.Column);
        Assert.Equal(1, item.Row);
    }

    [Fact]
    public async Task CreateShortcutFromPath_AvoidsNameCollision()
    {
        var store = new ShortcutDrawerStore(_tempRoot);
        var drawer = new ShortcutDrawerDefinition { Name = "Apps" };
        string source = Path.Combine(_tempRoot, "source", "launcher.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "binary");

        await store.CreateShortcutFromPathAsync(drawer, source);
        await store.CreateShortcutFromPathAsync(drawer, source);

        Assert.Contains("launcher.lnk", drawer.Items.Select(item => item.FileName));
        Assert.Contains("launcher_1.lnk", drawer.Items.Select(item => item.FileName));
    }

    [Fact]
    public async Task PackImport_RoundTripsDrawersAndLinkFiles()
    {
        var sourceStore = new ShortcutDrawerStore(_tempRoot);
        var drawer = new ShortcutDrawerDefinition { Name = "Packed" };
        string target = Path.Combine(_tempRoot, "packed-target.txt");
        File.WriteAllText(target, "content");
        await sourceStore.CreateShortcutFromPathAsync(drawer, target);
        await sourceStore.SaveAsync(new ShortcutDrawerData { Drawers = [drawer] });

        string zipPath = Path.Combine(_tempRoot, "pack.zip");
        await sourceStore.PackDrawersAsync(zipPath);
        Assert.True(File.Exists(zipPath));

        string importRoot = Path.Combine(_tempRoot, "imported");
        var importStore = new ShortcutDrawerStore(importRoot);
        await importStore.ImportPackAsync(zipPath);

        ShortcutDrawerData loaded = await importStore.LoadAsync();
        var restored = Assert.Single(loaded.Drawers);
        Assert.Equal(drawer.Id, restored.Id);
        Assert.Equal("Packed", restored.Name);
        Assert.Equal(new[] { "packed-target.lnk" }, restored.Items.Select(item => item.FileName));
        string restoredFolder = importStore.GetDrawerFolderPath(restored);
        Assert.True(File.Exists(Path.Combine(restoredFolder, "packed-target.lnk")));
    }

    [Fact]
    public async Task Spacing_IsClampedOnSave()
    {
        var store = new ShortcutDrawerStore(_tempRoot);
        var data = new ShortcutDrawerData { RowSpacing = -20, ColumnSpacing = 500 };
        await store.SaveAsync(data);

        ShortcutDrawerData reloaded = await new ShortcutDrawerStore(_tempRoot).LoadAsync();
        Assert.Equal(ShortcutDrawerStore.MinSpacing, reloaded.RowSpacing);
        Assert.Equal(ShortcutDrawerStore.MaxSpacing, reloaded.ColumnSpacing);
    }

    [Fact]
    public void IconSizeAndSpacing_FollowTheDrawerWideValueUnlessTheDrawerOverrides()
    {
        var data = new ShortcutDrawerData { IconSize = 48, RowSpacing = 12, ColumnSpacing = 4 };
        var inheriting = new ShortcutDrawerDefinition();
        var overriding = new ShortcutDrawerDefinition { IconSize = 20, RowSpacing = 0, ColumnSpacing = 30 };

        Assert.Equal(48, ShortcutDrawerStore.ResolveIconSize(data, inheriting));
        Assert.Equal(12, ShortcutDrawerStore.ResolveRowSpacing(data, inheriting));
        Assert.Equal(4, ShortcutDrawerStore.ResolveColumnSpacing(data, inheriting));

        Assert.Equal(20, ShortcutDrawerStore.ResolveIconSize(data, overriding));
        Assert.Equal(0, ShortcutDrawerStore.ResolveRowSpacing(data, overriding));
        Assert.Equal(30, ShortcutDrawerStore.ResolveColumnSpacing(data, overriding));

        // Missing data still yields sane defaults rather than throwing.
        Assert.Equal(32, ShortcutDrawerStore.ResolveIconSize(null, null));
        Assert.Equal(6, ShortcutDrawerStore.ResolveRowSpacing(null, null));
        Assert.Equal(6, ShortcutDrawerStore.ResolveColumnSpacing(null, null));
    }

    [Fact]
    public void IconSizeAndSpacing_OverridesAreClampedToTheSupportedRange()
    {
        var data = new ShortcutDrawerData { IconSize = 64, RowSpacing = 40, ColumnSpacing = 40 };
        var wild = new ShortcutDrawerDefinition { IconSize = 4096, RowSpacing = -50, ColumnSpacing = 999 };

        Assert.Equal(ShortcutDrawerStore.MaxIconSize, ShortcutDrawerStore.ResolveIconSize(data, wild));
        Assert.Equal(ShortcutDrawerStore.MinSpacing, ShortcutDrawerStore.ResolveRowSpacing(data, wild));
        Assert.Equal(ShortcutDrawerStore.MaxSpacing, ShortcutDrawerStore.ResolveColumnSpacing(data, wild));
    }

    [Fact]
    public async Task PerDrawerOverrides_AreClampedOnSave()
    {
        var store = new ShortcutDrawerStore(_tempRoot);
        var drawer = new ShortcutDrawerDefinition
        {
            Name = "Overridden",
            IconSize = 4096,
            RowSpacing = -10,
            ColumnSpacing = 500,
        };
        await store.SaveAsync(new ShortcutDrawerData { Drawers = [drawer] });

        ShortcutDrawerData reloaded = await new ShortcutDrawerStore(_tempRoot).LoadAsync();
        var restored = Assert.Single(reloaded.Drawers);
        Assert.Equal(ShortcutDrawerStore.MaxIconSize, restored.IconSize);
        Assert.Equal(ShortcutDrawerStore.MinSpacing, restored.RowSpacing);
        Assert.Equal(ShortcutDrawerStore.MaxSpacing, restored.ColumnSpacing);
    }

    [Fact]
    public async Task RemoveShortcut_DeletesTheLinkAndDropsTheEntry()
    {
        var store = new ShortcutDrawerStore(_tempRoot);
        var drawer = new ShortcutDrawerDefinition { Name = "Apps" };
        string source = Path.Combine(_tempRoot, "launcher.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "binary");
        await store.CreateShortcutFromPathAsync(drawer, source);

        var data = new ShortcutDrawerData { Drawers = [drawer] };
        bool removed = await store.RemoveShortcutAsync(data, drawer, "launcher.lnk");

        Assert.True(removed);
        Assert.Empty(drawer.Items);
        Assert.False(File.Exists(Path.Combine(store.GetDrawerFolderPath(drawer), "launcher.lnk")));
    }

    [Fact]
    public async Task RemoveShortcut_RefusesPathsOutsideTheDrawerFolder()
    {
        var store = new ShortcutDrawerStore(_tempRoot);
        var drawer = new ShortcutDrawerDefinition { Name = "Apps" };
        string folder = store.GetDrawerFolderPath(drawer);
        string inside = Path.Combine(folder, "keep.lnk");
        File.WriteAllText(inside, "placeholder");
        drawer.Items.Add(new ShortcutDrawerItem { FileName = "keep.lnk", Column = 1, Row = 1 });

        var data = new ShortcutDrawerData { Drawers = [drawer] };
        bool removed = await store.RemoveShortcutAsync(data, drawer, @"..\..\outside.lnk");

        Assert.False(removed);
        Assert.True(File.Exists(inside));
        var entry = Assert.Single(drawer.Items);
        Assert.Equal("keep.lnk", entry.FileName);
    }

    [Fact]
    public async Task RemoveShortcut_WithNoFileStillDropsTheEntry()
    {
        var store = new ShortcutDrawerStore(_tempRoot);
        var drawer = new ShortcutDrawerDefinition
        {
            Name = "Apps",
            Items = [new ShortcutDrawerItem { FileName = "gone.lnk" }],
        };
        var data = new ShortcutDrawerData { Drawers = [drawer] };

        bool removed = await store.RemoveShortcutAsync(data, drawer, "gone.lnk");

        Assert.True(removed);
        Assert.Empty(drawer.Items);
    }

    [Fact]
    public void AllocateNextPosition_FlowsRowMajorAcrossTheGivenColumnCount()
    {
        var store = new ShortcutDrawerStore(_tempRoot);
        var drawer = new ShortcutDrawerDefinition { Name = "Grid" };
        var placed = new List<(int, int)>();
        for (int i = 0; i < 7; i++)
        {
            store.AllocateNextPosition(drawer, 3, out int column, out int row);
            drawer.Items.Add(new ShortcutDrawerItem { FileName = $"file{i}.lnk", Column = column, Row = row });
            placed.Add((column, row));
        }

        Assert.Equal(
            [(1, 1), (2, 1), (3, 1), (1, 2), (2, 2), (3, 2), (1, 3)],
            placed);
    }

    [Fact]
    public void AllocateNextPosition_DoesNotCapTheColumnCount()
    {
        var store = new ShortcutDrawerStore(_tempRoot);
        var drawer = new ShortcutDrawerDefinition { Name = "Wide" };
        var placed = new List<(int, int)>();
        for (int i = 0; i < 5; i++)
        {
            store.AllocateNextPosition(drawer, 12, out int column, out int row);
            drawer.Items.Add(new ShortcutDrawerItem { FileName = $"file{i}.lnk", Column = column, Row = row });
            placed.Add((column, row));
        }

        // Unlimited grid: all five fit on one row when the caller allows it.
        Assert.Equal([(1, 1), (2, 1), (3, 1), (4, 1), (5, 1)], placed);
    }

    [Fact]
    public void AllocateNextPosition_ReusesTheGapLeftByAMovedItem()
    {
        var store = new ShortcutDrawerStore(_tempRoot);
        var drawer = new ShortcutDrawerDefinition
        {
            Name = "Grid",
            Items =
            [
                new ShortcutDrawerItem { FileName = "a.lnk", Column = 1, Row = 1 },
                new ShortcutDrawerItem { FileName = "b.lnk", Column = 2, Row = 1 },
            ],
        };

        drawer.Items[1].Column = 2;
        drawer.Items[1].Row = 2;
        store.AllocateNextPosition(drawer, 3, out int column, out int row);

        Assert.Equal((2, 1), (column, row));
    }

    [Fact]
    public async Task PopupFrameAndIconSize_AreClampedOnSave()
    {
        var store = new ShortcutDrawerStore(_tempRoot);
        var data = new ShortcutDrawerData
        {
            PopupWidth = -40,
            PopupHeight = -10,
            IconSize = 4096,
            Drawers = [new ShortcutDrawerDefinition { Name = "Wide" }],
        };
        await store.SaveAsync(data);

        ShortcutDrawerData reloaded = await new ShortcutDrawerStore(_tempRoot).LoadAsync();
        var drawer = Assert.Single(reloaded.Drawers);
        Assert.Equal(0, reloaded.PopupWidth);
        Assert.Equal(0, reloaded.PopupHeight);
        Assert.Equal(ShortcutDrawerStore.MaxIconSize, reloaded.IconSize);
    }

    [Fact]
    public async Task IconSize_DefaultsToAReadableSizeAndRoundTripsAcrossSizes()
    {
        var store = new ShortcutDrawerStore(_tempRoot);
        ShortcutDrawerData fresh = await store.LoadAsync();
        Assert.Equal(32, fresh.IconSize);

        foreach (int size in new[] { 16, 24, 48, 64 })
        {
            fresh.IconSize = size;
            await store.SaveAsync(fresh);
            ShortcutDrawerData reloaded = await new ShortcutDrawerStore(_tempRoot).LoadAsync();
            Assert.Equal(size, reloaded.IconSize);
        }
    }
    [Fact]
    public void DrawerFolderPath_UsesTheStoreRootAndTheDrawerName()
    {
        var store = new ShortcutDrawerStore(_tempRoot);

        string folder = store.GetDrawerFolderPath(new ShortcutDrawerDefinition { Name = "Games" });

        Assert.Equal(Path.Combine(_tempRoot, "Games"), folder);
        Assert.True(Directory.Exists(folder));
        // Never a GUID: the folder always reads like the drawer.
        Assert.DoesNotContain("drawers", folder, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SameDrawerName_ResolvesToTheSameFolder()
    {
        var store = new ShortcutDrawerStore(_tempRoot);
        var a = new ShortcutDrawerDefinition { Name = "Tools" };
        var b = new ShortcutDrawerDefinition { Name = "Tools" };

        Assert.Equal(
            store.GetDrawerFolderPath(a),
            store.GetDrawerFolderPath(b));
    }

    [Fact]
    public async Task RenameDrawerAsync_MovesTheFolderToTheNewName()
    {
        var store = new ShortcutDrawerStore(_tempRoot);
        var drawer = new ShortcutDrawerDefinition { Name = "Old Name" };
        string folder = store.GetDrawerFolderPath(drawer);
        File.WriteAllText(Path.Combine(folder, "keep.lnk"), "placeholder");
        drawer.Items.Add(new ShortcutDrawerItem { FileName = "keep.lnk" });
        var data = new ShortcutDrawerData { Drawers = [drawer] };

        bool moved = await store.RenameDrawerAsync(data, drawer, "New/Name");

        Assert.True(moved);
        Assert.Equal("New/Name", drawer.Name);
        Assert.True(File.Exists(Path.Combine(store.GetDrawerFolderPath(drawer), "keep.lnk")));
        Assert.Equal(Path.Combine(_tempRoot, "NewName"), store.GetDrawerFolderPath(drawer));
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task RenameDrawerAsync_KeepsTheLinksWhenTheTargetExists()
    {
        var store = new ShortcutDrawerStore(_tempRoot);
        var first = new ShortcutDrawerDefinition { Name = "First" };
        var second = new ShortcutDrawerDefinition { Name = "Second" };
        FolderService(store, first);
        var data = new ShortcutDrawerData { Drawers = [first, second] };

        // "First" already exists, so renaming "Second" to it must not clobber it.
        bool moved = await store.RenameDrawerAsync(data, second, "First");

        Assert.False(moved);
        Assert.Equal("First", second.Name);
        Assert.True(File.Exists(Path.Combine(store.GetDrawerFolderPath(first), "keep.lnk")));
    }

    private static void FolderService(ShortcutDrawerStore store, ShortcutDrawerDefinition drawer)
    {
        string folder = store.GetDrawerFolderPath(drawer);
        File.WriteAllText(Path.Combine(folder, "keep.lnk"), "placeholder");
        drawer.Items.Add(new ShortcutDrawerItem { FileName = "keep.lnk" });
    }

    [Fact]
    public async Task RemoveShortcutsAsync_RemovesEverySelectedLink()
    {
        var store = new ShortcutDrawerStore(_tempRoot);
        var drawer = new ShortcutDrawerDefinition { Name = "Batch" };
        string folder = store.GetDrawerFolderPath(drawer);
        foreach (string name in new[] { "a.lnk", "b.lnk", "c.lnk" })
        {
            File.WriteAllText(Path.Combine(folder, name), "link");
            drawer.Items.Add(new ShortcutDrawerItem { FileName = name });
        }

        var data = new ShortcutDrawerData { Drawers = [drawer] };
        int removed = await store.RemoveShortcutsAsync(data, drawer, ["a.lnk", "c.lnk"]);

        Assert.Equal(2, removed);
        Assert.Single(drawer.Items);
        Assert.False(File.Exists(Path.Combine(folder, "a.lnk")));
        Assert.False(File.Exists(Path.Combine(folder, "c.lnk")));
        Assert.True(File.Exists(Path.Combine(folder, "b.lnk")));
    }

    [Fact]
    public async Task MoveShortcutsAsync_MovesLinksOutOfTheDrawer()
    {
        var store = new ShortcutDrawerStore(_tempRoot);
        var drawer = new ShortcutDrawerDefinition { Name = "Moving" };
        string folder = store.GetDrawerFolderPath(drawer);
        foreach (string name in new[] { "a.lnk", "b.lnk" })
        {
            File.WriteAllText(Path.Combine(folder, name), "link");
            drawer.Items.Add(new ShortcutDrawerItem { FileName = name });
        }

        string other = Path.Combine(_tempRoot, "elsewhere");
        var data = new ShortcutDrawerData { Drawers = [drawer] };
        int moved = await store.MoveShortcutsAsync(data, drawer, ["a.lnk"], other);

        Assert.Equal(1, moved);
        Assert.True(File.Exists(Path.Combine(other, "a.lnk")));
        Assert.False(File.Exists(Path.Combine(folder, "a.lnk")));
        // The moved link left the drawer for good.
        Assert.Single(drawer.Items);
    }

    [Fact]
    public async Task MoveShortcutsAsync_RefusesPathsOutsideTheDrawer()
    {
        var store = new ShortcutDrawerStore(_tempRoot);
        var drawer = new ShortcutDrawerDefinition { Name = "Guarded" };
        string folder = store.GetDrawerFolderPath(drawer);
        string outside = Path.Combine(_tempRoot, "outside.lnk");
        File.WriteAllText(outside, "link");

        var data = new ShortcutDrawerData { Drawers = [drawer] };
        int moved = await store.MoveShortcutsAsync(data, drawer, [outside], folder);

        Assert.Equal(0, moved);
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task AutoArrangeDrawerAsync_PacksItemsRowMajorInFileOrder()
    {
        var store = new ShortcutDrawerStore(_tempRoot);
        var drawer = new ShortcutDrawerDefinition
        {
            Name = "Messy",
            Items =
            [
                new ShortcutDrawerItem { FileName = "c.lnk", Column = 5, Row = 9 },
                new ShortcutDrawerItem { FileName = "a.lnk", Column = 1, Row = 1 },
                new ShortcutDrawerItem { FileName = "b.lnk", Column = 4, Row = 3 },
            ],
        };
        var data = new ShortcutDrawerData { Drawers = [drawer] };

        await store.AutoArrangeDrawerAsync(data, drawer, 2);

        // Current reading order (row, then column) is preserved and packed.
        Assert.Equal(
            [("a.lnk", 1, 1), ("b.lnk", 2, 1), ("c.lnk", 1, 2)],
            drawer.Items
                .OrderBy(item => item.Row)
                .ThenBy(item => item.Column)
                .Select(item => (item.FileName, item.Column, item.Row)));
    }
}
