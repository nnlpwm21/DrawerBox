using System.Diagnostics;
using DeskBoxWhite.Helpers;
using DeskBoxWhite.Models;
using DeskBoxWhite.Platform;
using DeskBoxWhite.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace DeskBoxWhite.Views.SettingsSections;

/// <summary>
/// Settings for the shortcut drawer: manage every drawer, its folder, its own
/// global hotkey, and one-click package export / import. Reads and writes
/// drawer layout through the drawer store.
/// </summary>
public sealed partial class ShortcutDrawerSettingsSection : UserControl
{
    private readonly ShortcutDrawerStore _store = new();
    private LocalizationService _localization = null!;
    private nint _ownerWindow;
    private bool _isLoading;
    private ShortcutDrawerData? _data;
    private Guid? _recordingDrawerId;
    private bool _recordingGlobal;

    public ShortcutDrawerSettingsSection()
    {
        InitializeComponent();
        GlobalHotkeyButton.KeyDown += GlobalHotkeyButton_KeyDown;
        GlobalHotkeyButton.LostFocus += GlobalHotkeyButton_LostFocus;
    }

    public void Configure(LocalizationService localization, nint ownerWindow)
    {
        _localization = localization;
        _ownerWindow = ownerWindow;
        NewDrawerTextBox.PlaceholderText = localization.T(
            "Settings.ShortcutDrawer.Drawers.AddPlaceholder");
    }

    public async void RefreshFromSettings()
    {
        if (_isLoading)
        {
            return;
        }

        _isLoading = true;
        try
        {
            _data = await _store.LoadAsync();
            GlobalHotkeyToggle.IsOn = _data.GlobalHotkeyEnabled;
            SyncAppearanceControls();
            IconSizeSlider.Minimum = ShortcutDrawerStore.MinIconSize;
            IconSizeSlider.Maximum = ShortcutDrawerStore.MaxIconSize;
            IconSizeSlider.StepFrequency = 2;
            IconSizeSlider.Value = Math.Clamp(_data.IconSize, ShortcutDrawerStore.MinIconSize, ShortcutDrawerStore.MaxIconSize);
            IconSizeBox.Text = _data.IconSize.ToString();
            RowSpacingBox.Text = _data.RowSpacing.ToString();
            ColumnSpacingBox.Text = _data.ColumnSpacing.ToString();
            RenderGlobalHotkeyButton();
            RenderDrawers();
        }
        finally
        {
            _isLoading = false;
        }
    }

    // ── Drawer appearance (card / color / image) ──────────────────────
    // Settings live in the app-wide settings.json and apply to every drawer;
    // changes are broadcast through App.NotifyDrawerAppearanceChanged so open
    // windows repaint immediately.

    private Services.DrawerAppearanceSlice Appearance => (App.Current as App)?.SettingsService.Settings.DrawerAppearance
        ?? new Services.DrawerAppearanceSlice();

