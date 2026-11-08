// Command-line scanner: prints chests opened per replay.
//   ChestScan                      scan the default Fortnite Demos folder
//   ChestScan <folder|file>...     scan specific folders / files
//   -v                             also list every chest you opened (time + type)
//   --since 2026-11-01T00:00-04:00 only matches that started at/after this time (date alone = local midnight)
using System.Diagnostics;
using System.Globalization;
using ChestReplayReader;

var verbose = args.Contains("-v");
var memoryReport = args.Contains("--mem");
DateTime sinceUtc = DateTime.MinValue;
var targets = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "-v" || args[i] == "--mem") continue;
    if (args[i] == "--since" && i + 1 < args.Length)
    {
        sinceUtc = DateTimeOffset.Parse(args[++i], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal).UtcDateTime;
        continue;
    }

    targets.Add(args[i]);
}

if (targets.Count == 0)
{
    targets.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame", "Saved", "Demos"));
}

var files = targets
    .SelectMany(t => Directory.Exists(t) ? Directory.EnumerateFiles(t, "*.replay") : new[] { t })
    .Where(f => File.GetLastWriteTimeUtc(f) >= sinceUtc)
    .OrderBy(f => File.GetLastWriteTimeUtc(f))
    .ToList();
var skippedBefore = 0;
(int Chests, string File, DateTime? StartUtc) best = (0, "", null);

var reader = new ChestReader();
var total = 0;
var problems = 0;
var guids = new HashSet<string>();
var byClass = new Dictionary<string, int>();
var sw = Stopwatch.StartNew();

Console.WriteLine($"{"Replay",-42} {"Start (local)",-17} {"Len",6} {"Mine",5} {"Seen",5}  Notes");
foreach (var file in files)
{
    if (ReplayFile.IsStillRecording(file))
    {
        Console.WriteLine($"{Path.GetFileName(file),-42} {"",-17} {"",6} {"",5} {"",5}  RECORDING (match in progress; readable once it ends)");
        continue;
    }

    var proc = Process.GetCurrentProcess();
    var peakBefore = proc.PeakWorkingSet64;
    var commitBefore = proc.PeakPagedMemorySize64;
    var r = reader.Read(file);
    if (memoryReport)
    {
        proc.Refresh();
        var rise = proc.PeakWorkingSet64 - peakBefore;
        Console.WriteLine($"      mem: peak working set {proc.PeakWorkingSet64 / 1048576} MB (+{rise / 1048576} MB on this replay), peak commit {proc.PeakPagedMemorySize64 / 1048576} MB (+{(proc.PeakPagedMemorySize64 - commitBefore) / 1048576}), file {new FileInfo(file).Length / 1048576} MB, corrupt blocks skipped {reader.RejectedCorruptBlocks}");
        reader = new ChestReader();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }
    if (r.MatchStartUtc < sinceUtc)
    {
        skippedBefore++;
        continue;
    }

    total += r.ChestsOpened;
    if (r.ChestsOpened > best.Chests) best = (r.ChestsOpened, file, r.MatchStartUtc);
    if (r.ReplayGuid != null) guids.Add(r.ReplayGuid);
    foreach (var c in r.MyChests) byClass[c.ChestClass] = byClass.GetValueOrDefault(c.ChestClass) + 1;

    var notes = new List<string>();
    if (r.IsLive) notes.Add("LIVE");
    if (!r.RecorderFound) notes.Add("no recorder pawn");
    if (!r.Complete) notes.Add("partial: " + r.Error);
    if (!r.Complete || !r.RecorderFound) problems++;

    var start = r.MatchStartUtc?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "?";
    var len = TimeSpan.FromMilliseconds(r.LengthMs).ToString(@"mm\:ss");
    Console.WriteLine($"{Path.GetFileName(file),-42} {start,-17} {len,6} {r.ChestsOpened,5} {r.ChestsOpenedByAnyone,5}  {string.Join("; ", notes)}");

    if (verbose)
    {
        foreach (var c in r.MyChests)
        {
            Console.WriteLine($"      {TimeSpan.FromSeconds(c.Time):mm\\:ss}  {c.ChestClass,-38} {c.ChestId}");
        }
    }
}

Console.WriteLine();
Console.WriteLine($"Replays: {files.Count - skippedBefore} (unique recordings: {guids.Count}), with problems: {problems}"
    + (sinceUtc > DateTime.MinValue ? $", counting matches since {sinceUtc.ToLocalTime():yyyy-MM-dd HH:mm} local" : ""));
Console.WriteLine($"Chests you opened: {total}");
if (best.Chests > 0)
{
    Console.WriteLine($"Best match: {best.Chests} chests ({best.StartUtc?.ToLocalTime():MMM d h:mm tt}, {Path.GetFileName(best.File)})");
}
foreach (var (cls, n) in byClass.OrderByDescending(kv => kv.Value))
{
    Console.WriteLine($"  {n,5}  {cls}");
}
Console.WriteLine($"Scan time: {sw.Elapsed.TotalSeconds:F1}s");
