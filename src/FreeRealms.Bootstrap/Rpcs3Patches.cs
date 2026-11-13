using System.IO;
using System.Text;

namespace FreeRealms.Bootstrap;

/// <summary>
/// The game fixes the server needs in RPCS3 (<c>Rpcs3\FreeRealms_PS3_fixes.yml</c>, see docs/rpcs3/patches.md): added to RPCS3's
/// <c>patches\imported_patch.yml</c> and turned on in its <c>patch_config.yml</c>, the same as importing the file in RPCS3's
/// Manage &gt; Game Patches window and ticking each one. A patch already listed under its name keeps its on/off setting, so a person
/// who turned one off keeps it off; when its code is older than the shipped one, the code is brought up to date. The files are only
/// changed while RPCS3 is closed, because RPCS3 writes them when it saves.
/// </summary>
public static class Rpcs3Patches
{
    /// <summary>The PPU hash of the decrypted 1.77 game.elf; RPCS3 applies the patches only to that executable.</summary>
    public const string Hash = "PPU-5425f6d47ba410afbcbc7ce77f87f2ffcde68798";

    private const string ResourceName = "FreeRealms_PS3_fixes.yml";

    /// <summary>The patch file as shipped.</summary>
    public static string Text()
    {
        using var stream = typeof(Rpcs3Patches).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"embedded resource {ResourceName} is missing");
        using var reader = new StreamReader(stream, Encoding.UTF8);

