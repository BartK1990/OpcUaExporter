using System.IO;
using System.Text.Json;

namespace OpcUaExporter.Services;

public enum AppTheme
{
    Dark,
    Light
}

/// <summary>
/// Tracks the user's light/dark theme preference and persists it to
/// %LocalAppData%\OpcUaExporter\app-settings.json so it is restored on the
/// next launch.
/// </summary>
public class ThemeService
{
    private class SettingsFile
    {
        public string Theme { get; set; } = nameof(AppTheme.Dark);
    }

    public event Action? ThemeChanged;

    public AppTheme Theme { get; private set; } = AppTheme.Dark;

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

    private void Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return;

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<SettingsFile>(json);
            if (settings is not null && Enum.TryParse<AppTheme>(settings.Theme, ignoreCase: true, out var theme))
                Theme = theme;
        }
        catch
        {
            // Missing/corrupt settings file – fall back to the default theme.
        }
    }

    private void Save()
    {
        try
        {
            var settings = new SettingsFile { Theme = Theme.ToString() };
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort persistence – ignore write failures.
        }
    }
}
