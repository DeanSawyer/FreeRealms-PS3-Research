"""The jobs (profiles) a character has, as Sanctuary gives them.

Sanctuary's login.json sets UnlockAllProfiles, so a new character gets every job in Resources/Profiles.json at level 20
(CharacterCreateRequestHandler.UnlockAllProfiles), the starter job (Adventurer, id 1) first. The gateway copies each
job's name, description, type, icon, image sets and members-only flag into the player data
(GatewayConnection: ClientPcProfile) and sets the rank to the level.

JOBS is that file's entries in its order, with the fields the PS3 1.77 profile entry has room for (read with
tools/ppc_trace.py; see docs/protocol/gateway.md): id 0x00, name id 0x04, description id 0x08, type 0x0c, icon 0x10,
ability background, badge and button image sets 0x14, 0x18, 0x1c, members only 0x20 (one byte), rank 0x33.

FR_JOBS picks which jobs to send: unset or "all" sends every one; a list of ids ("1,11,35") sends those, and the
starter job is always included because it is the active profile.
"""
import os

STARTER_JOB = 1
JOB_RANK = 20   # Sanctuary's DbProfile Level for every job of a new character

# id, name id, description id, type, icon, ability background image set, badge image set, button image set, members only
JOBS = (
    (1, 2836, 2837, 1, 1334, 4, 19, 21, False),          # Adventurer
    (2, 3100, 3101, 2, 1342, 3, 10, 1, False),           # Ninja
    (4, 3237, 3238, 3, 1339, 4, 11, 2, False),           # Postman
    (11, 2278, 2279, 2, 1340, 15, 13, 9, False),         # Medic
    (12, 2280, 2281, 2, 1343, 14, 12, 8, False),         # Wizard
    (14, 2470, 2471, 9, 1341, 4, 11, 2, False),          # Miner
    (16, 2614, 2615, 12, 1336, 4, 11, 2, False),         # Blacksmith
    (32, 4664, 4665, 2, 21594, 31, 10, 1, False),        # Warrior
    (35, 4822, 4823, 2, 1335, 7, 32, 33, False),         # Archer
    (43, 2873, 3234, 2, 1337, 4, 10, 1, False),          # Brawler
    (45, 2856, 2857, 10, 156, 4, 11, 2, False),          # Chef
    (48, 2917, 2918, 5, 20725, 4, 46, 7494, False),      # Kart Driver
    (49, 2919, 2920, 7, 8059, 4, 46, 7494, False),       # Demo Derby Driver
    (52, 4650, 2920, 8, 20743, 4, 4842, 21, False),      # Soccer Star
    (120, 2410, 2411, 17, 396, 4, 1783, 1782, False),    # Card Duelist
    (137, 20178, 407842, 21, 20740, 4, 4843, 5885, False),  # Fisherman
    (58, 23268, 2302, 9, 258, 4, 19, 21, False),         # Referee
    (20, 2301, 2302, 9, 258, 4, 19, 21, False),          # Enforcer
)
BY_ID = {job[0]: job for job in JOBS}


def selected():
    """The jobs to put in the player data, starter first (see FR_JOBS above)."""
    raw = os.environ.get("FR_JOBS", "").strip()
    if not raw or raw.lower() == "all":
        return list(JOBS)
    wanted = set()
    for part in raw.replace(";", ",").split(","):
        try:
            wanted.add(int(part.strip()))
        except ValueError:
            pass
    wanted.add(STARTER_JOB)
    return [job for job in JOBS if job[0] in wanted]
