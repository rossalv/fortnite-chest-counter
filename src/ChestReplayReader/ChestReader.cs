using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Unreal.Core;
using Unreal.Core.Attributes;
using Unreal.Core.Contracts;
using Unreal.Core.Models;
using Unreal.Core.Models.Enums;

namespace ChestReplayReader;

/// <summary>
/// The replay's spectator controller sends an extra NetPlayerIndex byte when its channel opens;
/// Unreal.Core needs this marker to skip it.
/// </summary>
[PlayerController("BP_ReplayPC_Athena_C")]
internal sealed class ReplayPlayerControllerMarker
{
}

/// <summary>
/// Reads a Fortnite .replay file and finds the chests opened by the player who recorded it.
///
/// How attribution works: every searchable chest replicates <c>SearchingPawn</c> (the pawn that
/// opened it) alongside <c>bAlreadySearched</c>. The game state replicates
/// <c>RecorderPlayerState</c>, and each player pawn replicates its <c>PlayerState</c>, so we can tell
/// exactly which chests were opened by the recording player's pawn(s).
///
/// Instead of a hard-coded list of chest blueprints (which change every season), any replicated
/// class whose name contains "Chest" is decoded by hand, reading only the few fields we need.
/// </summary>
public sealed class ChestReader : ReplayReader<Replay>
{
    private enum GroupKind { Ignore, GameState, Pawn, ReplayController, Chest }

    private readonly record struct ChestUpdate(
        float Time, string ChestId, string ChestClass,
        uint? SearchingPawn, bool? Searched, bool? Destroyed, int? AnimCount)
    {
        public bool IsOpen => Searched == true || Destroyed == true || AnimCount > 0;
    }

    private readonly FrameClock _clock;
    private readonly Dictionary<string, GroupKind> _kinds = new(StringComparer.Ordinal);

    // Per-replay state (reset by Read).
    private uint? _recorderPlayerState;
    private uint? _controllerPlayerState;
    private readonly Dictionary<uint, uint> _pawnToPlayerState = new();
    private readonly List<ChestUpdate> _updates = new();
    private readonly Dictionary<uint, string> _dynamicChestIds = new();
    private readonly List<(string Class, double X, double Y, string Id)> _dynamicChests = new();

    /// <summary>Two spawned chests of the same class closer than this (in cm) are the same chest.</summary>
    private const double SameSpawnTolerance = 150;

    public ChestReader() : this(new FrameClock())
    {
    }

    private ChestReader(FrameClock clock) : base(clock, ParseMode.Minimal)
    {
        _clock = clock;
    }

    /// <summary>Scan one replay. Never throws for bad/partial data; check <see cref="MatchResult.Error"/>.</summary>
    public MatchResult Read(string path)
    {
        var info = new FileInfo(path);
        var result = new MatchResult
        {
            FilePath = path,
            FileSize = info.Length,
            LastWriteUtc = info.LastWriteTimeUtc,
        };

        _recorderPlayerState = null;
        _controllerPlayerState = null;
        _pawnToPlayerState.Clear();
        _updates.Clear();
        _dynamicChestIds.Clear();
        _dynamicChests.Clear();
        _clock.FrameTime = 0;

        try
        {
            using var archive = new Unreal.Core.BinaryReader(ReadShared(path));
            ReadReplay(archive);
            result.Complete = true;
        }
        catch (Exception ex)
        {
            // An in-progress replay usually ends in a half-written chunk. Keep everything read so far.
            result.Error = ex.Message;
            try { Cleanup(); } catch { /* reader state is reset on the next ReadReplay anyway */ }
        }

        FillMetadata(result);
        Resolve(result);
        return result;
    }

