# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

A local Rust (the Facepunch game) dedicated server, Steam app 258550, on NixOS. It hosts a small PvE world for the
Rust client running under Proton as `RustClient.exe` with EAC off, modded with Carbon (edge channel). There is no
test suite; the checks are the hooks and workflows in Development and CI below. The hand-maintained files are
`start.sh`, `harmony/Ipv4Only.cs`, `server/server/pve/cfg/server.cfg`, `server/carbon/plugins/SmeltSpeed.cs`,
`SkinsCatalog.cs`, `DeveloperFlag.cs`, `scripts/`, `docs/`, `flake.nix`, `.github/`, and `README.md`; steamcmd, the
mod build in `start.sh`, Carbon, uMod downloads, Nix (`flake.lock`), or the server writes everything else.

The git repo (branch `main`) pushes to the private GitHub repo https://github.com/Bad3r/rust-server (`origin`). It
tracks those files, `CLAUDE.md`, `LICENSE` (AGPL-3.0-or-later), `flake.lock`, and Carbon's `server/carbon/config.json`,
`config.profiler.json`, `modules/*/config.json`, each installed plugin's `plugins/<Name>.cs` and
`configs/<Name>.json`, and the Monument Addons profile `data/MonumentAddons/Default.json`. `.gitignore` is a
whitelist: a new file needs its own `!` entry, plus one for each ignored parent directory. Everything tracked is
pushed, so never whitelist the secrets `.rcon-password`, `config.webpanel.json`, and `relay_cfg.json`, or
`configs/ImageLibrary.json`, whose empty `Steam API key` setting would carry a key. Carbon rewrites its tracked
configs on boot, after a self-update, and on `c.setmodule` or panel edits, and Monument Addons rewrites its profile
on in-game edits, so `git diff` shows what changed.

## Commands

- `./start.sh`: update the server with steamcmd, then run it in the foreground while streaming `server.log`.
- `SKIP_UPDATE=1 ./start.sh`: start without updating.
- `CARBON=0 ./start.sh`: start vanilla, without Carbon (for example when an edge build breaks after a Rust update).
- Ctrl+C (or SIGTERM/SIGHUP to the script): graceful stop through RCON `quit`, which saves the world. Exit status
  137 after a clean save is normal because `quit` ends with `Process.Kill()` on itself.
- Join from the client: F1 console, `client.connect 127.0.0.1:28015`. In chat, `/cp` opens Carbon's admin panel.
- Lint after editing `start.sh`: `bash -n start.sh && shellcheck start.sh` (the current script passes both).
- `nix develop`: shell with shellcheck, actionlint, jq, gitleaks, websocat, and nixfmt; entering it installs the
  pre-commit hooks. `nix develop -c pre-commit run --all-files` runs every hook, and `nix flake check` runs them in
  the build sandbox.
- `scripts/check-mods.sh`: compile `harmony/Ipv4Only.cs` with `start.sh`'s `mcs` flags and each plugin with Roslyn
  `csc` the way Carbon does: `-langversion:preview`, `-unsafe+`, the define symbols from `server/carbon/config.json`
  plus Carbon's always-on `UNIX`, and the assemblies it lists under `Publicizer` made public with Carbon's bundled
  AsmResolver, which also gains the `BasePlayer.IPlayer` field Carbon injects into `Assembly-CSharp.dll` at boot
  (`Carbon.Publicizer.AssemblyCSharp.InjectIPlayer` in Carbon.Startup.dll). The script takes `csc` from
  `nixpkgs#roslyn`, listed before `nixpkgs#mono` since both provide one, and exits if it ever resolves below
  Roslyn 4. It downloads `RustDedicated_Data/Managed` of the public build (Linux depot 258552, about 25 MB) and the
  Carbon edge archive into a temp directory on every run.
  `MANAGED_DIR=server/RustDedicated_Data/Managed CARBON_DIR=server/carbon/managed scripts/check-mods.sh` checks the
  local install instead, in a few seconds. `PLUGINS_DIR` compiles another directory in place of
  `server/carbon/plugins`, such as `scripts/monument-addons` for the survey tool that must not sit there.
- `scripts/check-convars.sh`: resolve every convar in `server.cfg` through a fresh `ilspycmd` decompile of
  `ConsoleGen.cs` to the `[ServerVar]` field or property it sets, exiting 1 on a name a Rust update renamed or
  removed (the server only logs `Command '<name>' not found` for those). It reads the local install unless
  `MANAGED_DIR` points elsewhere, and checks names, not what a convar now does.
- `uv run --with UnityPy python3 scripts/game-modes.py`: print each game mode in `GameModeManifest` (the values
  `server.gamemode` takes) with its shortname, convar overrides, feature list, and settings that differ from
  `vanilla`, then the map generation overrides in `RustDedicated_Data/resources.assets`, in about 20 s.
  `docs/game-modes.md` summarizes that output and the code that applies a mode.

