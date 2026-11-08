using System;
using System.IO;

namespace ChestCounter;

internal static class AppPaths
{
    /// <summary>
    /// %LOCALAPPDATA%\FortniteChestCounter, or the FORTNITE_CHEST_COUNTER_DATA environment variable if set
    /// (lets a test copy run beside the real one without touching its history).
    /// </summary>
    public static string DataFolder { get; } =
        Environment.GetEnvironmentVariable("FORTNITE_CHEST_COUNTER_DATA") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteChestCounter");

    public static bool IsCustomDataFolder => Environment.GetEnvironmentVariable("FORTNITE_CHEST_COUNTER_DATA") is { Length: > 0 };

    /// <summary>Extra logging, enabled with FORTNITE_CHEST_COUNTER_DEBUG=1.</summary>
    public static bool DebugLog { get; } = Environment.GetEnvironmentVariable("FORTNITE_CHEST_COUNTER_DEBUG") == "1";

    public static void Trace(string message)
    {
        if (DebugLog)
        {
            Log(message);
        }
    }

    public static string SettingsFile => Path.Combine(DataFolder, "settings.json");

    public static string CacheFile => Path.Combine(DataFolder, "replay-cache.json");

    public static string LogFile => Path.Combine(DataFolder, "log.txt");

    public static string DefaultDemosFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame", "Saved", "Demos");

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            var info = new FileInfo(LogFile);
            if (info.Exists && info.Length > 1_000_000)
            {
                info.Delete();
            }

            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never take the overlay down.
        }
    }
}
