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

## Replies

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

## Not known yet

- What `Status` values other than 1 mean for `checkName` (the client has separate messages for taken,
  profane, invalid and wrong-length names).
- The meaning and format of `GatewayAddress`/`GatewayTicket`/`Key`. After `character/login` the client
  connects to the game gateway, and no gateway server exists in this project.
