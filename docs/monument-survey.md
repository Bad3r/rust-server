# Monument survey and recycler placement

How `server/carbon/data/MonumentAddons/Default.json` (commit `cbfef8e`) was produced, and how to redo it after a
map wipe or for a monument the current profile does not cover. Verified against Rust buildid 25454815, Carbon
edge `Carbon.Common 2.1.1278.0`, uMod Monument Addons 0.21.4 and Monument Finder 3.1.5, on 2026-09-24.

Two tools:

- `scripts/monument-addons/MonumentSurvey.cs`: a Carbon/Oxide plugin that searches for a clear, spawn-verified
  recycler placement at every monument type missing one, and dumps the result as JSON. It is a maintenance tool,
  not a shipped plugin; see the header comment in the file for why it must not sit in `server/carbon/plugins/`
  outside an active survey session, and how to compile-check a change to it.
- `scripts/monument-addons/make-profile.sh`: converts that JSON into a Monument Addons profile. See its header
  comment for the exact survey-JSON contract and the Monument Addons profile schema it targets.

## Workflow

1. Copy `MonumentSurvey.cs` into `server/carbon/plugins/`; Carbon hot-loads it.
2. Over RCON, run `survey.solve` (optionally `survey.solve <substring>` to scope to one monument). It writes
   `server/carbon/data/MonumentSurvey/placements.json` and replies with a solved count.
3. Run `scripts/monument-addons/make-profile.sh placements.json out.json <ProfileName>`.
4. For the Underwater Lab (see Lab modules below), run `survey.labscan` to list single-occurrence module rooms,
   then `survey.labsolve <moduleShortName>` for the room that should get a recycler. Splice its `placement` into
   `out.json`'s `MonumentData` by hand, keyed by that module's shortName, with a fresh `Id` (any GUID) and
   `PrefabName: assets/bundled/prefabs/static/recycler_static.prefab`: `make-profile.sh` does not do this step.
5. Copy `out.json` into `server/carbon/data/MonumentAddons/<ProfileName>.json`, select it in game (or make it
   the default profile), and verify with `mashow` and a recycler count check over RCON.
6. Remove `MonumentSurvey.cs` from `server/carbon/plugins/` before leaving the survey session: it registers
   unauthenticated `survey.*` console commands that have no reason to exist during normal operation.

## Console commands

All write to `server/carbon/data/MonumentSurvey/`, creating the directory if needed.

| Command                  | Output            | Purpose                                                                          |
| ------------------------ | ----------------- | -------------------------------------------------------------------------------- |
| `survey.dump`            | `dump.json`       | every monument and recycler on the map, with recycler-to-monument assignment     |
| `survey.solve [filter]`  | `placements.json` | one placement search per in-scope monument type missing a recycler               |
| `survey.labdump`         | `labdump.json`    | every `DungeonBaseLink` (lab module) with its transform and per-shortName counts |
| `survey.labscan`         | `labscan.json`    | quick best-candidate check for every single-occurrence lab module, no spawn      |
| `survey.labsolve <name>` | `labsolve.json`   | full spawn-verified placement search for one lab module                          |
| `survey.labprobe <name>` | `labprobe.json`   | raw `RaycastAll` dump through one module, for calibrating probe ranges           |

## Two physics bugs it hit

1. **Underwater Lab rooms are roofed.** A sky-down raycast (the normal outdoor search) always stops on the lab's
   hull and never reaches an interior floor. `FindIndoorAnchorHits` works around this by probing a short ray
   from just above entities the game itself already placed inside the monument's bounds (loot containers, NPCs,
   props), on the theory that anything the game placed is already resting on a real floor.
2. **A moonpool's wet deck occludes the dry floor underneath.** A single `Physics.Raycast` stops at the first
   collider hit, which in a moonpool room is the wet pool/deck mesh a couple of metres above the actual dry
   floor. `ProbeModuleFloor` uses `Physics.RaycastAll` and walks past every wet or non-flat hit (confirmed with
   `survey.labprobe`: the dry floor collider, "PrimitiveColliders", sits right behind the first wet hit).

