namespace ChestReplayReader;

/// <summary>A chest the replay's recording player opened.</summary>
/// <param name="Time">Seconds since the replay started recording.</param>
/// <param name="ChestClass">Blueprint class, e.g. "Tiered_Chest_Athena" or "AlwaysSpawn_RareChest".</param>
/// <param name="ChestId">Stable actor name of the chest within the match.</param>
public sealed record ChestOpen(float Time, string ChestClass, string ChestId);

/// <summary>Result of scanning one .replay file.</summary>
public sealed class MatchResult
{
    public string FilePath { get; set; } = "";
    public long FileSize { get; set; }
    public DateTime LastWriteUtc { get; set; }

    /// <summary>Unique id of the recording (survives the file being renamed or copied).</summary>
    public string? ReplayGuid { get; set; }
    public string? FriendlyName { get; set; }
    public DateTime? MatchStartUtc { get; set; }
    public uint LengthMs { get; set; }
    public string? Branch { get; set; }

    /// <summary>True while Fortnite is still recording this replay.</summary>
    public bool IsLive { get; set; }

    /// <summary>False if parsing stopped early (file still being written, or unsupported data).</summary>
    public bool Complete { get; set; }
    public string? Error { get; set; }

    /// <summary>True if the recording player's pawn was found (otherwise the count can't be attributed).</summary>
    public bool RecorderFound { get; set; }

    public List<ChestOpen> MyChests { get; set; } = new();

    /// <summary>Chests seen being opened by anyone (you, teammates, enemies) while in view.</summary>
    public int ChestsOpenedByAnyone { get; set; }

    public int ChestsOpened => MyChests.Count;
}
