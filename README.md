# rust-server

Local dedicated server for Rust (Steam app 258550), hosting a small PvE world for a Proton client with EAC off.

It runs RustDedicated under `steam-run` on NixOS and loads Carbon (edge channel) for gameplay mods.
A Harmony mod moves Mono's sockets to IPv4 when the host kernel has no IPv6 address family.

## Requirements

- x86_64 Linux with Nix and flakes enabled; `start.sh` builds the Harmony mod with `nix shell nixpkgs#mono`.
- `steam-run`, `websocat`, and `curl` on `PATH`.
- About 6 GB of disk for the server install, plus world saves and backups.

## Quick start

```bash
git clone https://github.com/Bad3r/rust-server.git
cd rust-server
./start.sh
```

The first run downloads steamcmd, installs the RustDedicated server (about 6 GB), builds the Harmony mod, and installs Carbon.
Wait for `Server startup complete` in the terminal.

Join from the Rust client: open the F1 console and run `client.connect 127.0.0.1:28015`.
Press Ctrl+C to stop.
`start.sh` saves the world over RCON before exiting, and exit status 137 after a clean save is normal.

## Gameplay changes

- Gather rates are 2x for trees, ore nodes, corpses, planted crops, fishing, ground pickups, and quarry or excavator output, via Carbon's GatherManager module.
- Stack sizes are 2x for every item that stacks in vanilla, via Carbon's StackManager module.
- Furnaces and refineries smelt 2x faster, via [SmeltSpeed.cs](server/carbon/plugins/SmeltSpeed.cs).
  Fuel and charcoal per smelted item both drop by half, since the multiplier scales cook progress only.
- Days last 60 minutes and nights 10, via uMod's Time Of Day ([TimeOfDay.cs](server/carbon/plugins/TimeOfDay.cs)).
- Teleports are instant, with no cooldown or daily limit, via uMod's NTeleportation (not vendored; see License): `/sethome <name>` saves up to 5 homes, `/home <name>` returns to one, and `/outpost`, `/bandit`, `/deepsea`, and `/tpb` (back) warp.
  `/town` works once an admin runs `/town set` at its spot.
- Lanterns, campfires, fireplaces, and other lights switch on at dusk and off at dawn, via uMod's Night Lantern ([NightLantern.cs](server/carbon/plugins/NightLantern.cs)); furnaces and cooking appliances stay as you left them.
- `/remove` takes down your own building blocks and deployables for a full refund of the current grade's cost, via uMod's Remover Tool ([RemoverTool.cs](server/carbon/plugins/RemoverTool.cs)).
- Player-placed doors close 5 seconds after opening, via uMod's Auto Doors ([AutoDoors.cs](server/carbon/plugins/AutoDoors.cs)).
  Chat command `/ad` toggles it per player, and `/ad <5-10>` changes the delay.
- Every monument with a map name has a recycler: uMod's Monument Addons ([MonumentAddons.cs](server/carbon/plugins/MonumentAddons.cs)) and Monument Finder ([MonumentFinder.cs](server/carbon/plugins/MonumentFinder.cs)) add one from the [Default profile](server/carbon/data/MonumentAddons/Default.json) wherever vanilla ships none.
  That covers the fishing villages, Ranch, Large Barn, Apartment Complex, Abandoned Military Base, Abandoned Cabins, Missile Silo, the three quarries, both oil rigs, the Train Tunnel Links of the surface railway, and an Underwater Lab room; recyclers in a safe zone are the yellow kind, as at Outpost.
- Every building skin works without owning it, via uMod's Building Skins ([BuildingSkins.cs](server/carbon/plugins/BuildingSkins.cs)) and its required Image Library ([ImageLibrary.cs](server/carbon/plugins/ImageLibrary.cs)).
  `/bskin` picks a skin per grade, and the next hammer hit or upgrade applies it; `/bskin build` and the tool cupboard's "Change building skin" button reskin a whole base.
  It needs `c.grant group admin buildingskins.use` over RCON once, plus `buildingskins.build`, `.all`, `.tc`, and `.admin` for the bulk and admin paths.
