#!/usr/bin/env bash
# Converts a monument survey (see placements.json contract below) into a Monument Addons profile that places
# assets/bundled/prefabs/static/recycler_static.prefab at every surveyed monument that has a non-null placement.
#
# Survey contract (produced by MonumentSurvey.cs's survey.solve console command; see ../monument-addons's sibling
# MonumentSurvey.cs and docs/monument-survey.md):
#   {"monuments":[{"prefab":"...","shortName":"gas_station_1","displayName":"...",
#     "instances":3,"hasVanillaRecycler":true,
#     "placement":{"position":[x,y,z],"rotation":[x,y,z]} or null,"notes":"..."}]}
# Position/rotation are local to MonumentInfo.transform (rotation as Euler degrees). This is
# already the frame Monument Addons stores profile data in for ordinary (MonumentInfo-based)
# monuments: Monument Finder's NormalMonumentAdapter (MonumentFinder.cs) captures Position/
# Rotation once from the monument's own Transform and implements TransformPoint/
# InverseTransformPoint as Position + Rotation * local / Quaternion.Inverse(Rotation) *
# (world - Position), which is exactly transform.TransformPoint/InverseTransformPoint for an
# unscaled transform. No frame correction is applied here. This does NOT hold for cargo ship,
# train tunnels or underwater labs (see report); the survey's schema (a "prefab" field per
# entry, one placement per monument) only covers ordinary MonumentInfo monuments, so those
# cases are out of scope for this script.
#
# Only entries with a non-null "placement" become profile entities. An entry with a non-null
# placement AND hasVanillaRecycler=true is treated as a contract violation (the survey is
# expected to only place recyclers where none already exist) and is skipped with a warning
# instead of silently duplicating a vanilla recycler.
#
# Monument Addons profile schema used here (MonumentAddons.cs, WhiteThunder, 0.21.4):
#   Profile:      Name, SchemaVersion, MonumentData (dict keyed by monument shortName/alias)
#   MonumentData: Entities (list of EntityData)
#   EntityData:   Id (Guid), Position {x,y,z}, RotationAngles {x,y,z}, PrefabName
# The monument key is the survey's shortName as-is: ordinary monuments (NormalMonumentAdapter)
# never get an Alias assigned by Monument Finder or Monument Addons, so no renaming/aliasing is
# applied here.
#
# Usage: make-profile.sh <survey.json> <output.json> [profile-name]
set -euo pipefail

usage() {
  echo "Usage: $(basename "$0") <survey.json> <output.json> [profile-name]" >&2
  exit 1
}

if [[ $# -lt 2 || $# -gt 3 ]]; then
  usage
fi

survey_file="$1"
output_file="$2"
profile_name="${3:-Recyclers}"
recycler_prefab="assets/bundled/prefabs/static/recycler_static.prefab"

if ! command -v jq >/dev/null 2>&1; then
  echo "jq is required on PATH (nix develop provides it)" >&2
  exit 1
fi

if [[ ! -f "$survey_file" ]]; then
  echo "survey file not found: $survey_file" >&2
  exit 1
fi

uuid() {
  if [[ ! -r /proc/sys/kernel/random/uuid ]]; then
    echo "cannot read /proc/sys/kernel/random/uuid to generate an entity Id" >&2
    exit 1
  fi
  cat /proc/sys/kernel/random/uuid
}

# Fail loudly, not silently, on a malformed survey file rather than emitting a profile with
# null/short-array positions that would fail to deserialize (or silently zero out) in Carbon.
jq -e '
  if (.monuments | type) != "array" then
    error("input is missing a top-level \"monuments\" array")
  else . end
  | [
      .monuments[]
      | select(.placement != null)
      | select(
          (.shortName | type) != "string"
          or (.shortName | length) == 0
          or (.placement.position | type) != "array"
          or (.placement.position | length) != 3
          or (.placement.position | map(type == "number") | all | not)
        )
      | (.shortName // "<missing shortName>")
    ] as $bad
    | if ($bad | length) > 0 then
        error("invalid placement entries (bad shortName or position): " + ($bad | join(", ")))
      else true end
' "$survey_file" >/dev/null

eligible_count="$(jq '
  [.monuments[] | select(.placement != null and ((.hasVanillaRecycler // false) | not))] | length
' "$survey_file")"

skipped_vanilla="$(jq '
  [.monuments[] | select(.placement != null and (.hasVanillaRecycler // false))] | length
' "$survey_file")"

if [[ "$skipped_vanilla" -gt 0 ]]; then
  jq -r '
    .monuments[]
    | select(.placement != null and (.hasVanillaRecycler // false))
    | "warning: \(.shortName) already has hasVanillaRecycler=true but a non-null placement; skipping"
  ' "$survey_file" >&2
fi

ids_json="[]"
if [[ "$eligible_count" -gt 0 ]]; then
  ids=()
  for ((i = 0; i < eligible_count; i++)); do
    ids+=("\"$(uuid)\"")
  done
  ids_json="[$(
    IFS=,
    echo "${ids[*]}"
  )]"
fi

jq --arg name "$profile_name" --arg prefab "$recycler_prefab" --argjson ids "$ids_json" '
  [
    .monuments[]
    | select(.placement != null and ((.hasVanillaRecycler // false) | not))
  ] as $eligible
  | {
      Name: $name,
      SchemaVersion: 2.0,
      MonumentData: (
        reduce range(0; $eligible | length) as $i
          ({};
            $eligible[$i] as $m
            | .[$m.shortName].Entities += [{
                Id: $ids[$i],
                Position: {
                  x: $m.placement.position[0],
                  y: $m.placement.position[1],
                  z: $m.placement.position[2]
                },
                RotationAngles: {
                  x: ($m.placement.rotation[0] // 0),
                  y: ($m.placement.rotation[1] // 0),
                  z: ($m.placement.rotation[2] // 0)
                },
                PrefabName: $prefab
              }]
          )
      )
    }
' "$survey_file" >"$output_file"

echo "wrote $eligible_count entities to $output_file (skipped $skipped_vanilla vanilla-recycler entries)"
