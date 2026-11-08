using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using ChestReplayReader;

namespace ChestCounter;

/// <summary>
/// Reads one replay in a separate copy of this exe ("FortniteChestCounter.exe --read-replay file").
/// A replay the parser can't handle might make it loop forever; in a child process that can be
/// killed after a time limit, so one bad file can never stall the scan or keep burning a CPU core.
/// It also means the parser's memory is returned to Windows the moment each replay is done.
/// </summary>
internal static class ReplayWorker
{
    public const string Arg = "--read-replay";

    /// <summary>Child process entry point: parse the file and write the result as JSON to stdout.</summary>
    public static int Run(string path)
    {
        try
        {
            var result = new ChestReader().Read(path);
            var json = JsonSerializer.SerializeToUtf8Bytes(result);
            using var stdout = Console.OpenStandardOutput();
            stdout.Write(json);
            stdout.Flush();
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                using var stderr = Console.OpenStandardError();
                stderr.Write(Encoding.UTF8.GetBytes(ex.ToString()));
            }
            catch
            {
                // nothing else we can do
            }

            return 2;
        }
    }

    /// <summary>Parent side: run a child for <paramref name="path"/>; null + reason if it failed or timed out.</summary>
    public static MatchResult? Read(string path, TimeSpan timeout, Action<Process?> trackChild, out string? failure)
    {
        failure = null;
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            // Should not happen for the published exe; fall back to reading in-process.
            return new ChestReader().Read(path);
        }

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(Arg);
        psi.ArgumentList.Add(path);

        using var child = Process.Start(psi);
        if (child is null)
        {
            failure = "could not start the replay reader";
            return null;
        }

        trackChild(child);
        try
        {
            try
            {
                child.PriorityClass = ProcessPriorityClass.BelowNormal; // never compete with the game
            }
            catch (InvalidOperationException)
            {
                // already exited
            }

            using var output = new MemoryStream();
            var copyOut = child.StandardOutput.BaseStream.CopyToAsync(output);
            var readErr = child.StandardError.ReadToEndAsync();

            if (!child.WaitForExit(timeout))
            {
                try
                {
                    child.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // exited just now
                }

                failure = $"took longer than {timeout.TotalSeconds:0}s to read; skipped";
                return null;
            }

            child.WaitForExit(); // let the output pipes drain
            copyOut.Wait(TimeSpan.FromSeconds(5));
            if (child.ExitCode != 0 || output.Length == 0)
            {
                var err = readErr.Wait(TimeSpan.FromSeconds(2)) ? readErr.Result : "";
                failure = $"replay reader failed (exit {child.ExitCode}): {FirstLine(err)}";
                return null;
            }

            return JsonSerializer.Deserialize<MatchResult>(output.ToArray());
        }
        finally
        {
            trackChild(null);
        }
    }

    private static string FirstLine(string s)
    {
        var line = s.Split('\n', 2)[0].Trim();
        return line.Length > 200 ? line[..200] : line;
    }
}
