#!/usr/bin/env bash
# Local Rust server with EAC off, for the Proton client launched as RustClient.exe.
# Join from the game: F1 console, then: client.connect 127.0.0.1:28015
# Ctrl+C sends "quit" over RCON, which saves the world before exiting.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
server="$root/server"
log="$root/server.log"
rcon_port=28016

# Steam auto-updates the client, and the client refuses servers on an older protocol.
# HOME is redirected so steamcmd never touches ~/.steam of the running Steam client.
if [[ "${SKIP_UPDATE:-0}" != 1 ]]; then
  HOME="$root/home" steam-run "$root/steamcmd/steamcmd.sh" \
    +force_install_dir "$server" +login anonymous +app_update 258550 +quit
fi

# Harmony mod that makes Mono's sockets IPv4-only on this ipv6.disable=1 host; see harmony/Ipv4Only.cs.
mod_src="$root/harmony/Ipv4Only.cs"
mod_dll="$server/HarmonyMods/Ipv4Only.dll"
if [[ ! "$mod_dll" -nt "$mod_src" ]]; then
  managed="$server/RustDedicated_Data/Managed"
  mkdir -p "$server/HarmonyMods"
  nix shell nixpkgs#mono -c mcs -target:library -nostdlib -noconfig -lib:"$managed" \
    -r:mscorlib.dll -r:netstandard.dll -r:System.dll -r:Rust.Harmony.dll -r:UnityEngine.CoreModule.dll \
    -out:"$mod_dll" "$mod_src"
fi

# Carbon edge build; it self-updates from the same release tag on every boot. CARBON=0 starts vanilla.
# carbon.sh sources carbon/tools/environment.sh, which preloads libdoorstop.so and sets its own LD_LIBRARY_PATH.
if [[ "${CARBON:-1}" == 1 ]]; then
  if [[ ! -f "$server/carbon.sh" ]]; then
    curl -fsSL https://github.com/CarbonCommunity/Carbon/releases/download/edge_build/Carbon.Linux.Debug.tar.gz |
      tar -xz -C "$server"
  fi
  launch=(bash ./carbon.sh)
else
  launch=(./RustDedicated)
fi

pwfile="$root/.rcon-password"
if [[ ! -s "$pwfile" ]]; then
  (umask 077 && od -An -tx1 -N16 /dev/urandom | tr -d ' \n' >"$pwfile")
fi
rcon_password="$(<"$pwfile")"

if [[ -f "$log" ]]; then
  mv -f "$log" "$log.prev"
fi

cd "$server"
export LD_LIBRARY_PATH="$server/RustDedicated_Data/Plugins:$server/RustDedicated_Data/Plugins/x86_64${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"

# RustDedicated has no signal handler, so a terminal Ctrl+C would kill it without saving.
# setsid keeps it out of the terminal's process group; stop() shuts it down through RCON.
# app.port -1 lives in server/pve/cfg/server.cfg because launch arguments cannot carry negative values.
setsid env HOME="$root/home" steam-run "${launch[@]}" -batchmode -insecure \
  +server.identity pve \
  +server.hostname "Local PvE" \
  +server.port 28015 \
  +server.level "Procedural Map" \
  +server.seed 783331605 \
  +server.worldsize 3500 \
  +server.maxplayers 4 \
  +server.encryption 0 \
  +server.saveinterval 300 \
  +rcon.ip 127.0.0.1 +rcon.port "$rcon_port" +rcon.web 1 +rcon.password "$rcon_password" \
  -logfile "$log" </dev/null &
server_pid=$!

tail -n +1 -F "$log" 2>/dev/null &
tail_pid=$!

stop() {
  trap - INT TERM HUP
  # quit saves synchronously, and during startup that writes the half-loaded world over the .sav.
  if ! grep -qsF 'Server startup complete' "$log"; then
    echo "Server is still starting, killing it. The last save on disk is unchanged." >&2
    kill -TERM -- -"$server_pid"
    return
  fi
  if ! printf '{"Identifier":1,"Message":"quit","Name":"WebRcon"}\n' |
    timeout 10 websocat -u -1 "ws://127.0.0.1:$rcon_port/$rcon_password" >/dev/null; then
    echo "RCON quit failed, killing the server. Progress since the last autosave is lost." >&2
    kill -TERM -- -"$server_pid"
  fi
}
trap stop INT TERM HUP

status=0
while kill -0 "$server_pid" 2>/dev/null; do
  wait "$server_pid" && status=0 || status=$?
done
if kill -0 "$tail_pid" 2>/dev/null; then
  kill "$tail_pid"
fi
last_save=""
if [[ -f "$log" ]]; then
  last_save="$(awk '/^Saved [0-9,]+ ents/ { s = $0 } END { print s }' "$log")"
fi
# quit ends with Process.Kill() on itself, so 137 follows a clean save.
echo "Server stopped (exit $status).${last_save:+ Last save this run: $last_save}"
