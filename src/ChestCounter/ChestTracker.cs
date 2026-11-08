using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using ChestReplayReader;

namespace ChestCounter;

/// <summary>What the overlay shows.</summary>
public sealed record CounterSnapshot(
    int Total,
    int Today,
    int LastMatch,
    int BestMatch,
    bool InMatch,
    int Matches,
    int Pending,
    bool Ready,
    string? Problem);

/// <summary>One scanned replay, persisted so restarts don't re-read everything and so matches
/// still count after Fortnite deletes old replays.</summary>
public sealed class CacheEntry
{
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public DateTime ParsedAtUtc { get; set; }
    public int ParserVersion { get; set; }

    public string? ReplayGuid { get; set; }
    public DateTime? MatchStartUtc { get; set; }
    public uint LengthMs { get; set; }
    /// <summary>The replay is still being recorded (can't be read until the match ends).</summary>
    public bool IsLive { get; set; }
    public bool Complete { get; set; }
    public string? Error { get; set; }

    public int Chests { get; set; }
    public Dictionary<string, int> ChestsByType { get; set; } = new();

    public DateTime StartUtc => MatchStartUtc ?? LastWriteUtc;

    /// <summary>Fortnite had the file open for writing at the last check (runtime only).</summary>
    [JsonIgnore]
    public bool BeingWritten { get; set; }
}

/// <summary>
/// Scans the Fortnite Demos folder in the background and caches per-replay chest counts.
/// Fortnite only makes a replay readable once the match ends, so new chests are added a few
/// seconds after each match finishes; while a match is being recorded we just report that.
/// </summary>
public sealed class ChestTracker : IDisposable
{
    /// <summary>Bump whenever ChestReader's counting logic changes, to force a re-scan.</summary>
    private const int ParserVersion = 1;

    /// <summary>
    /// A replay still flagged as recording that nobody is writing and that hasn't changed for this long
    /// is a crashed/abandoned match: stop re-checking it on a timer.
    /// </summary>
    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromMinutes(10);

    private readonly Settings _settings;
    private volatile Process? _child; // replay reader currently running, if any
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly CancellationTokenSource _cts = new();
    private FileSystemWatcher? _watcher;
    private Thread? _thread;
    private volatile bool _rescanRequested;
    private volatile int _pending;
    private volatile bool _ready;
    private volatile string? _problem;

    public event Action<CounterSnapshot>? Updated;

    public ChestTracker(Settings settings)
    {
        _settings = settings;
        LoadCache();
    }

    public string DemosFolder => _settings.ResolvedDemosFolder;

    private DateTime CountFromUtc => _settings.CountFrom?.UtcDateTime ?? DateTime.MinValue;

