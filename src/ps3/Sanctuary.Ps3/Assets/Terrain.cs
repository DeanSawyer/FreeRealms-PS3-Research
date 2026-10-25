using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Sanctuary.Ps3.Assets;

/// <summary>A tile origin in cells.</summary>
public readonly record struct TileKey(int X, int Z) : IComparable<TileKey>
{
    public int CompareTo(TileKey other)
    {
        var byX = X.CompareTo(other.X);

        return byX != 0 ? byX : Z.CompareTo(other.Z);
    }
}

/// <summary>A tile the client needs for a position, and whether it lies inside the terrain box (outside counts as loaded).</summary>
public readonly record struct NearTile(int X, int Z, bool Inside);

/// <summary>A chosen spawn: world position, the tile it is in, and why.</summary>
public sealed record SpawnChoice(double X, double Z, TileKey Tile, string Note);

/// <summary>
/// Terrain tile arithmetic of the PS3 client, ported from <c>assetlib.py</c>. The loading overlay drops only when the tiles
/// within 96 world units of the camera are loaded (docs/re/zone-loading.md), so the spawn has to be a tile whose
/// neighbourhood is stored in the install.
/// </summary>
public static class Terrain
{
    /// <summary>Floor of a world coordinate in cells.</summary>
    public static int FloorCell(double value, double cellSize) => (int)Math.Floor(value / cellSize);

    /// <summary>
    /// Tile origin of a cell as <c>GetCell</c> (0x7ded28) computes it: an unsigned divide by the tile edge, then a multiply, as a
    /// signed 32-bit number. Exact for power-of-two edges, which is what the game uses.
    /// </summary>
    public static int TileOrigin(int cell, int edge)
    {
        unchecked
        {
            var origin = (uint)cell / (uint)edge * (uint)edge;

            return (int)origin;
        }
    }

    /// <summary>
    /// Tiles the client must have loaded for <c>IsLoaded(position x, z; radius)</c> (0x7cfdd4), sorted by origin.
    /// A tile outside the terrain box counts as loaded at once.
    /// </summary>
    public static List<NearTile> TilesNear(GzneHeader header, double x, double z, double radius)
    {
        var x0 = FloorCell(x - radius, header.CellSize);
        var x1 = FloorCell(x + radius, header.CellSize);
        var z0 = FloorCell(z - radius, header.CellSize);
        var z1 = FloorCell(z + radius, header.CellSize);

        var found = new SortedSet<(int X, int Z, bool Inside)>();

        for (var cx = x0; cx <= x1; cx++)
        {
            for (var cz = z0; cz <= z1; cz++)
            {
                var ox = TileOrigin(cx, header.Edge);
                var oz = TileOrigin(cz, header.Edge);

                var inside = header.MinX <= ox && (long)ox < (long)header.MinX + header.ExtX
                    && header.MinZ <= oz && (long)oz < (long)header.MinZ + header.ExtZ;

                found.Add((ox, oz, inside));
            }
        }

        return found.Select(tile => new NearTile(tile.X, tile.Z, tile.Inside)).ToList();
    }

    /// <summary>Asset names the client requests for one tile (0x7e71bc): the <c>.gcnk</c> always, the <c>.gck2</c> when the tile has extra data.</summary>
    public static string[] TileFileNames(string zone, int ox, int oz)
    {
        var stem = string.Create(CultureInfo.InvariantCulture, $"{zone}_{ox}_{oz}");

        return [stem + ".gcnk", stem + ".gck2"];
    }

    /// <summary>Tile origins whose <c>&lt;zone&gt;_&lt;x&gt;_&lt;z&gt;.gcnk</c> file is stored in the packs.</summary>
    public static HashSet<TileKey> LocalTiles(IEnumerable<PackedEntry> entries, string zone)
    {
        var pattern = new Regex(
            "^" + Regex.Escape(zone) + @"_(-?\d+)_(-?\d+)\.gcnk$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var tiles = new HashSet<TileKey>();

        foreach (var entry in entries)
        {
            var match = pattern.Match(entry.Name);

            if (!match.Success)
                continue;

            if (int.TryParse(match.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var x)
                && int.TryParse(match.Groups[2].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var z))
            {
                tiles.Add(new TileKey(x, z));
            }
        }

        return tiles;
    }

    /// <summary>
    /// Chooses a spawn whose terrain tiles are all stored locally: a tile whose 3 x 3 neighbourhood is local (or lies outside the
    /// terrain box), as near the world origin as possible, and its centre. Returns null when the install cannot tell.
    /// </summary>
    public static SpawnChoice? PickSpawn(PackSet packs, GzneHeader? header, string zone)
    {
        if (header is null || header.Edge <= 0 || packs.Entries.Count == 0)
            return null;

        var local = LocalTiles(packs.Entries, zone);

        if (local.Count == 0)
            return null;

        var edge = header.Edge;
        var cellSize = header.CellSize;

        var inBox = new HashSet<TileKey>();

        for (var x = (long)header.MinX; x < (long)header.MinX + header.ExtX; x += edge)
        {
            for (var z = (long)header.MinZ; z < (long)header.MinZ + header.ExtZ; z += edge)
                inBox.Add(new TileKey((int)x, (int)z));
        }

        local.IntersectWith(inBox); // a tile outside the terrain box is never requested

        if (local.Count == 0)
            return null;

        double Distance(TileKey tile)
        {
            var cx = (tile.X + edge / 2.0) * cellSize;
            var cz = (tile.Z + edge / 2.0) * cellSize;

            return (cx * cx) + (cz * cz);
        }

        bool NeighbourhoodIsLocal(TileKey tile)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dz = -1; dz <= 1; dz++)
                {
                    var neighbour = new TileKey(tile.X + (dx * edge), tile.Z + (dz * edge));

                    if (!local.Contains(neighbour) && inBox.Contains(neighbour))
                        return false;
                }
            }

            return true;
        }

        var full = local.Where(NeighbourhoodIsLocal).ToList();

        var (pool, what) = full.Count > 0
            ? (full, "3 x 3 tiles around it are local")
            : (local.OrderBy(tile => tile).ToList(), "only the tile itself is local");

        // Ties: the smaller (x, z), so the choice never depends on enumeration order.
        var best = pool.OrderBy(Distance).ThenBy(tile => tile).First();

        var note = string.Create(
            CultureInfo.InvariantCulture,
            $"{zone}_{best.X}_{best.Z}.gcnk, {what} ({local.Count} of {inBox.Count} tiles are local)");

        return new SpawnChoice((best.X + edge / 2.0) * cellSize, (best.Z + edge / 2.0) * cellSize, best, note);
    }

    /// <summary>Reads the install's packs and terrain header and picks a spawn for <paramref name="zone"/>.</summary>
    public static SpawnChoice? PickSpawn(string usrDir, string zone)
    {
        var packs = PackIndex.Load(usrDir);

        if (packs.Entries.Count == 0)
            return null;

        var found = PackIndex.ReadAsset(usrDir, zone + ".gzne");

        return found is null ? null : PickSpawn(packs, GzneHeader.Parse(found.Data), zone);
    }
}
