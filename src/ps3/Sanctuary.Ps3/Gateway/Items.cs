using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Sanctuary.Ps3.Gateway;

/// <summary>One item a character owns: its id, its definition, its tint and how many (stackable items bought in the coin store).</summary>
public readonly record struct OwnedItem(int Id, int Definition, int Tint, int Count = 1);

/// <summary>
/// The items a new character owns and the item definitions the client needs for them, as Sanctuary gives them, like
/// <c>bootstrap/items.py</c>. The definitions are <c>bootstrap/data/item_definitions.tsv.gz</c> (embedded in this assembly, written by
/// <c>tools/gen_item_definitions.py</c> from Sanctuary's <c>Resources/ClientItemDefinitions.json</c>): 34 fields per definition in the
/// order the PS3 client reads them.
/// </summary>
public static class Items
{
    private const string ResourceName = "Sanctuary.Ps3.item_definitions.tsv.gz";

    /// <summary>Field kinds: <c>i</c> i32, <c>b</c> bool (one byte), <c>u</c> u8, <c>s</c> string (i32 length + UTF-8).</summary>
    public const string FieldKinds = "iiiiiiiiiiibbssiiibbiiiiiiibibsbui";

    public const int IconTintField = 4;
    public const int SlotField = 10;
    public const int ModelField = 13;
    public const int TextureAliasField = 14;
    public const int GenderUsageField = 15;
    public const int TintAliasField = 30;

    /// <summary>Highest equipment slot: a job's 17 equipment words (entry offset 0x43) are indexed by slot, word 0 unused.</summary>
    public const int EquipmentSlots = 16;

    private static readonly (string Item, string Tint)[] CreatorItems =
    [
        ("ItemChest", "ItemChestTint"), ("ItemLegs", "ItemLegsTint"), ("ItemFeet", "ItemFeetTint"),
        ("ItemHead", "ItemHeadTint"), ("ItemHands", "ItemHandsTint"), ("ItemShoulders", "ItemShouldersTint"),
    ];

    /// <summary>Sanctuary's <c>Models.txt</c>: human and fairy, male (1) and female (2).</summary>
    private static readonly Dictionary<int, int> GenderByModel = new() { [1] = 1, [2] = 1, [60] = 2, [61] = 2 };

    private static readonly Lazy<IReadOnlyDictionary<int, string[]>> Table = new(Load);

    /// <summary>
    /// The definitions, by id, each as its 34 fields in text form; read from the embedded file on first use, with the coin store's
    /// (<see cref="CoinStore.Catalog"/>) added.
    /// </summary>
    public static IReadOnlyDictionary<int, string[]> Definitions => Table.Value;

    public static int Int(string[] row, int field) => int.Parse(row[field], CultureInfo.InvariantCulture);

    /// <summary>One definition as the client reads it: the 34 fields, then an int and two empty lists.</summary>
    public static byte[] DefinitionPayload(string[] row)
    {
        var writer = new PacketWriter();

        for (var index = 0; index < FieldKinds.Length; index++)
        {
            switch (FieldKinds[index])
            {
                case 's':
                    writer.Str(row[index]);
                    break;
                case 'b':
                    writer.U8(Int(row, index) != 0 ? 1 : 0);
                    break;
                case 'u':
                    writer.U8(Int(row, index) & 0xFF);
                    break;
                default:
                    writer.I32(Int(row, index));
                    break;
            }
        }

        return writer.I32(0).I32(0).I32(0).ToArray();
    }

    /// <summary>PlayerUpdate (35) sub-opcode 33 with one definition (handler 0x110ab0, then 0x11e008); i32 length, then the payload.</summary>
    public static byte[] ItemDefinition(int definitionId)
    {
        var payload = DefinitionPayload(Definitions[definitionId]);

        return new PacketWriter().U16(35).U16(33).I32(payload.Length).Raw(payload).ToArray();
    }

    /// <summary>Sanctuary's <c>ItemInstance.LastCastTime</c> default, <c>int.MaxValue</c>.</summary>
    public const int LastCastTime = int.MaxValue;

    /// <summary>
    /// ClientUpdate (38) sub-opcode 2, ItemAdd: one item and its definition in an i32-length payload, as Sanctuary's RewardManager
    /// sends them. The PS3 handler (0xfacf0) builds the client's item object from it (definition, tint, id, count, consumed count, last
    /// cast time, a rental byte), reads the ActivateEnabled byte, adds the item to the player, then reads and caches the definition.
    /// These, not the player data's item list, put the items in the inventory and equipment screens.
    /// </summary>
    public static byte[] ItemAdd(OwnedItem item, int count = 1, bool activateEnabled = false)
    {
        var payload = new PacketWriter()
            .I32(item.Definition).I32(item.Tint).I32(item.Id).I32(count).I32(0).I32(LastCastTime).U8(0).U8(activateEnabled ? 1 : 0)
            .Raw(DefinitionPayload(Definitions[item.Definition]))
            .ToArray();

        return new PacketWriter().U16(38).U16(2).I32(payload.Length).Raw(payload).ToArray();
    }

