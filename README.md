# Fortnite Chest Counter

An always-on-top overlay for Windows that counts every chest **you** have opened in Fortnite. It
gets the count by reading the replays Fortnite saves on your PC. It never touches the game itself.

```
 [chest] 1,815 chests
 Last match 35 · Today 74 · Best 81
```

## Download and run

1. Download `FortniteChestCounter.exe` from the [Releases](../../releases) page, or [build it yourself](#building).
2. Install the free [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
   (Windows x64) if you don't already have it. If it's missing, Windows offers a download link
   when you open the app.
3. Double-click the exe. Windows may show "Windows protected your PC" because the app isn't
   code-signed: click **More info → Run anyway**.

A chest icon appears in the system tray, and the counter appears in the top-left corner of your
screen. The first run reads every replay since the counting date, which takes about 40 seconds for
80 replays. After that, results are cached and startup is instant.

In Fortnite:

- **Replays must be recorded:** Settings → Game → Replays → *Record Replays* **On**.
- **Display mode must be *Windowed Fullscreen*** (or Windowed). No overlay window can draw over
  exclusive *Fullscreen*.

## Controls

| | |
|---|---|
| **Ctrl+Alt+C** | Show/hide the overlay (works in-game) |
| Tray → **Move overlay** | Drag the counter where you want it, then right-click it (or untick) to lock it |
| Tray → **Size** | Small / Normal / Large / Extra large |
| Tray → **Show match / today line** | Toggle the second line |
| Tray → **Rescan all replays** | Re-read every replay in the counting window from scratch |
| Tray → **Start with Windows** | Launch automatically at sign-in |

The overlay is click-through and never takes focus, so it can't interfere with your game input.

## How it counts

- Every chest in a replay records which player's character opened it (`SearchingPawn`). The replay
  also records which player made the recording. A chest counts when *your* character opened it.
- Any chest type counts: regular, rare, buried, relic, booty, and so on, including new types in
  future seasons. Ammo boxes, cash registers, safes, and other containers don't count.
- Each chest counts once per match, even if it streams out of view and back in.
- **Best** is your most chests in a single match.
- **Counting window:** by default only matches since Nov 1, 2026, 12:00 AM Eastern count (the start
  of Chapter 7 Season 3). For a new season, set `CountFrom` in `settings.json`, e.g.
  `"2027-02-01T00:00:00-05:00"`, or set it to `null` to count everything, then restart the app.
- Teammates' chests aren't counted. A replay only records chests close enough to your own
  character, so their counts would be incomplete.

### When it updates

The counter goes up **within a few seconds of each match ending**, with a little "+N" pop.
During a match the second line shows **● Match in progress**.

It can't update mid-match. Fortnite encrypts replays and only writes the decryption key into the
file when the match is over, so nothing in the file can be read until then. This is deliberate on
Epic's part, so live match data can't be pulled out of a replay.

### History

Fortnite only keeps your most recent replays (about 100). Every match the counter has read is stored
in its cache, so your total keeps growing after Fortnite deletes old replays. Matches that were
deleted before you first ran the app can't be recovered.

The app doesn't need to be running while you play: when it starts, it catches up on every replay it
hasn't seen yet. The only way to lose a match is to play more than ~100 matches without opening it,
because Fortnite deletes those replays first. *Start with Windows* (tray menu) prevents that.

## Resource use

Measured on a desktop with a 32-thread CPU:

| | CPU | Memory |
|---|---|---|
| Idle / in a match | ~0% (a few KB read every few seconds) | ~50 MB |
| Right after a match ends | ~0.5 s, low priority | the overlay stays ~50 MB |
| First run ever (~80 replays) | ~40 s, low priority, one replay at a time | the overlay stays ~55 MB |

No GPU use; the overlay only redraws when the number changes.

Each replay is read by a short-lived, low-priority copy of the app. Its memory goes back to Windows
as soon as it exits, and a replay that can't be read within 30 seconds is skipped instead of
stalling the scan. Skipped replays are listed in `log.txt`, and the limit is `ReplayReadTimeoutSeconds`
in `settings.json`.

## Files

All files are in `%LOCALAPPDATA%\FortniteChestCounter\` (Tray → *Open settings folder*):

- `settings.json`: position, size, opacity, counting window (`CountFrom`), replay folder (if it isn't
  the default), and poll timing
- `replay-cache.json`: per-match results, i.e. your history. Delete it to start the count over.
- `log.txt`: errors, if any

## Command-line scanner

`ChestScan.exe` prints a per-match table straight from the replay files, without using the cache.

```
ChestScan                                  # scan the default Demos folder (all matches)
ChestScan --since 2026-11-01T00:00-04:00   # only matches since a date (same total as the overlay)
ChestScan -v <file-or-folder>              # list every chest with its time and type
ChestScan --mem ...                        # also print memory use per replay
```

## Building

Requires Windows and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```
dotnet publish src\ChestCounter -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o publish
dotnet publish src\ChestScan -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o publish
```

Project layout:

- `src/ChestReplayReader`: replay parsing, built on [FortniteReplayDecompressor](https://github.com/Shiqan/FortniteReplayDecompressor)'s
  `Unreal.Core` (decryption, Oodle decompression, network stream). `ChestReader.cs` decodes only
  the chest, pawn, and game-state fields it needs.
- `src/ChestCounter`: the WPF overlay, tray icon, folder watcher, and cache.
- `src/ChestScan`: the console scanner.

If a Fortnite update ever breaks parsing, update the `Unreal.Core` / `Unreal.Encryption` package
versions first, then bump `ParserVersion` in `ChestTracker.cs` so the cache re-reads every replay.

For testing, two environment variables let a second copy run beside your real one:
`FORTNITE_CHEST_COUNTER_DATA=<folder>` uses a different data folder (and its own single-instance
lock), and `FORTNITE_CHEST_COUNTER_DEBUG=1` writes a detailed `log.txt`.

## Safety

The app only reads replay files from disk and draws its own window. It doesn't touch the Fortnite
process, inject anything, or read game memory.

## Credits and license

- Replay decoding uses [FortniteReplayDecompressor](https://github.com/Shiqan/FortniteReplayDecompressor)
  by Shiqan (MIT). A few methods in `ChestReader.cs` mirror its code with changes.
- Released under the [MIT License](LICENSE).

Not affiliated with or endorsed by Epic Games. Fortnite is a trademark of Epic Games, Inc.
