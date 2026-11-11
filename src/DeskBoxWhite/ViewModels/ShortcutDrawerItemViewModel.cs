using Microsoft.UI.Xaml.Media.Imaging;

namespace DeskBoxWhite.ViewModels;

/// <summary>
/// Display projection of one shortcut drawer column used by the drawer popup.
/// </summary>
public sealed class ShortcutDrawerDefinitionViewModel
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public bool IsEditMode { get; set; }

    public bool ShowBadges { get; set; } = true;

    public IReadOnlyList<ShortcutDrawerItemViewModel> Items { get; init; } =
        Array.Empty<ShortcutDrawerItemViewModel>();
}

/// <summary>
/// Display projection of one shortcut drawer item: the raw path plus the
/// precomputed display name and shell icon.
/// </summary>
public sealed class ShortcutDrawerItemViewModel
{
    public string FileName { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public int Column { get; init; } = 1;

    public int Row { get; init; } = 1;

    public BitmapImage? Icon { get; set; }

    public string? FullPath { get; set; }
}