# Developer mode and hidden switches

Verified against Rust buildid 25454815 (`server/steamapps/appmanifest_258550.acf`) on 2026-09-24, from an `ilspycmd`
decompile of `Assembly-CSharp.dll` (see CLAUDE.md Host tools) and the bundles in `server/Bundles/shared/`. The client
is an IL2CPP build with obfuscated metadata, so client behavior below comes from in-game tests, not from its code.

## Turn on developer mode

The server half is automatic. `server/carbon/plugins/DeveloperFlag.cs` gives Rust's `IsDeveloper` player flag to every
player with `developerflag.use` each time they connect, and sets their server-side `client.skins_access` to 1. The
owner's SteamID holds the permission; a fresh install needs it granted once over RCON:

```text
c.grant user <SteamID> developerflag.use
```

The client half is one command. After every join, open the F1 console in the game and run:

```text
client.skins_access 1
```

The client does not save this convar, so it is back at 0 after each game restart and must be typed again. With it at
1, the game's own skin menus list every skin, including ones the Steam account does not own, and crafting applies them
(tested in game on 2026-09-24).

- Check the state from F1 or over RCON with `developerflag.status`. A working session prints
  `IsDeveloper=True client.skins_access=1 effective=1` for the player.
- `client.skins_access 0` returns to owned skins only, and `client.skins_access -1` locks every skin, which shows the
  game as an account with no skins sees it.
- If F1 answers `Command 'client.skins_access' not found`, a client update removed the convar.

## What the developer flag changes

`BasePlayer.IsDeveloper` reads the flag. Most checks that read it also accept `IsAdmin`, so they add nothing for an
admin. These are the ones that need the developer flag itself:

| Feature | Code | Effect |
| --- | --- | --- |
| Skins and DLC | `BasePlayer.GetSkinsAccessLevel` | `client.skins_access` 1 passes every skin and DLC ownership check, -1 fails them all, any other value keeps them; see `docs/skin-ownership-gates.md` |
| Silent item commands | `ConVar.Inventory` | `inventory.give`, `giveequip`, `giveid`, `givearm`, `givebp`, and `copyto` reply "you silently gave yourself ..." (or "... copied items to ...") to the caller only; for anyone else they announce the action to every player through `Chat.BroadcastPlayerAction` |
| Chat name color | `Chat.GetNameColor` | orange `#fa5` instead of the owner and moderator green `#af5` (players `#5af`) |
| Admin-only underwear | `Underwear.Validate` | an `adminOnly` underwear needs both the admin and the developer flag (list below) |
| NPC AI designer | `BaseAIBrain.PlayerCanDesignAI` | with `ai.designingenabled true` (admin convar, saved, default false), the `RequestAIDesign`, `SubmitAIDesign`, and `StopAIDesign` RPCs edit an NPC's AI design; `LoadAIDesign` takes the `Default` scope only from a developer and `EntityServerWide` from an admin or developer; whether the retail client has an editor for it is untested |
| Anti-hack | `AntiHack.ShouldIgnore` | an admin is exempt when `antihack.userlevel` is below 1, a developer when it is below 2; at the default 2 both are exempt only while using an admin cheat such as noclip (`antihack.admincheat`) |

Underwear is picked through the `client.underwearskin` client setting and checked by `Underwear.Validate`, which also
matches the body type (`ValidForPlayer`), `steamItem` ownership, and `steamDLC` (`SteamDLCItem.CanUse`, which passes at
`client.skins_access` 1). Of the 13 `Underwear` assets in `content.bundle`, three are `adminOnly`: `redonepiece`
(`OnePiece`) and `trunks` (`Trunks`), which also need the Summer DLC (app 1353060), and the test asset `redunderwear`
(`TestUnderwearReplacement`). Six more Summer DLC swimwear pieces (`swimwear_palmleaves`, `boardshorts`,
`swimwear_scribble`, `pinkbikini`, `rapido`, `swimwear_gradient`) need only the DLC.

The flag does not give what Facepunch's own `DeveloperList` (their manifest of staff SteamIDs) grants on connect:

- `ConnectionAuth`: auth level 3, and joining with a mismatched network protocol.
- `ConnectionQueue`: skipping the join queue.
- `ServerMgr`: more than one connection from an `editor` client.
- `CompanionServer.Handlers.CameraSubscribe`: developer camera permissions in Rust+.