        return reader.ReadToEnd();
    }

    /// <summary>Each patch in the shipped file: its name and its lines (from the name line to the last <c>be32</c> line).</summary>
    public static IReadOnlyList<(string Name, string[] Lines)> Patches()
    {
        var lines = Text().Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var start = Array.FindIndex(lines, line => line.TrimEnd() == Hash + ":");
        var patches = new List<(string, string[])>();

        if (start < 0)
            return patches;

        string? name = null;
        var block = new List<string>();

        void Close()
        {
            if (name is not null)
                patches.Add((name, [.. block]));

            block.Clear();
        }

        foreach (var line in lines.Skip(start + 1))
        {
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (!line.StartsWith(' '))
                break;

            if (line.StartsWith("  \"", StringComparison.Ordinal) && !line.StartsWith("   ", StringComparison.Ordinal))
            {
                Close();
                name = line.Trim().TrimEnd(':').Trim('"');
            }

            block.Add(line);
        }

        Close();

        return patches;
    }

    /// <summary>
    /// Adds the missing patches to the RPCS3 next to <paramref name="rpcs3Executable"/> and turns them on. Returns what was done, one
    /// line each (nothing when everything was already there).
    /// </summary>
    public static IReadOnlyList<string> Install(string rpcs3Executable)
    {
        var folder = Path.GetDirectoryName(rpcs3Executable);

        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return ["RPCS3 patches: rpcs3.exe is not set, so the game fixes were not added"];

        if (System.Diagnostics.Process.GetProcessesByName("rpcs3").Length > 0)
            return ["RPCS3 patches: RPCS3 is open, so the game fixes were not checked (close it and start the test run again)"];

        var done = new List<string>();
        var patches = Patches();

        var imported = Path.Combine(folder, "patches", "imported_patch.yml");
        var added = AddUnderHash(imported, patches.Select(patch => (patch.Name, (IReadOnlyList<string>)patch.Lines)).ToList(), "Version: 1.2");

        if (added.Count > 0)
            done.Add($"RPCS3 patches: added {string.Join(", ", added)} to {imported}");

        var updated = UpdateUnderHash(imported, patches.Select(patch => (patch.Name, (IReadOnlyList<string>)patch.Lines)).ToList());

        if (updated.Count > 0)
            done.Add($"RPCS3 patches: updated {string.Join(", ", updated)} in {imported}");

        // Newer RPCS3 keeps its settings in config\; older ones next to rpcs3.exe.
        var config = Path.Combine(folder, "config", "patch_config.yml");

        if (!File.Exists(config) && File.Exists(Path.Combine(folder, "patch_config.yml")))
            config = Path.Combine(folder, "patch_config.yml");

        var enabled = AddUnderHash(
            config,
            patches.Select(patch => (patch.Name, (IReadOnlyList<string>)new[] { $"  {patch.Name}:", "    All:", "      All:", "        All:", "          Enabled: true" })).ToList(),
            null);

        if (enabled.Count > 0)
            done.Add($"RPCS3 patches: turned on {string.Join(", ", enabled)} in {config}");

        return done;
    }

    /// <summary>
    /// Puts each block whose name <paramref name="file"/> does not mention yet under the <see cref="Hash"/> key (made when missing),
    /// keeping a .bak copy of the file as it was. Returns the names added.
    /// </summary>
    internal static List<string> AddUnderHash(string file, IReadOnlyList<(string Name, IReadOnlyList<string> Lines)> blocks, string? header)
    {
        List<string> lines = File.Exists(file) ? [.. File.ReadAllLines(file)] : [];
        var text = string.Join('\n', lines);
        var missing = blocks.Where(block => !text.Contains(block.Name, StringComparison.Ordinal)).ToList();

        if (missing.Count == 0)
            return [];

        if (File.Exists(file))
            File.Copy(file, file + ".bak", overwrite: true);
        else if (header is not null)
            lines.AddRange([header, string.Empty]);

        var insert = missing.SelectMany(block => block.Lines).ToList();
        var key = lines.FindIndex(line => line.TrimStart('﻿').TrimEnd() == Hash + ":");

        if (key >= 0)
        {
            lines.InsertRange(key + 1, insert);
        }
        else
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0)
                lines.Add(string.Empty);

            lines.Add(Hash + ":");
            lines.AddRange(insert);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllLines(file, lines, new UTF8Encoding(false));

        return [.. missing.Select(block => block.Name)];
    }

    /// <summary>
    /// Replaces each block of <paramref name="blocks"/> that <paramref name="file"/> has under the <see cref="Hash"/> key with
    /// different code (its <c>be32</c> lines, compared without spaces or quotes, so RPCS3 rewriting the file the way it likes does not
    /// count), keeping a .bak copy of the file as it was. Returns the names updated.
    /// </summary>
    internal static List<string> UpdateUnderHash(string file, IReadOnlyList<(string Name, IReadOnlyList<string> Lines)> blocks)
    {
        if (!File.Exists(file))
            return [];

        List<string> lines = [.. File.ReadAllLines(file)];
        var updated = new List<string>();

        foreach (var (name, patch) in blocks)
        {
            var key = lines.FindIndex(line => line.TrimStart('\uFEFF').TrimEnd() == Hash + ":");

            if (key < 0)
                return updated.Count > 0 ? Write(file, lines, updated) : new List<string>();

            // The hash's section: up to the next line that is not indented (blank lines and comments do not end it).
            var end = key + 1;

            while (end < lines.Count && (lines[end].Trim().Length == 0 || lines[end].TrimStart().StartsWith('#') || lines[end].StartsWith(' ')))
                end++;

            var start = -1;

            for (var index = key + 1; index < end; index++)
            {
                if (Indent(lines[index]) <= 2 && lines[index].Trim().TrimEnd(':').Trim().Trim('"', '\'') == name)
                {
                    start = index;
                    break;
                }
            }

            if (start < 0)
                continue;

            // The block: up to the next line indented as little as the name (another patch) or the end of the section.
            var stop = start + 1;

            while (stop < end && (lines[stop].Trim().Length == 0 || Indent(lines[stop]) > Indent(lines[start])))
                stop++;

            if (Code(lines.Skip(start).Take(stop - start)) == Code(patch))
                continue;

            // Keep the blank lines between this patch and the next one where they were.
            while (stop > start + 1 && lines[stop - 1].Trim().Length == 0)
                stop--;

            lines.RemoveRange(start, stop - start);
            lines.InsertRange(start, patch);
            updated.Add(name);
        }

        return updated.Count > 0 ? Write(file, lines, updated) : new List<string>();

        static int Indent(string line) => line.Length - line.TrimStart(' ').Length;

        static string Code(IEnumerable<string> block) =>
            string.Join('\n', block
                .Select(line => new string(line.Where(c => !char.IsWhiteSpace(c) && c != '"' && c != '\'').ToArray()))
                .Where(line => line.StartsWith("-[be32", StringComparison.OrdinalIgnoreCase))
                .Select(line => line.ToLowerInvariant()));

        static List<string> Write(string file, List<string> lines, List<string> updated)
        {
            File.Copy(file, file + ".bak", overwrite: true);
            File.WriteAllLines(file, lines, new UTF8Encoding(false));

            return updated;
        }
    }
}
