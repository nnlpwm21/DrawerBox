using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DrawerBox.Tests")]

namespace DeskBoxWhite.Services;

/// <summary>
/// DrawerBox-only settings shell. Deliberately minimal: language, the managed
/// storage root the drawer data lives under, and nothing else. Kept the
/// <see cref="FileWidgetSlice"/> shape so the copied drawer code reads
/// <c>Settings.FileWidget.DefaultManagedStorageRootPath</c> unchanged.
/// </summary>
public sealed class SettingsService
{
    public const string LanguageSystem = "System";
    public const string LanguageChinese = "zh-CN";
    public const string LanguageChineseTraditional = "zh-TW";
    public const string LanguageEnglish = "en-US";
    public const string LanguageJapanese = "ja-JP";
    public const string LanguageGerman = "de-DE";
    public const string LanguagePortuguese = "pt-BR";
    public const string LanguageHindi = "hi-IN";
    public const string LanguageSpanish = "es-ES";
    public const string LanguageFrench = "fr-FR";
    public const string LanguageArabic = "ar-SA";
    public const string LanguageBengali = "bn-BD";
    public const string LanguageRussian = "ru-RU";

    private static readonly SemaphoreSlim s_saveGate = new(1, 1);
    private CancellationTokenSource? _saveDebounce;
    private int _isSaving;

    public AppSettings Settings { get; private set; } = new();

    public SettingsService()
    {
        Settings = Load();
    }

    public void SaveDebounced()
    {
        var existing = _saveDebounce;
        if (existing is not null)
        {
            existing.Cancel();
        }

        var source = new CancellationTokenSource();
        _saveDebounce = source;
        _ = SaveDebouncedAsync(source);
    }

    private async Task SaveDebouncedAsync(CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(500, source.Token);
            _saveDebounce = null;
            await SaveAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.Log($"[Settings] Save failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        var source = _saveDebounce;
        _saveDebounce = null;
        source?.Cancel();
        Task.Run(async () =>
        {
            await s_saveGate.WaitAsync().ConfigureAwait(false);
            try
            {
                WriteFile();
            }
            finally
            {
                s_saveGate.Release();
            }
        });
    }

    private async Task SaveAsync()
    {
        if (Interlocked.Exchange(ref _isSaving, 1) == 1)
        {
            return;
        }

        await s_saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            WriteFile();
        }
        finally
        {
            s_saveGate.Release();
            Interlocked.Exchange(ref _isSaving, 0);
        }
    }

    private void WriteFile()
    {
        try
        {
            string path = GetSettingsFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string json = JsonSerializer.Serialize(
                Settings,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });
            File.WriteAllText(path, json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception ex)
        {
            App.Log($"[Settings] Write failed: {ex.Message}");
        }
    }

    private AppSettings Load()
    {
        try
        {
            string path = GetSettingsFilePath();
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(path)) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            App.Log($"[Settings] Load failed: {ex.Message}");
        }

        return new AppSettings();
    }

    private static string GetSettingsFilePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DrawerBox",
            "settings.json");

    public static string GetDefaultManagedStorageRootPath()
    {
        // Same default as the full app, so the standalone drawer keeps reading
        // and writing the existing shortcut data folder.
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "DeskBoxWhite");
    }

    public static string NormalizeManagedStorageRootPath(string? path)
    {
        string candidate = string.IsNullOrWhiteSpace(path)
            ? GetDefaultManagedStorageRootPath()
            : Environment.ExpandEnvironmentVariables(path.Trim());

        try
        {
            return Path.GetFullPath(candidate);
        }
        catch
        {
            return GetDefaultManagedStorageRootPath();
        }
    }
}

public sealed class AppSettings
{
    public string Language { get; set; } = SettingsService.LanguageSystem;

    /// <summary>
    /// Retained for shape compatibility with the copied drawer code, which
    /// looks up the managed storage root via the FileWidget slice.
    /// </summary>
    public FileWidgetSlice FileWidget { get; set; } = new();

    /// <summary>User-facing drawer surface appearance shared by all drawers.</summary>
    public DrawerAppearanceSlice DrawerAppearance { get; set; } = new();
}

public sealed class FileWidgetSlice
{
    public string? DefaultManagedStorageRootPath { get; set; }
}

public sealed class DrawerAppearanceSlice
{
    /// <summary>"Card" (theme card), "Color" (user color) or "Image" (user picture).</summary>
    public string Mode { get; set; } = "Card";

    /// <summary>Background color as #AARRGGBB; alpha decides translucency.</summary>
    public string Color { get; set; } = "#B31F1F1F";

    /// <summary>Local picture path for the Image mode; missing files fall back to the card.</summary>
    public string ImagePath { get; set; } = string.Empty;
}