Send an RCON command and print its reply. WebRCON listens on `127.0.0.1:28016`. Filter on the request
`Identifier` so server log broadcasts on the same socket are dropped. The call always takes the full 3 s timeout
because the server keeps the socket open:

```bash
rcon() {
  jq -cn --arg m "$1" '{Identifier: 1001, Message: $m, Name: "WebRcon"}' |
    timeout 3 websocat -n "ws://127.0.0.1:28016/$(<.rcon-password)" |
    jq -r 'select(.Identifier == 1001) | .Message'
}
rcon status
```

`.rcon-password` is a secret (mode 600, generated by `start.sh`). Read it only through `$(<.rcon-password)` and
never print it. Rust masks it as `******` in the logged command line.

## How start.sh works

1. Creates `home/`, then, unless `SKIP_UPDATE=1`, runs `steamcmd.sh +app_update 258550` under `steam-run` with
   `HOME=$root/home`, so steamcmd never touches the real `~/.steam` of the running Steam client. It updates on every
   start because the Steam client auto-updates and refuses servers on an older network protocol. `steamcmd/` is
   untracked, so on a fresh clone it first extracts Valve's `steamcmd_linux.tar.gz` there.
2. Exits with an error when `server/RustDedicated` is missing, which happens only on a fresh clone started with
   `SKIP_UPDATE=1`.
3. Builds `server/HarmonyMods/Ipv4Only.dll` from `harmony/Ipv4Only.cs` with nixpkgs `mcs` when the DLL is missing
   or older than the source. It compiles against the game's own `RustDedicated_Data/Managed/` assemblies, including
   the `netstandard.dll` facade that `UnityEngine.CoreModule` needs.
4. Unless `CARBON=0`, extracts the Carbon edge archive into `server/` when `server/carbon.sh` is missing and
   launches through `bash ./carbon.sh` (see Carbon below). The archive holds only binaries and empty directories, so
   on a fresh clone extraction keeps the checked-out Carbon configs and plugins.
5. Creates `.rcon-password` if missing and rotates `server.log` to `server.log.prev`. `websocat` is expected on
   `PATH` (installed on the host system profile).
6. Launches `setsid env HOME=$root/home steam-run bash ./carbon.sh ...` (or `./RustDedicated` with `CARBON=0`) in
   the background. The exported `LD_LIBRARY_PATH` covers `RustDedicated_Data/Plugins{,/x86_64}` for the vanilla
   path; `carbon.sh` replaces it. RustDedicated has no signal handler, so a terminal Ctrl+C would kill it unsaved;
   `setsid` keeps it out of the terminal's process group. Facepunch's `server/runds.sh` is unused.
7. The `stop()` trap kills the process group instead of quitting while `Server startup complete` is absent from
   the log, because `quit` saves synchronously and during startup writes the half-loaded world over the `.sav`.
   After startup it sends `quit` over WebRCON with `websocat -u -1` (fire and forget) and kills the group if that
   fails, losing progress since the last autosave.
8. After exit it prints the last `^Saved [0-9,]+ ents` line of the run.

`stop()` and the exit summary parse those two exact log strings. If a Rust update changes them, Ctrl+C degrades
to an unsaved kill.

`-insecure` and `+server.encryption 0` turn EAC off for the Proton client.

Startup applies the `+` arguments, runs `server.readcfg` (logged as `Server Config Loaded`; it reads
`serverauto.cfg` and `server.cfg`), runs `users.cfg`, then applies the `+` arguments again, so `start.sh` values
override cfg files. `server.log` records the full `Command Line:` and echoes each applied argument as
`name: "value"`. Check those to see what a run actually used.

Launch arguments cannot carry negative values. `Facepunch.CommandLine` treats every argv token starting with `-`
or `+` as a new switch, and `ConsoleSystem.UpdateValuesFromCommandLine` replays a switch without a value as `1`, so
`+app.port -1` ran as `app.port 1`. Such settings go in `server/server/pve/cfg/server.cfg`, which holds
`app.port -1`: `CompanionServer.Server.Initialize` returns early for negative ports, so Rust+ stays off.

`server.cfg` also turns vehicle decay off by setting every vehicle decay convar (`playerhelicopter`, `playerboat`,
`motorrowboat`, `tugboat`, `basesubmarine`, `snowmobile`, `basesiegeweapon`, `ridablehorse`, `bike`,
`hotairballoon`, `modularcar`) and `traincar.decayminutes`, the idle workcart despawn timer, to 1e9 minutes. Each
decay tick of about 60 s removes `MaxHealth / decayminutes`, so 0 destroys a vehicle on its first tick instead of
disabling decay. PT boats and kayaks read the `motorrowboat` values, and the diver propulsion vehicle reads the
`basesubmarine` ones. `traincarunloadable.decayminutesafterunload` stays at 10: emptied ore and fuel wagons must
despawn, or they fill `traincar.population` and full wagons stop respawning. None of these convars is saved to
`serverauto.cfg`. `docs/vehicle-decay-convars.md` lists each convar's backing field and vanilla default, the
`BaseBoat.WaterVehicleDecay` formula, and which vehicle classes reuse another class's fields.

