# Game modes

Verified against Rust buildid 25454815 (`server/steamapps/appmanifest_258550.acf`) on 2026-09-24.
`scripts/game-modes.py` prints every mode's prefab settings from `server/Bundles/shared/content.bundle` and
`assetscenes.bundle`, and the map generation overrides from `RustDedicated_Data/resources.assets`; the code paths
come from an `ilspycmd` decompile of `Assembly-CSharp.dll` (see CLAUDE.md Host tools). Facepunch changes a mode's
rules in its prefab data, not in code, so rerun the script after a Rust update and compare:

```bash
uv run --with UnityPy python3 scripts/game-modes.py
```

This server runs vanilla, with no `server.gamemode`. It ran `primitive` for part of 2026-09-24; see
[Primitive on this server](#primitive-on-this-server).

## How a mode is applied

- `server.gamemode <name>`: a `[ServerVar]` property on `ConVar.Server`, not saved to `serverauto.cfg`, so it lives
  in `server/server/pve/cfg/server.cfg`. Its setter runs `ApplyGamemode()`, which applies the convar overrides of the
  `GameModeManifest` prefab whose `shortname` matches. Later in the boot, `BaseGameMode.CreateGameMode()` spawns
  `assets/prefabs/gamemodes/<name>.prefab` and logs `Gamemode Convar :<name>`, or `No Gamemode.` when the convar is
  empty. The mode entity has `enableSaving` off, so the `.sav` never holds it and each boot recreates it.
- `gamemode.set <shortname>` switches a running server. It matches `shortname`, leaves `server.gamemode` unchanged,
  and refuses to replace an active `permanent` mode (`This game mode is permanent, you must reset the server to
  switch game modes.`).
- Three match modes have a shortname that differs from their file name (table below). `server.gamemode` needs the
  file name, because `CreateGameMode` builds the prefab path from it; `ApplyGamemode` then logs
  `Couldn't find gamemode: <name>`, which costs nothing because those three override no convars.
- `BaseGameMode.ApplyConVars()` runs each entry of the prefab's `convars` list through `ConsoleSystem.Run`, then
  sets the map, fog of war, and compass convars from the prefab's flags. Nothing restores them: `ShutdownGame()`
  only resets scores and teams, and overrides of saved convars reach `serverauto.cfg` at the next `quit`, so they
  outlive the mode.
- `WorldConfig.LoadScriptableConfigs()` merges each `ScriptableWorldConfig` whose `GameModes` list contains
  `server.gamemode` into the map generation config. `PlaceMonuments`, `PlaceMonumentsRoadside`,
  `GenerateDungeonGrid`, `GenerateDungeonBase`, and `Prefab.FindPrefabNames` read it only while generating a map,
  so a cached `proceduralmap.*.map` loads unchanged.

## Modes

| `server.gamemode`   | `gamemode.set` | Title            | Class               | Kind      |
| ------------------- | -------------- | ---------------- | ------------------- | --------- |
| `vanilla`           | `vanilla`      | Vanilla          | `BaseGameMode`      | permanent |
| `softcore`          | `softcore`     | Softcore         | `GameModeSoftcore`  | permanent |
| `hardcore`          | `hardcore`     | Hardcore         | `GameModeHardcore`  | permanent |
| `primitive`         | `primitive`    | Primitive        | `PrimitiveGameMode` | permanent |
| `deathmatch`        | `deathmatch`   | Deathmatch       | `BaseGameMode`      | match     |
| `teamdeathmatch`    | `teamdm`       | TEAM DM          | `BaseGameMode`      | match     |
| `kingofthehilldm`   | `koth_solo`    | King of the Hill | `BaseGameMode`      | match     |
| `kingofthehillteam` | `koth_team`    | Team KOTH        | `BaseGameMode`      | match     |
| `onedeath`          | `onedeath`     | oNe dEaTh        | `BaseGameMode`      | match     |
| `weapontest`        | `weapontest`   | WeaponTest       | `BaseGameMode`      | match     |

An empty `server.gamemode` and `vanilla` behave the same: the vanilla prefab overrides nothing.

## Survival rule sets

Each entry quotes the prefab's feature list (the English fallback of the text the client shows for the mode), then
the settings behind it. "(saved)" marks a convar that `quit` writes to `serverauto.cfg`.

### softcore

Feature list: respawn with 50% of loot on body when killed; destroyed Tool Cupboards apply building authorization
for 48 hours; destroyed loot containers create corpses that can be repaired; increased upkeep on doors; can respawn a
single time at destroyed Sleeping Bags; don't lose tea buffs on death; player bullets do 25% less damage; removed
reclaim terminal from Outpost. Its `softcore.gatherrate` and `softcore.raidwindow` entries repeat the Tool Cupboard
text as their fallback, so those two are described from code here.

- Death: `GameModeSoftcore.OnPlayerDeath` fills a reclaim backpack with `gamemodesoftcore.reclaim_fraction_wear`
  1.0 of the clothing, `reclaim_fraction_belt` 0.5 of the belt, and `reclaim_fraction_main` 0.5 of the main
  inventory. `reclaim_suicide` and `reclaim_building_auth` default to false, so a suicide or a death inside a base
  the player is authorized on leaves no backpack.
- Gathering: `gamemodesoftcore.gather_rate` (saved, default 2) multiplies `ResourceDispenser`, `CollectibleEntity`,
  and `MiningQuarry` yields. Carbon's `GatherManager` multiplies again in its hooks, so with its 2x, as on this
  server, trees, ore, and quarries would give 4x; ground pickups would stay 2x because `GatherManager` replaces
  vanilla pickup.
- Raid window: `softcore.raidwindow_enabled` (saved, default true) allows explosive damage to building blocks only
  between `raidwindow_start_hour` 18 and `raidwindow_end_hour` 21, server local time. Twig stays raidable, players
  authorized on the target's tool cupboard are exempt, a base stays raidable for `raidwindow_fresh_tc_seconds` 3600
  after its cupboard is placed, and a separate weekend window is off. `softcore.raidwindow_status` prints the state.
- Overrides: `storagecontainer.dropcorpseondeath true`, `storagecontainer.canrequireauthifnolock true`,
  `server.pvpbulletdamagemultiplier 0.75` (saved), `player.keepteaondeath true`, `player.dropbackpackondowned false`,
  `decay.use_door_upkeep_brackets true`, `relationshipmanager.maxteamsize 4`.
- Flags and tags: `limitTeamAuths` on; tags `reclaim` and `compoundspawnsoftcore` switch `GameModeObjectToggle`
  objects, and the mode ships `staticrespawnzonecompound` and `staticrespawnzonebandit` respawn zone prefabs.

### hardcore

Feature list: limit of 5 sleeping bags and beds; fog of war on map; no contact system, team system, crawling when
wounded, Rust+, compass UI, safe zones, MLRS, mission system, firearm crafting, firearms at vendors, or firearms from
the tech tree; blueprints wipe monthly; increased respawn times; 5x ammunition crafting cost; 1.5x damage against
NPCs; 2x building upkeep cost.

- Flags: `teamSystem`, `safeZone`, `compass`, `contactSystem`, `crawling`, `rustPlus`, `missionSystem`, `mlrs`,
  `allowPings`, and `mapMarkers` off; `fogOfWar` on with a reveal size of 128; `hidePlayerOnMap`,
  `hideMapPlayerDirectionIndicator`, and `hideUndergroundOnMap` on; `maximumSleepingBags` 5; `wipeBpsOnProtocol`
  on; 35 item restrictions, four allowed tech trees, one loot distribution and one crafting cost modifier; tag
  `marketplace-disabled`.
- Starting it on an existing world is destructive: `BaseGameMode.DeleteEntities()` kills the NPCs, NPC turrets, and
  marketplaces inside safe-zone and town monuments, every MLRS, and every mission provider.
- Overrides: `clan.enabled 0`, `server.pvebulletdamagemultiplier 1.5` (saved), `decay.bracket_0_costfraction` through
  `bracket_3_costfraction` 0.2, 0.3, 0.4, and 0.66, `server.hardcoreFirearmAmmunitionCraftingMultiplier 5` (saved),
  `server.respawnTimeAdditionBag 600` and `server.respawnTimeAdditionBed 480` (saved),
  `server.npcAmmoLootMultiplier 0.5` (saved), `sleepingbag.UseTeamLabels false` (saved).

### primitive

Feature list: increased wild horse spawns (2x); restricted to bows and melee weapons; sulfur node spawns reduced by
50%; no rockets and C4, MLRS, helicopter vehicles, cargo ship, patrol helicopter, or Bradley; no armored doors; no
oil rig, missile silo, excavator, or underwater labs; no underground trains; no jackhammer or chainsaw at vendors; no
metal or armored building grade; no face mask or metal chest plate; no T3 workbench; increased tech tree tax; T3
crafting recipes moved to the T2 workbench.

- Era: `server.era primitive` carries most of it. `ItemDefinition.IsAllowed` gates crafting, research, repair,
  recycling, vending, weapon racks, loot spawns, diggables, and mission rewards; `ItemBlueprint` switches to its
  primitive-era overrides (the T3 recipes on T2); `Workbench` offers only the tech trees allowed in the era; and
  `PrefabWeight` entries tied to an era spawn only in that era. The era does not stop anyone using an item they
  already hold.
- Overrides: `server.era primitive` (saved), `decay.upgrade_metal_enabled false` and `decay.upgrade_hqm_enabled
  false` (saved), `npc.vendor_minicopter_enabled`, `npc.vendor_scrap_heli_enabled`, and
  `npc.vendor_attack_heli_enabled` false (saved), `ridablehorse.population 4`, `ai.scientist_spawners_enabled
  false`, `events.set_event_enabled` false for `patrol_heli`, `cargo`, and `bradley`, and `server.workbenchtaxrate1`
  through `workbenchtaxrate3` 10, 20, and 40 (saved).
- Map generation: `primitive_world`, the only `ScriptableWorldConfig`, lists `primitive` in `GameModes`, blacklists
  the `nuclear_missile_silo`, `excavator_1`, `oilrig_1`, `oilrig_2`, and `tunnel-entrance` prefabs, and sets
  `UnderwaterLabs` and `UndergroundTrains` to false.
- Flags and tags: every flag matches vanilla; tag `compoundspawn`.

## Match modes

| Mode                | Teams         | Round  | Warmup | Round ends at           | Min players |
| ------------------- | ------------- | ------ | ------ | ----------------------- | ----------- |
| `deathmatch`        | free for all  | 600 s  | 10 s   | 10 kills                | 2           |
| `teamdeathmatch`    | COBALT, NOMAD | 600 s  | 10 s   | 25 team kills           | 2           |
| `kingofthehilldm`   | free for all  | 900 s  | 10 s   | 240 score               | 2           |
| `kingofthehillteam` | COBALT, NOMAD | 900 s  | 10 s   | 300 team score          | 2           |
| `onedeath`          | Cobalt        | 600 s  | 10 s   | the first death         | 1           |
| `weapontest`        | none          | 1e10 s | 1 s    | 9,999,999 kills (never) | 1           |

`BaseGameMode.CheckGameConditions()` ends a round once a team or player reaches `numScoreForVictory` in
`victoryScoreName`, which for `onedeath` is one death. All six set `useCustomSpawns`, so `ServerMgr` takes each spawn
from `BaseGameMode.GetPlayerSpawn()`, which picks among objects tagged `spawnpoint` and throws on a map without any
(this procedural map has 16: every mode logs `Installed : 16spawn points.` when it starts). The KOTH modes also score
at capture point entities. `weapontest` turns off sleeping, wounding, bleeding, and temperature, respawns in 0.1 s at
100 health, heals after 4 s, tops up magazines, and shows a kill feed; `teamdeathmatch` also turns off sleeping.

## Primitive on this server

For part of 2026-09-24, `server/server/pve/cfg/server.cfg` held `server.gamemode primitive`. The world from before
the switch is in `backups/pve-20260924T184111/`, taken while the server was stopped. A boot logged
`Gamemode Convar :primitive` and `Game created! type was : assets/prefabs/gamemodes/primitive.prefab`, and echoed the
overrides (`server.era: "Primitive"`, `Disabled event 'cargo'`, and the rest) each time a cfg pass set the convar and
once more when the mode spawned.

- The cached map kept its oil rigs, missile silo, excavator, underwater labs, and train tunnels, and the Monument
  Addons recyclers placed there stayed. Only a newly generated world, started with the mode set, drops them.
- `server.cfg` set the era before the save loaded, so world loot containers kept their saved contents and restocked
  from primitive loot tables as they respawned. A live `server.era` change instead re-rolls every `LootContainer` at
  once and enforces spawn limits (`ConVar.Server.OnEraChanged()`).
- Existing items and buildings stayed. The era gated what could be crafted, researched, repaired, recycled, bought,
  or racked, and the overrides stopped upgrades to metal and armored.
- The revert, the same day, removed the `server.gamemode` line while the server was stopped, then set the saved
  overrides in `cfg/serverauto.cfg` back to this world's earlier values: `server.era None`,
  `decay.upgrade_metal_enabled True`, `decay.upgrade_hqm_enabled True`, `npc.vendor_minicopter_enabled True`,
  `npc.vendor_scrap_heli_enabled True`, `npc.vendor_attack_heli_enabled True`, and `server.workbenchtaxrate1`, `2`,
  and `3` at 0. No other saved convar had changed since the switch, so `server.cfg` and `serverauto.cfg` then matched
  the backup byte for byte. The unsaved overrides (`ridablehorse.population`, `ai.scientist_spawners_enabled`, and
  the three events) reset on restart. Containers filled while the mode ran keep primitive-era loot until they
  respawn from vanilla tables.
