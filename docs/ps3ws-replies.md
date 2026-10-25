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
and continue to character creation. The inner layout of `Character` (Details/Guid/Name, Model, Head, Hair,
ModelCustomization, FacePaint, SkinTone, EyeColor, HairColor, Attachments/Attachment with Model, Texture,
Tint, TintOverrideId, EffectId, Slot, plus HeadshotUrl, LastPlayed, Server, Zone, Online, Locked) is taken
from tag-name strings and has not been checked against the parser.

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

- What `Status` values other than 1 mean for `checkName` (the client has separate messages for taken,
  profane, invalid and wrong-length names).
- The meaning and format of `GatewayAddress`/`GatewayTicket`/`Key`. After `character/login` the client
  starts the SOE UdpLibrary against the gateway. `bootstrap/gateway_probe.py` listens there and logs the
  client's packets, but no game server exists in this project. The reply currently carries
  `GatewayAddress="<advertise address>:<--gateway-port>"`; whether the client expects `host:port` is unconfirmed
  until a packet arrives.