    private void SyncAppearanceControls()
    {
        AppearanceModeCombo.SelectedIndex = Appearance.Mode switch
        {
            "Color" => 1,
            "Image" => 2,
            _ => 0
        };
        if (AppearanceColorBox.Text != Appearance.Color)
        {
            AppearanceColorBox.Text = Appearance.Color;
        }
        try
        {
            AppearanceColorSwatch.Background = new SolidColorBrush(ParseColorOrFallback(Appearance.Color));
        }
        catch
        {
        }
        AppearanceImagePathText.Text = Appearance.ImagePath;
        AppearanceColorCard.Visibility = AppearancModeIs("Color") ? Visibility.Visible : Visibility.Collapsed;
        AppearanceImageCard.Visibility = AppearancModeIs("Image") ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool AppearancModeIs(string mode) => string.Equals(Appearance.Mode, mode, StringComparison.OrdinalIgnoreCase);

    private static readonly string[] ColorPresets =
    {
        "#B31F1F1F", "#D82B2B45", "#E03A3A3A", "#E0101828", "#667A7A7A"
    };

    private static Windows.UI.Color ParseColorOrFallback(string text)
    {
        string hex = (text ?? string.Empty).Trim().TrimStart('#');
        if (hex.Length == 6 || hex.Length == 8)
        {
            try
            {
                byte a = hex.Length == 8 ? Convert.ToByte(hex.Substring(0, 2), 16) : (byte)0xFF;
                int o = hex.Length - 6;
                return new Windows.UI.Color
                {
                    A = a,
                    R = Convert.ToByte(hex.Substring(o, 2), 16),
                    G = Convert.ToByte(hex.Substring(o + 2, 2), 16),
                    B = Convert.ToByte(hex.Substring(o + 4, 2), 16)
                };
            }
            catch
            {
            }
        }
        return new Windows.UI.Color { A = 0xB3, R = 0x1F, G = 0x1F, B = 0x1F };
    }

    private void AppearanceModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // This fires while XAML is still parsing (SelectedIndex="0" is set in
        // markup), so the sibling fields declared after the combo are not
        // assigned yet — an excursion to them here breaks the whole window.
        if (_isLoading ||
            AppearanceColorCard is null ||
            AppearanceColorSwatch is null ||
            AppearanceImagePathText is null)
        {
            return;
        }
        Appearance.Mode = AppearanceModeCombo.SelectedIndex switch
        {
            1 => "Color",
            2 => "Image",
            _ => "Card"
        };
        SyncAppearanceControls();
        (App.Current as App)?.SettingsService.SaveDebounced();
        if (App.Current is App) { App.NotifyDrawerAppearanceChanged(); }
    }

