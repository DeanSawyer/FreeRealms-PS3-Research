#!/usr/bin/env python3
"""One-command test run: restore the original scripts, start the bootstrap server, start RPCS3, then collect a report.

What it does, in order (Windows, run from an Administrator PowerShell so ports 53 and 80 can be bound):

  1. puts the original ui/scriptsbase.bin.sdat back wherever a patched copy is live (the .orig next to it);
  2. runs tools/zone_report.py and tools/tile_report.py on the install (what the client can load, where it spawns);
  3. starts bootstrap/fr_ps3_bootstrap.py (its output is shown here and saved);
  4. starts RPCS3 on the game and waits - log in, make or pick a character, watch the screen, then CLOSE RPCS3;
  5. stops the server and writes one zip with everything needed to read the result: both reports, the server log,
     the lines of interest, the end of the RPCS3 log and the end of the gateway trace.

Usage (PowerShell, from the repo folder):
    python tools\\run_test.py
    python tools\\run_test.py --install C:\\Users\\USER\\Desktop\\FR_INSTALL_TEST --no-restore

Nothing from the client is copied except text logs; the zip is written to the Desktop.
"""
import argparse
import datetime
import os
import re
import shutil
import subprocess
import sys
import threading
import time
import zipfile

REPO = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
DEFAULT_INSTALL = r"C:\Users\USER\Desktop\FR_INSTALL_TEST"
DEFAULT_EXTRA = r"C:\Users\USER\Downloads\Free-Realms-PS3-main\Free-Realms-PS3-main\NPUA30048"
INTEREST = re.compile(r"spawn:|zone:|ZoneDetails|gcnk|gzne|\.cob|\.odd|asset_missing|not in manifest|404|Failed|"
                      r"Traceback|Error|error|WARNING|FinishedLoading|AddPc|LoginReply", re.I)


def log(out_dir, text):
    print(text)
    with open(os.path.join(out_dir, "steps.txt"), "a", encoding="utf-8") as f:
        f.write(text + "\n")


def restore_scripts(install, out_dir):
    """Copy scriptsbase.bin.sdat.orig over the live scriptsbase.bin.sdat (outside the Preservation backups)."""
    done = 0
    for root, _dirs, files in os.walk(install):
        if os.sep + "Preservation" in root:
            continue
        for name in files:
            if name.lower() == "scriptsbase.bin.sdat.orig":
                src = os.path.join(root, name)
                dst = src[:-len(".orig")]
                if not os.path.exists(dst) or os.path.getsize(dst) != os.path.getsize(src):
                    shutil.copyfile(src, dst)
                    log(out_dir, "restored original script: " + dst)
                    done += 1
    if not done:
        log(out_dir, "scripts: nothing to restore (already the original)")


def run_tool(script, args, out_file, out_dir):
    cmd = [sys.executable, os.path.join(REPO, "tools", script)] + args + ["-o", out_file]
    res = subprocess.run(cmd, capture_output=True, text=True, errors="replace")
    log(out_dir, "%s: exit %d" % (script, res.returncode))
    if res.returncode:
        log(out_dir, (res.stderr or res.stdout)[-1500:])


def tee(proc, path):
    with open(path, "w", encoding="utf-8", errors="replace") as f:
        for line in proc.stdout:
            sys.stdout.write(line)
            sys.stdout.flush()
            f.write(line)
            f.flush()


