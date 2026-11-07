using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Sanctuary.Ps3.Gateway;

/// <summary>
/// Reference data the client gets when it is ready: item classes and item categories, as Sanctuary sends them, like
/// <c>bootstrap/reference_data.py</c>. The tables are Sanctuary's <c>Resources/ItemClasses.txt</c>, <c>ItemCategories.txt</c> and
/// <c>ItemCategoryGroups.txt</c>, copied unchanged into <c>bootstrap/data/</c> and embedded in this assembly. The PS3 1.77 handler is
/// 0xd4764; sub-opcode 1 is read by 0xb0688c, sub-opcode 2 by 0xb066fc (layouts in <c>bootstrap/reference_data.py</c>).
/// </summary>
public static class ReferenceData
{
    private static readonly Lazy<IReadOnlyList<int[]>> Classes = new(() => Rows("ItemClasses.txt").Select(row => row.Take(6).ToArray()).ToList());

    private static readonly Lazy<IReadOnlyList<int[]>> Categories = new(() => Rows("ItemCategories.txt").Select(row => row.Take(6).ToArray()).ToList());

    private static readonly Lazy<IReadOnlyList<(int Group, int Category)>> Groups = new(LoadGroups);

    /// <summary>Every item class: id, name id, icon id, wield type, stat id, profile name id.</summary>
    public static IReadOnlyList<int[]> ItemClasses => Classes.Value;

    /// <summary>Every item category: id, name id, icon id, unknown, unknown 2, display.</summary>
    public static IReadOnlyList<int[]> ItemCategories => Categories.Value;

    /// <summary>(group id, category id), grouped by group id in first-seen order, as Sanctuary's dictionary keeps them.</summary>
    public static IReadOnlyList<(int Group, int Category)> ItemCategoryGroups => Groups.Value;

    /// <summary>ReferenceData (44) sub-opcode 1: a count, then per class its id (the key) and its six columns.</summary>
    public static byte[] ItemClassDefinitions()
    {
        var rows = ItemClasses;
        var writer = new PacketWriter().U16(44).U16(1).I32(rows.Count);

        foreach (var row in rows)
        {
            writer.I32(row[0]);

            foreach (var value in row)
                writer.I32(value);
        }

        return writer.ToArray();
    }

    /// <summary>
    /// ReferenceData (44) sub-opcode 2: a count, then per category its id (the key), id, name id, icon id, unknown and a byte for
    /// unknown 2 (21 bytes); then a count and per group entry the group id twice and the category id.
    /// </summary>
    public static byte[] ItemCategoryDefinitions()
    {
        var categories = ItemCategories;
        var groups = ItemCategoryGroups;
        var writer = new PacketWriter().U16(44).U16(2).I32(categories.Count);

        foreach (var category in categories)
            writer.I32(category[0]).I32(category[0]).I32(category[1]).I32(category[2]).I32(category[3]).U8(category[4] != 0 ? 1 : 0);

        writer.I32(groups.Count);

        foreach (var (group, category) in groups)
            writer.I32(group).I32(group).I32(category);

        return writer.ToArray();
    }

    private static List<(int Group, int Category)> LoadGroups()
    {
        var order = new List<int>();
        var byGroup = new Dictionary<int, List<int>>();

        foreach (var row in Rows("ItemCategoryGroups.txt"))
        {
            if (!byGroup.TryGetValue(row[0], out var list))
            {
                list = [];
                byGroup[row[0]] = list;
                order.Add(row[0]);
            }

            list.Add(row[1]);
        }

        return order.SelectMany(group => byGroup[group].Select(category => (group, category))).ToList();
    }

    /// <summary>The table's rows as integers, skipping blank lines and <c>#</c> comments, as <c>reference_data._rows</c> reads them.</summary>
    private static IEnumerable<int[]> Rows(string name)
    {
        var resource = "Sanctuary.Ps3." + name;

        using var stream = typeof(ReferenceData).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"embedded resource {resource} is missing");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var rows = new List<int[]>();

        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var parts = line.Split('^');

            if (line.EndsWith('^'))
                parts = parts[..^1].Where(part => part.Length > 0).ToArray();

            rows.Add(parts.Select(part => int.Parse(part.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)).ToArray());
        }

        return rows;
    }
}