    private void AppearanceColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Same load-order guard as the mode combo: this can fire while the
        // XAML tree is still being built.
        if (_isLoading || AppearanceColorSwatch is null)
        {
            return;
        }
        string hex = AppearanceColorBox.Text.Trim();
        if (hex.Length == 6 || hex.Length == 8)
        {
            Appearance.Color = "#" + (hex.Length == 6 ? "FF" : string.Empty) + hex.TrimStart('#');
            try
            {
                AppearanceColorSwatch.Background = new SolidColorBrush(ParseColorOrFallback(Appearance.Color));
            }
            catch
            {
            }
            (App.Current as App)?.SettingsService.SaveDebounced();
            if (App.Current is App) { App.NotifyDrawerAppearanceChanged(); }
        }
    }

    private void AppearanceColorSwatch_Click(object sender, RoutedEventArgs e)
    {
        // Cycles presets that read well behind the white labels; the exact hex
        // stays editable in the box above.
        int index = Array.FindIndex(
            ColorPresets,
            p => string.Equals(p, Appearance.Color, StringComparison.OrdinalIgnoreCase));
        Appearance.Color = ColorPresets[(index + 1) % ColorPresets.Length];
        SyncAppearanceControls();
        (App.Current as App)?.SettingsService.SaveDebounced();
        if (App.Current is App) { App.NotifyDrawerAppearanceChanged(); }
    }

    private async void AppearanceImageBrowse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? picked = await FileOpenPickerService.PickSingleFileAsync(
                _ownerWindow,
                new List<string> { ".png", ".jpg", ".jpeg", ".bmp", ".webp" },
                Microsoft.Windows.Storage.Pickers.PickerLocationId.PicturesLibrary);
            if (!string.IsNullOrWhiteSpace(picked) && File.Exists(picked))
            {
                Appearance.ImagePath = picked;
                SyncAppearanceControls();
                (App.Current as App)?.SettingsService.SaveDebounced();
                if (App.Current is App) { App.NotifyDrawerAppearanceChanged(); }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[Settings] Appearance image browse failed: {ex.Message}");
        }
    }

    private void IconSizeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoading || _data is null)
        {
            return;
        }

        if (!int.TryParse(IconSizeBox.Text.Trim(), out int size))
        {
            return;
        }

        _data.IconSize = Math.Clamp(size, ShortcutDrawerStore.MinIconSize, ShortcutDrawerStore.MaxIconSize);
        IconSizeText.Text = _data.IconSize.ToString();
        if (Math.Abs(IconSizeSlider.Value - _data.IconSize) > 0.01)
        {
            _isLoading = true;
            IconSizeSlider.Value = _data.IconSize;
            _isLoading = false;
        }

        _ = SaveAsync();
        App.NotifyShortcutDrawerLayoutChanged();
    }

    private void IconSizeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isLoading || _data is null)
        {
            return;
        }

        int size = (int)Math.Round(e.NewValue);
        _data.IconSize = Math.Clamp(size, ShortcutDrawerStore.MinIconSize, ShortcutDrawerStore.MaxIconSize);
        IconSizeText.Text = _data.IconSize.ToString();
        if (IconSizeBox.Text != _data.IconSize.ToString())
        {
            _isLoading = true;
            IconSizeBox.Text = _data.IconSize.ToString();
            _isLoading = false;
        }

        _ = SaveAsync();
        App.NotifyShortcutDrawerLayoutChanged();
    }

    private void RowSpacingBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoading || _data is null || !int.TryParse(RowSpacingBox.Text.Trim(), out int spacing))
        {
            return;
        }

        _data.RowSpacing = Math.Clamp(spacing, ShortcutDrawerStore.MinSpacing, ShortcutDrawerStore.MaxSpacing);
        _ = SaveAsync();
        App.NotifyShortcutDrawerLayoutChanged();
    }

    private void ColumnSpacingBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoading || _data is null || !int.TryParse(ColumnSpacingBox.Text.Trim(), out int spacing))
        {
            return;
        }

        _data.ColumnSpacing = Math.Clamp(spacing, ShortcutDrawerStore.MinSpacing, ShortcutDrawerStore.MaxSpacing);
        _ = SaveAsync();
        App.NotifyShortcutDrawerLayoutChanged();
    }

    /// <summary>Shows the current drawer-wide hotkey on the global hotkey button.</summary>
    private void RenderGlobalHotkeyButton()
    {
        if (_data is null)
        {
            return;
        }

        GlobalHotkeyButton.Content = GlobalHotkeyService.FormatGesture(
            GlobalHotkeyService.NormalizeGesture(_data.GlobalHotkeyModifiers, _data.GlobalHotkeyKey),
            _localization);
    }

    public void SetActive(bool active)
    {
        if (active)
        {
            RefreshFromSettings();
        }
    }

    private void RenderDrawers()
    {
        DrawersEditorPanel.Children.Clear();
        _recordingDrawerId = null;

        if (_data is null)
        {
            return;
        }

        foreach (var drawer in _data.Drawers)
        {
            DrawersEditorPanel.Children.Add(BuildDrawerRow(drawer));
        }

        if (_data.Drawers.Count == 0)
        {
            DrawersEditorPanel.Children.Add(new TextBlock
            {
                Text = _localization.T("Settings.ShortcutDrawer.Drawers.Empty"),
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Resources["SettingDescriptionTextStyle"],
            });
        }
    }

    private Border BuildDrawerRow(ShortcutDrawerDefinition drawer)
    {
        var root = new StackPanel { Spacing = 8 };

        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameBox = new TextBox
        {
            Text = drawer.Name,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        nameBox.LostFocus += async (_, _) =>
        {
            if (_data is null)
            {
                return;
            }

            string renamed = nameBox.Text.Trim();
            if (renamed.Length == 0 || string.Equals(renamed, drawer.Name, StringComparison.Ordinal))
            {
                return;
            }

            // The drawer folder follows the drawer name, so renaming moves it.
            await _store.RenameDrawerAsync(_data, drawer, renamed);
            RenderDrawers();
        };
        Grid.SetColumn(nameBox, 0);
        header.Children.Add(nameBox);

        var deleteButton = new Button
        {
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(8),
            Style = (Style)Resources["IconActionButtonStyle"],
            Content = new FontIcon { Glyph = "\uE74D" },
        };
        ToolTipService.SetToolTip(deleteButton, _localization.T("Settings.ShortcutDrawer.Drawers.Delete"));
        deleteButton.Click += async (_, _) => await DeleteDrawerAsync(drawer);
        Grid.SetColumn(deleteButton, 2);
        header.Children.Add(deleteButton);

        var openButton = new Button
        {
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(8),
            Style = (Style)Resources["IconActionButtonStyle"],
            Content = new FontIcon { Glyph = "\uE8A7" },
        };
        ToolTipService.SetToolTip(openButton, _localization.T("Settings.ShortcutDrawer.Drawers.Open"));
        openButton.Click += (_, _) => App.Current?.OpenShortcutDrawer(drawer.Id);
        Grid.SetColumn(openButton, 1);
        header.Children.Add(openButton);

        root.Children.Add(header);

        string folder = _store.GetDrawerFolderPath(drawer);
        var folderRow = new Grid { ColumnSpacing = 8 };
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var folderText = new TextBlock
        {
            Text = folder,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Resources["SettingDescriptionTextStyle"],
        };
        Grid.SetColumn(folderText, 0);
        folderRow.Children.Add(folderText);

        var openFolderButton = new Button
        {
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(8, 4, 8, 4),
            Content = new TextBlock { Text = _localization.T("Settings.ShortcutDrawer.Drawers.OpenFolder") },
        };
        ToolTipService.SetToolTip(openFolderButton, _localization.T("Settings.ShortcutDrawer.Drawers.OpenFolder"));
        openFolderButton.Click += (_, _) => OpenFolder(folder);
        Grid.SetColumn(openFolderButton, 1);
        folderRow.Children.Add(openFolderButton);

        var autoArrangeButton = new Button
        {
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(8, 4, 8, 4),
            Style = (Style)Resources["ActionButtonStyle"],
            Content = new TextBlock { Text = _localization.T("Settings.ShortcutDrawer.Drawers.AutoArrange") },
        };
        ToolTipService.SetToolTip(
            autoArrangeButton,
            _localization.T("Settings.ShortcutDrawer.Drawers.AutoArrangeHint"));
        autoArrangeButton.Click += async (_, _) =>
        {
            if (_data is null)
            {
                return;
            }

            // Auto-layout follows the files, using as many columns as the drawer
            // window currently shows.
            await _store.AutoArrangeDrawerAsync(_data, drawer, 0);
            RenderDrawers();
            App.NotifyShortcutDrawerLayoutChanged();
        };
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(autoArrangeButton, 2);
        folderRow.Children.Add(autoArrangeButton);

        root.Children.Add(folderRow);

        root.Children.Add(BuildDrawerOverrideRow(
            drawer,
            "Settings.ShortcutDrawer.Icons.OwnTitle",
            ShortcutDrawerStore.MinIconSize,
            ShortcutDrawerStore.MaxIconSize,
            2,
            drawer.IconSize ?? 0,
            drawer.IconSize is not null,
            ShortcutDrawerStore.ResolveIconSize(_data, drawer),
            value => drawer.IconSize = value > 0 ? value : null));

        root.Children.Add(BuildDrawerOverrideRow(
            drawer,
            "Settings.ShortcutDrawer.Spacing.OwnTitle",
            ShortcutDrawerStore.MinSpacing,
            ShortcutDrawerStore.MaxSpacing,
            2,
            Math.Max(drawer.RowSpacing ?? 0, drawer.ColumnSpacing ?? 0),
            drawer.RowSpacing is not null || drawer.ColumnSpacing is not null,
            ShortcutDrawerStore.ResolveRowSpacing(_data, drawer),
            value =>
            {
                if (value > 0)
                {
                    drawer.RowSpacing = value;
                    drawer.ColumnSpacing = value;
                }
                else
                {
                    drawer.RowSpacing = null;
                    drawer.ColumnSpacing = null;
                }
            }));

        var hotkeyRow = new Grid { ColumnSpacing = 8 };
        hotkeyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hotkeyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hotkeyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var hotkeyToggle = new ToggleSwitch
        {
            MinWidth = 0,
            OffContent = string.Empty,
            OnContent = string.Empty,
            IsOn = drawer.HotkeyEnabled,
        };
        ToolTipService.SetToolTip(hotkeyToggle, _localization.T("Settings.ShortcutDrawer.Hotkey.FollowGlobal"));
        hotkeyToggle.Toggled += async (_, _) =>
        {
            if (_isLoading)
            {
                return;
            }

            drawer.HotkeyEnabled = hotkeyToggle.IsOn;
            await SaveAsync();
            App.Current?.RefreshShortcutDrawerHotkeys();
        };
        Grid.SetColumn(hotkeyToggle, 0);
        hotkeyRow.Children.Add(hotkeyToggle);

        var hotkeyButton = new Button
        {
            MinWidth = 132,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Style = (Style)Resources["ActionButtonStyle"],
            Content = _recordingDrawerId == drawer.Id
                ? _localization.T("Settings.ShortcutDrawer.Hotkey.Recording")
                : GlobalHotkeyService.FormatGesture(GlobalHotkeyService.NormalizeGesture(drawer.HotkeyModifiers, drawer.HotkeyKey), _localization),
        };
        hotkeyButton.Click += (_, _) =>
        {
            _recordingDrawerId = drawer.Id;
            hotkeyButton.Content = _localization.T("Settings.ShortcutDrawer.Hotkey.Recording");
            hotkeyButton.Focus(FocusState.Programmatic);
        };
        hotkeyButton.KeyDown += async (_, e) => await OnHotkeyCaptureKeyDownAsync(drawer, e);
        hotkeyButton.LostFocus += (_, _) =>
        {
            if (_recordingDrawerId == drawer.Id)
            {
                _recordingDrawerId = null;
                hotkeyButton.Content = GlobalHotkeyService.FormatGesture(GlobalHotkeyService.NormalizeGesture(drawer.HotkeyModifiers, drawer.HotkeyKey), _localization);
            }
        };
        Grid.SetColumn(hotkeyButton, 1);
        hotkeyRow.Children.Add(hotkeyButton);

        var itemCountText = new TextBlock
        {
            Text = drawer.Items.Count.ToString(),
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Resources["SettingDescriptionTextStyle"],
        };
        Grid.SetColumn(itemCountText, 2);
        hotkeyRow.Children.Add(itemCountText);

        root.Children.Add(hotkeyRow);

        return new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(8),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            BorderThickness = new Thickness(1),
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Child = root,
        };
    }

    private async Task OnHotkeyCaptureKeyDownAsync(ShortcutDrawerDefinition drawer, KeyRoutedEventArgs e)
    {
        if (_recordingDrawerId != drawer.Id)
        {
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            _recordingDrawerId = null;
            RenderDrawers();
            e.Handled = true;
            return;
        }

        if (IsModifierKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        var gesture = new GlobalHotkeyGesture(GetPressedHotkeyModifiers(), (int)e.Key);
        _recordingDrawerId = null;
        e.Handled = true;

        var service = App.Current?.ShortcutDrawerHotkeyService;
        if (service is not null && !service.TryApplyGesture(drawer.Id, gesture, out _))
        {
            RenderDrawers();
            return;
        }

        drawer.HotkeyModifiers = (int)gesture.Modifiers;
        drawer.HotkeyKey = gesture.VirtualKey;
        drawer.HotkeyEnabled = true;
        await SaveAsync();
        App.Current?.RefreshShortcutDrawerHotkeys();
        RenderDrawers();
    }


    private static void OpenFolder(string folder)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            App.Log($"[ShortcutDrawer] Failed to open drawer folder: {ex.Message}");
        }
    }

    /// <summary>
    /// A per-drawer override row: the toggle decides whether this drawer carries
    /// its own value (off = inherit the drawer-wide one, exactly like the global
    /// hotkey fallback), the box accepts a typed number, and the slider gives
    /// quick adjustment. While the toggle is off both controls stay disabled and
    /// show the inherited value so the effective size is always visible.
    /// </summary>
    private Grid BuildDrawerOverrideRow(
        ShortcutDrawerDefinition drawer,
        string titleKey,
        int minimum,
        int maximum,
        int step,
        int ownValue,
        bool hasOwnValue,
        int inheritedValue,
        Action<int?> applyValue)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var toggle = new ToggleSwitch
        {
            MinWidth = 0,
            OffContent = string.Empty,
            OnContent = string.Empty,
            IsOn = hasOwnValue,
        };
        ToolTipService.SetToolTip(toggle, _localization.T(titleKey));

        int effective = hasOwnValue ? Math.Clamp(ownValue, minimum, maximum) : inheritedValue;
        var valueBox = new TextBox
        {
            Text = effective.ToString(),
            MinWidth = 56,
            MaxWidth = 88,
            IsEnabled = hasOwnValue,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var valueSlider = new Slider
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = effective,
            StepFrequency = step,
            MinWidth = 120,
            MaxWidth = 220,
            IsEnabled = hasOwnValue,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };

        toggle.Toggled += async (_, _) =>
        {
            if (_isLoading || _data is null)
            {
                return;
            }

            bool isOn = toggle.IsOn;
            valueBox.IsEnabled = isOn;
            valueSlider.IsEnabled = isOn;
            if (isOn)
            {
                int previous = Math.Clamp((int)Math.Round(valueSlider.Value), minimum, maximum);
                applyValue(previous);
            }
            else
            {
                applyValue(null);
                valueBox.Text = ShortcutDrawerStore.ResolveIconSize(_data, drawer).ToString();
            }

            await SaveAsync();
            App.NotifyShortcutDrawerLayoutChanged();
            RenderDrawers();
        };

        valueBox.TextChanged += async (_, _) =>
        {
            if (_isLoading || _data is null || !toggle.IsOn ||
                !int.TryParse(valueBox.Text.Trim(), out int parsed))
            {
                return;
            }

            int clamped = Math.Clamp(parsed, minimum, maximum);
            applyValue(clamped);
            if (Math.Abs(valueSlider.Value - clamped) > 0.01)
            {
                _isLoading = true;
                valueSlider.Value = clamped;
                _isLoading = false;
            }

            await SaveAsync();
            App.NotifyShortcutDrawerLayoutChanged();
        };

        valueSlider.ValueChanged += async (_, args) =>
        {
            if (_isLoading || _data is null || !toggle.IsOn)
            {
                return;
            }

            int clamped = Math.Clamp((int)Math.Round(args.NewValue), minimum, maximum);
            applyValue(clamped);
            if (valueBox.Text != clamped.ToString())
            {
                _isLoading = true;
                valueBox.Text = clamped.ToString();
                _isLoading = false;
            }

            await SaveAsync();
            App.NotifyShortcutDrawerLayoutChanged();
        };

        Grid.SetColumn(toggle, 0);
        Grid.SetColumn(valueBox, 1);
        Grid.SetColumn(valueSlider, 2);
        row.Children.Add(toggle);
        row.Children.Add(valueBox);
        row.Children.Add(valueSlider);
        return row;
    }

    private async Task DeleteDrawerAsync(ShortcutDrawerDefinition drawer)    {
        if (_data is null || XamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = _localization.T("Settings.ShortcutDrawer.Drawers.Delete"),
            Content = new TextBlock
            {
                Text = _localization.T("Settings.ShortcutDrawer.Drawers.DeleteConfirm"),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = _localization.T("Common.Delete"),
            CloseButtonText = _localization.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _data.Drawers.Remove(drawer);
        await SaveAsync();
        App.Current?.RefreshShortcutDrawerHotkeys();
        RenderDrawers();
    }

    private void AddDrawerButton_Click(object sender, RoutedEventArgs e)
    {
        _ = AddDrawerFromTextBoxAsync();
    }

    private void GlobalHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _recordingGlobal = true;
        GlobalHotkeyButton.Content = _localization.T("Settings.ShortcutDrawer.Hotkey.Recording");
        GlobalHotkeyButton.Focus(FocusState.Programmatic);
    }

    private void GlobalHotkeyButton_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_recordingGlobal)
        {
            return;
        }

        _recordingGlobal = false;
        RenderGlobalHotkeyButton();
    }

    private void GlobalHotkeyButton_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_recordingGlobal || _data is null)
        {
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            _recordingGlobal = false;
            RenderGlobalHotkeyButton();
            e.Handled = true;
            return;
        }

        if (IsModifierKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        var gesture = new GlobalHotkeyGesture(GetPressedHotkeyModifiers(), (int)e.Key);
        var service = App.Current?.ShortcutDrawerHotkeyService;
        if (service is not null && !service.TryApplyGesture(Guid.Empty, gesture, out _))
        {
            _recordingGlobal = false;
            RenderGlobalHotkeyButton();
            e.Handled = true;
            return;
        }

        _data.GlobalHotkeyModifiers = (int)gesture.Modifiers;
        _data.GlobalHotkeyKey = gesture.VirtualKey;
        _data.GlobalHotkeyEnabled = true;
        _recordingGlobal = false;
        e.Handled = true;
        _ = SaveAsync().ContinueWith(_ =>
        {
            GlobalHotkeyToggle.IsOn = true;
            App.Current?.RefreshShortcutDrawerHotkeys();
        });
        RenderGlobalHotkeyButton();
    }

    private async void GlobalHotkeyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading || _data is null)
        {
            return;
        }

        _data.GlobalHotkeyEnabled = GlobalHotkeyToggle.IsOn;
        await SaveAsync();
        App.Current?.RefreshShortcutDrawerHotkeys();
    }

    private void NewDrawerTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            _ = AddDrawerFromTextBoxAsync();
            e.Handled = true;
        }
    }

    private async Task AddDrawerFromTextBoxAsync()
    {
        if (_data is null)
        {
            return;
        }

        string name = NewDrawerTextBox.Text.Trim();
        if (name.Length == 0)
        {
            return;
        }

        var drawer = new ShortcutDrawerDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            HotkeyEnabled = false,
            HotkeyModifiers = (int)(HotkeyModifierKeys.Control | HotkeyModifierKeys.Alt),
            HotkeyKey = 0x44,
        };

        // The folder is derived from the drawer name, so there is nothing to
        // create here: the store root already holds it.
        _data.Drawers.Add(drawer);
        _ = _store.GetDrawerFolderPath(drawer);

        NewDrawerTextBox.Text = string.Empty;
        await SaveAsync();
        RenderDrawers();
    }

    private async void ExportPackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_ownerWindow == 0)
        {
            return;
        }

        string? folder = await FolderPickerService.PickFolderAsync(_ownerWindow);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        ExportPackButton.IsEnabled = false;
        try
        {
            string fileName = $"shortcut-drawer-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
            string path = Path.Combine(folder, fileName);
            await _store.PackDrawersAsync(path);
            PackStatusText.Text = _localization.T("Settings.ShortcutDrawer.Pack.Exported");
        }
        catch (Exception ex)
        {
            App.Log($"[ShortcutDrawer] Pack export failed: {ex}");
            PackStatusText.Text = _localization.T("Settings.ShortcutDrawer.Pack.Failed");
        }
        finally
        {
            ExportPackButton.IsEnabled = true;
        }
    }

    private async void ImportPackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_ownerWindow == 0)
        {
            return;
        }

        string? path = await FileOpenPickerService.PickSingleFileAsync(
            _ownerWindow,
            [".zip"],
            Microsoft.Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        ImportPackButton.IsEnabled = false;
        try
        {
            await _store.ImportPackAsync(path);
            _data = await _store.LoadAsync();
            RenderDrawers();
            App.Current?.RefreshShortcutDrawerHotkeys();
            PackStatusText.Text = _localization.T("Settings.ShortcutDrawer.Pack.Imported");
        }
        catch (Exception ex)
        {
            App.Log($"[ShortcutDrawer] Pack import failed: {ex}");
            PackStatusText.Text = _localization.T("Settings.ShortcutDrawer.Pack.Failed");
        }
        finally
        {
            ImportPackButton.IsEnabled = true;
        }
    }

    private async Task SaveAsync()
    {
        if (_data is null)
        {
            return;
        }

        await _store.SaveAsync(_data);
    }

    private static bool IsModifierKey(VirtualKey key)
    {
        return key is VirtualKey.Control or VirtualKey.Menu or VirtualKey.Shift
            or VirtualKey.LeftControl or VirtualKey.RightControl
            or VirtualKey.LeftMenu or VirtualKey.RightMenu
            or VirtualKey.LeftShift or VirtualKey.RightShift
            or VirtualKey.LeftWindows or VirtualKey.RightWindows;
    }

    private static HotkeyModifierKeys GetPressedHotkeyModifiers()
    {
        HotkeyModifierKeys modifiers = HotkeyModifierKeys.None;
        if (Win32Helper.IsKeyPressed(VirtualKey.Control))
        {
            modifiers |= HotkeyModifierKeys.Control;
        }

        if (Win32Helper.IsKeyPressed(VirtualKey.Menu))
        {
            modifiers |= HotkeyModifierKeys.Alt;
        }

        if (Win32Helper.IsKeyPressed(VirtualKey.Shift))
        {
            modifiers |= HotkeyModifierKeys.Shift;
        }

        return modifiers;
    }
}
