# PS3 web-service reply formats (recovered from the decrypted client)

Source: static reading of a decrypted `game.elf` (NPUA30048). Nothing here comes from a live server.
Requests go to `http://<host>/ps3ws/<path>.action` as `application/x-www-form-urlencoded` POSTs; replies are
XML read with TinyXML (an `<?xml ...?>` declaration is accepted).

## The success rule

Every reply parser reads `<Status>` as a decimal integer and passes `Status == 1` to its handler as the
success flag (`OnRequest...: requestId=%d, result=%d`). Any other value takes the failure path. For
`serverList` that path shows the localized text for `Char_Unable_Log_Server_PS3`
("Unable to log into the servers right now, please try again later."), which is the box seen when the
server answered `<Status>0</Status>`. Missing elements do not abort the parse: the reader sets an error
flag and keeps going, and integer fields default to 0.

## Attributes, not child elements

The client's reply reader has two modes for plain values (`Status`, `StatusMessage`, `Name`, `Online`,
`Guid`, ...): read them as attributes of the current element, or as child elements. The web-service
object sets attribute mode in its constructor (`this+152 = 1`) and nothing in the code turns it off, so
the replies are attribute style:

```xml
<ServerListReply Status="1" StatusMessage="Success"><Servers><Server Name="NorthForge" Online="1" Locked="0"/></Servers></ServerListReply>
<AccountInfoReply Status="1" StatusMessage="Success"><Account Member="1" MaxCharacters="3" Admin="0"/><Characters/></AccountInfoReply>
```

Structural elements (`Servers`/`Server`, `Account`, `Characters`/`Character`, `Suggestions`, `Friends`)
stay elements; they are entered by name. A reply written with child elements (`<Status>1</Status>`)
finds no `Status`, reads 0, and takes the failure path, which is what the first versions of this server
did. The server keeps the other form behind `--xml-style elements` in case this reading is wrong.

## Replies

The field names below are the attributes (or child elements, in `elements` mode) read from each element.

| Endpoint | Root element | Fields read |
|---|---|---|
| `game/serverList` | `ServerListReply` | `Status`, `StatusMessage`, `Servers/Server*` with `Name`, `Online` (default 0), `Locked` (default 1) |
| `account/info` | `AccountInfoReply` | `Status`, `StatusMessage`, `Account` (`Member`, `MaxCharacters`, `Admin`), `Characters/Character*` |
| `character/checkName` | `CheckNameReply` | `Status`, `StatusMessage`, `Suggestions/Name*` |
| `character/create` | `CharacterCreateReply` | `Status`, `StatusMessage`, `Guid` (64-bit) |
| `character/login` | `CharacterLoginReply` | `Status`, `StatusMessage`, `GatewayAddress`, `GatewayTicket`, `Key` |
| `character/delete` | `CharacterDeleteReply` | `Status`, `StatusMessage` |
| `character/friendsList` | `CharacterFriendsReply` | `Status`, `StatusMessage`, `Friends/Friend*` |

An account with no `Character` entries makes the client log "No existing character found on the server"
and continue to character creation.

### The `Character` element

Read from the parser (`0xd9b6f8`, called for `Characters` by the `AccountInfoReply` reader at `0xa1f040`). Offsets are in the
client's character record; strings are the client's 52-byte string objects.

```xml
<Character HeadshotUrl="">
  <Details Guid="1" Name="Mentor Kenner" Model="1" Head="&lt;race&gt;_&lt;gender&gt;_head_african.adr"
           Hair="&lt;race&gt;_m_hair_fauxhawk_&lt;hairtype&gt;.adr" ModelCustomization="" FacePaint="none"
           SkinTone="skintone2" EyeColor="63" HairColor="10">
    <Attachments>
      <Attachment Model="freestyle_&lt;race&gt;_m_chest_rolledslvshirt.adr" Texture="freestyle-stitchup-L1"
                  Tint="dyetint" TintOverrideId="234" EffectId="0" Slot="3"/>
    </Attachments>
  </Details>
</Character>
```

