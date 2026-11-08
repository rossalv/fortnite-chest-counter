using System;
using System.IO;
using System.Text.Json;

namespace ChestCounter;

/// <summary>User settings, stored in %LOCALAPPDATA%\FortniteChestCounter\settings.json (editable by hand).</summary>
public sealed class Settings
{
    /// <summary>Folder with .replay files. Empty = Fortnite's default Demos folder.</summary>
    public string? DemosFolder { get; set; }

    /// <summary>
    /// Only matches that started at or after this moment count. Default: the Chapter 7 Season 3 launch
    /// day, Nov 1 2026, 12:00 AM Eastern (still daylight time then, UTC-4). Set to null to count everything.
    /// </summary>
    public DateTimeOffset? CountFrom { get; set; } = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(-4));

    public double Left { get; set; } = 24;
    public double Top { get; set; } = 24;

    /// <summary>Overlay size multiplier.</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>Overall overlay opacity, 0.2 - 1.0.</summary>
    public double Opacity { get; set; } = 1.0;

    /// <summary>Show the "this match / today" line under the total.</summary>
    public bool ShowDetails { get; set; } = true;

    public bool Hidden { get; set; }

    /// <summary>How often to look for new or growing replays.</summary>
    public int PollSeconds { get; set; } = 5;

    /// <summary>A replay that takes longer than this to read is skipped (and logged) instead of stalling the scan.</summary>
    public double ReplayReadTimeoutSeconds { get; set; } = 30;

    /// <summary>Minimum time between checks of a replay that is still being recorded.</summary>
    public int LiveReparseSeconds { get; set; } = 10;

    public string ResolvedDemosFolder =>
        string.IsNullOrWhiteSpace(DemosFolder) ? AppPaths.DefaultDemosFolder : Environment.ExpandEnvironmentVariables(DemosFolder);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new Settings();
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("Could not read settings, using defaults: " + ex.Message);
        }

        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataFolder);
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            AppPaths.Log("Could not save settings: " + ex.Message);
        }
    }
}
