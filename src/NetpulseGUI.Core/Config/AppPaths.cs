namespace NetpulseGUI.Core.Config;

public static class AppPaths
{
    /// <summary>Папка данных пользователя: %LOCALAPPDATA%\NetpulseGUI.</summary>
    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetpulseGUI");

    public static string DatabasePath => Path.Combine(DataDir, "netpulse.db");
    public static string SettingsPath => Path.Combine(DataDir, "settings.json");
    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string BackupsDir => Path.Combine(DataDir, "backups");

    public const string EtwSessionName = "NetpulseGUI-Kernel-Network";
    public const string AutoStartTaskName = "NetpulseGUI Autostart";

    // Имена до переименования NetPulse → NetpulseGUI (для переноса данных и очистки).
    public const string LegacyEtwSessionName = "NetPulse-Kernel-Network";
    public const string LegacyAutoStartTaskName = "NetPulse Autostart";

    private static string LegacyDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetPulse");

    /// <summary>Переносит данные из старой папки %LOCALAPPDATA%\NetPulse, если новой ещё нет.</summary>
    public static bool MigrateLegacyDataDir()
    {
        try
        {
            if (Directory.Exists(DataDir) || !Directory.Exists(LegacyDataDir)) return false;
            Directory.Move(LegacyDataDir, DataDir);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