The world runs vanilla survival rules: `server.cfg` sets no `server.gamemode`. It ran Facepunch's `primitive` mode
for part of 2026-09-24. The revert removed that line while the server was stopped and set the nine saved convars the
mode had written to `serverauto.cfg` back to their earlier values, after which both files matched
`backups/pve-20260924T184111/`, the world from before the switch. A mode's saved overrides outlive its
`server.gamemode` line, so switching modes always needs that second step. `docs/game-modes.md` covers all ten modes
and what the primitive run changed.

## IPv4-only host

The host boots with `ipv6.disable=1`. Unity's Mono sets `Socket.OSSupportsIPv6` from a build-time platform flag
instead of probing, so every `HttpClient` request (map upload, analytics) failed while creating an AF_INET6 socket
with `An address incompatible with the requested protocol was used`, and the map upload's 10 retries added 235 s to
startup. Neither `machine.config` nor `DOTNET_SYSTEM_NET_HTTP_USESOCKETSHTTPHANDLER` changes that path in this build.

`harmony/Ipv4Only.cs` is a Harmony mod. Rust loads `server/HarmonyMods/*.dll` before scene load and calls
`IHarmonyModHooks.OnLoaded`, where the mod probes AF_INET6 and, only if the kernel rejects it, clears Mono's
`Socket.s_OSSupportsIPv6` and `s_SupportsIPv6` so connects and DNS use IPv4. A working start logs
`[Ipv4Only] Kernel has no IPv6 address family` and `[Rust.MapCache] Map uploaded to backend: <url>`, after which
clients download the map from Facepunch's CDN through `server.levelurl` (in memory only, not saved). The mod is
temporary: remove it once Unity's runtime probes IPv6 support or the host enables the IPv6 address family again.

The map image upload never runs: Rust renders `Map.ImageData` only inside the Rust+ companion server startup,
which `app.port -1` skips.

## Carbon

Carbon runs Oxide/uMod plugins and adds an in-game admin panel. The install is the `edge_build` release asset
`Carbon.Linux.Debug.tar.gz` (Carbon's staging build from its `main` branch; edge ships no Linux `Release`
target). There is no `preview_build` tag any more, and `experimental_build` targeted an older Rust protocol, so edge
is the only pre-release channel that matches the running Rust build. `c.version` over RCON prints the loaded
version, channel, and Rust protocol.

- Loading: `carbon.sh` sources `carbon/tools/environment.sh`, which preloads `server/libdoorstop.so` so that
  `carbon/managed/Carbon.Preloader.dll` loads before the game, then runs `RustDedicated`. The archive ships
  `carbon.sh` without the executable bit, hence `bash ./carbon.sh`. `environment.sh` sets `LD_LIBRARY_PATH` to
  `server:server/RustDedicated_Data/Plugins/x86_64`; the game still finds the libraries in
  `RustDedicated_Data/Plugins/` on its own.
- Self-update: the channel is compiled into the build, so an edge build updates from `edge_build` on every boot
  (`SelfUpdating` in `server/carbon/config.json`). The version check and download run the external `curl` (from the
  user profile, visible inside `steam-run`), not Mono's HTTP stack. The result goes to the server's stdout, not
  `server.log`: `Carbon Debug is up to date ... on tag 'edge_build'`.
- `Ipv4Only` still loads through Rust's HarmonyLoader; Carbon patches `Rust.Harmony.dll` in memory but keeps it.
- Files: `server/carbon/` holds `config.json` (`Analytics.Enabled` is on by default), `modules/<Name>/config.json`,
  `plugins/` (drop Oxide `.cs` plugins here; they hot-load), `data/` (Oxide-format `oxide.groups.data` and
  `oxide.users.data`), and `logs/Carbon.Core.log` (timestamped). `config.webpanel.json` has the web panel disabled
  and holds a generated account password; treat it as secret. All identities in `server/` share this directory.
  The `admin` group, which Carbon gives auth level 2, holds `monumentaddons.admin`, the five `buildingskins.*`
  permissions, `skins.use`, and `skins.admin` in `data/oxide.groups.data`, which is untracked, so a fresh install
  needs each `c.grant group admin <permission>` once. The owner's SteamID (the `ownerid` in `cfg/users.cfg`) holds
  `developerflag.use` as a user grant in the untracked `data/oxide.users.data`, restored with
  `c.grant user <SteamID> developerflag.use`.