| Element | Field | Read as | Record offset |
| :--- | :--- | :--- | :--- |
| `Character` | `HeadshotUrl` | string | `+352` |
| `Details` | `Guid` | 64-bit (`0xd9b310`) | `+0` |
| `Details` | `Name` | string | `+8` |
| `Details` | `Model` | integer | `+60` |
| `Details` | `Head`, `Hair`, `ModelCustomization`, `FacePaint`, `SkinTone` | strings (asset names) | `+64`, `+116`, `+168`, `+220`, `+272` |
| `Details` | `EyeColor`, `HairColor` | integers | `+324`, `+328` |
| `Details/Attachments/Attachment` | `Model`, `Texture`, `Tint` (strings), `TintOverrideId`, `EffectId`, `Slot` | read by `0xd9ad74` | |

The appearance fields are asset names, not the creator's numbers: the server sends the same names as the game server's
SendSelfToClient (`CharacterMappings`), and one `Attachment` per piece of clothing the creator picked (`Clothing.Attachments`).
Character select (`0x78bc88`, login state 5) builds the model from the first entry. The other tag names in the client
(`LastPlayed`, `Server`, `Zone`, `Online`, `Locked`) are not read by this parser.

### Saved characters

`character/create` adds a character to the server's list and answers with its new `Guid` (1, 2, 3, ... never reused). Its
request carries `FirstName`, `LastName`, the appearance numbers, the six `Item*`/`Item*Tint` pairs, `Location`, and for a custom
name also `TemporaryFirstName`/`TemporaryLastName` (the request builder at `0xd9ec78` adds them when the record's `+80` string is
set). Everything but `Session` is kept.

`account/info` answers `Status` 1 with one `Character` per saved character, so the client goes to character select; with none it
keeps `Status` 2 (see below). `character/login` sends `Guid`, `Server` and `Session`: the server then sends that character to the
game. `character/delete` removes the character with the request's `Guid`.

The rules follow Sanctuary's login server (`CharacterCreateRequestHandler`, `CharacterSelectInfoRequestHandler`): up to 10
characters (`MaxCharacters`), and a name already used by a saved character (ignoring case) is refused; here the name check
answers `Status` 2, "Name already taken". **The PS3 client shows only the first character** of the list (the setup reads the
first record only, and the select screen offers *Start Game* and *Delete*, with no way to page or to make another). So the list
starts with the character to show next: the one picked in the Server Manager's **Characters** page, else the one made or played
last. *New character next time* (also on that page) answers the next `account/info` with `Status` 2, so the game opens the
creator; making the character turns it off. Separate accounts are not possible yet: the PS3 sends an empty `Session` to the web
server, so every player shares one list.

**Character select froze with the full record** (13 November test, `fr_test_20261113_012648`): the account reply with every
field above arrived, the loading screen ended, and the main thread then waited on a lightweight mutex for good
(`_sys_lwmutex_lock`, 94 s, every other thread idle); no `character/login` followed. Login state 5 calls the setup `0x78bc88`
with `r7 = 1`, which builds the select actor from the first saved record; the creator calls the same function with `r7 = 0` and
an empty default record (`0x78c6f4`: empty strings, `Model` 0, colours 0), which works. So one of the fields sent hangs it. The
server could send less, chosen with `FR_SELECT_DETAIL`:

| `FR_SELECT_DETAIL` | `Details` carries |
| :--- | :--- |
| `minimal` | `Guid`, `Name` |
| `basic` | plus `Model` |
| `looks` | plus `Head`, `Hair`, `ModelCustomization`, `FacePaint`, `SkinTone`, `EyeColor`, `HairColor` |
| `full` (default) | plus `Attachments` |

The freeze did not come back: later runs with `full` showed the character in their clothes and went on to log in, while with
`basic` character select shows nobody (13 November, `fr_test_20261113_033656`). So `full` is the default again; the others stay
for testing.

Whatever character select shows, the game server sends the saved character's full appearance and clothes in the world.

The list is kept in `characters.json` (`--characters`; by default in the Server Manager's folder,
`%LOCALAPPDATA%\FreeRealmsPS3Server`), written after every change and read when the server starts. A file that cannot be read is
renamed to `characters.json.unreadable-<time>` instead of being overwritten.

