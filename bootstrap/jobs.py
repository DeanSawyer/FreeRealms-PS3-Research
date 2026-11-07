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

# Each job's DefaultItems in Resources/Profiles.json (item definition ids). Jobs not listed (Adventurer, Card Duelist)
# have none and wear the character creator's clothes.
JOB_ITEMS = {
    2: (19522, 19431, 19197, 19787, 19253, 75091),            # Ninja
    4: (13137, 12827, 13197, 13317, 12957),                   # Postman
    11: (18783, 18695, 510, 18882, 18482, 4622),              # Medic
    12: (12178, 12060, 12709, 12416, 12574, 12252, 4914),     # Wizard
    14: (21665, 21060, 21784, 21219, 6561),                   # Miner
    16: (21496, 21324, 21012, 21683, 21149, 6459),            # Blacksmith
    32: (46644, 46389, 46137, 46732, 46224, 47019, 29963),    # Warrior
    35: (1238, 11530, 353, 11709, 354, 11828, 4266),          # Archer
    43: (7676, 7536, 7495, 7939, 7571, 7781, 75030),          # Brawler
    45: (14794, 13597, 14826, 13821),                         # Chef
    48: (14599, 13419, 15017, 14105),                         # Kart Driver
    49: (14481, 616, 623, 13998),                             # Demo Derby Driver
    52: (61907, 61353, 62169, 61609, 62473),                  # Soccer Star
    137: (68430, 68360, 68465, 68395),                        # Fisherman
    58: (13682, 421, 429),                                    # Referee
    20: (13682, 421, 429),                                    # Enforcer
}


