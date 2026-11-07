using System.Collections.Generic;
using System.Globalization;

namespace Sanctuary.Ps3.Gateway;

/// <summary>
/// The character creator's choices as the asset names the client draws, like <c>bootstrap/character_mappings.py</c>. The tables are
/// in <c>CharacterMappings.g.cs</c> (Sanctuary's <c>Resources/CharacterCreate/*Mappings.txt</c>); the client fills in
/// <c>&lt;race&gt;</c>, <c>&lt;gender&gt;</c> and <c>&lt;hairtype&gt;</c> itself.
/// </summary>
public static partial class CharacterMappings
{
    public static string Head(IReadOnlyDictionary<string, string> character) =>
        Heads.GetValueOrDefault(Number(character, "Head", 1), Heads[1]);

    /// <summary>Hair 0, and any number the table does not have, is bald.</summary>
    public static string Hair(IReadOnlyDictionary<string, string> character) =>
        Hairs.GetValueOrDefault(Number(character, "Hair", 0), Hairs[0]);

    public static string SkinTone(IReadOnlyDictionary<string, string> character)
    {
        var tone = Number(character, "SkinTone", 1);

        return $"skintone{(tone == 0 ? 1 : tone).ToString(CultureInfo.InvariantCulture)}";
    }

    public static string FacePaint(IReadOnlyDictionary<string, string> character) =>
        FacePaints.GetValueOrDefault(Number(character, "FacePaint", 0), "none");

    /// <summary>Facial hair and similar options; 0 means none (an empty string, as Sanctuary sends).</summary>
    public static string ModelCustomization(IReadOnlyDictionary<string, string> character)
    {
        var choice = Number(character, "ModelCustomization", 0);

        return choice > 0 ? ModelCustomizations.GetValueOrDefault(choice, string.Empty) : string.Empty;
    }

    /// <summary>Python's <c>int(str(value).strip() or default)</c>: missing, empty or not a number gives the default.</summary>
    internal static int Number(IReadOnlyDictionary<string, string> character, string key, int fallback)
    {
        if (!character.TryGetValue(key, out var text))
            return fallback;

        text = text.Trim();

        if (text.Length == 0)
            return fallback;

        return int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }
}
