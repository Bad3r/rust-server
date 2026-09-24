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
- Rust+ is off: [server.cfg](server/server/pve/cfg/server.cfg) sets `app.port -1`, which disables the companion server.
- EAC is off for the Proton client: the launch passes `-insecure` and `server.encryption 0`.
- Carbon's admin panel opens in chat with `/cp`.

## Operations

- `SKIP_UPDATE=1 ./start.sh` skips the steamcmd update and starts the existing install.
  A fresh clone with no install exits with an error telling you to run once without it.
- `CARBON=0 ./start.sh` starts vanilla RustDedicated, without Carbon, for example when an edge build breaks after a Rust update.
- [CLAUDE.md](CLAUDE.md) has an RCON helper function for sending commands over WebRCON on port 28016.
- Back up `server/server/pve/` before anything risky, only while the server is stopped.
  It holds the world and player progress, which cannot be regenerated.
- A Facepunch force-wipe update bumps the save protocol number, and the server then generates a fresh world.
  Blueprints, deaths, and identities use a separate version number and survive the wipe.

## Repository layout

[.gitignore](.gitignore) is a whitelist: only paths re-included with `!` are tracked, and every ignored parent directory needs its own `!` entry.
Tracked paths include [CLAUDE.md](CLAUDE.md), [LICENSE](LICENSE), [start.sh](start.sh), [harmony/](harmony/), [scripts/](scripts/), [flake.nix](flake.nix), [flake.lock](flake.lock), [.github/](.github/), [server.cfg](server/server/pve/cfg/server.cfg), and Carbon's `config.json`, `config.profiler.json`, `modules/*/config.json`, `plugins/<Name>.cs`, and `configs/<Name>.json`.
Everything else under `server/`, plus `home/`, `steamcmd/`, and `backups/`, is a generated install, cache, or world state, and stays untracked.
`.rcon-password`, `server/carbon/config.webpanel.json`, and `server/server/pve/cfg/relay_cfg.json` hold secrets and are never tracked.

## Development

```bash
nix develop
```

`flake.nix` provides `shellcheck`, `actionlint`, `jq`, `gitleaks`, `websocat`, and `nixfmt`.
It installs git-hooks.nix pre-commit hooks: `shellcheck`, `actionlint`, JSON validity, `nixfmt`, `gitleaks` on staged changes, and a guard that refuses server secrets and world state (`.rcon-password`, `config.webpanel.json`, `relay_cfg.json`, `users.cfg`, saves, databases, logs, `home/`, `steamcmd/`, `backups/`).
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

Full operator notes, including the RCON helper, Carbon internals, and world-state layout, live in [CLAUDE.md](CLAUDE.md).

## License

[AGPL-3.0-or-later](LICENSE).
The vendored [AutoDoors.cs](server/carbon/plugins/AutoDoors.cs) is uMod's [Auto Doors](https://umod.org/plugins/auto-doors) plugin under the MIT license.
