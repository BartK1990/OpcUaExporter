using System.IO;
using System.Text.Json;

namespace OpcUaExporter.Services;

public enum AppTheme
{
    Dark,
    Light
}

/// <summary>
/// Tracks the user's light/dark theme preference and other small UI
/// preferences, persisting them to
/// %LocalAppData%\OpcUaExporter\app-settings.json so they are restored on
/// the next launch.
/// </summary>
public class ThemeService
{
    private class SettingsFile
    {
        public string Theme { get; set; } = nameof(AppTheme.Dark);
        public bool CompactSelectedRows { get; set; }
    }

    public event Action? ThemeChanged;
    public event Action? CompactSelectedRowsChanged;

    public AppTheme Theme { get; private set; } = AppTheme.Dark;
    public bool CompactSelectedRows { get; private set; }

    private static string SettingsPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpcUaExporter");

            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "app-settings.json");
        }
    }

    public ThemeService()
    {
        Load();
    }

    public void Toggle() => SetTheme(Theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);

    public void SetTheme(AppTheme theme)
    {
        if (Theme == theme)
            return;

        Theme = theme;
        Save();
        ThemeChanged?.Invoke();
    }

    public void SetCompactSelectedRows(bool compact)
    {
        if (CompactSelectedRows == compact)
            return;

        CompactSelectedRows = compact;
        Save();
        CompactSelectedRowsChanged?.Invoke();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return;

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<SettingsFile>(json);
            if (settings is null)
                return;

            if (Enum.TryParse<AppTheme>(settings.Theme, ignoreCase: true, out var theme))
                Theme = theme;
            CompactSelectedRows = settings.CompactSelectedRows;
        }
        catch
        {
            // Missing/corrupt settings file – fall back to the defaults.
        }
    }

    private void Save()
    {
        try
        {
            var settings = new SettingsFile { Theme = Theme.ToString(), CompactSelectedRows = CompactSelectedRows };
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort persistence – ignore write failures.
        }
    }
}
