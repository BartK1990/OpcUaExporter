namespace OpcUaExporter;

/// <summary>
/// Single source of truth for the per-user application data locations.
/// Everything the app persists — the OPC UA client PKI, the crash log, UI
/// settings and the last-used connection profile — lives under
/// <c>%LocalAppData%\OpcUaExporter\</c>.
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// <c>%LocalAppData%\OpcUaExporter</c>. The directory is created on first access.
    /// </summary>
    public static string RootDirectory
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpcUaExporter");

            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>OPC UA client certificate stores (own/trusted/issuer/rejected).</summary>
    public static string PkiDirectory => Path.Combine(RootDirectory, "pki");

    /// <summary>Log written by the global unhandled-exception handlers.</summary>
    public static string CrashLogFile => Path.Combine(RootDirectory, "crash.log");

    /// <summary>Persisted UI preferences (theme, row density).</summary>
    public static string SettingsFile => Path.Combine(RootDirectory, "app-settings.json");

    /// <summary>Path of the connection profile that was last saved or loaded.</summary>
    public static string LastProfilePointerFile => Path.Combine(RootDirectory, "last-profile.txt");
}