- Every item skin works without owning it, via uMod's Skins ([Skins.cs](server/carbon/plugins/Skins.cs)): `/skin` opens a panel, an item dropped in its first slot fills the rest with skinned copies, and taking one applies it.
  The local [SkinsCatalog.cs](server/carbon/plugins/SkinsCatalog.cs) feeds it every skin the server knows, the game's own skin directory plus every approved store skin from Steam, about 6,400 across 160 items.
  The game client ships most of them, but the few hundred newest download from the Steam Workshop the first time the client shows them, one at a time and about 5 seconds each; Steam keeps them afterwards, so each one waits only once.
  It needs `c.grant group admin skins.use` over RCON once, plus `skins.admin` for `/skin add` and `/skin remove`.
- The owner's account gets Rust's developer flag, via the local [DeveloperFlag.cs](server/carbon/plugins/DeveloperFlag.cs), which turns on the game's own "all skins unlocked" mode.
  The server then accepts any skin when crafting, reskinning at a repair bench, or upgrading a building, and any DLC item.
  After joining, run `client.skins_access 1` in the F1 console, and the game's own menus list every skin; the client does not keep the setting between sessions, and `developerflag.status` over RCON shows the state.
  It needs `c.grant user <SteamID> developerflag.use` over RCON once.
  [docs/developer-and-admin-switches.md](docs/developer-and-admin-switches.md) covers the client command, what else developer mode changes, other hidden client switches such as god mode and infinite ammo, and the built-in creative mode.
- A livestock farm on the E6 river meadow runs itself, via the local [LivestockFarm.cs](server/carbon/plugins/LivestockFarm.cs): cattle, sheep, and chickens in 4 coops on a Frontier timber farmstead with a barn, a farmhouse, a field shelter, a water tower, and a wind turbine, fed from 8 planters of potatoes, corn, pumpkins, and wheat, 4 of them heated, that it harvests and replants, with milk, wool, eggs, meat, and the rest collected into the barn's boxes, barrels, and two fridges.
  It bonds, feeds, waters, breeds, and culls the herd at 9 cattle and 9 sheep, culling adults only, repairs anything destroyed, and creates feed or water only when a stock runs low; nothing but a player can damage it, and its buildings cannot be upgraded, demolished, or built onto and return to Frontier timber after any reskin, though you can furnish the barn and farmhouse with your own deployables.
  `farm.build` over RCON places it, `farm.upgrade confirm` turns a standing farm into the current layout in place, keeping the animals, crops, and stored items, and `farm.status` shows the herd and the stores. Players can use `/farm` for a compact summary and `/farm help` for manual farming instructions; labeled storage and an entrance guide explain the layout, and barn lights and path lanterns follow dusk and dawn. The assisted profile reserves milk, wool, and two heated crop beds for players, with automatic care and an absence fallback; the garden hive receives a starter colony for native honeycomb production. [docs/livestock-farm.md](docs/livestock-farm.md) covers operation, and [docs/farm-improvement-plan.md](docs/farm-improvement-plan.md) records the improvement plan.
- Vehicles do not decay: [server.cfg](server/server/pve/cfg/server.cfg) sets every vehicle decay convar and the idle workcart despawn timer to 1e9 minutes, about 1900 years of uptime.
  Emptied ore and fuel wagons still despawn 10 minutes after unloading, so full ones keep respawning.
- Playing alone costs nothing: [server.cfg](server/server/pve/cfg/server.cfg) keeps ore, barrels, and pickups at the density and respawn rate of a full server instead of 62.5% with one player of four.
  Buildings never decay, locked crates open in 5 minutes instead of 15, and a corpse stays 30 minutes instead of 5.
