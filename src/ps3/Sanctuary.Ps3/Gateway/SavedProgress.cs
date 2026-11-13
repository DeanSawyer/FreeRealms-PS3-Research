using System.Collections.Generic;
using System.Linq;

using Sanctuary.Ps3.Web;

namespace Sanctuary.Ps3.Gateway;

/// <summary>Turns a saved character's progress (<see cref="CharacterProgress"/>, kept in the database) into a <see cref="Loadout"/> and back.</summary>
public static class SavedProgress
{
    /// <summary>The loadout of <paramref name="progress"/>; the saved position only when it was saved in <paramref name="zone"/> and is believable.</summary>
    public static Loadout ToLoadout(CharacterProgress progress, string? zone = null) => new(
        [.. progress.Items.Select(item => new OwnedItem(item.Id, item.Definition, item.Tint, item.Count > 0 ? item.Count : 1))],
        progress.Equipment.ToDictionary(pair => pair.Key, pair => Slots(pair.Value)),
        progress.ActiveJob,
        progress.Coins,
        progress.Position is { } saved && zone is not null && string.Equals(saved.Zone, zone, System.StringComparison.OrdinalIgnoreCase)
            && new Placement(saved.X, saved.Y, saved.Z, saved.RotationX, saved.RotationY, saved.RotationZ) is { Believable: true } placement
            ? placement
            : null);

    /// <summary>The progress to save for <paramref name="loadout"/>; its position is saved as being in <paramref name="zone"/>.</summary>
    /// <param name="gotStartingCoins">Whether the character has had its starting coins (the game server gives them at its first login).</param>
    public static CharacterProgress ToProgress(Loadout loadout, string? zone = null, bool gotStartingCoins = false) => new(
        loadout.ActiveJob,
        loadout.Coins,
        [.. loadout.Items.Select(item => new SavedItem(item.Id, item.Definition, item.Tint, item.Count))],
        loadout.Equipment.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<SavedSlot>)pair.Value.Select(slot => new SavedSlot(slot.Key, slot.Value)).ToList()),
        loadout.Position is { } place && zone is not null
            ? new SavedPosition(zone, place.X, place.Y, place.Z, place.RotationX, place.RotationY, place.RotationZ)
            : null,
        gotStartingCoins);

    /// <summary>The slots in the order they were saved (the order they were filled).</summary>
    private static IReadOnlyDictionary<int, int> Slots(IReadOnlyList<SavedSlot> slots)
    {
        var ordered = new OrderedDictionary<int, int>();

        foreach (var slot in slots)
            ordered[slot.Slot] = slot.Item;

        return ordered;
    }
}
