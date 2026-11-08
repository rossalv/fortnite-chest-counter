using System.Text;

namespace ChestReplayReader;

public static class ReplayFile
{
    private const uint Magic = 0x1CA2E27F;

    /// <summary>
    /// True while Fortnite is still recording this replay. Fortnite only writes the replay's
    /// encryption key once the match ends, so a recording replay can't be read until then.
    /// Reads just the small file header (UE LocalFileNetworkReplayStreaming FLocalFileReplayInfo).
    /// </summary>
    public static bool IsStillRecording(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[(int)Math.Min(fs.Length, 8192)];
            fs.ReadExactly(buffer);

            using var reader = new System.IO.BinaryReader(new MemoryStream(buffer), Encoding.Unicode);
            if (reader.ReadUInt32() != Magic)
            {
                return false;
            }

            var fileVersion = reader.ReadUInt32();
            if (fileVersion >= 6) // HISTORY_CUSTOM_VERSIONS: count, then (FGuid, int32) pairs
            {
                var count = reader.ReadInt32();
                reader.BaseStream.Seek(count * 20L, SeekOrigin.Current);
            }

            reader.ReadUInt32(); // LengthInMS
            reader.ReadUInt32(); // NetworkVersion
            reader.ReadUInt32(); // Changelist

            var nameLength = reader.ReadInt32(); // FString: negative length = UTF-16
            reader.BaseStream.Seek(nameLength < 0 ? -nameLength * 2L : nameLength, SeekOrigin.Current);

            return reader.ReadUInt32() != 0; // bIsLive
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ArgumentException)
        {
            return false;
        }
    }
}