- The cargo ship, Chinook crate drops, and the patrol helicopter each come about every 1 to 2 hours of play, and airdrops about every 26 to 53 minutes.
  The Deep Sea opens right after a wipe and reopens 30 minutes after it closes instead of 90.
- Rust+ is off: [server.cfg](server/server/pve/cfg/server.cfg) sets `app.port -1`, which disables the companion server.
- EAC is off for the Proton client: the launch passes `-insecure` and `server.encryption 0`.
- Carbon's admin panel opens in chat with `/cp`.

## Operations

- `SKIP_UPDATE=1 ./start.sh` skips the steamcmd update and starts the existing install.
  A fresh clone with no install exits with an error telling you to run once without it.
- `CARBON=0 ./start.sh` starts vanilla RustDedicated, without Carbon, for example when an edge build breaks after a Rust update.
- [CLAUDE.md](CLAUDE.md) has an RCON helper function for sending commands over WebRCON on port 28016.
- Monument Addons edits happen in game once `c.grant group admin monumentaddons.admin` has run over RCON: `maspawn <entity>` places an addon where you look, `makill` removes one, and `mashow` shows nearby ones.
  Edits land in the Default profile, so `git diff` shows them.
- Back up `server/server/pve/` before anything risky, only while the server is stopped.
  It holds the world and player progress, which cannot be regenerated.
- A Facepunch force-wipe update bumps the save protocol number, and the server then generates a fresh world.
  Blueprints, deaths, and identities use a separate version number and survive the wipe unless that number changes too, as it did with buildid 25797215 in October 2026.
  Such an update can also change world generation, so the same seed may place monuments, roads, and rails elsewhere.
  After any wipe, `farm.build` over RCON places the livestock farm again from the layout in its config, buildings included.
- Before choosing a new seed, `uv run scripts/seed-scan.py --random 24 --jobs 4` generates random seeds on throwaway servers next to the live one and ranks them in `seedscan/<buildid>/<size>/index.html`, with every map rendered and labeled.
  [docs/map-seeds.md](docs/map-seeds.md) covers what the seed and the world size decide, and how to switch the world to a new seed.
- [docs/livestock.md](docs/livestock.md) covers the cattle and sheep of the October 2026 update: `spawn` names, a test farm from the F1 console, taming by trust, needs, breeding, and the `livestock.*` commands.

## Repository layout

[.gitignore](.gitignore) is a whitelist: only paths re-included with `!` are tracked, and every ignored parent directory needs its own `!` entry.
Tracked paths include [CLAUDE.md](CLAUDE.md), [LICENSE](LICENSE), [start.sh](start.sh), [harmony/](harmony/), [scripts/](scripts/), [docs/](docs/), [flake.nix](flake.nix), [flake.lock](flake.lock), [.github/](.github/), [server.cfg](server/server/pve/cfg/server.cfg), and Carbon's `config.json`, `config.profiler.json`, `modules/*/config.json`, `plugins/<Name>.cs`, `configs/<Name>.json`, and the Monument Addons profile `data/MonumentAddons/Default.json`.
Everything else under `server/`, plus `home/`, `steamcmd/`, `backups/`, and `seedscan/`, is a generated install, cache, world state, or tool output, and stays untracked.
`.rcon-password`, `server/carbon/config.webpanel.json`, and `server/server/pve/cfg/relay_cfg.json` hold secrets and are never tracked.
`server/carbon/configs/ImageLibrary.json` stays untracked too, because its `Steam API key` setting would be pushed with it.

## Development

```bash
nix develop
```

`flake.nix` provides `shellcheck`, `actionlint`, `jq`, `gitleaks`, `websocat`, and `nixfmt`.
It installs git-hooks.nix pre-commit hooks: `shellcheck`, `actionlint`, JSON validity, `nixfmt`, `gitleaks` on staged changes, and a guard that refuses server secrets and world state (`.rcon-password`, `config.webpanel.json`, `relay_cfg.json`, `ImageLibrary.json`, `users.cfg`, saves, databases, logs, `home/`, `steamcmd/`, `backups/`, `seedscan/`), and a check that keeps the local Building Skins fix in place.
`nix flake check` runs the same hooks.

