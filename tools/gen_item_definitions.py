#!/usr/bin/env python3
"""Write bootstrap/data/item_definitions.tsv.gz: the item definitions the server sends the client.

The client needs a definition for every item a character owns: its name, icon, slot, model, texture and tint alias
and so on (PlayerUpdate sub-opcode 33; see docs/protocol/gateway.md, *Items*). They come from Sanctuary's
Resources/ClientItemDefinitions.json, which is 33 MB, so this keeps only the ones a new character can own:

* class 1 items worn in slots 1 to 6 (the adventurer clothes the character creator offers), and
* every job's DefaultItems in Resources/Profiles.json (the starter outfit and weapon of each job).

    python tools/gen_item_definitions.py PATH/TO/Sanctuary/src/Resources

The file is gzip with a fixed timestamp, so the same input always gives the same bytes. One row per definition, tab
separated, in the order of FIELDS (the order the PS3 client reads them, which is Sanctuary's BaseItemDefinition
order up to VipRankRequired; the PS3 reads MemberDiscount as one byte). Booleans are 0 or 1. ForceDisablePreview is set for type 16 items, as Sanctuary does.

Standard library only.
"""
import argparse
import gzip
import io
import json
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "bootstrap", "data", "item_definitions.tsv.gz")
CLOTHING_CLASS = 1
CLOTHING_SLOTS = (1, 2, 3, 4, 5, 6)

# column, kind (i = int, b = bool, s = string), where it comes from in ClientItemDefinitions.json
FIELDS = (
    ("id", "i", "Id"), ("name_id", "i", "NameId"), ("description_id", "i", "DescriptionId"),
    ("icon_id", "i", "Icon.Id"), ("icon_tint", "i", "Icon.TintId"), ("unknown", "i", "Unknown"),
    ("unknown2", "i", "Unknown2"), ("cost", "i", "Cost"), ("class", "i", "Class"),
    ("profile_override", "i", "ProfileOverride"), ("slot", "i", "Slot"), ("no_trade", "b", "NoTrade"),
    ("no_sale", "b", "NoSale"), ("model", "s", "ModelName"), ("texture_alias", "s", "TextureAlias"),
    ("gender_usage", "i", "GenderUsage"), ("type", "i", "Type"), ("category_id", "i", "CategoryId"),
    ("members_only", "b", "MembersOnly"), ("non_mini_game", "b", "NonMiniGame"),
    ("weapon_trail_effect_id", "i", "WeaponTrailEffectId"), ("composite_effect_id", "i", "CompositeEffectId"),
    ("power_rating", "i", "PowerRating"), ("min_profile_rank", "i", "MinProfileRank"), ("rarity", "i", "Rarity"),
    ("activatable_ability_id", "i", "ActivatableAbilityId"), ("passive_ability_id", "i", "PassiveAbilityId"),
    ("single_use", "b", "SingleUse"), ("max_stack_size", "i", "MaxStackSize"), ("is_tintable", "b", "IsTintable"),
    ("tint_alias", "s", "TintAlias"), ("force_disable_preview", "b", "ForceDisablePreview"),
    ("member_discount", "i", "MemberDiscount"), ("vip_rank_required", "i", "VipRankRequired"),
)
HEADER = "# " + "\t".join(name for name, _kind, _key in FIELDS)


def clean(text):
    return str(text or "").replace("\t", " ").replace("\n", " ").replace("\r", " ")


def value(item, key, kind):
    if key == "ForceDisablePreview":
        return 1 if item.get(key) or item.get("Type") == 16 else 0
    if "." in key:
        outer, inner = key.split(".")
        raw = (item.get(outer) or {}).get(inner)
    else:
        raw = item.get(key)
    if kind == "s":
        return clean(raw)
    if kind == "b":
        return 1 if raw else 0
    return int(raw or 0)


def wanted(definitions, profiles):
    jobs = {item_id for job in profiles for item_id in job.get("DefaultItems") or ()}
    out = []
    for item in definitions:
        clothing = (item.get("Class") == CLOTHING_CLASS and item.get("Slot") in CLOTHING_SLOTS and item.get("ModelName"))
        if clothing or item["Id"] in jobs:
            out.append(item)
    return sorted(out, key=lambda item: item["Id"])


def encode(rows):
    lines = [HEADER]
    for item in rows:
        lines.append("\t".join(str(value(item, key, kind)) for _name, kind, key in FIELDS))
    buf = io.BytesIO()
    with gzip.GzipFile(filename="", mode="wb", compresslevel=9, fileobj=buf, mtime=0) as gz:
        gz.write(("\n".join(lines) + "\n").encode("utf-8"))
    return buf.getvalue()


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("resources", help="Sanctuary's src/Resources folder")
    args = ap.parse_args()
    with open(os.path.join(args.resources, "ClientItemDefinitions.json"), encoding="utf-8-sig") as f:
        definitions = json.load(f)
    with open(os.path.join(args.resources, "Profiles.json"), encoding="utf-8-sig") as f:
        profiles = json.load(f)
    rows = wanted(definitions, profiles)
    data = encode(rows)
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "wb") as f:
        f.write(data)
    print("wrote %s (%d definitions, %d bytes)" % (os.path.relpath(OUT, ROOT), len(rows), len(data)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
