using System;
using System.Globalization;
using System.Linq;

using Microsoft.Extensions.Logging;

using Sanctuary.Ps3.Assets;

namespace Sanctuary.Ps3.Gateway;

/// <summary>
/// The zone and spawn position the gateway sends. The zone name becomes the client's asset requests <c>&lt;name&gt;.gzne</c>,
/// <c>.cob</c> and <c>.odd</c>, which must exist in the manifest and the packs or the loading screen never goes away
/// (docs/re/zone-loading.md). Order of choice: the configured zone, then the zone picked from the install's own packs, then
/// <see cref="DefaultZone"/>. Ported from <c>choose_zone_from_install</c>, <c>configured_zone</c> and <c>configured_spawn</c>.
/// </summary>
public sealed class GatewayWorld
{
    /// <summary>The PC game's zone name; unverified for the PS3 packs.</summary>
    public const string DefaultZone = "FabledRealms";

    /// <summary>The PC game's position (Clover); its tile is not in the PS3 packs.</summary>
    public static readonly (double X, double Y, double Z, double W) PcSpawn = (-1904.883, -39.7098, 412.6024, 1.0);

    private readonly GatewayOptions _options;
    private readonly string? _installZone;
    private readonly (double X, double Z)? _installSpawn;

    public GatewayWorld(GatewayOptions? options = null, string? installZone = null, (double X, double Z)? installSpawn = null)
    {
        _options = options ?? new GatewayOptions();
        _installZone = installZone;
        _installSpawn = installSpawn;
    }

    public GatewayOptions Options => _options;

    public string Zone => _options.Zone ?? _installZone ?? DefaultZone;

    public int ZoneType => _options.ZoneType;

    /// <summary>Where AddPc puts the player: x, height, z and a 1.0.</summary>
    public (double X, double Y, double Z, double W) Spawn
    {
        get
        {
            if (_options.Spawn is { } set)
                return (set.X, set.Y, set.Z, 1.0);

            if (_installSpawn is { } picked)
                return (picked.X, _options.SpawnY, picked.Z, 1.0);

            return PcSpawn;
        }
    }

    /// <summary>
    /// Picks the zone from the packs under <paramref name="usrDir"/> and a spawn position near the middle of a tile whose
    /// neighbourhood is stored locally. Never throws: a problem with the packs leaves the defaults.
    /// </summary>
    public static GatewayWorld FromInstall(string usrDir, GatewayOptions options, ILogger? logger = null)
    {
        ZoneChoice choice;

        try
        {
            choice = ZoneCatalog.Pick(usrDir, DefaultZone);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger?.LogWarning("could not read the packs in {UsrDir} to pick a zone: {Error}", usrDir, exception.Message);

            return new GatewayWorld(options);
        }

        if (choice.Zone is null)
        {
            logger?.LogWarning("zone: {Note}; sending '{Zone}'", choice.Note, DefaultZone);

            return new GatewayWorld(options);
        }

        var zone = choice.Zone;

        logger?.LogInformation("zone: {Note}; {Count} complete zones in the packs: {Zones}", choice.Note, choice.CompleteZones.Count, string.Join(", ", choice.CompleteZones.Take(12)));

        (double, double)? spawn = null;

        try
        {
            var picked = Terrain.PickSpawn(usrDir, zone);

            if (picked is not null)
            {
                spawn = (picked.X, picked.Z);

                logger?.LogInformation(
                    "spawn: x={X} z={Z} in tile ({TileX}, {TileZ}): {Note}",
                    picked.X.ToString("F1", CultureInfo.InvariantCulture),
                    picked.Z.ToString("F1", CultureInfo.InvariantCulture),
                    picked.Tile.X,
                    picked.Tile.Z,
                    picked.Note);
            }
            else
            {
                logger?.LogWarning("spawn: no local terrain tile found for '{Zone}'; using the PC game's position, whose tile may have to be downloaded", zone);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger?.LogWarning("could not pick a spawn position from the tiles: {Error}", exception.Message);
        }

        if (!string.Equals(zone, DefaultZone, StringComparison.OrdinalIgnoreCase))
            logger?.LogWarning("zone: '{Default}' is not in the PS3 packs, using '{Zone}' instead (set FR_ZONE to choose another)", DefaultZone, zone);

        return new GatewayWorld(options, zone, spawn);
    }
}
