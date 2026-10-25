using System;
using System.Globalization;

namespace Sanctuary.Ps3.Gateway;

/// <summary>Which AddPc packets the gateway sends once the client has finished loading (<c>FR_ADDPC</c>).</summary>
public enum AddPcMode
{
    /// <summary>Nothing (also what an unknown value means).</summary>
    None,

    /// <summary>A second test character, guid 2 (the default).</summary>
    Other,

    /// <summary>The player's own character, guid 1.</summary>
    Self,

    /// <summary>Both.</summary>
    Both,
}

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

    /// <summary><c>FR_SPAWN</c>: <c>x,y,z</c> of the player; null uses the position picked from the packs.</summary>
    public (double X, double Y, double Z)? Spawn { get; init; }

    /// <summary><c>FR_SPAWN_Y</c>: height used with a position picked from the packs (the terrain height there is unknown).</summary>
    public double SpawnY { get; init; } = 400.0;

    /// <summary><c>FR_ADDPC</c>.</summary>
    public AddPcMode AddPc { get; init; } = AddPcMode.Other;

    /// <summary><c>FR_WELCOME</c> (not 0): send LoadWelcomeScreen. Layout from Clover, untested on the PS3 client.</summary>
    public bool Welcome { get; init; } = true;

    /// <summary><c>FR_EXTRA=1</c>: also send packets whose layouts come from Clover and are untested on the PS3 client.</summary>
    public bool Extra { get; init; }

    /// <summary><c>FR_TIME</c> (not 0): send GameTimeSync, and answer the client's request for it.</summary>
    public bool TimeSync { get; init; } = true;

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

        if (rawSpawn.Length > 0)
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

        // Unset means the default; set to anything else (even empty) selects exactly that mode, and an unknown mode sends nothing.
        var mode = Env(get, "FR_ADDPC", "other").ToLowerInvariant();

        return new GatewayOptions
        {
            Zone = Value("FR_ZONE") is { Length: > 0 } zone ? zone : null,
            ZoneType = zoneType,
            Spawn = spawn,
            SpawnY = spawnY,
            AddPc = mode switch
            {
                "other" => AddPcMode.Other,
                "self" => AddPcMode.Self,
                "both" => AddPcMode.Both,
                _ => AddPcMode.None,
            },
            Welcome = Env(get, "FR_WELCOME", "1") != "0",
            Extra = Value("FR_EXTRA") == "1",
            TimeSync = Env(get, "FR_TIME", "1") != "0",
        };
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