Besides the creator's choices, each character keeps its **progress**, like the item, profile, coin and position columns of
Sanctuary's `DbCharacter`: the items it owns, each job's equipment (slots in the order they were filled), the active job, the coins
and where it stood. The game server writes it after every equip, unequip and job switch, every 15 seconds while the character moves,
and when the client leaves (Sanctuary saves the position then); a returning character logs in with it: the player data's item list,
job list, active job and position come from it, and the character is dressed in its active job's equipment. A character that never
changed anything starts as a new one (Sanctuary's starter items and spawn point) until it does. Nothing changes coins yet; the coin
store will.

The position comes from the client's position update, inner opcode 125. On the PS3 it is 40 bytes, not the 44 of Sanctuary's
`PlayerUpdatePacketUpdatePosition`: `u16` opcode, `u64` guid, position x, y, z and rotation x, y, z as `f32`, a state byte, an
unused byte and the speed as `f32` (the rotation has no fourth component). Read from a test run: the first update after loading
reports Sanctuary's spawn point (-1904.88, -39.71, 412.60) and exactly the spawn rotation the server sent (0.5532, 0, 0.8331). A
saved position is used only in the zone it was saved in, and only when its numbers are finite and within 100 000 units.

## The name check

`character/checkName` sends `FirstName`, `LastName` and `Session`; the reply's `Status` is the result code the creator's handler
(`OnRequestCheckName`, `0x789a40`) acts on. Its names come from the table at `0xeb6f50` (indexed by `Status + 1`):

| `Status` | Meaning | The creator shows |
| ---: | :--- | :--- |
| -1 | Unknown error | |
| 0 | None | |
| 1 | Success | goes on to create the character |
| 2 | Name already taken | `Char_Name_Taken_PS3` |
| 3 | Name is profane | `Char_Name_Profane_PS3` |
| 4 | Name is invalid | `Char_Name_Invalid_PS3` |
| 5 | Name has an incorrect length | `Char_Name_Incorrect_Length_PS3` |
| 6 | Server failure | `Char_Server_Failure_PS3` |
| 7 | First name is too short | `First_Name_Short_PS3` |
| 8 | Last name is too short | `Last_Name_Short_PS3` |
| 9 | First name is too long | `First_Name_Long_PS3` |
| 10 | Last name is too long | `Last_Name_Long_PS3` |

On a failure the creator shows `'<first> <last>' <message>` and the `Suggestions/Suggestion` entries (`Name`, `LastPrefix`,
`LastSuffix`, which only the log prints). The server always answers 1.

A custom name comes from the system keyboard. The creator's keyboard handler (`0x78ae60`) splits the text at the first space into
first and last name. In RPCS3 it only gets the text with the keyboard close fix ([`docs/rpcs3/patches.md`](../rpcs3/patches.md)).

## Result codes the client names

The client prints `OnRequest...: requestId=%d, result=%d (%s)` for each reply. Names found in its table:
-1 "Unknown error", 0 "None", 1 "Success", 2 "No servers" (server list). For account info: 1 "Success",
2 "No characters for account" and a further "Game server is unavailable" code. For `account/info` the
handler has three branches: `Status` 1 (stores the account and character list; an empty list sets login
state 5), `Status` 2 (logs "No existing character found on the server", login state 6), anything else
(the "unable to log in" dialog). Both state 5 and 6 move on to state 8.

`Status` 1 with an empty list crashes the client: state 5 calls the character-select setup (`0x78bc88`),
which reads the first list entry without checking that one exists (`lwz r4, 60(r31)` with `r31 = 0`).
RPCS3 reports it as `Access violation reading location 0x3c`. An account with no characters is therefore
reported with `Status` 2 (`--account-status 2`, the default); `Status` 1 needs at least one `Character`.

## Not known yet

- What `character/delete` sends. The server takes its `Guid` as login's is sent; no delete has been seen in a test run yet.
- Whether the one character select freeze with the full record (`fr_test_20261113_012648`) comes back.
- Whether character select lists more than the first saved character, and what it does with three.
- Whether `Key` is used to encrypt the game connection. `GatewayAddress` is `host:port` and `GatewayTicket` comes back
  as the ticket in the client's first gateway packet; see `docs/protocol/gateway.md`.