def tail_lines(path, count):
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            return f.readlines()[-count:]
    except OSError:
        return []


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--install", default=DEFAULT_INSTALL, help="folder holding NPUA30048 and RPCS3_TEST")
    ap.add_argument("--extra-client", default=DEFAULT_EXTRA, help="second client folder for --extra-client (if it exists)")
    ap.add_argument("--digest", default=os.path.join(os.path.expanduser("~"), "Downloads", "NPUA30048.original.soe"),
                    help="the original PS3 digest (used with --match-local when the file exists)")
    ap.add_argument("--no-restore", action="store_true", help="leave scriptsbase.bin.sdat as it is")
    ap.add_argument("--rpcs3", help="RPCS3 executable (default: RPCS3_TEST\\rpcs3.exe)")
    ap.add_argument("--elf", help="game.elf to start (default: inside RPCS3_TEST)")
    ap.add_argument("--bootstrap-args", default="", help="extra arguments for fr_ps3_bootstrap.py")
    ap.add_argument("--out", help="folder for the report (default: Desktop\\fr_test_<time>)")
    args = ap.parse_args()

    install = args.install
    client = os.path.join(install, "NPUA30048")
    usrdir = os.path.join(client, "USRDIR")
    rpcs3 = args.rpcs3 or os.path.join(install, "RPCS3_TEST", "rpcs3.exe")
    elf = args.elf or os.path.join(install, "RPCS3_TEST", "dev_hdd0", "game", "NPUA30048", "USRDIR", "game.elf")
    stamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
    desktop = os.path.join(os.path.expanduser("~"), "Desktop")
    out_dir = args.out or os.path.join(desktop if os.path.isdir(desktop) else os.getcwd(), "fr_test_" + stamp)
    os.makedirs(out_dir, exist_ok=True)

    for label, path in (("client folder", client), ("USRDIR", usrdir), ("RPCS3", rpcs3), ("game.elf", elf)):
        if not os.path.exists(path):
            log(out_dir, "MISSING %s: %s" % (label, path))
            return 2
    log(out_dir, "test started " + stamp)

    if not args.no_restore:
        restore_scripts(install, out_dir)

    run_tool("zone_report.py", [usrdir], os.path.join(out_dir, "zone_report.txt"), out_dir)
    run_tool("tile_report.py", [usrdir], os.path.join(out_dir, "tile_report.txt"), out_dir)

    cmd = [sys.executable, "-u", os.path.join(REPO, "bootstrap", "fr_ps3_bootstrap.py"), "--client", client,
           "--codec", "zs-zlib-zero-trailer"]
    if os.path.isdir(args.extra_client):
        cmd += ["--extra-client", args.extra_client]
    cmd += ["--match-local"]      # the local game.self differs from the original digest; serve a digest that matches it
    if os.path.isfile(args.digest):
        cmd += ["--digest", args.digest]
    env = dict(os.environ)
    env.setdefault("FR_EXTRA", "1")       # extra packets after FinishedLoading (as in the earlier test commands)
    env.setdefault("FR_ADDPC", "other")
    if args.bootstrap_args:
        cmd += args.bootstrap_args.split()
    log(out_dir, "starting the server: " + " ".join(cmd))
    boot = subprocess.Popen(cmd, cwd=REPO, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                            errors="replace", bufsize=1, env=env)
    pump = threading.Thread(target=tee, args=(boot, os.path.join(out_dir, "bootstrap.log")), daemon=True)
    pump.start()
    time.sleep(6)
    if boot.poll() is not None:
        pump.join(timeout=3)
        log(out_dir, "THE SERVER STOPPED at once (exit %s). Run from an Administrator PowerShell; see bootstrap.log." %
            boot.returncode)
    else:
        log(out_dir, "")
        log(out_dir, ">>> Starting RPCS3. Log in, make or pick a character, watch the screen for a minute or two,")
        log(out_dir, ">>> then CLOSE RPCS3. This window finishes the report by itself.")
        try:
            subprocess.run([rpcs3, "--no-gui", elf], check=False)
        except OSError as err:
            log(out_dir, "could not start RPCS3: %s" % err)
        log(out_dir, "RPCS3 closed")
        time.sleep(2)

    if boot.poll() is None:
        boot.terminate()
        try:
            boot.wait(timeout=8)
        except subprocess.TimeoutExpired:
            boot.kill()
    pump.join(timeout=3)

    # Collect what is needed to read the result.
    boot_log = os.path.join(out_dir, "bootstrap.log")
    lines = [ln for ln in tail_lines(boot_log, 20000) if INTEREST.search(ln)]
    with open(os.path.join(out_dir, "lines_of_interest.txt"), "w", encoding="utf-8") as f:
        f.write("".join(lines[-400:]))
    rpcs3_log = os.path.join(install, "RPCS3_TEST", "log", "RPCS3.log")
    with open(os.path.join(out_dir, "rpcs3_log_tail.txt"), "w", encoding="utf-8") as f:
        f.write("".join(tail_lines(rpcs3_log, 3000)))
    # RPCS3 lines that show what the client opened (packs, tiles) and every patch line, kept in full.
    keep = re.compile(r"PAT|patch|assets_ps3|\.gcnk|\.gzne|FabledRealms|\.cob|\.odd|FreeRealms\.log|GameRuntimeTerrain", re.I)
    with open(os.path.join(out_dir, "rpcs3_log_filtered.txt"), "w", encoding="utf-8") as f:
        f.write("".join([ln for ln in tail_lines(rpcs3_log, 200000) if keep.search(ln)][-1500:]))
    # The patch files and RPCS3 settings (text only), so the diagnostic patch can use the exact same format.
    rp = os.path.join(install, "RPCS3_TEST")
    for rel in ("config\\patch_config.yml", "config\\log.yml", "config\\config.yml"):
        src = os.path.join(rp, rel.replace("\\", os.sep))
        if os.path.isfile(src):
            shutil.copyfile(src, os.path.join(out_dir, rel.replace("\\", "_")))
    pdir = os.path.join(rp, "patches")
    if os.path.isdir(pdir):
        for name in sorted(os.listdir(pdir)):
            if name.lower().endswith(".yml") and os.path.getsize(os.path.join(pdir, name)) < 400000:
                shutil.copyfile(os.path.join(pdir, name), os.path.join(out_dir, "patches_" + name))
    trace = os.path.join(REPO, "bootstrap", "ps3_gateway_trace.jsonl")
    with open(os.path.join(out_dir, "gateway_trace_tail.jsonl"), "w", encoding="utf-8") as f:
        f.write("".join(tail_lines(trace, 600)))

    zip_path = out_dir + ".zip"
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for name in sorted(os.listdir(out_dir)):
            z.write(os.path.join(out_dir, name), name)
    print("\nDONE. Attach this file and say what the screen showed:\n  " + zip_path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