    /// <summary>Fortnite keeps the replay open while recording, so allow concurrent writers.</summary>
    private static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[fs.Length];
        fs.ReadExactly(buffer);
        return buffer;
    }

    private void FillMetadata(MatchResult result)
    {
        var replay = Replay;
        if (replay is null)
        {
            return;
        }

        if (replay.Info is { } info)
        {
            result.FriendlyName = info.FriendlyName?.Trim();
            result.LengthMs = info.LengthInMs;
            result.IsLive = info.IsLive;
            if (info.Timestamp > DateTime.MinValue)
            {
                // Written by the game as local time (matches the "UnsavedReplay-yyyy.MM.dd-HH.mm.ss" file name).
                result.MatchStartUtc = DateTime.SpecifyKind(info.Timestamp, DateTimeKind.Local).ToUniversalTime();
            }
        }

        if (replay.Header is { } header)
        {
            result.ReplayGuid = string.IsNullOrWhiteSpace(header.Guid) ? null : header.Guid;
            result.Branch = header.Branch;
        }
    }

    private void Resolve(MatchResult result)
    {
        var recorder = _recorderPlayerState ?? _controllerPlayerState;
        var myPawns = new HashSet<uint>();
        if (recorder is uint ps)
        {
            foreach (var (pawn, owner) in _pawnToPlayerState)
            {
                if (owner == ps)
                {
                    myPawns.Add(pawn);
                }
            }
        }

        result.RecorderFound = myPawns.Count > 0;

        foreach (var chest in _updates.GroupBy(u => u.ChestId))
        {
            var updates = chest.ToList();

            // Opened by anyone while we watched: someone was recorded searching it, or it flipped to searched.
            bool seenClosed = false, openedByAnyone = false;
            foreach (var u in updates)
            {
                if (u.SearchingPawn is > 0 && u.IsOpen) { openedByAnyone = true; break; }
                if (u.Searched == true && seenClosed) { openedByAnyone = true; break; }
                if (!u.IsOpen) seenClosed = true;
            }

            if (openedByAnyone)
            {
                result.ChestsOpenedByAnyone++;
            }

            // Opened by me: my pawn is recorded as the searcher, and the chest is (or later becomes) opened.
            var mine = updates.FindIndex(u => u.SearchingPawn is uint p && myPawns.Contains(p));
            if (mine >= 0 && updates.Skip(mine).Any(u => u.IsOpen))
            {
                var first = updates[mine];
                result.MyChests.Add(new ChestOpen(first.Time, first.ChestClass, first.ChestId));
            }
        }

        result.MyChests.Sort((a, b) => a.Time.CompareTo(b.Time));
    }

    private GroupKind Classify(string path)
    {
        if (_kinds.TryGetValue(path, out var kind))
        {
            return kind;
        }

        kind = GroupKind.Ignore;
        // Skip RPC parameter groups ("Class:Function") and ClassNetCache groups.
        if (!path.Contains(':') && !path.EndsWith("ClassNetCache", StringComparison.Ordinal))
        {
            var dot = path.LastIndexOf('.');
            var name = dot >= 0 ? path[(dot + 1)..] : path;

            if (name.Contains("Chest", StringComparison.OrdinalIgnoreCase))
                kind = GroupKind.Chest;
            else if (name.Contains("PlayerPawn", StringComparison.Ordinal))
                kind = GroupKind.Pawn;
            else if (name.Contains("ReplayPC", StringComparison.Ordinal))
                kind = GroupKind.ReplayController;
            else if (name.Contains("GameState", StringComparison.Ordinal) && !name.Contains("Component", StringComparison.Ordinal))
                kind = GroupKind.GameState;
        }

        _kinds[path] = kind;
        return kind;
    }

    /// <summary>
    /// Map-placed chests have a stable actor name. Spawned chests (buried, relic, booty, ...) don't, and
    /// the recording client gives them a new network id every time they come back into view, so identify
    /// those by class + spawn position instead.
    /// </summary>
    private string ChestId(uint actor, FVector? location, string groupPath)
    {
        if (_netGuidCache.TryGetPathName(actor, out var name))
        {
            return name;
        }

        if (_dynamicChestIds.TryGetValue(actor, out var known))
        {
            return known;
        }

        string id;
        if (location is null)
        {
            id = $"#{actor}";
        }
        else
        {
            // Match horizontally only: buried chests rise out of the ground, so their Z changes.
            var cls = ClassName(groupPath);
            var match = _dynamicChests.FindIndex(c => c.Class == cls
                && Math.Abs(c.X - location.X) < SameSpawnTolerance
                && Math.Abs(c.Y - location.Y) < SameSpawnTolerance);
            if (match >= 0)
            {
                id = _dynamicChests[match].Id;
            }
            else
            {
                id = $"{cls}@{location.X:F0},{location.Y:F0}";
                _dynamicChests.Add((cls, location.X, location.Y, id));
            }
        }

        _dynamicChestIds[actor] = id;
        return id;
    }

    private static string ClassName(string path)
    {
        var dot = path.LastIndexOf('.');
        var name = dot >= 0 ? path[(dot + 1)..] : path;
        return name.EndsWith("_C", StringComparison.Ordinal) ? name[..^2] : name;
    }

    /// <summary>
    /// Mirrors Unreal.Core's property reader (handle, bit count, payload) for the handful of classes
    /// we care about, decoding only the fields we need and skipping the rest.
    /// See RepLayout.cpp ReceiveProperties_BackwardsCompatible in Unreal Engine.
    /// </summary>
    // Returns true with a null exportGroup for the groups we decode ourselves; callers only use the bool.
