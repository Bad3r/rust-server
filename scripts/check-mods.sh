#!/usr/bin/env bash
# Compiles harmony/Ipv4Only.cs and the Carbon plugins against the public Rust build and the Carbon edge build, the
# way start.sh and Carbon's runtime compiler do, without the 6 GB server install. Exits non-zero if any compile fails.
# MANAGED_DIR (a RustDedicated_Data/Managed dir) and CARBON_DIR (a carbon/managed dir) replace the downloads, and
# PLUGINS_DIR replaces server/carbon/plugins, for tools such as scripts/monument-addons that must not live there.
set -euo pipefail

# Every tool comes from nixpkgs, so only nix, curl, and tar need to be on PATH.
if [[ -z "${CHECK_MODS_SHELL:-}" ]]; then
  # roslyn before mono: both ship bin/csc and the first listed wins on PATH. mono's is its bundled Roslyn 3.9
  # (C# 9 at most); roslyn's 4.14 csc.exe still runs on mono, which also provides mcs.
  exec nix shell --extra-experimental-features "nix-command flakes" \
    nixpkgs#depotdownloader nixpkgs#jq nixpkgs#roslyn nixpkgs#mono \
    -c env CHECK_MODS_SHELL=1 bash "${BASH_SOURCE[0]}" "$@"
fi

