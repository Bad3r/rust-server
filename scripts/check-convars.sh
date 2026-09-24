#!/usr/bin/env bash
# Resolves every convar in server.cfg to its ConsoleGen.cs registration, then confirms the field its SetOveride
# assigns is actually declared where ConsoleGen.cs says it is. A convar Facepunch renamed or removed still parses
# as an ordinary key=value line in server.cfg and does nothing at runtime; this catches that before a restart
# wastes one. MANAGED_DIR (a RustDedicated_Data/Managed dir) points at an install other than this repo's server/.
set -uo pipefail

if [[ -z "${CHECK_CONVARS_SHELL:-}" ]]; then
  exec nix shell --extra-experimental-features "nix-command flakes" nixpkgs#ilspycmd \
    -c env CHECK_CONVARS_SHELL=1 bash "${BASH_SOURCE[0]}" "$@"
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cfg="$root/server/server/pve/cfg/server.cfg"
managed_dir="${MANAGED_DIR:-$root/server/RustDedicated_Data/Managed}"
if [[ ! -f "$managed_dir/Assembly-CSharp.dll" ]]; then
  echo "no Assembly-CSharp.dll in $managed_dir; run ./start.sh once or set MANAGED_DIR to a" \
    "RustDedicated_Data/Managed dir" >&2
  exit 1
fi

work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT
classes_dir="$work_dir/classes"
mkdir "$classes_dir"

# Caches one decompile per type for the run: several convars share a backing class (basesubmarine.*, for one).
# -s, not -f: a failed ilspycmd leaves nothing or a truncated file, which must not read back as a cache hit.
decompile_type() {
  local type="$1"
  local out="$classes_dir/$type.cs"
  [[ -s "$out" ]] && return 0
  if ! ilspycmd --disable-updatecheck -r "$managed_dir" -t "$type" "$managed_dir/Assembly-CSharp.dll" \
    >"$out" 2>"$work_dir/ilspy.err"; then
    rm -f "$out"
    return 1
  fi
}

if ! decompile_type ConsoleGen; then
  cat "$work_dir/ilspy.err" >&2
  exit 1
fi
consolegen="$classes_dir/ConsoleGen.cs"

# A bare class name in a ConsoleGen.cs SetOveride (App.port, MotorRowboat.outsidedecayminutes) compiles against
# every one of these using directives plus the global namespace, so the same name can mean different types
# (App.port is really ConVar.App.port: some other App class wins if only the global namespace is tried).
mapfile -t using_namespaces < <(sed -nE 's/^using ([A-Za-z_][A-Za-z0-9_.]*);$/\1/p' "$consolegen")

# Tries the bare name (global namespace) first, then each using'd namespace, stopping at the first candidate
# that both decompiles and declares the field; prints which one on a WARN so a wrong pick is visible, not silent.
resolve_field() {
  local class="$1" field="$2"
  local candidates=("$class")
  local ns
  for ns in "${using_namespaces[@]}"; do
    candidates+=("$ns.$class")
  done
  local cand decl
  for cand in "${candidates[@]}"; do
    decompile_type "$cand" || continue
    # A ServerVar field with an implicit default (an int or bool with no explicit initializer, e.g.
    # ConVar.App.port) decompiles as "public static int port;", with no "=" at all, and a ServerVar property
    # (ConVar.Server.gamemode) ends its line at the name, with the accessor block on the next line.
    decl_re="(public|private|internal|protected)([[:space:]]+static)?[[:space:]]+[A-Za-z_<>]+[[:space:]]+"
    decl="$(grep -nE "${decl_re}${field}[[:space:]]*([=;{]|$)" "$classes_dir/$cand.cs" | head -1)"
    if [[ -n "$decl" ]]; then
      resolved_class="$cand"
      resolved_declline="$(cut -d: -f1 <<<"$decl")"
      return 0
    fi
  done
  return 1
}

fail=0
count=0
while read -r name value; do
  count=$((count + 1))
  matches="$(grep -n "FullName = \"${name}\"," "$consolegen")"
  nmatches="$(printf '%s\n' "$matches" | grep -c .)"
  if [[ "$nmatches" -eq 0 ]]; then
    echo "FAIL  $name -> no ConsoleGen.cs FullName entry"
    fail=1
    continue
  fi
  if [[ "$nmatches" -gt 1 ]]; then
    echo "WARN  $name -> $nmatches FullName entries (using first)"
  fi

  line="$(printf '%s\n' "$matches" | head -1 | cut -d: -f1)"
  block="$(sed -n "${line},$((line + 14))p" "$consolegen")"

  # String convars assign the raw argument ("ConVar.Server.gamemode = str;"); the rest convert it first.
  setline="$(printf '%s\n' "$block" |
    grep -m1 -E '^\s*[A-Za-z_][A-Za-z0-9_.]*\s*=\s*str(\.To[A-Za-z]+\(\))?\s*;')"
  if [[ -z "$setline" ]]; then
    echo "FAIL  $name -> no SetOveride assignment found near ConsoleGen.cs:$line"
    fail=1
    continue
  fi
  backing="$(printf '%s\n' "$setline" | sed -E 's/^[[:space:]]*([A-Za-z_][A-Za-z0-9_.]*)[[:space:]]*=.*/\1/')"
  vartype=String
  if [[ "$setline" =~ str\.To([A-Za-z]+)\(\) ]]; then
    vartype="${BASH_REMATCH[1]}"
  fi
  class="${backing%.*}"
  field="${backing##*.}"

  resolved_class=""
  resolved_declline=""
  if ! resolve_field "$class" "$field"; then
    echo "FAIL  $name -> field '$field' not declared in '$class' or any using'd namespace of it (backing ref: $backing)"
    fail=1
    continue
  fi
  if [[ "$resolved_class" != "$class" ]]; then
    echo "WARN  $name -> '$class' resolved via using directive to '$resolved_class'"
  fi
  classfile="$classes_dir/$resolved_class.cs"
  servervar="$(sed -n "$((resolved_declline - 3)),${resolved_declline}p" "$classfile" | grep -c 'ServerVar')"

  sv=NO
  if ((servervar > 0)); then
    sv=yes
  fi
  echo "OK    $name = $value -> $backing [$vartype] ($resolved_class:$resolved_declline, ServerVar=$sv)"
done < <(awk '!/^[[:space:]]*#/ && NF>=2 {print $1, $2}' "$cfg")

echo
echo "checked $count convars, fail=$fail"
exit "$fail"
