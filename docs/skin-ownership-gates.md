# Skin ownership gates

Facts below were verified against Rust dedicated server build 25454815 (`buildid` in
`server/steamapps/appmanifest_258550.acf`), checked 2026-09-24. A Rust update can move line numbers or rename
fields; regenerate research with `scripts/skin-grades.py`, `scripts/skin-freeitems.py`, `scripts/skin-coverage.py`
(below), or `ilspycmd` against a fresh decompile rather than trusting stale line numbers.

## Why this matters

Facepunch's Community Server and Hosting Guidelines (<https://facepunch.com/legal/servers>, "Lasted updated on 15
July 2025"), clause 3: players "must be able to freely and unconditionally access their validly purchased Facepunch
DLC across all Servers", and a server "must not grant access to any Facepunch DLC to players who have not validly
purchased it. Servers may not bypass ownership checks or artificially enable Facepunch DLC." "Facepunch DLC"
explicitly includes building skins. Breach risk is delisting from the server browser or a game ban (guideline
section 4). A uMod community thread (<https://umod.org/community/rust/56533-big-change-coming-to-servers>) reports
enforcement started 7 August 2025.

This server is a private, local-only test server that grants every skin on purpose: `BuildingSkins.cs` bypasses gate
1 below through `BuildingBlock.ChangeGradeAndSkin`, and `Skins.cs` with the local `SkinsCatalog.cs` bypasses gate 2
through `ItemManager.Create`, offering every `ItemSkinDirectory` and approved Steam Workshop skin. The local
`DeveloperFlag.cs` opens the shared override below, which passes all three gates, DLC included, for players with
`developerflag.use`. The guideline lets "Servers that are set up for testing of game mechanics" grant Facepunch DLC
for testing purposes only; a public, listed server running this setup would break clause 3(c).

A separate support article (<https://support.facepunchstudios.com/hc/en-us/articles/360009062817>, "Updated March
04, 2026") classifies plugins into the "Community" vs "Modded" server-browser section; it covers gameplay/UI
plugins, not DLC, and is unrelated to the ownership gates below.

## The three ownership gates

Source: `Assembly-CSharp.dll`, decompiled with `ilspycmd`

1. Building-grade skins: `BuildingBlock.DoUpgradeToGrade` (`[RPC_Server]`, fired by the client's grade-upgrade RPC)
   blocks with a `ConstructionErrors.SkinNotOwned` toast when `!constructionGrade.gradeBase.alwaysUnlock &&
constructionGrade.gradeBase.skin != 0L &&
!player.blueprints.steamInventory.HasItem((int)constructionGrade.gradeBase.skin)`.
2. Item/workshop skins (crafting, repair bench reskins, and the other skin pickers):
   `PlayerBlueprints.CheckSkinOwnership(skinItemId, player)`, called from `CanCraft` and `RepairBench.ChangeSkin`,
   returns true if `ItemSkinDirectory.FindByInventoryDefinitionId(skinItemId)` resolves to an `ItemSkin` whose
   `HasUnlocked(player)` is true (true when `UnlockedByDefault`, or via `player.AllSkinsUnlocked`, or via a skin
   `Redirect`), else falls back to `steamInventory.HasItem(skinItemId)`.
3. DLC-gated items (`ItemDefinition.Blueprint.NeedsSteamDLC`): `PlayerBlueprints.ItemPassesDlcChecks` calls
   `SteamDLCItem.HasLicense(player)`, true if the DLC's own `bypassLicenseCheck` is set, else
   `PlatformService.Instance.PlayerOwnsDownloadableContent(player.userID, dlcAppID)`.

Ownership data sources differ between gates 2 and 3. Gate 2 reads `player.blueprints.steamInventory` (a
`SteamInventory` `EntityComponent<BasePlayer>`) populated by the server RPC `SteamInventory.UpdateSteamInventory`
(`[RPC_Server][RPC_Server.FromOwner]`), which deserializes a client-submitted Steamworks inventory result blob and
checks only that its claimed owner SteamID matches the connecting player: it is client-reported data, not a
server-initiated query. Gate 3 instead calls `PlatformService.Instance.PlayerOwnsDownloadableContent`, a
server-side Steamworks query keyed on the player's Steam ID, independent of anything the client reports.

All three gates share one override: `BasePlayer.AllSkinsUnlocked` / `DefaultSkinAccess`
(`BasePlayer.GetSkinsAccessLevel`) grants non-default access only when the player has the `IsDeveloper` player flag
and the connection's client info holds `client.skins_access`: 1 passes every gate (`AllSkinsUnlocked`), -1 fails
them (`AllSkinsLocked`), and any other value keeps the checks above. Vanilla sets the flag only from Facepunch's own
`DeveloperList` on connect, together with auth level 3, and no console command sets it, so a normal server admin
cannot reach this path. `DeveloperFlag.cs` sets it for `developerflag.use` and writes 1 into the client info (CLAUDE.md,
Developer flag). The client applies the same override with its own `client.skins_access` convar: in an in-game test
on 2026-09-24, a player with the flag and that convar at 1 saw unowned skins in the game's menus and crafted them.
uMod's Faux Admin sets the same flag for its fake admins, so it opens this path too, though it never sets the convar.

## Vanilla admin bypass

`inventory.give <item> <amount> <condition> <skin>` (`ConVar/Inventory.cs`, `ServerAdmin = true`) gives to
`arg.Player()`, so it works only from a connected admin's F1 console; `inventory.giveto <player> <item> <amount>
<skin>` finds the player by name and also works over RCON. Both call `ItemManager.CreateByPartialName(name, 1, skin)`
directly and never reach `CheckSkinOwnership`, `HasUnlocked`, or `ItemPassesDlcChecks`, so an admin can hand out any
skin id on any item regardless of gates 2 or 3. `inventory.giveid` takes no skin argument.

For gate 1 (building-grade skins), vanilla's `skin_looking`, `skin_radius`, `upgrade_looking`, and `upgrade_radius`
console commands (CLAUDE.md Carbon section) apply a grade skin with no ownership check, but only from a connected
admin's F1 console (they throw on `arg.Player().eyes` over RCON) and they refuse Gingerbread
(`enabledInStandalone` 0). The installed `BuildingSkins.cs` bypasses gate 1 unconditionally instead, through
`BuildingBlock.ChangeGradeAndSkin` called from its own hooks rather than any vanilla command.

## Building-grade skin ids

CLAUDE.md's Carbon section (Building skins bullet) lists all 9 grades with a nonzero skin id and their tier, taken
from this same build. `scripts/skin-grades.py` (below) regenerates that list straight from `content.bundle` plus
each grade's `alwaysUnlock` flag: as of build 25454815, none of the 16 `BuildingGrade` assets, including the 9 with
a paid skin and the standalone-disabled `gingerbread`, are `alwaysUnlock`. The other 7 grades (metal, stone,
toptier, twigs, wood, wood_boat, wood_boat_hull) have `skin=0`: no paid skin, so gate 1 never fires for them.

## Free item skins and DLC

From `content.bundle` + `skinnables.preload.bundle` + `items.preload.bundle` via `scripts/skin-freeitems.py` /
`scripts/skin-coverage.py`, checked 2026-09-24. `assets/skins.asset` lists 619 named skin slots; scanning those
three bundles resolves 473 to an `ItemSkin` asset (9 `UnlockedByDefault`, the rest gated), leaving 146 names with
no matching asset in those bundles (other skin bundles, or non-skin prefabs the name heuristic also matched);
ownership for those 146 can only be read at runtime via `steamInventory.HasItem`.

The 9 free (`UnlockedByDefault`) item skins are all wallpaper-family: `wallpaper.green/red/yellow.skin` (2024-09
wallpaper starter pack, itemname `wallpaper.wall`) and 6 floor/ceiling-paper skins from the 2025-06 floor and
ceiling pack DLC (itemname `wallpaper.ceiling` / `wallpaper.flooring`).

6 `SteamDLCItem` assets exist (soundtrack appid 1701720, secretlab chair 1374000, warhammer 3891560, instruments
1174370, summer 1353060, voiceprops 1670430); all have `bypassLicenseCheck=0`, so gate 3 applies to every one.

## Tools

`scripts/skin-grades.py`, `scripts/skin-freeitems.py`, and `scripts/skin-coverage.py` regenerate the data above
from `server/Bundles/shared/*.bundle`. Run with `uv run --with UnityPy python3 scripts/skin-<name>.py` from the
repo root; each takes bundle paths as optional positional overrides and exits non-zero on a missing bundle, a
UnityPy load failure, or zero relevant hits. They only read bundles; they do not touch Carbon's plugin config.
