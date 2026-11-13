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

    [GeneratedRegex(@"Script event|store|market|coin|commerce|product|categor|serialize|search|Lua|script|error|fail", RegexOptions.IgnoreCase)]
    private static partial Regex StoreInterest();

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
        WriteClientData(folder, settings.ClientFolder);

        return Zip(folder);
    }

    /// <summary>
    /// The game's own Marketplace tables and character select movies, read from its packs, so a report shows what the store screen
    /// expects and how the select screens are built (characterCreateSelect is the select bar the PS3 never shows).
    /// </summary>
    private static readonly string[] ClientDataFiles =
    [
        "ClientStorePortalCategories.txt", "ClientStoreBillboardPanels.txt", "ClientStoreBillboards.txt",
        "characterCreateSelect.gfx", "characterNameScreen.gfx", "characterCreate.gfx", "characterCreateType.gfx",
    ];

    private static void WriteClientData(string folder, string clientFolder)
    {
        try
        {
            foreach (var name in ClientDataFiles)
            {
                foreach (var usrDir in new[] { Path.Combine(clientFolder, "USRDIR"), clientFolder })
                {
                    if (Sanctuary.Ps3.Assets.PackIndex.ReadAsset(usrDir, name) is { } asset)
                    {
                        File.WriteAllBytes(Path.Combine(folder, "client_" + name), asset.Data);
                        break;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            File.WriteAllText(Path.Combine(folder, "client_data_error.txt"), exception.Message);
        }

        try
        {
            WriteUiTextures(folder, clientFolder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            File.WriteAllText(Path.Combine(folder, "ui_textures_error.txt"), exception.Message);
        }
    }

    /// <summary>The start of the names of the select screen's movies, whose textures a report carries.</summary>
    private static readonly string[] UiTexturePrefixes = ["characterCreatePopUps_", "characterNameScreen_", "characterCreate_", "characterCreateType_"];

    /// <summary>
    /// The name of every asset in the packs (pack_names.txt), and the select screen's UI textures and any texture named like a
    /// button (ui_textures/*.gtf), to choose textures for the select panel from what the PS3 has.
    /// </summary>
    private static void WriteUiTextures(string folder, string clientFolder)
    {
        var usrDir = new[] { Path.Combine(clientFolder, "USRDIR"), clientFolder }
            .FirstOrDefault(candidate => Directory.Exists(candidate) && Directory.GetFiles(candidate, "assets_ps3_*.pack").Length > 0);

        if (usrDir is null)
            return;

        var packs = Sanctuary.Ps3.Assets.PackIndex.Load(usrDir);
        var names = packs.Entries.Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

        File.WriteAllLines(Path.Combine(folder, "pack_names.txt"), names);

        var wanted = packs.Entries
            .Where(entry => entry.Name.EndsWith(".gtf", StringComparison.OrdinalIgnoreCase)
                && (UiTexturePrefixes.Any(prefix => entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    || entry.Name.Contains("button", StringComparison.OrdinalIgnoreCase)
                    || entry.Name.Contains("btn", StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Take(400)
            .ToList();

        var textures = Path.Combine(folder, "ui_textures");
        long total = 0;

        Directory.CreateDirectory(textures);

        foreach (var entry in wanted)
        {
            total += entry.Length;

            if (total > 40_000_000)
                break;

            using var stream = new FileStream(Path.Combine(usrDir, entry.Pack), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var data = new byte[entry.Length];

            stream.Seek(entry.Offset, SeekOrigin.Begin);
            stream.ReadAtLeast(data, data.Length, throwOnEndOfStream: false);
            File.WriteAllBytes(Path.Combine(textures, Path.GetFileName(entry.Name)), data);
        }
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
        WriteClientData(folder, settings.ClientFolder);

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
        WriteCrash(folder, all);

        // The game's own log lines, written to the TTY by the "game log to TTY" fix (RPCS3 keeps them in TTY.log beside RPCS3.log).
        foreach (var tty in new[] { Path.Combine(rpcs3Folder, "log", "TTY.log"), Path.Combine(rpcs3Folder, "TTY.log") })
        {
            if (File.Exists(tty))
            {
                // Without the terrain's "Tile::CreateAll: Failed on Texture" lines, which the select screen repeats every frame and
                // which would push everything else out of the tail.
                var game = ReadLines(tty).Where(line => !line.StartsWith("Tile::CreateAll: Failed on Texture", StringComparison.Ordinal)).ToList();

                File.WriteAllLines(Path.Combine(folder, "game_log_tail.txt"), game.TakeLast(20000));

                // The store's lines on their own, since the HTTP and asset lines bury them.
                File.WriteAllLines(Path.Combine(folder, "game_log_store.txt"), game.Where(line => StoreInterest().IsMatch(line)).TakeLast(5000));
                break;
            }
        }

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

    /// <summary>
    /// When the game crashed: what RPCS3 wrote about the crashed thread (its registers and call stack) up to the access violation, which
    /// the filtered log leaves out and the tail is often too short to reach.
    /// </summary>
    private static void WriteCrash(string folder, List<string> all)
    {
        var fatal = all.FindIndex(line => line.Contains("Access violation", StringComparison.Ordinal));

        if (fatal < 0)
            return;

        var context = all.FindLastIndex(fatal, Math.Min(fatal + 1, 600), line => line.Contains("thread context:", StringComparison.Ordinal));
        var from = context >= 0 ? context : Math.Max(0, fatal - 200);

        File.WriteAllLines(Path.Combine(folder, "rpcs3_crash.txt"), all.Skip(from).Take(fatal - from + 4));
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
