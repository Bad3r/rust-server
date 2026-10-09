# Map seeds and world size

Verified against Rust buildid 25454815 on 2026-09-25. The size thresholds come from the procedural components of
the `Procedural Map` scene in `server/Bundles/maps/maps.bundle`, read with UnityPy; the code paths from an `ilspycmd`
decompile of `Assembly-CSharp.dll` (see CLAUDE.md Host tools); and the monument counts from `scripts/seed-scan.py`
over 26 random seeds at worldsize 3500 and 24 at 4500.

## Previewing seeds

`scripts/seed-scan.py` generates each seed on a throwaway vanilla server next to the live one, then writes
`seedscan/<buildid>/<size>/index.html`: a table ranked by missing monuments and land share, and every map rendered
by `world.rendermap` with monument labels, the in-game grid, and the above-ground railway drawn on top.

```bash
uv run scripts/seed-scan.py --random 24 --jobs 4        # worldsize from start.sh
uv run scripts/seed-scan.py --size 4500 1970515526      # given seeds at another size
uv run scripts/seed-scan.py --report                    # rebuild index.html only
```

- A seed takes 3.5 to 7 minutes at 3500 and 4.5 to 8 at 4500, depending on how many servers share the CPU, and
  each server holds about 3.5 GB of RAM at 3500. `Terrain Erosion` (about 60 s at 3500) and the cliff and rock
  placement take most of the time.
- Each server runs under identity `seedscan<N>` on game port `28115 + 10 N` and WebRCON port one above, with
  `server.autouploadmap 0` and `aimanager.nav_disable 1`. Runs lock their slots with `flock`, so two runs at once
  take different identities. The identities keep only `cfg/` and small databases between seeds; `rip
  server/server/seedscan*` once no scan runs.
- Seeds already scanned on the installed build are skipped; after a Rust update, the same command generates them
  afresh under the new buildid. `--stats FILE.map` prints the land, biome, and path figures of any procedural
  `.map`, such as the live one in `server/server/pve/`.

## What the seed and the size decide

`WorldSetup.InitCoroutine` runs every `ProceduralComponent` of the scene in order, each seeded with
`server.seed + index`, so seed, size, and build fix the map. For seeds 783331605, 1970515526, and 1739235182, the
land, biome, and path figures of the scanned map matched those of the live world's `.map` exactly. A Rust update
that changes procgen code or the scene data can change what a seed produces, so recheck a chosen seed after the
monthly update.

Components with a `MinWorldSize` place nothing below it:

| Feature              | Component                        | Minimum worldsize |
| -------------------- | -------------------------------- | ----------------- |
| Ring road            | `GenerateRoadRing`               | 4000              |
| Oases                | `PlaceMonuments` (`Oasis`)       | 4000              |
| Lakes                | `PlaceMonuments` (`Lakes`)       | 4000              |
| Above-ground railway | `GenerateRailRing`               | 4250              |
| Canyons              | `PlaceMonuments` (`Canyons`)     | 4500              |
| Mountains            | `PlaceMonuments` (`Mountains`)   | 5000              |

`GenerateRailLayout` only connects monuments to rails that already exist, so below 4250 no seed has surface rails:
none of the 26 at 3500 did, including the 14 with a Train Yard. Trains then run only in the underground network.

`PlaceMonuments` (`Main Monuments`) loads every prefab in `monument/xlarge`, `large`, `medium`, and `small`,
shuffles them with the seed, sorts them by priority, keeps the best of 8 placement passes plus relaxed-distance
passes, and then tries the prefabs it left out once more. A prefab with no spot that passes its terrain filter and
distance rules is left out. On small maps that hits the large monuments hardest, and the roadside and swamp
placers run short of room too:

| Monument              | Placed by                                   | Seeds with it, 3500 | Seeds with it, 4500 |
| --------------------- | ------------------------------------------- | ------------------- | ------------------- |
| Giant Excavator Pit   | `PlaceMonuments` (`Main Monuments`)         | 11 of 26            | 18 of 24            |
| Train Yard            | `PlaceMonuments` (`Main Monuments`)         | 14 of 26            | 24 of 24            |
| Radtown               | `PlaceMonumentsRoadside` (`Road Monuments`) | 16 of 26            | 22 of 24            |
| Airfield              | `PlaceMonuments` (`Main Monuments`)         | 17 of 26            | 24 of 24            |
| Abandoned Supermarket | `PlaceMonumentsRoadside` (`Road Monuments`) | 20 of 26            | 24 of 24            |
| Oxum's Gas Station    | `PlaceMonumentsRoadside` (`Road Monuments`) | 20 of 26            | 24 of 24            |
| Abandoned Cabins      | `PlaceMonuments` (`Swamps`)                 | 21 of 26            | 24 of 24            |
| Military Tunnel       | `PlaceMonuments` (`Main Monuments`)         | 21 of 26            | 24 of 24            |
| Jungle Ziggurat       | `PlaceMonuments` (`Jungle Ruins`)           | 24 of 26            | 24 of 24            |
| Mining Outpost        | `PlaceMonumentsRoadside` (`Road Monuments`) | 24 of 26            | 24 of 24            |
| Arctic Research Base  | `PlaceMonuments` (`Arctic Bases`)           | 25 of 26            | 24 of 24            |
| Launch Site           | `PlaceMonuments` (`Main Monuments`)         | 25 of 26            | 24 of 24            |