#pragma warning disable CS8765
    public override bool ReceiveProperties(FBitArchive archive, NetFieldExportGroup group, uint channelIndex,
        out INetFieldExportGroup? exportGroup, bool enablePropertyChecksum = true, bool netDeltaUpdate = false)
#pragma warning restore CS8765
    {
        var kind = netDeltaUpdate ? GroupKind.Ignore : Classify(group.PathName);
        if (kind == GroupKind.Ignore)
        {
            return base.ReceiveProperties(archive, group, channelIndex, out exportGroup, enablePropertyChecksum, netDeltaUpdate);
        }

        exportGroup = null;
        var channel = Channels[channelIndex];
        if (channel is null)
        {
            return false;
        }

        if (enablePropertyChecksum)
        {
            archive.ReadBit();
        }

        uint? searchingPawn = null, playerState = null, recorderPlayerState = null;
        bool? searched = null, destroyed = null;
        int? animCount = null;

        while (true)
        {
            var handle = archive.ReadIntPacked();
            if (handle == 0)
            {
                break;
            }

            handle--;
            if (!group.IsValidIndex(handle))
            {
                return false;
            }

            var numBits = (int)archive.ReadIntPacked();
            if (numBits == 0)
            {
                continue;
            }

            if (archive.IsError || numBits > archive.GetBitsLeft())
            {
                return false;
            }

            var start = archive.GetBitsLeft();
            switch (kind, group.NetFieldExports[handle]?.Name)
            {
                case (GroupKind.Chest, "SearchingPawn"):
                    searchingPawn = archive.ReadIntPacked();
                    break;
                case (GroupKind.Chest, "bAlreadySearched"):
                    searched = archive.ReadBit();
                    break;
                case (GroupKind.Chest, "bDestroyed"):
                    destroyed = archive.ReadBit();
                    break;
                case (GroupKind.Chest, "SearchAnimationCount") when numBits <= 32:
                    animCount = archive.ReadBitsToInt(numBits);
                    break;
                case (GroupKind.Pawn, "PlayerState"):
                case (GroupKind.ReplayController, "PlayerState"):
                    playerState = archive.ReadIntPacked();
                    break;
                case (GroupKind.GameState, "RecorderPlayerState"):
                    recorderPlayerState = archive.ReadIntPacked();
                    break;
            }

            var consumed = start - archive.GetBitsLeft();
            if (consumed > numBits || archive.IsError)
            {
                return false;
            }

            if (consumed < numBits)
            {
                archive.SkipBits(numBits - consumed);
            }
        }

        switch (kind)
        {
            case GroupKind.Chest when channel.ActorId is uint actor
                                      && (searchingPawn != null || searched != null || destroyed != null || animCount != null):
                var id = ChestId(actor, channel.Actor?.Location, group.PathName);
                _updates.Add(new ChestUpdate(_clock.FrameTime, id, ClassName(group.PathName), searchingPawn, searched, destroyed, animCount));
                break;
            case GroupKind.Pawn when playerState is > 0 && channel.ActorId is uint pawn:
                _pawnToPlayerState[pawn] = playerState.Value;
                break;
            case GroupKind.ReplayController when playerState is > 0:
                _controllerPlayerState ??= playerState;
                break;
            case GroupKind.GameState when recorderPlayerState is > 0:
                _recorderPlayerState ??= recorderPlayerState;
                break;
        }

        return true;
    }

    /// <summary>Real Fortnite classes have at most a few thousand replicated fields.</summary>
    private const uint MaxExportsPerGroup = 1 << 16;

    /// <summary>Corrupt export blocks skipped without allocating (diagnostics).</summary>
    public int RejectedCorruptBlocks { get; private set; }

    /// <summary>
    /// A few replays contain a corrupt export block whose string length reads as ~1 billion characters.
    /// Unreal.Core allocates a buffer that size (up to 2 GB) before the read fails and the packet is
    /// dropped. Walk the block first, checking every length against the data actually left; if one is
    /// impossible, drop the packet the same way (by throwing), just without the giant allocation.
    /// </summary>
    public override void ReceiveNetFieldExportsCompat(FBitArchive bitArchive)
    {
        bitArchive.Mark();
        var impossible = HasImpossibleLength(bitArchive);
        bitArchive.Pop();

        if (impossible)
        {
            RejectedCorruptBlocks++;
            throw new InvalidDataException("Corrupt net field export block (impossible length).");
        }

        base.ReceiveNetFieldExportsCompat(bitArchive);
    }

    /// <summary>
    /// Mirrors the layout read by ReceiveNetFieldExportsCompat / ReadNetFieldExport. Only reports true
    /// for a provably impossible length; if the data merely runs out it stops and lets the original
    /// code handle it. Never reads past the end, so it can't set the archive's error flag.
    /// </summary>
    private static bool HasImpossibleLength(FBitArchive a)
    {
        var version = a.EngineNetworkVersion;
        if (a.GetBitsLeft() < 32) return false;
        var count = a.ReadUInt32();

        for (var i = 0; i < count; i++)
        {
            if (a.GetBitsLeft() < 48) return false; // path index (packed) + flag bit
            a.ReadIntPacked();
            if (a.ReadBit())
            {
                if (SkipFString(a) is not { } ok) return false;
                if (!ok) return true;
                if (a.GetBitsLeft() < 32) return false;
                if (a.ReadUInt32() > MaxExportsPerGroup) return true;
            }

            // ReadNetFieldExport
            if (a.GetBitsLeft() < 1) return false;
            if (!a.ReadBit()) continue;
            if (a.GetBitsLeft() < 72) return false; // handle (packed) + checksum
            a.ReadIntPacked();
            a.ReadUInt32();

            if (version < EngineNetworkVersionHistory.HISTORY_NETEXPORT_SERIALIZE_FIX)
            {
                var strings = version < EngineNetworkVersionHistory.HISTORY_NETEXPORT_SERIALIZATION ? 2 : 1;
                for (var s = 0; s < strings; s++)
                {
                    if (SkipFString(a) is not { } ok) return false;
                    if (!ok) return true;
                }
            }
            else
            {
                // FName: hardcoded index, or string + number
                if (a.GetBitsLeft() < 1) return false;
                if (a.ReadBit())
                {
                    if (a.GetBitsLeft() < 40) return false;
                    if (version < EngineNetworkVersionHistory.HISTORY_CHANNEL_NAMES) a.ReadUInt32(); else a.ReadIntPacked();
                }
                else
                {
                    if (SkipFString(a) is not { } ok) return false;
                    if (!ok) return true;
                    if (a.GetBitsLeft() < 32) return false;
                    a.ReadInt32();
                }
            }
        }

        return false;
    }

    /// <summary>true = skipped a valid string, false = impossible length, null = ran out of data.</summary>
    private static bool? SkipFString(FBitArchive a)
    {
        if (a.GetBitsLeft() < 32) return null;
        long length = a.ReadInt32();
        var bits = (length < 0 ? -2 * length : length) * 8;
        if (bits > a.GetBitsLeft()) return false;
        a.SkipBits((int)bits);
        return true;
    }

    /// <summary>
    /// Unreal.Core marks the end of each chunk with BitReader.SetTempEnd(size, slot), which saves the
    /// previous end in that slot, and undoes it with RestoreTempEnd(slot) in a finally block. When a
    /// chunk claims to be larger than the data left, SetTempEnd fails without saving anything, and the
    /// restore then uses whatever an earlier packet left in the slot. That can move the read position
    /// backwards, so the packet loop re-reads the same bad chunk forever. One replay did exactly that.
    ///
    /// Fix: right before the parser opens such a chunk, store the current (correct) end in the slot.
    /// A failed SetTempEnd then "restores" to the end of the enclosing chunk, so reading moves on.
    /// </summary>
    private static void PrimeTempEnd(FBitArchive archive, FBitArchiveEndIndex slot)
    {
        if (archive.IsError)
        {
            return; // RestoreTempEnd would clear the error flag; leave an errored archive alone.
        }

        archive.SetTempEnd(0, slot);  // saves the current end in the slot; end = current position
        archive.RestoreTempEnd(slot); // position unchanged, end back to what it was
    }

    public override void ProcessBunch(DataBunch bunch)
    {
        PrimeTempEnd(bunch.Archive, FBitArchiveEndIndex.CONTENT_BLOCK_PAYLOAD);
        base.ProcessBunch(bunch);
    }

    public override bool ReceivedReplicatorBunch(DataBunch bunch, FBitArchive archive, uint? repObject, bool bHasRepLayout)
    {
        PrimeTempEnd(archive, FBitArchiveEndIndex.FIELD_HEADER_PAYLOAD);
        return base.ReceivedReplicatorBunch(bunch, archive, repObject, bHasRepLayout);
    }

    protected override void OnExportRead(uint channelIndex, INetFieldExportGroup? exportGroup)
    {
    }

    protected override void OnExternalDataRead(uint channelIndex, IExternalData? exportGroup)
    {
    }

    protected override void OnNetDeltaRead(uint channelIndex, NetDeltaUpdate update)
    {
    }

    /// <summary>Events (kill feed, match stats, ...) aren't needed; skip them.</summary>
    public override void ReadEvent(FArchive archive)
    {
        archive.ReadFString(); // id
        archive.ReadFString(); // group
        archive.ReadFString(); // metadata
        archive.ReadUInt32();  // start time
        archive.ReadUInt32();  // end time
        archive.SkipBytes(archive.ReadInt32());
    }

    protected override FArchive DecryptBuffer(FArchive archive, int size)
    {
        if (!Replay.Info.IsEncrypted)
        {
            return new Unreal.Core.BinaryReader(archive.ReadMemory(size))
            {
                EngineNetworkVersion = Replay.Header.EngineNetworkVersion,
                NetworkVersion = Replay.Header.NetworkVersion,
                ReplayHeaderFlags = Replay.Header.Flags,
                ReplayVersion = Replay.Info.FileVersion,
            };
        }

        using var aes = Aes.Create();
        aes.Key = Replay.Info.EncryptionKey.ToArray();
        var decrypted = aes.DecryptEcb(archive.ReadMemory(size).Span, PaddingMode.PKCS7);

        return new Unreal.Core.BinaryReader(decrypted.AsMemory())
        {
            EngineNetworkVersion = archive.EngineNetworkVersion,
            NetworkVersion = archive.NetworkVersion,
            ReplayHeaderFlags = archive.ReplayHeaderFlags,
            ReplayVersion = archive.ReplayVersion,
        };
    }

    protected override FArchive Decompress(FArchive archive)
    {
        if (!Replay.Info.IsCompressed)
        {
            return archive;
        }

        var decompressedSize = archive.ReadInt32();
        var compressedSize = archive.ReadInt32();
        var compressed = archive.ReadBytes(compressedSize);
        var output = Unreal.Encryption.Oodle.DecompressReplayData(compressed, decompressedSize);

        return new Unreal.Core.BinaryReader(output)
        {
            EngineNetworkVersion = archive.EngineNetworkVersion,
            NetworkVersion = archive.NetworkVersion,
            ReplayHeaderFlags = archive.ReplayHeaderFlags,
            ReplayVersion = archive.ReplayVersion,
        };
    }

    /// <summary>
    /// Unreal.Core doesn't expose the current demo-frame time, but it logs it at Debug level
    /// ("ReadDemoFrameIntoPlaybackPackets at {time}"). Capture that; everything else is dropped.
    /// Only used to timestamp chest opens, so it degrades gracefully if the message ever changes.
    /// </summary>
    private sealed class FrameClock : ILogger
    {
        public float FrameTime;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Debug
                && state is IReadOnlyList<KeyValuePair<string, object?>> { Count: 2 } values
                && values[0].Value is float time
                && values[1].Value is string format
                && format.StartsWith("ReadDemoFrameIntoPlaybackPackets", StringComparison.Ordinal))
            {
                FrameTime = time;
            }
        }
    }
}
