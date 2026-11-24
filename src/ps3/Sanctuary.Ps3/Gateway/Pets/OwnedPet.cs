using System;
using System.Collections.Generic;
using System.Linq;

using Sanctuary.Ps3.Web;

namespace Sanctuary.Ps3.Gateway;

/// <summary>A pet's temperament, which picks some of its idle animations (Sanctuary's PetTrait, the same numbers).</summary>
public enum Temperament
{
    Unset = 0,
    Friendly = 1,
    Lazy = 2,
    Nervous = 3,
    Excited = 4,
}

/// <summary>
/// A pet a character owns: the pet item it came from (which gives its kind, model, texture and names) and what the character has made
/// of it: name, tint, needs, temperament, favourite tricks and care items. Saved as a <see cref="SavedPet"/>.
/// </summary>
public sealed class OwnedPet
{
    /// <summary>Needs go from 0 to this; a new pet has them all full.</summary>
    public const float FullNeed = 100f;

    public const int FavoriteSlots = 4, CareSlots = 7;

    public OwnedPet(int id, PetItem item, int tint, string name, Temperament temperament, DateTimeOffset created)
    {
        Id = id;
        Item = item;
        Tint = tint;
        Name = name;
        Temperament = temperament;
        Created = created;
    }

    /// <summary>The pet's number among its owner's pets (from 1).</summary>
    public int Id { get; }

    public PetItem Item { get; }

    /// <summary>Its kind (tricks, animations, default tint), when Sanctuary has a definition for its model.</summary>
    public PetKind? Kind => PetCatalog.KindOf(Item);

    /// <summary>The tint bought with it, or 0 for the kind's default.</summary>
    public int Tint { get; }

    public string Name { get; set; }

    public float Hunger { get; set; } = FullNeed;

    public float Hygiene { get; set; } = FullNeed;

    public float Play { get; set; } = FullNeed;

    public float Mood { get; set; } = FullNeed;

    public Temperament Temperament { get; set; }

    public DateTimeOffset? TreasureFoundAt { get; set; }

    public int[] Favorites { get; } = new int[FavoriteSlots];

    public int[] CareItems { get; } = new int[CareSlots];

    public DateTimeOffset Created { get; }

    /// <summary>The tint it shows: its own, else its kind's default.</summary>
    public int ShownTint => Tint != 0 ? Tint : Kind?.DefaultTintId ?? 0;

    /// <summary>The tint alias its model is drawn with: the kind's default alias when it wears the default tint, else the item's.</summary>
    public string DrawnTintAlias => Kind is { DefaultTintAlias: { } alias } kind && ShownTint == kind.DefaultTintId ? alias : Item.TintAlias;

    /// <summary>1 for a male model, 0 for a female one (the pet list's sex).</summary>
    public byte Sex => Item.ModelGender == PetCatalog.MaleModelGender ? (byte)1 : (byte)0;

    public bool Vanity => Kind?.Vanity == true;

    /// <summary>The lowest of hunger, hygiene and play: the need the pet would complain about.</summary>
    public float LowestNeed => Math.Min(Hunger, Math.Min(Hygiene, Play));

    public SavedPet ToSaved() => new(Id, Item.Id, Tint, Name, Hunger, Hygiene, Play, Mood, (int)Temperament, TreasureFoundAt,
        [.. Favorites], [.. CareItems], Created);

    /// <summary>A saved pet as it was, or null when its pet item is unknown. A pet saved without a temperament gets one.</summary>
    public static OwnedPet? FromSaved(SavedPet saved, Random random)
    {
        if (!PetCatalog.Items.TryGetValue(saved.Definition, out var item))
            return null;

        var temperament = Enum.IsDefined((Temperament)saved.Trait) && saved.Trait != 0 ? (Temperament)saved.Trait : Roll(random);
        var pet = new OwnedPet(saved.Id, item, saved.Tint, saved.Name, temperament, saved.Created)
        {
            Hunger = saved.Hunger,
            Hygiene = saved.Hygiene,
            Play = saved.Play,
            Mood = saved.Mood,
            TreasureFoundAt = saved.TreasureFoundAt,
        };

        Fill(pet.Favorites, saved.FavoriteTricks);
        Fill(pet.CareItems, saved.CareItems);

        return pet;
    }

    /// <summary>A new pet from <paramref name="item"/>, with full needs and a temperament picked at random.</summary>
    public static OwnedPet Adopt(int id, PetItem item, int tint, string name, Random random, DateTimeOffset now)
        => new(id, item, tint, name, Roll(random), now);

    /// <summary>Friendly, lazy, nervous or excited, each as likely.</summary>
    public static Temperament Roll(Random random) => (Temperament)random.Next((int)Temperament.Friendly, (int)Temperament.Excited + 1);

    private static void Fill(int[] into, IReadOnlyList<int> from)
    {
        for (var index = 0; index < into.Length && index < from.Count; index++)
            into[index] = from[index];
    }
}

/// <summary>
/// How a pet's needs change while it is out, as Sanctuary's pet care: every 1.5 seconds play rises by a sixth a second (plus what
/// scratching added) and mood moves toward the harmonic mean of hunger, hygiene and play, rising a point a second and falling at once.
/// Hunger and hygiene change only by feeding and grooming.
/// </summary>
public static class PetCare
{
    public static readonly TimeSpan Period = TimeSpan.FromMilliseconds(1500);

    public const float PlayPerSecond = 1f / 6f;

    public const float MoodPerSecond = 1f;

    /// <summary>No need counts as lower than this in the mood's mean.</summary>
    public const float LeastNeedForMood = 1f;

    /// <summary>Scratching adds at most this much play at a time, and this much per unit of stick movement per millisecond.</summary>
    public const float MostScratchPlay = 2f, ScratchPlayPerMovement = 0.01f;

    /// <summary>A pet this happy or happier gives its owner the speed buff and does not look sad.</summary>
    public const float HappyMood = 25f;

    /// <summary>The mood the needs pull toward: the harmonic mean of hunger, hygiene and play (each at least 1).</summary>
    public static float MoodTarget(float hunger, float hygiene, float play) =>
        3f / ((1f / Math.Max(hunger, LeastNeedForMood)) + (1f / Math.Max(hygiene, LeastNeedForMood)) + (1f / Math.Max(play, LeastNeedForMood)));

    /// <summary>Moves <paramref name="pet"/>'s play and mood on by <paramref name="seconds"/>; true when either changed.</summary>
    public static bool Advance(OwnedPet pet, float seconds, float scratched)
    {
        var play = Math.Min(OwnedPet.FullNeed, pet.Play + (seconds * PlayPerSecond) + scratched);
        var mood = Math.Min(pet.Mood + (seconds * MoodPerSecond), MoodTarget(pet.Hunger, pet.Hygiene, play));

        if (play == pet.Play && mood == pet.Mood)
            return false;

        pet.Play = play;
        pet.Mood = mood;

        return true;
    }

    /// <summary>The play a scratch adds: stick movement per millisecond, at most <see cref="MostScratchPlay"/>.</summary>
    public static float ScratchPlay(int movementSquaredSum, int durationMs) =>
        movementSquaredSum <= 0 || durationMs <= 0 ? 0 : Math.Min(MostScratchPlay, movementSquaredSum / (float)durationMs * ScratchPlayPerMovement);
}