    public void Start()
    {
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "Replay scanner",
            Priority = ThreadPriority.BelowNormal, // never compete with the game
        };
        _thread.Start();
        Publish();
    }

    /// <summary>Forget cached results for replays that still exist and read them again.</summary>
    public void RequestRescan()
    {
        _rescanRequested = true;
        _wake.Set();
    }

    public CounterSnapshot Snapshot()
    {
        lock (_gate)
        {
            // The same recording can exist under two names (e.g. after saving it), so de-duplicate.
            // Matches from before CountFrom (e.g. last season) are ignored.
            var from = CountFromUtc;
            var matches = _entries.Values
                .Where(e => e.StartUtc >= from)
                .GroupBy(e => e.ReplayGuid ?? e.FileName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(e => e.LastWriteUtc).First())
                .ToList();

            var today = DateTime.Today;
            var last = matches.Where(m => !m.IsLive && (m.Complete || m.Chests > 0))
                .OrderByDescending(m => m.StartUtc)
                .FirstOrDefault();

            var inMatch = matches.Any(m => m.IsLive && (m.BeingWritten || DateTime.UtcNow - m.LastWriteUtc < TimeSpan.FromMinutes(2)));

            return new CounterSnapshot(
                Total: matches.Sum(m => m.Chests),
                Today: matches.Where(m => m.StartUtc.ToLocalTime().Date == today).Sum(m => m.Chests),
                LastMatch: last?.Chests ?? 0,
                BestMatch: matches.Where(m => !m.IsLive).Select(m => m.Chests).DefaultIfEmpty(0).Max(),
                InMatch: inMatch,
                Matches: matches.Count,
                Pending: _pending,
                Ready: _ready,
                Problem: _problem);
        }
    }

    private void Loop()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                EnsureWatcher();
                ScanOnce(token);
            }
            catch (Exception ex)
            {
                AppPaths.Log("Scan failed: " + ex);
            }

            WaitHandle.WaitAny(new[] { token.WaitHandle, _wake }, TimeSpan.FromSeconds(Math.Max(1, _settings.PollSeconds)));
        }
    }

    private void ScanOnce(CancellationToken token)
    {
        var folder = DemosFolder;
        if (!Directory.Exists(folder))
        {
            SetProblem($"Replay folder not found: {folder}");
            return;
        }

        SetProblem(null);

        if (_rescanRequested)
        {
            _rescanRequested = false;
            lock (_gate)
            {
                foreach (var entry in _entries.Values)
                {
                    entry.ParserVersion = 0; // forces a re-read of every replay that's still on disk
                }
            }
        }

        // Newest first, so the latest match shows up right away on the first run.
        var files = new DirectoryInfo(folder).EnumerateFiles("*.replay")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();

        var todo = files.Where(NeedsRead).ToList();
        if (todo.Count > 0)
        {
            AppPaths.Trace($"Scan: {files.Count} replays on disk, {todo.Count} to read");
        }

        _pending = todo.Count;
        if (todo.Count > 0)
        {
            Publish();
        }

        var changed = false;
        try
        {
            foreach (var file in todo)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                file.Refresh();
                var entry = ReadReplay(file);
                AppPaths.Trace($"Read {file.Name}: live={entry.IsLive} writing={entry.BeingWritten} complete={entry.Complete} chests={entry.Chests} error={entry.Error}");
                CacheEntry? previous;
                lock (_gate)
                {
                    _entries.TryGetValue(file.Name, out previous);
                    _entries[file.Name] = entry;
                }

                // Re-checking a match in progress usually finds nothing new: don't rewrite the history for that.
                var differs = previous is null || !SameResult(previous, entry);
                changed |= differs;
                _pending--;
                if (differs || todo.Count > 1)
                {
                    Publish();
                }
            }
        }
        finally
        {
            // Even if something above throws, never leave the overlay showing a stale "N left".
            _pending = 0;
            if (changed)
            {
                SaveCache();
            }
        }

        if (changed || !_ready)
        {
            _ready = true;
            Publish();
        }
    }

    private static bool SameResult(CacheEntry a, CacheEntry b) =>
        a.IsLive == b.IsLive
        && a.BeingWritten == b.BeingWritten
        && a.Chests == b.Chests
        && a.Complete == b.Complete
        && a.Error == b.Error
        && a.ParserVersion == b.ParserVersion
        && (a.IsLive || (a.Size == b.Size && a.LastWriteUtc == b.LastWriteUtc));

    private bool NeedsRead(FileInfo file)
    {
        CacheEntry? entry;
        lock (_gate)
        {
            _entries.TryGetValue(file.Name, out entry);
        }

        // Last written before the cutoff means the whole match was before it: no need to read it.
        if (file.LastWriteTimeUtc < CountFromUtc)
        {
            return false;
        }

        if (entry is null || entry.ParserVersion != ParserVersion)
        {
            return true;
        }

        var checkedRecently = DateTime.UtcNow - entry.ParsedAtUtc < TimeSpan.FromSeconds(Math.Max(2, _settings.LiveReparseSeconds));

        // While Fortnite holds a replay open, Windows' folder listing keeps reporting its old size and
        // time, so a recording replay is re-checked on a timer rather than when it looks changed.
        // That check only reads the file header, so do it often to pick up the match ending quickly.
        if (entry.IsLive && (entry.BeingWritten || DateTime.UtcNow - entry.LastWriteUtc < AbandonedAfter))
        {
            return DateTime.UtcNow - entry.ParsedAtUtc >= TimeSpan.FromSeconds(2);
        }

        if (entry.Size == file.Length && entry.LastWriteUtc == file.LastWriteTimeUtc)
        {
            return false;
        }

        // Changed since we read it: read it again, but not more often than LiveReparseSeconds.
        return !checkedRecently;
    }

    /// <summary>True if another program (Fortnite) currently has the file open for writing.</summary>
    private static bool IsBeingWritten(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private CacheEntry ReadReplay(FileInfo file)
    {
        var entry = new CacheEntry
        {
            FileName = file.Name,
            Size = file.Length,
            LastWriteUtc = file.LastWriteTimeUtc,
            ParsedAtUtc = DateTime.UtcNow,
            ParserVersion = ParserVersion,
        };

        // A match in progress can't be read yet (no encryption key until it ends); NeedsRead re-checks it.
        entry.BeingWritten = IsBeingWritten(file.FullName);
        if (file.Length == 0 || ReplayFile.IsStillRecording(file.FullName))
        {
            entry.IsLive = entry.BeingWritten || file.Length > 0;
            if (!entry.IsLive)
            {
                entry.Error = "empty file";
            }

            return entry;
        }

        try
        {
            // Read in a child process with a time limit, so a replay the parser chokes on is skipped
            // instead of hanging the scan (see ReplayWorker).
            var timeout = TimeSpan.FromSeconds(Math.Max(1, _settings.ReplayReadTimeoutSeconds));
            var result = ReplayWorker.Read(file.FullName, timeout, child => _child = child, out var failure);
            if (result is null)
            {
                if (entry.BeingWritten)
                {
                    entry.IsLive = true; // locked by the game: a match in progress; re-checked on a timer
                }
                else
                {
                    // Not retried until the file changes, so a bad replay costs one attempt, not one per poll.
                    entry.Error = failure;
                    AppPaths.Log($"{file.Name}: {failure}");
                }

                return entry;
            }

            entry.ReplayGuid = result.ReplayGuid;
            entry.MatchStartUtc = result.MatchStartUtc;
            entry.LengthMs = result.LengthMs;
            entry.IsLive = result.IsLive;
            entry.Complete = result.Complete;
            entry.Error = result.Error;
            entry.Chests = result.ChestsOpened;
            entry.ChestsByType = result.MyChests
                .GroupBy(c => c.ChestClass)
                .ToDictionary(g => g.Key, g => g.Count());

            if (result.Error != null && !result.IsLive)
            {
                AppPaths.Log($"{file.Name}: read stopped early ({result.Error}); counted {result.ChestsOpened} chests.");
            }
        }
        catch (IOException ex)
        {
            if (entry.BeingWritten)
            {
                // Locked by the game: it's a match in progress; NeedsRead re-checks it on a timer.
                entry.IsLive = true;
            }
            else
            {
                // Vanished or briefly locked mid-read; try again on the next pass.
                entry.ParserVersion = 0;
                entry.Error = ex.Message;
            }
        }
        catch (Exception ex)
        {
            entry.Error = ex.Message;
            AppPaths.Log($"{file.Name}: {ex}");
        }

        return entry;
    }

    private void EnsureWatcher()
    {
        if (_watcher != null || !Directory.Exists(DemosFolder))
        {
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(DemosFolder, "*.replay")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            };
            _watcher.Created += (_, _) => _wake.Set();
            _watcher.Changed += (_, _) => _wake.Set();
            _watcher.Renamed += (_, _) => _wake.Set();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            AppPaths.Log("File watcher unavailable, polling only: " + ex.Message);
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    private void SetProblem(string? problem)
    {
        if (_problem != problem)
        {
            _problem = problem;
            Publish();
        }
    }

    private void Publish() => Updated?.Invoke(Snapshot());

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(AppPaths.CacheFile))
            {
                return;
            }

            var list = JsonSerializer.Deserialize<List<CacheEntry>>(File.ReadAllText(AppPaths.CacheFile));
            foreach (var entry in list ?? new List<CacheEntry>())
            {
                _entries[entry.FileName] = entry;
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("Replay cache unreadable, starting fresh: " + ex.Message);
        }
    }

    private void SaveCache()
    {
        try
        {
            List<CacheEntry> list;
            lock (_gate)
            {
                list = _entries.Values.OrderBy(e => e.StartUtc).ToList();
            }

            Directory.CreateDirectory(AppPaths.DataFolder);
            var temp = AppPaths.CacheFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, AppPaths.CacheFile, overwrite: true);
            AppPaths.Trace($"Saved cache: {list.Count} entries");
        }
        catch (Exception ex)
        {
            AppPaths.Log("Could not save replay cache: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _child?.Kill(entireProcessTree: true);
        }
        catch
        {
            // already gone
        }

        _thread?.Join(TimeSpan.FromSeconds(3));
        _watcher?.Dispose();
        _wake.Dispose();
    }
}
