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
- Player-placed doors close 5 seconds after opening, via uMod's Auto Doors ([AutoDoors.cs](server/carbon/plugins/AutoDoors.cs)).
  Chat command `/ad` toggles it per player, and `/ad <5-10>` changes the delay.
- Every monument with a map name has a recycler: uMod's Monument Addons ([MonumentAddons.cs](server/carbon/plugins/MonumentAddons.cs)) and Monument Finder ([MonumentFinder.cs](server/carbon/plugins/MonumentFinder.cs)) add one from the [Default profile](server/carbon/data/MonumentAddons/Default.json) wherever vanilla ships none.
  That covers the fishing villages, Ranch, Large Barn, Apartment Complex, Abandoned Military Base, Missile Silo, the three quarries, both oil rigs, and an Underwater Lab room; recyclers in a safe zone are the yellow kind, as at Outpost.
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
- Vehicles do not decay: [server.cfg](server/server/pve/cfg/server.cfg) sets every vehicle decay convar and the idle workcart despawn timer to 1e9 minutes, about 1900 years of uptime.
  Emptied ore and fuel wagons still despawn 10 minutes after unloading, so full ones keep respawning.
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
  Blueprints, deaths, and identities use a separate version number and survive the wipe.

## Repository layout

[.gitignore](.gitignore) is a whitelist: only paths re-included with `!` are tracked, and every ignored parent directory needs its own `!` entry.
Tracked paths include [CLAUDE.md](CLAUDE.md), [LICENSE](LICENSE), [start.sh](start.sh), [harmony/](harmony/), [scripts/](scripts/), [docs/](docs/), [flake.nix](flake.nix), [flake.lock](flake.lock), [.github/](.github/), [server.cfg](server/server/pve/cfg/server.cfg), and Carbon's `config.json`, `config.profiler.json`, `modules/*/config.json`, `plugins/<Name>.cs`, `configs/<Name>.json`, and the Monument Addons profile `data/MonumentAddons/Default.json`.
Everything else under `server/`, plus `home/`, `steamcmd/`, and `backups/`, is a generated install, cache, or world state, and stays untracked.
`.rcon-password`, `server/carbon/config.webpanel.json`, and `server/server/pve/cfg/relay_cfg.json` hold secrets and are never tracked.
`server/carbon/configs/ImageLibrary.json` stays untracked too, because its `Steam API key` setting would be pushed with it.

## Development

```bash
nix develop
```

`flake.nix` provides `shellcheck`, `actionlint`, `jq`, `gitleaks`, `websocat`, and `nixfmt`.
It installs git-hooks.nix pre-commit hooks: `shellcheck`, `actionlint`, JSON validity, `nixfmt`, `gitleaks` on staged changes, and a guard that refuses server secrets and world state (`.rcon-password`, `config.webpanel.json`, `relay_cfg.json`, `ImageLibrary.json`, `users.cfg`, saves, databases, logs, `home/`, `steamcmd/`, `backups/`), and a check that keeps the local Building Skins fix in place.
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
