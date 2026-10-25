using System.Collections.Generic;

namespace Sanctuary.Ps3.Assets;

/// <summary>One asset listed in the index of a pack file.</summary>
public readonly record struct PackEntry(string Name, uint Offset, uint Length, uint Crc);

/// <summary>A <see cref="PackEntry"/> together with the pack file that holds it.</summary>
public sealed record PackedEntry(string Pack, string Name, uint Offset, uint Length, uint Crc);

/// <summary>The bytes of one asset read from a pack, with the check that confirms offset and length are understood.</summary>
public sealed record PackedAsset(byte[] Data, string Pack, uint StoredCrc, bool CrcMatches);

/// <summary>Every pack index of an install.</summary>
public sealed record PackSet(
    IReadOnlyList<string> Packs,
    IReadOnlyList<PackedEntry> Entries,
    IReadOnlyList<string> Unreadable);