    /// <summary>One <see cref="ItemAdd"/> per item the character owns, in item order.</summary>
    public static List<byte[]> ItemAddPackets(IReadOnlyList<OwnedItem> items) => items.Select(item => ItemAdd(item, item.Count)).ToList();

    /// <summary>One entry of the player data's item list (reader 0xb0f10c): id, an int, definition, tint, id, count, a byte, an int, an empty list, an int.</summary>
    public static byte[] ItemRecord(OwnedItem item, int count = 1) =>
        new PacketWriter().I32(item.Id).I32(0).I32(item.Definition).I32(item.Tint).I32(item.Id).I32(count).U8(0).I32(0).I32(0).I32(0).ToArray();

    /// <summary>The item list of the player data: a count, then one record per item.</summary>
    public static byte[] ItemList(IReadOnlyList<OwnedItem> items)
    {
        var writer = new PacketWriter().I32(items.Count);

        foreach (var item in items)
            writer.Raw(ItemRecord(item, item.Count));

        return writer.ToArray();
    }

    /// <summary>
    /// The character's items and each job's equipment ({slot: item id}), as Sanctuary's <c>CharacterCreateRequestHandler</c> and
    /// <c>UnlockAllProfiles</c> make them: the creator's pieces first (ids 1, 2, 3... with the creator's tints), worn by the starter job
    /// and by every job without DefaultItems; every other job's DefaultItems become items when not owned yet (the definition's icon
    /// tint), skipping the other gender's. A later item in the same slot wins.
    /// </summary>
    public static (List<OwnedItem> Items, Dictionary<int, Dictionary<int, int>> Equipment) Inventory(
        IReadOnlyDictionary<string, string> character,
        IReadOnlyList<Job> chosenJobs)
    {
        var table = Definitions;
        var gender = GenderByModel.GetValueOrDefault(CharacterMappings.Number(character, "Model", 1), 1);
        var items = new List<OwnedItem>();
        var byDefinition = new Dictionary<int, int>();

        int Add(int definition, int tint)
        {
            var item = new OwnedItem(items.Count + 1, definition, tint);

            items.Add(item);
            byDefinition.TryAdd(definition, item.Id);

            return item.Id;
        }

        var creator = new List<int>();

        foreach (var (itemField, tintField) in CreatorItems)
        {
            var definition = CharacterMappings.Number(character, itemField, 0);

            if (definition > 0 && table.ContainsKey(definition))
                creator.Add(Add(definition, CharacterMappings.Number(character, tintField, 0)));
        }

        Dictionary<int, int> Equip(IEnumerable<int> itemIds)
        {
            var slots = new Dictionary<int, int>();

            foreach (var id in itemIds)
            {
                var slot = Int(table[items[id - 1].Definition], SlotField);

                if (slot is >= 1 and <= EquipmentSlots)
                    slots[slot] = id;
            }

            return slots;
        }

        var equipment = new Dictionary<int, Dictionary<int, int>>();

        foreach (var job in chosenJobs)
        {
            var defaults = Jobs.DefaultItems.GetValueOrDefault(job.Id, []);

            if (job.Id == Jobs.StarterJob || defaults.Length == 0)
            {
                equipment[job.Id] = Equip(creator);

                continue;
            }

            var owned = new List<int>();

            foreach (var definition in defaults)
            {
                if (!table.TryGetValue(definition, out var row))
                    continue;

                var usage = Int(row, GenderUsageField);

                if (usage != 0 && usage != gender)
                    continue;

                owned.Add(byDefinition.TryGetValue(definition, out var existing) ? existing : Add(definition, Int(row, IconTintField)));
            }

            equipment[job.Id] = Equip(owned);
        }

        return (items, equipment);
    }

    /// <summary>One <see cref="ItemDefinition"/> per distinct definition the character owns, in item order.</summary>
    public static List<byte[]> DefinitionPackets(IReadOnlyList<OwnedItem> items) =>
        items.Select(item => item.Definition).Distinct().Select(ItemDefinition).ToList();

    private static IReadOnlyDictionary<int, string[]> Load()
    {
        using var stream = typeof(Items).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"embedded resource {ResourceName} is missing");
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);

        var table = new Dictionary<int, string[]>();

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#')
                continue;

            var parts = line.Split('\t');

            table[int.Parse(parts[0], CultureInfo.InvariantCulture)] = parts;
        }

        // The coin store's items, so that what is bought can be shown, worn and saved like any other item.
        foreach (var (id, item) in CoinStore.Catalog)
            table.TryAdd(id, item.Definition);

        return table;
    }
}