Never pass `path:.` to flake commands, such as `nix develop path:.` or `nix flake check path:.`, in a checkout that holds a server install.
`path:` copies the untracked install and its secret files into the world-readable Nix store.
The default git reference only copies tracked files.

The `CI` workflow runs `nix flake check` and a gitleaks scan of the full history, on pushes to `main` and on pull requests.
The `Mods` workflow runs [check-mods.sh](scripts/check-mods.sh), which fetches only the game's managed DLLs from Steam and the Carbon edge build, then compiles the Harmony mod and the Carbon plugins against them the way Carbon does in game.
It runs when those sources change, and weekly on Friday, after Facepunch's Thursday updates.
Dependabot keeps the SHA-pinned actions current.

Run the same check locally against the installed server and Carbon in a few seconds:

```bash
MANAGED_DIR=server/RustDedicated_Data/Managed CARBON_DIR=server/carbon/managed scripts/check-mods.sh
```

After a Rust update, check that every convar in [server.cfg](server/server/pve/cfg/server.cfg) still exists:

```bash
scripts/check-convars.sh
```

[docs/game-modes.md](docs/game-modes.md) describes the ten game modes `server.gamemode` accepts; after a Rust update, compare it with:

```bash
uv run --with UnityPy python3 scripts/game-modes.py
```

Full operator notes, including the RCON helper, Carbon internals, and world-state layout, live in [CLAUDE.md](CLAUDE.md).

## License

[AGPL-3.0-or-later](LICENSE).
The vendored [AutoDoors.cs](server/carbon/plugins/AutoDoors.cs) is uMod's [Auto Doors](https://umod.org/plugins/auto-doors) plugin under the MIT license.
The vendored [MonumentAddons.cs](server/carbon/plugins/MonumentAddons.cs) and [MonumentFinder.cs](server/carbon/plugins/MonumentFinder.cs) are WhiteThunder's uMod plugins [Monument Addons](https://umod.org/plugins/monument-addons) and [Monument Finder](https://umod.org/plugins/monument-finder), both under the MIT license.
The vendored [BuildingSkins.cs](server/carbon/plugins/BuildingSkins.cs) and [ImageLibrary.cs](server/carbon/plugins/ImageLibrary.cs) are uMod's [Building Skins](https://umod.org/plugins/building-skins) by Marat and [Image Library](https://umod.org/plugins/image-library) by Absolut and K1lly0u, both under the MIT license; BuildingSkins.cs carries one local fix, described in CLAUDE.md.
The vendored [Skins.cs](server/carbon/plugins/Skins.cs) is uMod's [Skins](https://umod.org/plugins/skins) plugin by misticos under the MIT license.
The vendored [TimeOfDay.cs](server/carbon/plugins/TimeOfDay.cs) is uMod's [Time Of Day](https://umod.org/plugins/time-of-day) by FuJiCuRa, maintained by misticos, under the MIT license.
The vendored [NightLantern.cs](server/carbon/plugins/NightLantern.cs) is uMod's [Night Lantern](https://umod.org/plugins/night-lantern) by k1lly0u under the MIT license.
The vendored [RemoverTool.cs](server/carbon/plugins/RemoverTool.cs) is uMod's [Remover Tool](https://umod.org/plugins/remover-tool) by Arainrr, maintained by Tryhard, under the MIT license.
uMod's [NTeleportation](https://umod.org/plugins/nteleportation) by nivex is not vendored: uMod lists it as unlicensed, with all rights reserved by its original author, so only its config is tracked, and a fresh clone downloads the plugin with the `curl` command in CLAUDE.md.
