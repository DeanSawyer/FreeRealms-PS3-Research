using System;
using System.Collections.Generic;
using System.Globalization;

namespace Sanctuary.Ps3.Gateway;

/// <summary>
/// The switches of the game-server stand-in. They are the <c>FR_*</c> environment variables of the Python probe, which the test
/// scripts set; <see cref="FromEnvironment"/> reads the same names.
/// </summary>
public sealed record GatewayOptions
{
    /// <summary><c>FR_ZONE</c>: zone name sent in ZoneDetails; null picks one from the install's packs.</summary>
    public string? Zone { get; init; }

    /// <summary><c>FR_ZONE_TYPE</c>: terrain implementation (0 TileStatic, 1 TileSeamless, 2 RuntimeSeamless, 3 Mesh; only 2 is set up at startup).</summary>
    public int ZoneType { get; init; } = 2;

    /// <summary><c>FR_SPAWN</c>: <c>x,y,z</c> of the player; null uses Sanctuary's spawn, or with <see cref="SpawnLocal"/> the position picked from the packs.</summary>
    public (double X, double Y, double Z)? Spawn { get; init; }

    /// <summary><c>FR_SPAWN=local</c>: use the position picked from the packs instead of Sanctuary's spawn.</summary>
    public bool SpawnLocal { get; init; }

    /// <summary><c>FR_SPAWN_Y</c>: height used with a position picked from the packs (the terrain height there is unknown).</summary>
    public double SpawnY { get; init; } = 400.0;

    /// <summary><c>FR_WELCOME</c> (not 0): send LoadWelcomeScreen. Layout from Clover, untested on the PS3 client.</summary>
    public bool Welcome { get; init; } = true;

    /// <summary><c>FR_EXTRA=1</c>: also send packets whose layouts come from Clover and are untested on the PS3 client.</summary>
    public bool Extra { get; init; }

    /// <summary><c>FR_TIME</c> (not 0): send GameTimeSync, and answer the client's request for it.</summary>
    public bool TimeSync { get; init; } = true;

    /// <summary>
    /// <c>FR_EQUIP</c>: how to dress the character in the creator's clothes after loading. 7 (default): one EquippedItemsChange, which also plays the job
    /// change effect; 5 (<c>FR_EQUIP=5</c>): one EquipItem (ClientUpdate) per piece; 0 (<c>FR_EQUIP=0</c>): not at all.
    /// </summary>
    public int EquipPacket { get; init; } = 7;

    /// <summary><c>FR_JOBS</c>: ids of the jobs to send ("1,11,35"; the starter job is always sent); null (unset or <c>all</c>) sends every job.</summary>
    public IReadOnlyCollection<int>? Jobs { get; init; }

    /// <summary><c>FR_ITEMS</c> (not 0): give the character Sanctuary's starter items, their definitions and each job's equipment.</summary>
    public bool Items { get; init; } = true;

    /// <summary>Reads the <c>FR_*</c> variables through <paramref name="get"/> (null means unset). Problems are reported to <paramref name="warn"/>.</summary>
    public static GatewayOptions FromEnvironment(Func<string, string?> get, Action<string>? warn = null)
    {
        string Value(string name) => (get(name) ?? string.Empty).Trim();

        var zoneType = 2;
        var rawType = Value("FR_ZONE_TYPE");

        if (rawType.Length > 0 && !int.TryParse(rawType, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out zoneType))
            zoneType = 2;

        (double, double, double)? spawn = null;
        var rawSpawn = Value("FR_SPAWN");
        var spawnLocal = string.Equals(rawSpawn, "local", StringComparison.OrdinalIgnoreCase);

        if (rawSpawn.Length > 0 && !spawnLocal)
        {
            spawn = ParseSpawn(rawSpawn);

            if (spawn is null)
                warn?.Invoke($"FR_SPAWN='{rawSpawn}' is not 'x,y,z'; ignored");
        }

        var spawnY = 400.0;
        var rawY = Value("FR_SPAWN_Y");

        // Python reads FR_SPAWN_Y with float(os.environ.get(..., DEFAULT)): an empty value is an error there and falls back too.
        if (rawY.Length > 0 && double.TryParse(rawY, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedY))
            spawnY = parsedY;

        return new GatewayOptions
        {
            Zone = Value("FR_ZONE") is { Length: > 0 } zone ? zone : null,
            ZoneType = zoneType,
            Spawn = spawn,
            SpawnLocal = spawnLocal,
            SpawnY = spawnY,
            Welcome = Env(get, "FR_WELCOME", "1") != "0",
            Extra = Value("FR_EXTRA") == "1",
            TimeSync = Env(get, "FR_TIME", "1") != "0",
            Jobs = ParseJobs(Value("FR_JOBS")),
            Items = Env(get, "FR_ITEMS", "1") != "0",
            EquipPacket = Env(get, "FR_EQUIP", "1") switch
            {
                "0" => 0,
                "5" => 5,
                _ => 7,
            },
        };
    }

    /// <summary>
    /// <c>FR_JOBS</c>: empty or <c>all</c> gives null (every job); otherwise the numbers in a comma or semicolon separated list (other parts
    /// are skipped), like <c>bootstrap/jobs.py</c>.
    /// </summary>
    public static IReadOnlyCollection<int>? ParseJobs(string text)
    {
        if (text.Length == 0 || string.Equals(text, "all", StringComparison.OrdinalIgnoreCase))
            return null;

        var ids = new HashSet<int>();

        foreach (var part in text.Replace(';', ',').Split(','))
        {
            if (int.TryParse(part.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id))
                ids.Add(id);
        }

        return ids;
    }

    /// <summary>Three numbers separated by commas or semicolons; null when it is anything else.</summary>
    public static (double X, double Y, double Z)? ParseSpawn(string text)
    {
        var parts = text.Replace(';', ',').Split(',');

        if (parts.Length != 3)
            return null;

        var values = new double[3];

        for (var index = 0; index < 3; index++)
        {
            if (!double.TryParse(parts[index].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[index]))
                return null;
        }

        return (values[0], values[1], values[2]);
    }

    private static string Env(Func<string, string?> get, string name, string fallback)
    {
        // os.environ.get(name, fallback).strip(): only an unset variable gets the fallback, an empty one stays empty.
        var value = get(name);

        return value is null ? fallback : value.Trim();
    }
}
