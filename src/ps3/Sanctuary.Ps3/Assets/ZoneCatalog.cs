using System;
using System.Collections.Generic;
using System.Linq;

namespace Sanctuary.Ps3.Assets;

/// <summary>The outcome of choosing a zone name for <c>ZoneDetails</c>.</summary>
public sealed record ZoneChoice(string? Zone, IReadOnlyList<string> CompleteZones, string Note);

/// <summary>
/// Which zones an install can load. The zone name in <c>ZoneDetails</c> becomes three asset requests —
/// <c>&lt;zone&gt;.gzne</c> (terrain header), <c>.cob</c> and <c>.odd</c> — and an unknown name makes the load fail silently
/// (docs/re/zone-loading.md).
/// </summary>
public static class ZoneCatalog
{
    public static readonly string[] Extensions = [".gzne", ".cob", ".odd"];

    /// <summary>For every asset named zone.gzne/.cob/.odd: the extensions found per zone name (case kept from the pack).</summary>
    public static Dictionary<string, HashSet<string>> Build(IEnumerable<PackedEntry> entries)
    {
        var zones = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var (stem, extension) = SplitExtension(entry.Name);
            var lowered = extension.ToLowerInvariant();

            if (Array.IndexOf(Extensions, lowered) < 0)
                continue;

            if (!zones.TryGetValue(stem, out var found))
            {
                found = new HashSet<string>(StringComparer.Ordinal);
                zones[stem] = found;
            }

            found.Add(lowered);
        }

        return zones;
    }

    /// <summary>Zones that have everything the client requests when it enters them, sorted.</summary>
    public static List<string> Complete(Dictionary<string, HashSet<string>> zones)
    {
        var complete = zones
            .Where(zone => Extensions.All(zone.Value.Contains))
            .Select(zone => zone.Key)
            .ToList();

        complete.Sort(StringComparer.Ordinal);

        return complete;
    }

    /// <summary>Chooses the zone to send from the install's own packs; the preferred one when it is complete.</summary>
    public static ZoneChoice Pick(string usrDir, string preferred = "FabledRealms")
    {
        var packs = PackIndex.Load(usrDir);

        if (packs.Entries.Count == 0)
            return new ZoneChoice(null, [], $"no pack index could be read in {usrDir}");

        var complete = Complete(Build(packs.Entries));

        var lowered = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var zone in complete)
            lowered[zone.ToLowerInvariant()] = zone;

        if (lowered.TryGetValue(preferred.ToLowerInvariant(), out var match))
            return new ZoneChoice(match, complete, "preferred zone exists");

        if (complete.Count > 0)
            return new ZoneChoice(complete[0], complete, $"preferred zone '{preferred}' is not in the packs");

        return new ZoneChoice(null, [], "the packs hold no zone with .gzne, .cob and .odd");
    }

    /// <summary>Splits a name at its extension like Python's <c>os.path.splitext</c> (leading dots of a file name are not an extension).</summary>
    public static (string Stem, string Extension) SplitExtension(string name)
    {
        var separator = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        var start = separator + 1;

        var firstNonDot = start;

        while (firstNonDot < name.Length && name[firstNonDot] == '.')
            firstNonDot++;

        var dot = name.LastIndexOf('.');

        if (dot < firstNonDot || dot <= separator)
            return (name, string.Empty);

        return (name[..dot], name[dot..]);
    }
}