No command in `Assembly-CSharp` checks for more than auth level 2 (`ConVar.Admin` compares against 2 at most), so level
3 would add nothing to an owner's commands.

## Client settings the server trusts

The client sends its client-info convars in the `ClientReady` message on connect and runs `global.setinfo <name>
<value>` on the server when one changes. `setinfo` is a `ServerUser` command, so any player can also type it to set
any key for their own connection; the switches that grant anything therefore check the admin or developer flag on the
server. Set any of these in F1 with `<name> <value>`:

| Setting | Server code | Effect | Who |
| --- | --- | --- | --- |
| `client.skins_access` | `BasePlayer.GetSkinsAccessLevel` | all skins and DLC unlocked (1), locked (-1), or owned only (0) | developer |
| `global.god` | `BasePlayer.IsGod` | no damage (`Hurt`, `IsImmortalTo`), fall damage, bleeding, poison, or negative modifiers | admin or developer |
| `player.infiniteammo` | `HeldEntity.UsingInfiniteAmmoCheat` | guns, launchers, the flamethrower, the spray can, and liquid containers keep their ammo or contents; melee weapons keep their condition | admin or developer |
| `global.skincol`, `global.skintex`, `global.skinmesh` | `BasePlayer.Save` | override the character's skin color, skin texture, and body mesh; -1 keeps the default | admin or developer |
| `global.spectatebots` | `BasePlayer` spectate targets | spectating (`global.spectate`, an admin command) also cycles through bots | anyone spectating |
| `client.errortoasts_debug` | `ConstructionErrors.Log` | a failed building placement sends its detailed reason to chat | anyone |
| `global.streamermode` | `NameHelper`, `UI_Utils` | the server replaces other players' names with generated ones in what it sends | anyone |
| `client.underwearskin` | `BasePlayer.GetUnderwearSkin` | the selected underwear, checked as above | anyone |

The server also reads `client.bagassignmode`, `client.SelectedShippingContainerBlockColour`,
`client.allowteaminvitesremoteplayers`, `client.shieldonback`, `client.respawnloadout`, `client.hascompletedtutorial`,
and `client.hasdeclinedtutorial`, which are ordinary player preferences.

## Creative mode

A built-in mode that needs no plugin and no developer flag. `BasePlayer.IsInCreativeMode` is true when
`creative.allusers` is on or the player has the `CreativeMode` player flag, and only the admin command
`creative.togglecreativemodeuser <name or SteamID> <true|false>` sets that flag; it also toggles the client's creative
UI (`debug.setcreative_ui`). Each setting below applies only to players in creative mode. All are admin convars saved
to `serverauto.cfg` at `quit`, so they stay on until set false:

| Setting | Help text | Where it applies |
| --- | --- | --- |
| `creative.freebuild` | "Build blocks for free" | building, upgrading, deploying, wallpaper, and simple upgrades cost nothing (`Planner`, `BuildingBlock`, `Deployer`, `WallpaperPlanner`, `SimpleUpgrade`) |
| `creative.freerepair` | "Bypass the 30s repair cooldown when repairing objects" | `BaseCombatEntity`, `ConstructableEntity` |
| `creative.freeplacement` | "Bypasses all placement checks" | `Construction` |
| `creative.bypassholdtoplaceduration` | "Bypasses bypassHoldToPlaceDuration when deploying items" | `Construction` |
| `creative.unlimitedio` | "Bypasses limits on IO length and points" | `WireTool`, `StringLights`; creative mode also allows the invisible wire color |
| `creative.allusers` | "Apply creative mode to the entire server" | every player |

`creative.alwaysonenabled true` unlocks three commands that set the always-on state of lights, ovens, and other
`IAlwaysOn` entities: `creative.togglealwayson <true|false>` for the one in view within 5 m,
`creative.togglealwaysonradius <true|false> <radius up to 100>`, and `creative.togglealwaysonall <true|false>`. They do
nothing unless the caller is in creative mode.

## Client-only tools

Developer features that live only in the client cannot be read from its IL2CPP build. The F1 console's
`find <text>` lists the commands and convars that match, with their help text, and `find .` lists everything.