- Modules: `Admin` (`/cp` or `/cpanel`, auth level 2) and its UI helpers, `GatherManager`, and `StackManager` are
  on. `AdminExtensions` (admin chat commands such as `/tpm`), `Vanish`, `Hammer`, `Whitelist`, `SelectiveEAC`, and
  `AutoWipe` are off; toggle them in the panel's Modules tab or with `c.setmodule <Name> 0|1`. Keep `AutoWipe` off:
  it wipes the world. `c.setmodule` and panel edits write the in-memory config over `modules/<Name>/config.json`,
  so edit that file while the server is stopped, or run `c.loadmodule <Name>` right after editing it.
- `GatherManager` (2x): `"*": 2.0` in `Gather` (trees, ore nodes, corpses, the final-hit bonus, planted crops,
  fishing), `Pickup` (ground collectibles), `Quarry`, and `Excavator`. An exact shortname entry overrides `*`, and
  the result is `ceil(amount * multiplier)`; the default seed and skull entries stay at 1.0. The module replaces
  vanilla collectible pickup, which then skips backpack overflow and the `Collectible_DoubleYield` modifier.
- `StackManager` (2x): only items that stack in vanilla are doubled. `Config.Items` lists each of them at its vanilla
  size and `GlobalItemsMultiplier 2.0` scales that list; `GlobalMultiplier` scales every other item and must stay
  1.0. A stack merge deletes the absorbed item with its contents, so stack-1 items (backpacks, guns with
  attachments) must stay unstackable. `data.json` records each item's vanilla size once, so Facepunch stack
  changes do not reach it, and items a Rust update adds stay vanilla until merged into `Items` while stopped:

  ```bash
  cd server/carbon/modules/StackManager && jq --slurpfile d data.json '.Config.Blacklist as $b |
    .Config.Items = (($d[0].Items | with_entries(select(.value > 1 and (.key | IN($b[]) | not)))) + .Config.Items)' \
    config.json >config.json.new && mv config.json.new config.json
  ```
- Plugins (config in `configs/<Name>.json`, runtime data in `data/`): `AutoDoors.cs` is uMod's Auto Doors 3.3.12,
  MIT-licensed on uMod, which the README's License section records; a new third-party plugin needs the same entry.
  Update it with `curl -fsSL -o server/carbon/plugins/AutoDoors.cs https://umod.org/plugins/AutoDoors.cs`. It closes
  owned doors 5 s after they open for every player, with no permission needed; `/ad` toggles it per player,
  `/ad <5-10>` sets the delay, and `/ad h` lists the per-door and per-type options. `SmeltSpeed.cs` is local: it
  multiplies the vanilla `BaseOven.smeltSpeed` of every oven that cooks at 1000 or hotter (all furnaces and
  refineries) by 2, and unloading it restores the prefab values. Fuel burns at the vanilla rate per second, so each
  smelted item costs half the wood and yields half the charcoal. uMod's QuickSmelt 5.1.5 (2022) fails to compile
  here (`ItemModCookable` has no `CanBeCookedByAtTemperature`), and Furnace Splitter cannot speed anything up
  because `BaseOven.IncreaseCookTime` divides cook time across all cooking stacks.