## Placement search heuristic

Outdoor (ordinary `MonumentInfo`) search: a radial grid in monument-local space, ring spacing 3 m out to
`clamp(monument extent * 1.15, 12, 70)` m, `clamp(round(radius / 2), 8, 24)` points per ring. Each ground hit is
scored (higher is better):

```
score = -distanceFromCenter - 25*onRoadRail - 4*indoors - 1000*notClear - 15*frontBlocked
        - 0.5*abs(hitY - medianY) + 10*(normalY - 0.97)
```

The `1000*notClear` term dominates everything else, so the ranking is really "clear placements ordered by
distance and terrain quality, with road/rail and indoor spots pushed to the back". Yaw snaps to the nearest 90
degrees facing the monument centre. Lab module search (`SearchModuleCandidates`) uses the same score shape
without the road/indoor terms, a much smaller radius (0.5 to 10 m around the module's own transform), and three
margin fallbacks in order (0.3 m, 0.15 m, 0.08 m) for tight interiors.

No candidate is trusted from scoring alone: `VerifyAtAllInstances` (or `VerifyModuleCandidate` for a lab module)
spawns a real `recycler_static` at the candidate's local offset applied to every instance of that monument type,
checks it for collider overlap (`Physics.OverlapBox`) and a resting gap under 10 cm against the ground, then
kills the test spawn. A placement is "verified clean" only if every instance passes; the survey falls back to the
best-effort candidate (most instances clean) if none is perfect.

## Monument Addons profile schema and aliasing facts

- Profile shape: `{Name, SchemaVersion, MonumentData: {<key>: {Entities: [{Id, Position, RotationAngles,
PrefabName}]}}}`. Position and rotation are local to the monument's own transform (Euler degrees), matching
  `Transform.TransformPoint`/`InverseTransformPoint` for the unscaled case.
- The `MonumentData` key is the monument's prefab shortName (`Path.GetFileNameWithoutExtension`) for an ordinary
  `MonumentInfo` monument. Monument Finder's `NormalMonumentAdapter` never assigns an Alias to these, so no
  renaming applies and `make-profile.sh` uses the survey's `shortName` field as-is.
- Monument Finder renamed the oil rigs to `oilrig_1`/`oilrig_2` in a 2024 fix for a Facepunch rename. WhiteThunder's
  own bundled example profile for Monument Addons (`MonumentsRecycler.json`, shipped with the plugin, not vendored
  in this repo) still keys them `OilrigAI`/`OilrigAI2`, which matches nothing on a current install. Do not copy
  entity data from that example without renaming the key.
- The Underwater Lab is not addressable as one ordinary monument for placement purposes: its interior is built
  from `DungeonBaseLink` modules per map, so an offset from the lab root (`underwater_lab_c`, which `survey.solve`
  also produces a placement for) lands in a wall or water on another layout. The entry is keyed by the room's
  module shortName instead, relative to the module's own transform, so it holds wherever that room appears; the
  module prefab is shared by lab variants a to d. On another map the room may be missing from a lab or appear
  twice, giving that lab no recycler or two, so re-check the module counts with `survey.labdump` after a wipe.
- `make-profile.sh` only handles entries `survey.solve` produces for ordinary `MonumentInfo` monuments; it does
  not handle the cargo ship, train tunnels, or lab modules (see its header comment).

## Map-specific provenance

The raw survey output behind the current `Default.json` (seed 783331605, worldsize 3500, buildid 25454815) is
kept untracked under `research/monument-addons/survey-783331605/` on the host that ran it, so a fresh clone lacks
it. Treat it as a worked example of the JSON shapes above. The profile's monument-relative positions carry over to
a new map, but after a map wipe or a Facepunch force-wipe update, re-run `survey.solve` to cover monument types this
map lacked and to re-verify every placement against the new terrain.