# Each job's ItemClasses in Resources/Profiles.json, in its order: (item class, a value Sanctuary calls Unknown). The PS3
# profile entry carries them as a list at offset 0x25 (12 bytes each: the class again as the key, the class, the value).
JOB_ITEM_CLASSES = {
    1: ((1, 3), (69, 1), (72, 5), (143, 3), (144, 1), (145, 0), (146, 0), (147, 0), (148, 0), (149, 0), (150, 0), (153, 0), (154, 0), (155, 0), (156, 0), (157, 0), (158, 0), (160, 0), (161, 0), (162, 3), (168, 0), (173, 0), (179, 3), (180, 0), (181, 3), (182, 3), (999, 0)),   # Adventurer
    2: ((2, 5), (6, 5), (29, 5), (30, 5), (44, 5), (50, 3), (54, 5), (69, 1), (144, 0), (145, 0), (146, 0), (150, 0), (151, 0), (153, 0), (154, 0), (155, 0), (156, 0), (158, 0), (161, 0), (162, 0), (168, 0), (173, 0), (179, 1), (999, 0)),   # Ninja
    4: ((3, 5), (48, 5), (60, 3), (65, 5), (66, 5), (67, 3), (69, 1), (144, 0), (145, 0), (146, 0), (150, 0), (153, 0), (154, 0), (155, 0), (156, 0), (158, 0), (161, 0), (162, 0), (168, 0), (173, 0), (999, 0)),   # Postman
    11: ((7, 5), (20, 5), (27, 5), (28, 5), (43, 1), (50, 3), (53, 5), (69, 1), (144, 0), (145, 0), (146, 0), (150, 0), (152, 0), (153, 0), (154, 0), (155, 0), (156, 0), (158, 0), (161, 0), (162, 0), (168, 0), (173, 0), (999, 0)),   # Medic
    12: ((8, 5), (10, 5), (46, 5), (50, 3), (56, 5), (69, 1), (144, 0), (145, 0), (146, 0), (150, 0), (151, 0), (153, 0), (154, 0), (155, 0), (156, 0), (158, 0), (161, 0), (162, 0), (168, 0), (173, 0), (999, 0)),   # Wizard
    14: ((12, 5), (37, 5), (48, 5), (57, 5), (60, 3), (67, 3), (68, 0), (69, 1), (144, 0), (145, 0), (146, 0), (150, 0), (153, 0), (154, 0), (155, 0), (156, 0), (158, 0), (161, 0), (162, 0), (168, 0), (173, 0), (999, 0)),   # Miner
    16: ((14, 5), (34, 5), (48, 5), (49, 5), (58, 5), (60, 3), (67, 3), (69, 1), (144, 0), (145, 0), (146, 0), (150, 0), (153, 0), (154, 0), (155, 0), (156, 0), (158, 0), (161, 0), (162, 0), (168, 0), (173, 0), (999, 0)),   # Blacksmith
    32: ((15, 5), (17, 5), (45, 5), (50, 3), (55, 5), (69, 1), (144, 0), (145, 0), (146, 0), (150, 0), (151, 0), (153, 0), (154, 0), (155, 0), (156, 0), (158, 0), (161, 0), (162, 0), (168, 0), (173, 0), (183, 0), (999, 0)),   # Warrior
    35: ((16, 5), (18, 5), (41, 5), (50, 3), (51, 5), (69, 1), (144, 0), (145, 0), (146, 0), (150, 0), (152, 0), (153, 0), (154, 0), (155, 0), (156, 0), (158, 0), (161, 0), (162, 0), (168, 0), (173, 0), (999, 0)),   # Archer
    43: ((5, 5), (25, 5), (42, 5), (50, 3), (52, 5), (69, 1), (71, 5), (144, 0), (145, 0), (146, 0), (150, 0), (152, 0), (153, 0), (154, 0), (155, 0), (156, 0), (158, 0), (161, 0), (162, 0), (168, 0), (173, 0), (181, 1), (182, 1), (183, 0), (999, 0)),   # Brawler
    45: ((21, 5), (38, 5), (59, 5), (60, 3), (64, 5), (67, 5), (68, 3), (69, 1), (144, 0), (145, 0), (146, 0), (150, 0), (153, 0), (154, 0), (155, 0), (156, 0), (158, 0), (161, 0), (162, 0), (168, 0), (173, 0), (999, 0)),   # Chef
    48: ((24, 5), (69, 1), (144, 0), (145, 0), (146, 0), (150, 0), (153, 0), (154, 0), (155, 0), (156, 0), (158, 0), (161, 0), (162, 0), (168, 0), (173, 0), (999, 0)),   # Kart Driver
    49: ((22, 5), (69, 1), (144, 0), (145, 0), (146, 0), (150, 0), (153, 0), (154, 0), (155, 0), (156, 0), (158, 0), (161, 0), (162, 0), (168, 0), (173, 0), (999, 0)),   # Demo Derby Driver
    52: ((69, 1), (134, 5), (135, 5), (136, 5), (143, 3), (144, 0), (145, 0), (146, 0), (150, 0), (153, 0), (154, 0), (155, 0), (156, 0), (158, 0), (161, 0), (162, 0), (168, 0), (173, 0), (999, 0)),   # Soccer Star
    120: ((1, 3), (62, 5), (69, 1), (144, 0), (145, 0), (146, 0), (147, 0), (148, 0), (149, 0), (150, 0), (153, 0), (154, 0), (155, 0), (156, 0), (157, 0), (158, 0), (160, 0), (161, 0), (162, 3), (168, 0), (173, 0), (179, 3), (181, 3), (182, 3), (999, 0)),   # Card Duelist
    137: ((69, 1), (137, 5), (138, 5), (139, 5), (144, 0), (145, 0), (146, 0), (150, 0), (153, 0), (154, 0), (155, 0), (156, 0), (158, 0), (161, 0), (162, 0), (168, 0), (173, 0), (999, 0)),   # Fisherman
    58: ((69, 1), (143, 3), (144, 1), (145, 0), (146, 0), (147, 0), (148, 0), (149, 0), (150, 0), (153, 0), (154, 0), (155, 0), (156, 0), (157, 0), (158, 0), (160, 0), (161, 0), (162, 3), (168, 0), (173, 0), (999, 0), (26, 0), (1, 0)),   # Referee
    20: ((69, 1), (143, 3), (144, 1), (145, 0), (146, 0), (147, 0), (148, 0), (149, 0), (150, 0), (153, 0), (154, 0), (155, 0), (156, 0), (157, 0), (158, 0), (160, 0), (161, 0), (162, 3), (168, 0), (173, 0), (999, 0), (26, 0), (1, 0)),   # Enforcer
}


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