- Monument recyclers: `MonumentAddons.cs` is uMod's Monument Addons 0.21.4 and `MonumentFinder.cs` its required
  Monument Finder 3.1.5, both by WhiteThunder under MIT. Update each with
  `curl -fsSL -o server/carbon/plugins/<Name>.cs https://umod.org/plugins/<Name>.cs`. The `Default` profile in
  `data/MonumentAddons/Default.json`, which Monument Addons enables on first load, places one `recycler_static` at
  each monument type that has a map name and no vanilla recycler: 13 keyed by prefab short name (`fishing_village_a`,
  `oilrig_1`, and so on), plus the Underwater Lab room `module_1200x1200_3way_ladder`, because lab interiors are
  assembled from modules and an offset from the lab root misses on other layouts. Positions are local to
  `MonumentInfo.transform` (the module's transform for the lab room) and come from a survey of this map that
  spawned a recycler at each spot and checked overlap and ground contact. They apply to every future map, but a
  monument type this map lacks gets none until someone places one in game. 21 monument types ship a recycler
  already. Recyclers in a safe zone turn yellow, as vanilla ones do. Monument Addons respawns its entities from the
  profile on every load and keeps them out of the `.sav`. `data/MonumentAddons.json` holds the enabled profiles and
  each player's selected profile by SteamID, so it stays untracked. `c.grant group admin monumentaddons.admin`
  unlocks the in-game commands `maspawn <entity>`, `makill`, `maundo`, `masave`, `mashow`, and `mahelp`. Skip the
  bundled `MonumentsRecycler` example profile: its oil rig keys `OilrigAI` and `OilrigAI2` predate the rename to
  `oilrig_2` and `oilrig_1` and match nothing. `docs/monument-survey.md` covers redoing the placements after a wipe
  with `scripts/monument-addons/MonumentSurvey.cs`, a plugin that goes into `server/carbon/plugins/` only for the
  survey session, and `make-profile.sh`, which turns its output into a profile.
- Building skins: `BuildingSkins.cs` is uMod's Building Skins 2.0.12 by Marat and `ImageLibrary.cs` its required
  Image Library 2.0.67 by Absolut and K1lly0u, both MIT, updated with the same `curl` pattern; Building Skins
  unloads itself when Image Library is missing. `/bskin` stores a skin per grade, and the `OnHammerHit` and
  `OnStructureUpgrade` hooks apply it through `BuildingBlock.ChangeGradeAndSkin`, which checks no ownership; only the
  `DoUpgradeToGrade` RPC rejects unowned skins (`SkinNotOwned`). Its default table covers every grade skin in this
  build: Frontier 10232 and Gingerbread 2 (wood); Adobe 10220, Brick 10223, Brutalist 10225, Jungle 10326, and Crypt
  10472 (stone); Shipping Container 10221 (metal); Space 10430 (top tier). At load, Image Library downloads item
  icons from `cdn.rusthelp.com` and the skin images from `i.ibb.co` and `i.postimg.cc`. The vendored 2.0.12 carries
  one temporary local fix: its bulk path `UpgradeSkin` (`/bskin build`, `/bskin all`, the tool cupboard button)
  skipped every block whose chosen skin the player owns on Steam, through a copy of the `HasItem` early return that
  only makes sense in `OnStructureUpgrade`, where vanilla then applies the owned skin itself. The fix deletes that
  line; the `building-skins-owned-skip` pre-commit hook refuses a `BuildingSkins.cs` that has it again, so re-apply
  the deletion after updating, and drop both once an upstream release removes the line (umod.org support thread
  55864 reports it). Vanilla's `skin_looking`, `skin_radius`, `upgrade_looking`,
  and `upgrade_radius` apply skins without a plugin, but only from a connected admin's F1 console (over RCON they
  throw a NullReferenceException on `arg.Player().eyes`), and they refuse Gingerbread, whose grade asset has
  `enabledInStandalone` 0.
- Item skins: `Skins.cs` is uMod's Skins 2.2.5 by misticos (MIT), and `SkinsCatalog.cs` is local. `/skin` opens a
  36-slot loot panel; an item dropped in its first slot fills the others with skinned copies, 35 a page, and taking
  one applies it through `ItemManager.Create`, which checks no ownership. Skins calls `OnSkinsFetch` before merging
  its config's `Skins` list, which stays empty: `SkinsCatalog.cs` adds every `ItemSkinDirectory.ForItem` id and, for
  each definition with a `workshopid` in `Steamworks.SteamInventory.Definitions` (loaded from Steam about 15 s after
  `OnServerInitialized`), the value `ItemDefinition.FindSkin` gives an item: its `workshopdownload`. The 73 legacy
  definitions (itemdefid 10032 to 10143) have none, and vanilla gives an item their `ItemSkinDirectory` id, which is
  already listed. Their `workshopid` is an old upload: the 13 the client downloaded here were 5 to 191 MB, had no
  `manifest.txt`, and failed with `Invalid skin manifest`. On this build each boot logs
  `[Skins Catalog] indexed 160 items, 6396 skins` and warns if Steam yields no workshop skins or a definition resolves
  to no skin. The plugin reads `workshopdownload` itself because `FindSkin` reads it through a `PlatformService`
  cache that updates in an `OnDefinitionsUpdated` handler registered after the plugin's on a cold boot. Steam keys
  all 102 LR-300 workshop skins by the item's prefab name, `lr300.item`, not its shortname `rifle.lr300`, so the
  plugin also matches prefab names, as `ItemDefinition.skins2` does. 157 of the 472 `ItemSkinDirectory` ids
  substitute a different item with skin 0 (`autoturret` 10479 becomes `industrial.autoturret`), and Skins handles
  that. Without the plugin, `inventory.give <item> <amount> <condition> <skinid>` from a connected admin's F1 console
  creates a skinned item.
