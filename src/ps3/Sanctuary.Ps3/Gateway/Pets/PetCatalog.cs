using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace Sanctuary.Ps3.Gateway;

/// <summary>A trick a kind of pet knows, as the pet list carries it (Sanctuary's PetTrickInfo).</summary>
public sealed record TrickInfo(int Id, int IconId, int NameId, int SkillLevel, int Unknown5, int Unknown6, int TooltipId, int Unknown8, bool Known);

/// <summary>An animation the pet plays and how long it lasts.</summary>
public sealed record TimedAnimation(int AnimationId, int DurationMs);

/// <summary>A way to lie down: how the pet gets down and up again, the animations it loops while down and those it fidgets with.</summary>
public sealed record RestPosture(TimedAnimation Enter, TimedAnimation Exit, IReadOnlyList<int> Loops, IReadOnlyList<TimedAnimation> Fidgets);

/// <summary>A trick's animations: the pet's, the owner's, how long it lasts and how long after the start the pet begins.</summary>
public sealed record TrickAnimation(int AnimationId, int OwnerAnimationId, int DurationMs, int StartDelayMs);

/// <summary>An item class a kind of pet can wear or use (the pet list's item classes).</summary>
public readonly record struct PetItemClass(int Key, int Id, int Unknown);

/// <summary>
/// A kind of pet, by its model: everything Sanctuary's pet definitions (<c>Resources/Pets.json</c>) hold for it.
/// </summary>
public sealed record PetKind(
    int ModelId,
    int IconId,
    int DefaultTintId,
    string? DefaultTintAlias,
    bool Vanity,
    IReadOnlyList<TrickInfo> Tricks,
    IReadOnlyList<PetItemClass> ItemClasses,
    IReadOnlyDictionary<int, TrickAnimation> TrickAnimations,
    IReadOnlyList<TimedAnimation> Idles,
    IReadOnlyList<TimedAnimation> BathroomBreaks,
    IReadOnlyList<RestPosture> RestPostures);

/// <summary>A pet item (a whistle, a pet bought or won): its names, picture and the model, texture and tint the pet is drawn with.</summary>
public sealed record PetItem(
    int Id,
    int NameId,
    int DescriptionId,
    int ImageSetId,
    int IconTint,
    int ModelId,
    string ModelFile,
    float ModelScale,
    int ModelGender,
    string TextureAlias,
    string TintAlias,
    bool MembersOnly,
    bool Tintable,
    int MaxStack,
    int Cost,
    string Comment);

/// <summary>
/// The kinds of pets (<c>Data/pet_definitions.json.gz</c>, Sanctuary's <c>Pets.json</c> as it is) and the pet items
/// (<c>Data/pet_items.tsv.gz</c>: the pet items of its <c>ClientItemDefinitions.json</c> with their models from <c>Models.txt</c>).
/// </summary>
public static class PetCatalog
{
    /// <summary>A model's gender in <c>Models.txt</c> that makes a male pet.</summary>
    public const int MaleModelGender = 1;

    private static readonly Lazy<IReadOnlyDictionary<int, PetKind>> LoadedKinds = new(() => ParseKinds(Read("Sanctuary.Ps3.pet_definitions.json.gz")));
    private static readonly Lazy<IReadOnlyDictionary<int, PetItem>> LoadedItems = new(() => ParseItems(Read("Sanctuary.Ps3.pet_items.tsv.gz")));

    /// <summary>The kinds of pets by model id.</summary>
    public static IReadOnlyDictionary<int, PetKind> Kinds => LoadedKinds.Value;

    /// <summary>The pet items by item id.</summary>
    public static IReadOnlyDictionary<int, PetItem> Items => LoadedItems.Value;

    /// <summary>The kind of pet item <paramref name="item"/> makes, when there is one.</summary>
    public static PetKind? KindOf(PetItem item) => Kinds.GetValueOrDefault(item.ModelId);

    public static IReadOnlyDictionary<int, PetKind> ParseKinds(string json)
    {
        using var document = JsonDocument.Parse(json);
        var kinds = new Dictionary<int, PetKind>();

        foreach (var kind in document.RootElement.EnumerateArray())
        {
            int Int(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
            bool Bool(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
            IEnumerable<JsonElement> Array(JsonElement element, string name) =>
                element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
            TimedAnimation Timed(JsonElement element) => new(Int(element, "AnimationId"), Int(element, "DurationMs"));

            var tricks = Array(kind, "PetTricks").Select(trick => new TrickInfo(Int(trick, "Id"), Int(trick, "IconId"), Int(trick, "NameId"),
                Int(trick, "SkillLevel"), Int(trick, "Unknown5"), Int(trick, "Unknown6"), Int(trick, "TooltipStringId"), Int(trick, "Unknown8"),
                Bool(trick, "Known"))).ToList();

            var classes = Array(kind, "ItemClasses").Select(pair => new PetItemClass(Int(pair, "Key"),
                Int(pair.GetProperty("Value"), "Id"), Int(pair.GetProperty("Value"), "Unknown"))).ToList();

            var trickAnimations = new Dictionary<int, TrickAnimation>();

            if (kind.TryGetProperty("TrickAnimations", out var animations) && animations.ValueKind == JsonValueKind.Object)
            {
                foreach (var trick in animations.EnumerateObject())
                {
                    trickAnimations[int.Parse(trick.Name, CultureInfo.InvariantCulture)] = new TrickAnimation(Int(trick.Value, "AnimationId"),
                        Int(trick.Value, "OwnerAnimationId"), Int(trick.Value, "DurationMs"), Int(trick.Value, "StartDelayMs"));
                }
            }

            var postures = Array(kind, "RestPostures").Select(posture => new RestPosture(
                new TimedAnimation(Int(posture, "EnterAnimationId"), Int(posture, "EnterDurationMs")),
                new TimedAnimation(Int(posture, "ExitAnimationId"), Int(posture, "ExitDurationMs")),
                Array(posture, "LoopAnimationIds").Select(loop => loop.GetInt32()).ToList(),
                Array(posture, "FidgetAnimations").Select(Timed).ToList())).ToList();

            var modelId = Int(kind, "Id");
            kinds[modelId] = new PetKind(modelId, Int(kind, "IconId"), Int(kind, "DefaultTintId"),
                kind.TryGetProperty("DefaultTintAlias", out var alias) && alias.ValueKind == JsonValueKind.String ? alias.GetString() : null,
                Bool(kind, "IsVanityPet"), tricks, classes, trickAnimations,
                Array(kind, "IdleAnimations").Select(Timed).ToList(), Array(kind, "BathroomAnimations").Select(Timed).ToList(), postures);
        }

        return kinds;
    }

    public static IReadOnlyDictionary<int, PetItem> ParseItems(string text)
    {
        var items = new Dictionary<int, PetItem>();

        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var c = line.TrimEnd('\r').Split('\t');
            int I(int index) => int.Parse(c[index], CultureInfo.InvariantCulture);

            var item = new PetItem(I(0), I(1), I(2), I(3), I(4), I(5), c[6], float.Parse(c[7], CultureInfo.InvariantCulture), I(8), c[9], c[10],
                c[11] == "1", c[12] == "1", I(13), I(14), c.Length > 15 ? c[15] : string.Empty);

            items[item.Id] = item;
        }

        return items;
    }

    private static string Read(string resource)
    {
        using var stream = typeof(PetCatalog).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"{resource} is not in the assembly.");
        using var unzipped = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(unzipped);

        return reader.ReadToEnd();
    }
}
