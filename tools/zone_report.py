#!/usr/bin/env python3
"""Answer one question from the user's own install: which zone name can the PS3 client actually load?

The client turns the zone name from the server's ZoneDetails packet into three asset requests
(read from game.elf, see docs/re/zone-loading.md):

    <zone>.gzne   terrain header      fn_007df72c   ("%s.gzne")
    <zone>.cob                        fn_007cf7b0   ("%s.cob")
    <zone>.odd    object placement    FUN_007d13d8  ("%s.odd")

A request is accepted only if the name is in assets_ps3_manifest.bin (hash of the upper-cased name, 0x802828); an
unknown name returns failure at once, the ZoneDetails handler (fn_0010cb84) gives up before the world is set up,
and the loading screen is never dropped. This tool reads the pack indexes and the manifest and reports which zones
exist and whether the zone the server currently sends is among them.

Usage (PowerShell, from the repo folder):
    python tools\\zone_report.py "C:\\...\\NPUA30048\\USRDIR"
    python tools\\zone_report.py "C:\\...\\USRDIR" --zone FabledRealms -o zone_report.txt

Only the packs' index chunks and the manifest are read; the only file written is the report.
"""
import argparse
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "bootstrap"))
import assetlib  # noqa: E402


def build_report(usrdir, zone):
    lines = []
    out = lines.append
    packs, entries, unreadable = assetlib.load_packs(usrdir)
    out("packs found: %d, unreadable: %d %s" % (len(packs), len(unreadable), unreadable[:3]))
    out("asset names in the pack indexes: %d" % len(entries))
    if not entries:
        out("NO PACK INDEX COULD BE READ. Run tools\\list_packs.py and attach its *_head_hex.txt instead.")
        return lines

    manifest = None
    manifest_path = os.path.join(usrdir, "assets_ps3_manifest.bin")
    if os.path.exists(manifest_path):
        manifest = assetlib.Manifest(manifest_path)
        out("manifest: %d records, %s endian, sorted %.4f" % (
            manifest.count, "big" if manifest.endian == ">" else "little", manifest.sorted_fraction))
        found = same_crc = 0
        for _pk, name, _off, _length, crc in entries:
            rec = manifest.find(name)
            if rec:
                found += 1
                same_crc += rec[0] == crc
        out("pack names found in the manifest by CRC-64: %d of %d (%.1f%%)" % (
            found, len(entries), 100.0 * found / len(entries)))
        out("pack crc equal to manifest crc (needed for 'local copy is current', 0x801020): %d of %d" % (
            same_crc, len(entries)))
        if found < 0.9 * len(entries):
            out("  -> the name hash or record layout in bootstrap/assetlib.py does not match this file;")
            out("     report this, do not trust the zone verdicts below.")
    else:
        out("manifest not found at %s (zone verdicts use the packs only)" % manifest_path)

    zones = assetlib.zone_catalog(entries)
    complete = assetlib.complete_zones(zones)
    lower_names = {name.lower() for _pk, name, *_ in entries}

    out("")
    out("zones in the packs (a name with at least a .gzne file); columns show which of gzne/cob/odd exist:")
    for z in sorted(z for z, exts in zones.items() if ".gzne" in exts):
        marks = " ".join((e.lstrip(".") if e in zones[z] else "--") for e in assetlib.ZONE_EXTS)
        out("  %-40s %s" % (z, marks))
    out("")
    out("zones with all of .gzne .cob .odd: %s" % (", ".join(complete) if complete else "(none)"))

    out("")
    out("zone the server sends now: %s" % zone)
    for ext in assetlib.ZONE_EXTS:
        name = zone + ext
        in_pack = name.lower() in lower_names
        in_man = ("yes" if manifest.find(name) else "NO") if manifest else "?"
        out("  %-34s in a pack: %-3s in manifest: %s" % (name, "yes" if in_pack else "NO", in_man))
    out("")
    if (zone + ".gzne").lower() in lower_names:
        out("verdict: the current zone name exists in the packs; the stall is not caused by the zone name.")
    elif complete:
        out("verdict: the current zone name is NOT in the packs. Use one of the zones above, e.g. in PowerShell:")
        out('    $env:FR_ZONE="%s"   (then start the bootstrap server again)' % complete[0])
    else:
        out("verdict: the current zone name is not in the packs and no complete zone was found; attach this report.")
    return lines


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("usrdir", help="folder holding assets_ps3_NNN.pack and assets_ps3_manifest.bin")
    ap.add_argument("--zone", default="FabledRealms", help="zone name the server currently sends")
    ap.add_argument("-o", "--out", default="zone_report.txt")
    args = ap.parse_args()
    text = "\n".join(build_report(args.usrdir, args.zone)) + "\n"
    with open(args.out, "w", encoding="utf-8") as f:
        f.write(text)
    print(text)
    print("Report written to", os.path.abspath(args.out))
    return 0


if __name__ == "__main__":
    sys.exit(main())