- Client skin loading: an `ItemSkinDirectory` skin is game content. A workshop skin goes through
  `Rust.Workshop.WorkshopSkin` (the server's `Rust.Workshop.dll` has it, with `LoadFromWorkshop` emptied), which
  reads `Assets/Skins/<id>/` from the client's `Bundles/textures/textures.<n>.bundle`, `n` being the id's
  hundred-millions digit, when that bundle has it, and otherwise has Steam download the workshop item into
  `steamapps/workshop/content/252490/<id>/`, where it stays between sessions. The bundles of the client installed here
  hold 5651 skins (9.8 GB), and 274 catalog skins are missing from them. `ItemQueueCoroutine` loads one skin at a
  time and polls a download once a second for up to 60 s, so each missing skin costs about 5 s the first time an icon
  or model needs it, and every icon queued behind it waits. The server cannot ship that content.
- Skin research: `docs/skin-ownership-gates.md` records the three ownership gates in `Assembly-CSharp` (grade skins,
  item skins, DLC items), the vanilla admin bypasses, and Facepunch's DLC clause with its test-server exception.
  `scripts/skin-grades.py`, `skin-freeitems.py`, and `skin-coverage.py` regenerate its bundle data with
  `uv run --with UnityPy python3 scripts/<name>.py` in about 8 s each.
- Developer flag: `DeveloperFlag.cs` is local. It sets Rust's `IsDeveloper` player flag on every player with
  `developerflag.use` in `OnPlayerConnected`, which runs after `BasePlayer.PlayerInit` resets the flag from Facepunch's
  `DeveloperList`, and unloading it restores that value. With the flag, `BasePlayer.GetSkinsAccessLevel` returns the
  `client.skins_access` value in the connection's client info, and every skin and DLC ownership check reads it before
  Steam ownership: 1 passes all three gates of `docs/skin-ownership-gates.md`, -1 fails them, and 0 keeps ownership.
  The client reports that convar on connect and through `global.setinfo`, and its `cfg/client.cfg` does not keep it,
  so the plugin writes 1 into the client info when it sets the flag. The IL2CPP client runs its own copy of the
  checks against its local convar: after `client.skins_access 1` in F1, its menus listed unowned skins and crafting
  applied them (in-game test, 2026-09-24). Run it after every join.
  `developerflag.status` (RCON, or F1 for an admin or developer) prints each player's flag, reported value, and
  effective level. The flag also turns the chat name orange and makes `inventory.give` reply "you silently gave
  yourself". Facepunch's developers also get auth level 3; admin rights here still come from `users.cfg`.
- Mono profiler: `config.profiler.json` enables it and tracks `Assembly-CSharp`, `Assembly-CSharp-firstpass`, and
  all plugins, modules, and extensions. `carbon/native/libCarbonNative.so` reads that file once at boot, so changes
  need a restart. `c.profile <seconds> -c` records calls, `c.profiler.print -t` exports a table to
  `server/carbon/profiles/`, and `c.find profile` lists the other flags (the docs warn that timing capture is
  expensive). The `Profiler Disabled!` log line is unrelated: Rust's `ServerProfiler` logs it unless the command
  line has `-enableProfiler`.

## Server state

`server/` is the steamcmd install, and `app_update` replaces game files there. Do not hand-edit them. The Carbon
archive adds `server/carbon/`, `server/carbon.sh`, `server/Carbon.targets`, and `server/libdoorstop.so`, which
steamcmd leaves alone. `server/HarmonyMods/` holds only the built `Ipv4Only.dll`, and Harmony writes its debug log to
`server/harmony_log.txt`. The installed build is `buildid` in `server/steamapps/appmanifest_258550.acf`.

`server/server/pve/` (identity `pve`) holds the world and player progress, which cannot be regenerated. Back up the
whole directory, only while the server is stopped, before anything risky; earlier copies are in
`backups/pve-<timestamp>/`.

- The world is `proceduralmap.<worldsize>.<seed>.<save protocol>.sav`. `.sav.1` and `.sav.2` are the two previous
  saves (`server.saveinterval 300`). Changing `server.seed` or `server.worldsize` in `start.sh` starts a fresh
  world under a new file name.
- `proceduralmap.<worldsize>.<seed>.<save protocol>.map` caches the generated map, and `Loading World` reads it in
  under a second. `Generating procedural map of size ...` still appears on every boot.
- `*.db` files are SQLite in WAL mode, and most data currently sits in the `*.db-wal` files (for example,
  `player.states.288.db` is 4 KB with a 160 KB WAL). Never delete a `-wal` file or copy a `.db` without it.
- `288` is the save protocol (`Protocol: 2633.288.1` in the log is network.save.report). A Facepunch force-wipe
  update bumps it, and the server then ignores the old files and generates a fresh world under the new number.
  Because `start.sh` always updates, that update wipes the world. Blueprints, deaths, and identities use a separate
  version (`*.17.db`) and survive map wipes until that number changes.
- `cfg/serverauto.cfg` holds about 390 saved convars. The server rewrites it only on `quit` (logged as
  `Config Saved`), so edit it only while the server is stopped. `cfg/users.cfg` holds owner and moderator entries.
  `cfg/relay_cfg.json` is disabled but contains an `AuthToken`; treat it as secret.
- `command_history/commands_<date>.log` records startup arguments and players' F1 console commands with UTC
  timestamps. `server.log` has no timestamps.

`home/` is the redirected HOME (Steam config and cache) for both steamcmd and RustDedicated's steamclient.
`steamcmd/` is the steamcmd install. `steam-run` comes from the NixOS system profile.

## Expected log noise

steamcmd's output and the native Steam lines below go only to stdout, which `start.sh` shows next to `server.log`.

- `Command 'batchmode'`, `'insecure'`, `'logfile'`, and `'rcon.password' not found`, three times each: the server
  replays argv through the console at three bootstrap stages, and those tokens are not convars.
- `[HarmonyLoader] Couldn't unload mod 'Ipv4Only': not loaded`: the loader always unloads a mod before loading it.
- `Nexus endpoint and/or secret key is not set`: no Nexus (Facepunch's linked-server feature) is configured.
- `Forcing GfxDevice: Null`, missing TOD or TextMeshPro shaders, `HDR Render Texture not supported`, and `3D Noise
  requires higher shader capabilities`: the dedicated build renders nothing and runs Unity's Null graphics device.
- `AsyncResourceUpload failed.`, six times right after the Carbon version banner: Unity's asynchronous resource
  upload fails during scene load, before any plugin loads, and the boot continues. `Facepunch.Output.LogHandler`
  keeps this line and the shader and HDR lines off the RCON stream.
- `The referenced script (Unknown) on this Behaviour is missing!` and its `input_redirector.prefab` twin: that prefab
  in the `AssetScene-bootstrap` scene of `assetscenes.bundle` holds the client-only `InputRedirector` component,
  which the server's `Assembly-CSharp.dll` lacks.
- `CreateBoundSocket: failed to create socket, error EAFNOSUPPORT (97)`, once inside steamcmd's
  `Loading Steam API...OK` and once before `SteamServer Initialized`: Steam's native `steamclient.so` also tries
  IPv6, which this host rejects, and carries on over IPv4. `Ipv4Only` patches only Mono.
- `[S_API FAIL] Tried to access Steam interface SteamNetworkingUtils004 before SteamAPI_Init succeeded.` and
  `SteamInternal_SetMinidumpSteamID: ... [API loaded no]`: the server runs only the game server API, and
  Facepunch.Steamworks resolves `ISteamNetworkingUtils` through `SteamAPI_SteamNetworkingUtils_SteamAPI_v004`, which
  tries the client interface first.
- `[ImageDatabase] The server identifier has changed` and `[Image Library] The CommunityEntity instance ID has
  changed!`: both key stored images to the network ID of `CommunityEntity`, which the `pve` save keeps, and record
  it in the shared `server/carbon/`. A boot of any other identity rewrites that record, so the next `pve` boot logs
  both lines and stores the images again.
- `[Codefling] Failed fetching vendor. Error code 404!`: Carbon's Codefling plugin browser endpoint answers 404.
- `Carbon Analytics are ON`, and `gpath.c:115: assertion 'filename != NULL' failed` on stdout: Carbon defaults; the
  assertion appears only with Carbon and does not stop the boot.

`An address incompatible with the requested protocol was used` is no longer noise: it means `Ipv4Only` did not load
or no longer finds Mono's fields. Check the `[HarmonyLoader ...]` and `[Ipv4Only]` lines near the top of the log.

## Host tools

The NixOS system profile puts these on `PATH`, mostly for reading game code and data. `start.sh` and
`scripts/check-mods.sh` get Mono through `nix shell nixpkgs#mono` instead, which resolves to the same Mono on this
host and also works in CI. The examples run from the repo root with `m=server/RustDedicated_Data/Managed`.

- `ilspycmd --disable-updatecheck -r "$m" -t BaseOven "$m/Assembly-CSharp.dll"` decompiles one type to stdout, and
  `-p -o <dir>` writes the whole assembly as a project in under 15 s. Without `--disable-updatecheck` it checks
  online for a release and prints `You are not using the latest version`, which the nixpkgs build gets even when
  current. `-il` ignores `-t` and disassembles the whole assembly (over 2 million lines for `Assembly-CSharp.dll`),
  so write it to a file and cut one class out with `sed -n '/^\.class .* BaseOven$/,/^} \/\/ end of class BaseOven$/p'`.
- Mono provides `mono`, `mcs`, and `monodis`. `monodis` prints metadata tables: `--assemblyref` (what a DLL links
  against, such as the built `Ipv4Only.dll`), `--assembly` (name and version, such as
  `server/carbon/managed/Carbon.Common.dll`), and `--typedef`. `mcs` checks that a changed `harmony/Ipv4Only.cs`
  still compiles: run the `start.sh` command with `-out:` in a scratch directory. `scripts/check-mods.sh` remains the
  full check.
- `AssetRipper` exports Unity bundles. It serves a web UI on `127.0.0.1`, and `--headless` stops it from opening a
  browser. A Unity-project export writes each prefab as YAML, so `rg` finds item fields such as `stackable`,
  `cookTime`, or `lowTemp` in `ExportedProject/Assets/prefabs/**/<name>.item.prefab`:

  ```bash
  AssetRipper --headless --port 18765 >/tmp/assetripper.log 2>&1 &
  pid=$!
  for _ in {1..30}; do curl -fs -o /dev/null http://127.0.0.1:18765/ && break; sleep 1; done
  out=$(mktemp -d)/items
  curl -fsS --data-urlencode "Path=$PWD/server/Bundles/shared/items.preload.bundle" http://127.0.0.1:18765/LoadFile
  curl -fsS --data-urlencode "Path=$out" http://127.0.0.1:18765/Export/UnityProject
  kill "$pid"
  ```

  Each request returns when its work is done. An export deletes a non-empty target directory recursively, and
  `--headless` skips the confirmation, so always export to a fresh path like `$out`, never into the repo or
  `server/`. Load one bundle at a time: `content.bundle` and `monuments.bundle` are gigabytes each. The exported
  `Scripts/` are dummy classes because no assemblies were loaded, and `/swagger` on the same port documents the rest
  of the HTTP API.
- UnityPy reads the same bundles from Python with `uv run --with UnityPy python <script>`. Rust's bundles ship type
  trees, so `obj.read_typetree()` returns a MonoBehaviour's fields without the game assemblies.
- `websocat` is the WebRCON client behind `start.sh`'s `quit` and the `rcon` helper above; `nix develop` has it too.

## Development and CI

`flake.nix` pins nixpkgs and git-hooks.nix in `flake.lock` (refresh with `nix flake update`). Once `nix develop` has
installed them, the hooks run on every commit: shellcheck, actionlint, check-json (the Carbon configs must parse),
nixfmt, gitleaks on the staged content, `forbidden-paths`, which refuses secrets, saves, databases, logs,
`home/`, `steamcmd/`, and `backups/` even when force-added, and `building-skins-owned-skip` (see Carbon). Keep
`guardedPathPatterns` in `flake.nix` in step with the secret files named in this document.

Flake commands read this checkout through the default git fetch, which copies only tracked files, so a new file
needs at least `git add -N` before Nix sees it. Never pass `path:.` (`nix develop path:.`, `nix flake check path:.`)
in a checkout that holds the server install: it copies the untracked install and its secret files into the
world-readable Nix store.

- `CI` (`.github/workflows/ci.yml`) runs on pushes to `main`, pull requests, and manual dispatch: `nix flake check`,
  then gitleaks over the full history, which catches a secret that a later commit removed.
- `Mods` (`.github/workflows/mods.yml`) runs `scripts/check-mods.sh` when `harmony/`, `server/carbon/plugins/`,
  `server/carbon/config.json`, the script, or the workflow changes, and every Friday at 06:00 UTC to catch a
  Thursday Rust update or a Carbon edge build that breaks the mods. A failed scheduled run notifies the account
  that last changed its `cron` line.
- Actions are pinned to full commit SHAs with a version comment. Dependabot opens weekly update PRs labeled
  `Dependencies` and `CI`.
- Rulesets and branch protection need GitHub Pro on a private repo (the API answers HTTP 403), so CI reports
  failures but blocks nothing.

## Validating changes

Run `nix develop -c pre-commit run --all-files`, and `scripts/check-mods.sh` after changing C# code or Carbon's
compiler settings. For runtime behavior, run `SKIP_UPDATE=1 ./start.sh`, wait for
`Server startup complete`, press Ctrl+C, and confirm the final
`Server stopped (exit 137). Last save this run: Saved ...` line. This runs the real world with autosaves every
300 s, so back up `server/server/pve/` first when testing the shutdown path.

To test server-side behavior without touching the `pve` world, launch `steam-run bash ./carbon.sh` (or
`./RustDedicated` for vanilla) from `server/` as `start.sh` does, but with a throwaway `+server.identity`,
`+server.worldsize 1000`, and other ports. It can run next to the live server. Copy any needed `cfg/server.cfg`
into the throwaway identity first, and `rip` its `server/server/<identity>/` afterwards. That run also uploads its
map to Facepunch and writes into the shared `server/carbon/`. `steam-run` mounts a private tmpfs on `/tmp`, so a
`-logfile` under `/tmp` is readable only through `/proc/<pid>/fd/`. A background `setsid` from a shell with job
control forks, so `$!` is not the server PID; use `pgrep -f RustDedicated`.
