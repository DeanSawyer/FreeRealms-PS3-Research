# Coin store (opcode 165)

The server's coin store is Sanctuary's, one for one: its items, its prices and what its handlers do on each buy and sell
(`Sanctuary.Gateway/Handlers/BaseCoinStorePacket/*`). The code is in
[`Gateway/CoinStore.cs`](../../src/ps3/Sanctuary.Ps3/Gateway/CoinStore.cs) (packets, items) and
[`Gateway/Inventory.cs`](../../src/ps3/Sanctuary.Ps3/Gateway/Inventory.cs) (buying and selling).

## In the game

The PS3 client has the whole store: the screen is `CoinStore.lua` (`GoToCoinStore`, category tree, filters, search, preview,
buy and sell). Opening it calls the client function `0x1f507c` (Lua `OnCoinStoreOpened`), which logs *Coin store opened* and
sends sub-opcode 8. What it shows comes from the item list (sub-opcode 1) sent when the client is ready, and the item
definitions.

## Sub-opcodes

Every packet is `u16 165, u16 sub-opcode`, then the fields. The client's reader for each was read from `game.elf`; its
dispatcher is `0x1388c8` (jump table `0x1389f8`, sub-opcodes 0 to 13).

| Sub | Direction | Name (Sanctuary) | Fields | PS3 code |
| ---: | :--- | :--- | :--- | :--- |
| 1 | server | ItemList | static items: `i32` count, then per item `i32` key (the id), `i32` id, `i32` category, `u8` (0); dynamic items: `i32` count (0) | reader `0x133b78`, maps `0xb2daf4` / `0xb2eb08`, used by `0x13193c` |
| 2 | client | ItemDefinitionsRequest | `i32` count, `i32` ids | sent by `0x134da8` / `0x134f54` |
| 3 | server | ItemDefinitionsResponse | `u8` success, `i32` count, `i32` ids | reader `0x1348d4` |
| 4 | client | SellToClientRequest (the player buys) | `u64` merchant guid, `i32` definition, `i32` tint, `i32` quantity | sent by `0x134278` |
| 5 | client | BuyFromClientRequest (the player sells) | `u64` merchant guid, `i32` item guid, `i32` quantity | sent by `0x1340dc` |
| 6 | server | TransactionComplete (PS3: *CoinStoreSellToClientResponse*) | `i32` result, `i32` item guid, record (below) | reader `0x1368a4`, record `0xb2d538` |
| 7 | server | (CoinStoreOpenPacket) | not used by Sanctuary | `0x137c20` |
| 8 | client | ItemDynamicListUpdateRequest | none | sent by `0x13324c` |
| 9 | server | ItemDynamicListUpdateResponse | `i32` count (0) | reader `0x1332e4` |
| 10 | server | MerchantList | an NPC merchant's list (not done yet) | `0x135378` |
| 11, 13 | server | (clear transaction history, buy back) | not used by Sanctuary | `0x1339e4`, `0x137660` |

**Different from the PC:**
- An item list entry is the id, the category and one byte. The PC's `ItemDefinitionMetaData` is three ints (id, category,
  tint group) and three bools. `0x13193c` files each entry under its id and groups it by the second int. The byte is sent as 0.
- The transaction record is `i32` type (1 buy, 2 sell), `u64` merchant guid, `i32` id, `i64` time (Unix seconds), `i32`
  definition, `i32` tint, `i32` quantity, `i32` price, then one `u8`. The PC ends with an `i32` quantity remaining instead of the
  byte.
- The buy request's packet object has no int between the merchant guid and the item record; the PC sends one (Sanctuary's
  `MerchantUnknown`). The server reads both.

Results (Sanctuary's `CoinStoreTransactionResult`): 1 success, 3 invalid item, 4 invalid quantity, 7 not enough coins, 8 error.

ClientUpdate (38) packets the store sends, from the client's ClientUpdate table (`0xf934c`): sub-opcode 2 ItemAdd, 3 ItemUpdate
(`0xfabdc`: item guid, count, action bar id -1, consumed count, ability count, `i64` rental time), 4 ItemDelete (`0xfa9e4`: item
guid) and 19 CoinCount (`0xfa3dc`: coins). These are the PC's layouts.

## What the server does

- **When the client is ready:** the item list with every store item, in Sanctuary's order (by id % 10,000,000, then id), and the
  character's coins.
- **Buy (4):** Sanctuary's `CoinStoreSellToClientRequestPacketHandler`.
  - The definition is the id % 10,000,000. Store ids at or above it are recoloured copies.
  - It must be of type 1, 3, 4 or 12. Pets (type 2) are refused here: this server has no pets yet.
  - The price is the cost less the member discount, because the account is a member. Sanctuary's `GetMemberPurchasePrice`
    is `(int)((1 - discount × 0.01) × cost)`.
  - The tint asked for is kept only when the store item has a tint group; otherwise it is the icon tint.
  - The same definition and tint goes on the same stack, within the definition's maximum stack size.
  - Replies: ItemAdd for a new item, or ItemUpdate; then CoinCount and TransactionComplete.
- **Sell (5):** Sanctuary's `CoinStoreBuyFromClientRequestPacketHandler`.
  - The item must be owned in that quantity.
  - The coins go up by the definition's resell value times the quantity.
  - Replies: ItemDelete when all of it is sold, or ItemUpdate; then CoinCount and TransactionComplete.
- **Definitions (2):** Sanctuary answers success with the same ids. Its PC client gets the definitions through PlayerUpdate
  ItemDefinitionRequest (35/34). Here each requested definition (up to 100) is sent (PlayerUpdate 35/33), then the response;
  35/34 is answered with that definition.
- **Store screen opened (8):** no dynamic items (Sanctuary's TODO).

Different from Sanctuary:
- A quantity below 1 is refused. Sanctuary checks it only for pets.
- A sold item is also taken off every job that wore it, so a saved character never wears an item it no longer has.

Every buy and sell is saved, with the items' counts and the coins (`characters.json`, see [`ps3ws-replies.md`](ps3ws-replies.md)).

**Coins:** a character gets Sanctuary's `StartingCoins` (`Sanctuary.Login/login.json`: 999,999,999) at its first login. A
character saved before the coin store gets them once, at its next login.

## Data

- `Data/coin_store.tsv.gz`: Sanctuary's `Resources/CoinStoreItems.json` (13,096 items), each row joined with the item's
  definition from `Resources/ClientItemDefinitions.json`. The columns are the store category, tint group, buy disabled, coin
  store only, hidden and resell value, then the 34 definition fields in the order of `Items.FieldKinds`. The store's definitions
  are added to the item table (`Items.Definitions`), so a bought item can be shown, worn and saved.
- `Data/ResellValues.txt`: the resell values of the items in `item_definitions.tsv.gz` that have one, for selling the items a
  character starts with.

Both were written from Sanctuary's resources with the same field rules as `tools/gen_item_definitions.py` (on the
`python-archive` branch).

## Switches

| Variable | Default | |
| :--- | :--- | :--- |
| `FR_COINSTORE` | on | `0`: the empty item list as before, and no answers to the store |
| `FR_STARTING_COINS` | 999999999 | The coins given at a character's first login |

## Not done yet

- **Merchants:** NPC shops (sub-opcode 10 MerchantList, the merchant interaction type 17).
- **Pets** from the store.
- **Checking the item list entry's byte:** it could be *buy disabled* or *hidden*, and is sent as 0.
- **Testing in RPCS3:** the whole store has only been checked by the tests so far. The PS3 might not have a model or icon for an
  item added to the PC game after the PS3 version.