Every other named monument was on every seed of both sizes. The land covered 47 to 56% of the square at 3500 and
57 to 62% at 4500. Seeds had 2 to 4 rivers at 3500, most often 3, and 3 to 5 at 4500, most often 4. Every seed at
4500 had the railway, one canyon, and two lakes, and all but one had two oases.

## World generation config

`world.configstring` (JSON) or `world.configfile` (a JSON file under `server/server/<identity>/`) replaces the
defaults of `WorldConfig` at every boot, before the map loads or generates:

- `PercentageTier0`, `PercentageTier1`, `PercentageTier2`: shares of the three tier zones (topology `TIER0` to
  `TIER2`) that the height, topology, and biome generators lay out, normalized to sum 1.
- `PercentageBiomeArid`, `PercentageBiomeTemperate`, `PercentageBiomeTundra`, `PercentageBiomeArctic`: normalized
  the same way. `LoadFromWorldConfig` never copies `PercentageBiomeJungle`, so the jungle share keeps its default.
- `MainRoads`, `SideRoads`, `Trails`, `Rivers`, `Powerlines`, `AboveGroundRails`, `BelowGroundRails`,
  `UnderwaterLabs`: on by default; off skips that layer.
- `PrefabBlacklist`, `PrefabWhitelist`: substrings matched against the full prefab path in every folder
  `Prefab.Load`: monuments, decor, cliffs, and, at runtime, the spawn populations (`SpawnPopulationBase`). A
  blacklist hit drops a prefab. A non-empty whitelist drops everything it does not match, trees and ore included.

The generators read the config only when they run, and the `.map` cache name holds only size, seed, and save
protocol, so a cached map ignores a changed config until its `.map` is removed. The server sends the JSON to
clients as `levelConfig`, and with `server.autouploadmap` on they download the uploaded map anyway.

## Switching the pve world to a new seed

With the server stopped and `server/server/pve/` backed up, change `+server.seed` (and `+server.worldsize`, if it
changes) in `start.sh`. The next boot generates `proceduralmap.<size>.<seed>.<protocol>.map` and a fresh `.sav`. The old
seed's files stay: `World.CleanupOldFiles` deletes only `.map` and `.dat` files of other save protocols, so setting
the old seed back returns to the old world. Blueprints (`player.blueprints.18.db`) carry over; bases, items, and
sleepers stay in the old `.sav`.

A full wipe also moves everything else in the identity out with `rip`, except `cfg/server.cfg`, which git tracks,
and `cfg/users.cfg`, which holds the owner entry. The next boot recreates the databases without blueprints, plus
`bans.cfg`, `relay_cfg.json`, and a `serverauto.cfg` that matched the old one except for a new `server.server_id`.
Carbon's permissions and plugin data live in the shared `server/carbon/data/` and stay.

On 2026-09-25 the pve world moved from seed 783331605 to 1970515526 at worldsize 3500 (hostname
`Local PvE: Halcyon`). It is one of two scanned seeds at that size with every monument; the other, 1232849729,
reaches its Missile Silo and Ranch only by trails over beach isthmuses. The 783331605 world is in
`backups/pve-20260925T182415/`.

On 2026-09-26 a full wipe moved the pve world to seed 1739235182 at worldsize 4500 (hostname `Local PvE: Pandora`):
60% land, 4 rivers, the railway, and no monument missing in the scan. Halcyon was not copied to `backups/`. The
live map's land, biome, and path figures matched the scan, and all 135 monuments sat within 0.5 m of their scanned
positions.

On 2026-10-09 the force-wipe update to buildid 25797215 moved the save protocol to 289 and the player database
version to 18, so the world and blueprints started over on the same seed and size. That build keeps the terrain of
1739235182 (59.5% land, 4 rivers, biome shares within 0.6 points) but lays out the rest anew: 13 of the 46 named
monuments stayed within 5 m (the coastal and offshore ones, the Abandoned Military Base, and the Arctic Research
Base), while the other 33, the roads, and the rails moved, and the map holds 131 monuments instead of 135. Every
named monument type kept its count. `seedscan/25454815/4500/` shows the old layout. The previous world and
databases are in `backups/pve-20261009T093145/`.

The Monument Addons recyclers in `data/MonumentAddons/Default.json` are keyed by monument prefab, so they reappear
on a new map, but a monument type the surveyed map lacked gets none. The survey in
[monument-survey.md](monument-survey.md) was re-run after each switch: on 1970515526 (commit 2266533) it added
`swamp_c` (Abandoned Cabins) and a second lab room, and on 1739235182 it added `desert_military_base_c` and the two
surface Train Tunnel Links. On buildid 25797215 it moved 6 spots and swapped the lab's ladder room for its 1-way
moonpool room. Redo it after each wipe.
