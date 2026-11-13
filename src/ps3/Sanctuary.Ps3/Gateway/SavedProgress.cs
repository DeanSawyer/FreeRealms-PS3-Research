using System.Collections.Generic;
using System.Linq;

using Sanctuary.Ps3.Web;

namespace Sanctuary.Ps3.Gateway;

/// <summary>Turns a saved character's progress (<see cref="CharacterProgress"/>, in characters.json) into a <see cref="Loadout"/> and back.</summary>
public static class SavedProgress
{
    public static Loadout ToLoadout(CharacterProgress progress) => new(
        [.. progress.Items.Select(item => new OwnedItem(item.Id, item.Definition, item.Tint))],
        progress.Equipment.ToDictionary(pair => pair.Key, pair => Slots(pair.Value)),
        progress.ActiveJob,
        progress.Coins);

    public static CharacterProgress ToProgress(Loadout loadout) => new(
        loadout.ActiveJob,
        loadout.Coins,
        [.. loadout.Items.Select(item => new SavedItem(item.Id, item.Definition, item.Tint))],
        loadout.Equipment.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<SavedSlot>)pair.Value.Select(slot => new SavedSlot(slot.Key, slot.Value)).ToList()));

    /// <summary>The slots in the order they were saved (the order they were filled).</summary>
    private static IReadOnlyDictionary<int, int> Slots(IReadOnlyList<SavedSlot> slots)
    {
        var ordered = new OrderedDictionary<int, int>();

        foreach (var slot in slots)
            ordered[slot.Slot] = slot.Item;

        return ordered;
    }
}
