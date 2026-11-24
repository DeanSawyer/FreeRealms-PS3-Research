using System;

namespace Sanctuary.Ps3.Database.Entities;

/// <summary>
/// A pet a character owns, with the columns of Sanctuary's <c>DbPet</c>: its id among the character's pets, the pet item it came from,
/// its tint and name, its four needs, its trait, when it last dug up treasure, its four favourite tricks and its seven care items.
/// </summary>
public sealed class DbPet
{
    public int Id { get; set; }

    public int Definition { get; set; }

    public int Tint { get; set; }

    public string Name { get; set; } = string.Empty;

    public float Hunger { get; set; }

    public float Hygiene { get; set; }

    public float Play { get; set; }

    public float Mood { get; set; }

    public int Trait { get; set; }

    public DateTimeOffset? TreasureFoundAt { get; set; }

    public int EquippedPetTrick1 { get; set; }

    public int EquippedPetTrick2 { get; set; }

    public int EquippedPetTrick3 { get; set; }

    public int EquippedPetTrick4 { get; set; }

    public int ShampooItemId { get; set; }

    public int ConditionerItemId { get; set; }

    public int ToyItemId { get; set; }

    public int BrushItemId { get; set; }

    public int FoodBowlItemId { get; set; }

    public int FoodItemId { get; set; }

    public int VitaminItemId { get; set; }

    public DateTimeOffset Created { get; set; }

    public ulong CharacterId { get; set; }

    public DbCharacter Character { get; set; } = null!;
}
