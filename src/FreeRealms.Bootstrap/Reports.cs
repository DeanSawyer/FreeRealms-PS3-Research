using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace FreeRealms.Bootstrap;

/// <summary>
/// Test runs and diagnostics: starts RPCS3 on the game, waits until it is closed, then writes a report zip to the Desktop with what is
/// needed to read the result (server log, the lines that matter, the end of RPCS3's log, its patches and settings, the trace files).
/// </summary>
public static partial class Reports
{
    [GeneratedRegex(@"spawn:|zone:|ZoneDetails|gcnk|gzne|\.cob|\.odd|asset_missing|not in manifest|404|Failed|Error|WARN|FinishedLoading|AddPc|LoginReply|sanctuary:", RegexOptions.IgnoreCase)]
    private static partial Regex Interest();

    [GeneratedRegex(@"PAT|patch|assets_ps3|\.gcnk|\.gzne|FabledRealms|\.cob|\.odd|FreeRealms\.log|GameRuntimeTerrain|fatal|terminated|Call stack|Registers|Access violation|main_thread|LoadingScreenThread", RegexOptions.IgnoreCase)]
    private static partial Regex Rpcs3Interest();

    /// <summary>Where report zips go: the Desktop, or the manager's folder when there is none.</summary>
    public static string ReportFolder
    {
        get
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

            return Directory.Exists(desktop) ? desktop : Path.Combine(ManagerSettings.DataFolder, "reports");
        }
    }

    /// <summary>
    /// A whole test run: restores the original scripts if asked, starts the server if it is not running, runs RPCS3 until it is closed,
    /// then writes the report. Returns the zip's path.
    /// </summary>
    public static async Task<string> TestRunAsync(ManagerSettings settings, ServerController server, Func<IReadOnlyList<LogEntry>> log, Action<string> step)
    {
        if (!File.Exists(settings.Rpcs3Executable))
            throw new InvalidOperationException("Choose rpcs3.exe in Settings.");

        if (!File.Exists(settings.GameElf))
            throw new InvalidOperationException("Choose the game's game.elf in Settings.");

        var started = DateTime.Now;
        var folder = NewFolder("fr_test");
        var steps = new StringBuilder();

        void Step(string text)
        {
            steps.AppendLine(text);
            step(text);
        }

        Step($"test started {started:yyyyMMdd_HHmmss}");

        if (settings.RestoreScripts)
            RestoreScripts(Path.GetDirectoryName(settings.ClientFolder.TrimEnd('\\', '/')) ?? settings.ClientFolder, Step);

        if (settings.InstallPatches)
        {
            try
            {
                foreach (var line in Rpcs3Patches.Install(settings.Rpcs3Executable))
                    Step(line);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Step($"RPCS3 patches: could not be added ({exception.Message})");
            }
        }

        if (!server.IsRunning)
        {
            Step("starting the server");
            await server.StartAsync(settings);
        }

        Step($"gameplay: {string.Join(", ", settings.GatewaySwitches().Select(pair => $"{pair.Key}={pair.Value}"))}");
        Step("starting RPCS3: log in, make or pick a character, play, then close RPCS3 to finish the report");

        using (var rpcs3 = Process.Start(new ProcessStartInfo(settings.Rpcs3Executable)
        {
            ArgumentList = { "--no-gui", settings.GameElf },
            WorkingDirectory = Path.GetDirectoryName(settings.Rpcs3Executable)!,
            UseShellExecute = false,
        }))
        {
            if (rpcs3 is null)
                throw new InvalidOperationException("RPCS3 did not start.");

            await rpcs3.WaitForExitAsync();
        }

        Step("RPCS3 closed");
        await Task.Delay(TimeSpan.FromSeconds(2));

        var rpcs3Folder = Path.GetDirectoryName(settings.Rpcs3Executable)!;

        File.WriteAllText(Path.Combine(folder, "steps.txt"), steps.ToString());
        WriteServerLog(folder, log().Where(entry => entry.Time >= started));
        WriteRpcs3(folder, rpcs3Folder);
        WriteTraces(folder);

        return Zip(folder);
    }

    /// <summary>Logs and traces as they are now, without RPCS3: for a report at any time. Returns the zip's path.</summary>
    public static string Diagnostics(ManagerSettings settings, IReadOnlyList<LogEntry> log)
    {
        var folder = NewFolder("fr_diagnostics");

        File.WriteAllText(
            Path.Combine(folder, "settings.txt"),
            string.Join(Environment.NewLine, settings.GatewaySwitches().Select(pair => $"{pair.Key}={pair.Value}"))
            + Environment.NewLine + $"client={settings.ClientFolder}" + Environment.NewLine + $"rpcs3={settings.Rpcs3Executable}");

        WriteServerLog(folder, log);

        if (File.Exists(settings.Rpcs3Executable))
            WriteRpcs3(folder, Path.GetDirectoryName(settings.Rpcs3Executable)!);

        WriteTraces(folder);

        return Zip(folder);
    }

    /// <summary>Copies scriptsbase.bin.sdat.orig over a patched scriptsbase.bin.sdat (outside the Preservation backups).</summary>
    private static void RestoreScripts(string install, Action<string> step)
    {
        var restored = 0;

        if (Directory.Exists(install))
        {
            foreach (var original in Directory.EnumerateFiles(install, "scriptsbase.bin.sdat.orig", SearchOption.AllDirectories))
            {
                if (original.Contains(Path.DirectorySeparatorChar + "Preservation", StringComparison.OrdinalIgnoreCase))
                    continue;

                var live = original[..^".orig".Length];

                if (!File.Exists(live) || new FileInfo(live).Length != new FileInfo(original).Length)
                {
                    File.Copy(original, live, overwrite: true);
                    step($"restored original script: {live}");
                    restored++;
                }
            }
        }

        if (restored == 0)
            step("scripts: nothing to restore (already the original)");
    }

    private static void WriteServerLog(string folder, IEnumerable<LogEntry> entries)
    {
        var lines = entries.Select(entry => entry.ToString()).ToList();

        File.WriteAllLines(Path.Combine(folder, "server.log"), lines);
        File.WriteAllLines(Path.Combine(folder, "lines_of_interest.txt"), lines.Where(line => Interest().IsMatch(line)).TakeLast(400));
    }

    private static void WriteRpcs3(string folder, string rpcs3Folder)
    {
        var logPath = Path.Combine(rpcs3Folder, "log", "RPCS3.log");
        var all = ReadLines(logPath);

        File.WriteAllLines(Path.Combine(folder, "rpcs3_log_tail.txt"), all.TakeLast(3000));

        // What the client opened (packs, tiles), crashes, and the patch lines from start-up, so a report shows which patches were on.
        var kept = all.TakeLast(200000).Where(line => Rpcs3Interest().IsMatch(line)).ToList();
        var applied = kept.Take(Math.Max(0, kept.Count - 1500)).Where(line => line.Contains("PAT: Applied", StringComparison.Ordinal));

        File.WriteAllLines(Path.Combine(folder, "rpcs3_log_filtered.txt"), applied.Concat(kept.TakeLast(1500)));

        foreach (var name in new[] { "patch_config.yml", "log.yml", "config.yml" })
        {
            var source = Path.Combine(rpcs3Folder, "config", name);

            if (File.Exists(source))
                File.Copy(source, Path.Combine(folder, "config_" + name), overwrite: true);
        }

        var patches = Path.Combine(rpcs3Folder, "patches");

        if (Directory.Exists(patches))
        {
            foreach (var file in Directory.EnumerateFiles(patches, "*.yml").Where(file => new FileInfo(file).Length < 400_000))
                File.Copy(file, Path.Combine(folder, "patches_" + Path.GetFileName(file)), overwrite: true);
        }
    }

    private static void WriteTraces(string folder)
    {
        foreach (var name in new[] { "ps3_gateway_trace.jsonl", "ps3_protocol_trace.jsonl" })
        {
            var lines = ReadLines(Path.Combine(ManagerSettings.TraceFolder, name));

            File.WriteAllLines(Path.Combine(folder, Path.GetFileNameWithoutExtension(name) + "_tail.jsonl"), lines.TakeLast(600));
        }
    }

    private static string NewFolder(string prefix)
    {
        var folder = Path.Combine(ReportFolder, $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}");

        Directory.CreateDirectory(folder);

        return folder;
    }

    private static string Zip(string folder)
    {
        var zip = folder + ".zip";

        if (File.Exists(zip))
            File.Delete(zip);

        ZipFile.CreateFromDirectory(folder, zip, CompressionLevel.Optimal, includeBaseDirectory: false);

        return zip;
    }

    /// <summary>A text file's lines, read while another program may still write it; empty when it is missing.</summary>
    private static List<string> ReadLines(string path)
    {
        if (!File.Exists(path))
            return [];

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var lines = new List<string>();

            while (reader.ReadLine() is { } line)
                lines.Add(line);

            return lines;
        }
        catch (IOException)
        {
            return [];
        }
    }
}