# mono's csc shadowing roslyn's again would fail C# 10+ plugin syntax with a bare CS1003.
csc_version="$(csc -version 2>&1)"
if [[ ! "$csc_version" =~ ^([0-9]+)\. ]] || ((BASH_REMATCH[1] < 4)); then
  echo "csc resolved to $(command -v csc), version $csc_version; need Roslyn 4+, not mono's bundled csc" >&2
  exit 1
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
plugins_dir="${PLUGINS_DIR:-$root/server/carbon/plugins}"
mapfile -t plugin_sources < <(find "$plugins_dir" -maxdepth 1 -name '*.cs' | sort)
if ((${#plugin_sources[@]} == 0)); then
  echo "no *.cs plugins in $plugins_dir" >&2
  exit 1
fi
work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT
failed=0

# Anonymous Steam logins from shared CI address ranges get dropped at times, and DepotDownloader does not retry.
fetch_depot() {
  local attempt=1
  while ! DepotDownloader "$@"; do
    if ((attempt >= 3)); then
      echo "DepotDownloader failed after $attempt attempts" >&2
      return 1
    fi
    attempt=$((attempt + 1))
    echo "DepotDownloader failed, retrying ($attempt/3)" >&2
    sleep 5
  done
}

if [[ -n "${MANAGED_DIR:-}" ]]; then
  managed_dir="$MANAGED_DIR"
  echo "Managed DLLs: $managed_dir"
  acf="$managed_dir/../../steamapps/appmanifest_258550.acf"
  if [[ -f "$acf" ]]; then
    echo "  local install buildid $(grep -m1 '"buildid"' "$acf" | grep -o '[0-9]\+')"
  fi
else
  # Probe every Linux depot's manifest, then fetch only the Managed subtree of the one that holds it.
  mkdir -p "$work_dir/probe"
  fetch_depot -app 258550 -os linux -manifest-only -dir "$work_dir/probe"
  mapfile -t manifests < <(grep -lF 'RustDedicated_Data/Managed/' "$work_dir"/probe/manifest_*.txt)
  if [[ ${#manifests[@]} -ne 1 ]]; then
    echo "expected one Linux depot with RustDedicated_Data/Managed/, found ${#manifests[@]}" >&2
    exit 1
  fi
  ids="$(basename "${manifests[0]}" .txt)"
  ids="${ids#manifest_}"
  depot_id="${ids%%_*}"
  manifest_id="${ids#*_}"
  manifest_date="$(sed -n 's/^Manifest ID \/ date *: *[0-9]* \/ *//p' "${manifests[0]}" | tr -d '\r')"
  echo "Managed DLLs: app 258550 public branch, depot $depot_id, manifest $manifest_id ($manifest_date)"
  printf 'regex:^RustDedicated_Data/Managed/.*\n' >"$work_dir/filelist.txt"
  fetch_depot -app 258550 -depot "$depot_id" -manifest "$manifest_id" -filelist "$work_dir/filelist.txt" \
    -dir "$work_dir/game"
  managed_dir="$work_dir/game/RustDedicated_Data/Managed"
fi

if [[ -n "${CARBON_DIR:-}" ]]; then
  carbon_dir="$CARBON_DIR"
else
  mkdir -p "$work_dir/carbon"
  curl -fsSL https://github.com/CarbonCommunity/Carbon/releases/download/edge_build/Carbon.Linux.Debug.tar.gz |
    tar -xz -C "$work_dir/carbon"
  carbon_dir="$work_dir/carbon/carbon/managed"
fi
carbon_lib="$carbon_dir/lib"
carbon_version="$(monodis --assembly "$carbon_dir/Carbon.Common.dll" | awk -F': *' '/^Version:/ { print $2 }')"
echo "Carbon managed: $carbon_dir, Carbon.Common $carbon_version"

# The same mcs invocation as start.sh.
if mcs -target:library -nostdlib -noconfig -lib:"$managed_dir" \
  -r:mscorlib.dll -r:netstandard.dll -r:System.dll -r:Rust.Harmony.dll -r:UnityEngine.CoreModule.dll \
  -out:"$work_dir/Ipv4Only.dll" "$root/harmony/Ipv4Only.cs"; then
  echo "OK harmony/Ipv4Only.cs"
else
  echo "FAIL harmony/Ipv4Only.cs"
  failed=1
fi

# Carbon compiles plugins with Roslyn (LanguageVersion.Preview, allowUnsafe, and "UNIX" added to the define symbols
# in its config: Carbon.Jobs.ScriptCompilationThread in Carbon.dll) against publicized copies of the listed game
# assemblies, which it rewrites in memory at boot with its bundled AsmResolver. The same library rewrites on-disk
# copies here, so plugins that touch non-public members (BaseOven.cookingTemperature, for one) compile as in game.
config="$root/server/carbon/config.json"
symbols="$(jq -r '.Compiler.ConditionalCompilationSymbols | join(",")' "$config"),UNIX"
mapfile -t publicized < <(jq -r '.Publicizer.PublicizedAssemblies[]' "$config")
jq -r '.Publicizer.PublicizerMemberIgnores[]' "$config" >"$work_dir/ignores.txt"

cat >"$work_dir/Publicizer.cs" <<'CSHARP'
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Metadata.Tables.Rows;

class Publicizer
{
    // Usage: publicizer.exe <out-dir> <ignore-regex-file> <carbon-common.dll> <in.dll>...
    static int Main(string[] args)
    {
        var ignores = File.ReadAllLines(args[1]).Where(l => l.Length > 0).Select(l => new Regex(l)).ToArray();
        Directory.CreateDirectory(args[0]);
        // Carbon.Publicizer.AssemblyCSharp.InjectIPlayer (Carbon.Startup.dll) adds this same field to BasePlayer
        // at boot, sourcing the type from Carbon.Common.dll; plugins like MonumentAddons.cs read player.IPlayer.
        var iPlayerType = ModuleDefinition.FromFile(args[2]).GetAllTypes()
            .First(t => t.Namespace == "Oxide.Core.Libraries.Covalence" && t.Name == "IPlayer");
        foreach (var inPath in args.Skip(3))
        {
            var module = ModuleDefinition.FromFile(inPath);
            foreach (var type in module.GetAllTypes())
            {
                if (ignores.Any(r => r.IsMatch(type.Name)))
                    continue;
                type.Attributes &= ~TypeAttributes.VisibilityMask;
                type.Attributes |= type.IsNested ? TypeAttributes.NestedPublic : TypeAttributes.Public;
                foreach (var method in type.Methods.Where(m => !ignores.Any(r => r.IsMatch(m.Name))))
                {
                    method.Attributes &= ~MethodAttributes.MemberAccessMask;
                    method.Attributes |= MethodAttributes.Public;
                }
                foreach (var field in type.Fields.Where(f => !ignores.Any(r => r.IsMatch(f.Name))))
                {
                    field.Attributes &= ~FieldAttributes.FieldAccessMask;
                    field.Attributes |= FieldAttributes.Public;
                }
            }
            if (Path.GetFileName(inPath) == "Assembly-CSharp.dll")
            {
                var basePlayer = module.GetAllTypes().First(t => t.Name == "BasePlayer" && !t.IsNested);
                if (!basePlayer.Fields.Any(f => f.Name == "IPlayer"))
                {
                    var imported = module.DefaultImporter.ImportType(iPlayerType);
                    var attrs = FieldAttributes.Public | FieldAttributes.NotSerialized;
                    basePlayer.Fields.Add(new FieldDefinition("IPlayer", attrs, imported.ToTypeSignature()));
                }
            }
            module.Write(Path.Combine(args[0], Path.GetFileName(inPath)));
        }
        return 0;
    }
}
CSHARP
netstandard="$(dirname "$(dirname "$(command -v mono)")")/lib/mono/4.5/Facades/netstandard.dll"
# CS1685: Carbon's System.Memory.dll redefines System.Span, which mono's mscorlib already has; mcs uses mscorlib's.
mcs -target:exe -nowarn:1685 -r:"$netstandard" \
  -r:"$carbon_lib/AsmResolver.dll" -r:"$carbon_lib/AsmResolver.PE.dll" -r:"$carbon_lib/AsmResolver.PE.File.dll" \
  -r:"$carbon_lib/AsmResolver.DotNet.dll" -r:"$carbon_lib/System.Text.Json.dll" -r:"$carbon_lib/System.Memory.dll" \
  -r:"$carbon_lib/System.Text.Encodings.Web.dll" -out:"$work_dir/publicizer.exe" "$work_dir/Publicizer.cs"
publicize_inputs=()
for name in "${publicized[@]}"; do
  publicize_inputs+=("$managed_dir/$name")
done
MONO_PATH="$carbon_lib" mono "$work_dir/publicizer.exe" "$work_dir/publicized" "$work_dir/ignores.txt" \
  "$carbon_dir/Carbon.Common.dll" "${publicize_inputs[@]}"

# Carbon's own copies of shared dependencies (0Harmony.dll, System.* polyfills) win over the game's, as in its
# compiler, and the publicized copies replace the game's originals.
declare -A seen=() is_publicized=()
for name in "${publicized[@]}"; do
  is_publicized["$name"]=1
done
refs=()
while IFS= read -r f; do
  seen["$(basename "$f")"]=1
  refs+=("$f")
done < <(find "$carbon_dir" -name '*.dll' -not -path '*/lib/x64/*' -not -path '*/lib/x86/*' | sort)
while IFS= read -r f; do
  name="$(basename "$f")"
  if [[ -n "${seen[$name]:-}" ]]; then
    continue
  fi
  seen["$name"]=1
  if [[ -n "${is_publicized[$name]:-}" ]]; then
    refs+=("$work_dir/publicized/$name")
  else
    refs+=("$f")
  fi
done < <(find "$managed_dir" -maxdepth 1 -name '*.dll' | sort)
refs_joined="$(IFS=, && echo "${refs[*]}")"

for src in "${plugin_sources[@]}"; do
  if csc -nologo -target:library -nostdlib+ -noconfig -langversion:preview -unsafe+ -define:"$symbols" \
    -reference:"$refs_joined" -out:"$work_dir/$(basename "$src" .cs).dll" "$src"; then
    echo "OK ${src#"$root"/}"
  else
    echo "FAIL ${src#"$root"/}"
    failed=1
  fi
done

exit "$failed"
