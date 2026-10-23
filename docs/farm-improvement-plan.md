# Farm improvement plan

## Goal

Make Pandora Farm understandable during a visit, give its lighting a daily rhythm, and preserve the existing
unattended care and production loop. Layout changes use the plugin's existing upgrade planner and preserve stored
items, animal records, and the configured wire routes.

## Phase 1: guidance and daily lighting

Implemented and applied on 2026-10-23. The live layout contains 394 entries and 30 connections. The nine new signs are
painted and locked; daytime and simulated nighttime farm-light states were checked with refrigeration powered in
both states, and the normal schedule was restored. Independent review and focused validation are recorded in
[livestock-farm.md](livestock-farm.md#verification). Visual placement and chat presentation need a game-client check.

1. Add an entrance guide and readable labels for barn storage groups. Reuse the existing sign painting, ownership,
   image tagging, and repair code. Keep labels clear of doors and equipment.
2. Add a compact, read-only `/farm` summary showing actual herd, water, refrigeration, and storage conditions, with
   actionable explanations. Keep administrative commands and detailed diagnostics separate. Describe existing public
   access accurately.
3. Put the barn lights on a dedicated electrical switch, with configurable sunrise and sunset times. Keep the
   refrigerator, irrigation pump, and heaters on their existing supply branches.
4. Replace selected static lighting props with controllable equivalents through an explicit planned layout upgrade.
   Scope automatic light control to farm fixtures and coordinate with Night Lantern. Keep the hearth and candles as
   interior decoration.
5. Extend the existing status output and documentation to explain lighting state, migration requirements, and the
   player-facing summary.

Research and implementation are delegated to separate lighting and usability agents. The supervising agent integrates
their changes. An independent agent reviews the combined implementation and migration before deployment.

### Acceptance criteria

- Existing wire routing and unrelated local edits survive integration.
- Signs have reproducible images, bounded repair work, and the existing per-sign storage tagging.
- The summary reports missing equipment and full storage without presenting invented production measurements.
- Only designated farm lights follow the schedule; refrigerator and irrigation power remain available during daylight.
- Sunrise and sunset settings are validated, including schedules crossing midnight.
- A reload does not delete or replace standing fixtures; layout migration remains an explicit upgrade operation.
- The default layout and tracked configuration agree, and upgrade planning reports no unresolved placement problems.
- Required repository checks and compilation are attempted; live behavior and client visual checks are reported
  separately from static validation.

## Phase 2: assisted farming

Implemented and applied on 2026-10-23.

Implementation reserves milking and shearing for players and designates the heated east and south scarecrow beds as
player-managed. Feeding, watering, soil care, and fertilizer remain automatic. A configurable 60-minute absence grace
resumes milk, wool, and ripe-crop collection while leaving player planting choices and native crop lifecycles intact.
The last visit persists across reloads. Automatic culling remains explicitly enabled in the tracked profile and can be
disabled with a corresponding breeding hold. The previous fully automatic profile remains selectable, and on
2026-10-23 the tracked profile returned to it, so the farm runs automated even while a player is present.

The accompanying bee fix initializes a missing starter colony once per physical hive and reports native production
conditions. Honeycomb production and extraction remain native. Runtime validation is recorded in the farm guide.

## Phase 3: access and shared produce

Implemented and applied on 2026-10-23.

The confirmed policy keeps storage, livestock, and equipment open to every player. Helpers are the owner's Rust team,
which the farm cupboard authorizes for building privilege, with a chat line on each change. A four-stall market by the
barn entrance (dairy, wool, crops, meat) is refilled every pass with surplus above the farm's feed and hatching
reserves. `/farm`, `farm.status`, and the entrance guide describe access and the market.

## Phase 4: purposeful outdoor spaces

Improve the entrance path, add a sheltered work area, and organize tools and compost. Survey placement, gate and door
clearance, coop sunlight, wiring, grazing area, and animal navigation before changing the layout.

## Phase 5: maintenance and validation

Separate layout recipes, upkeep policies, presentation, and migration responsibilities within the plugin's supported
deployment model. Develop isolated validation for routes, schema changes, migration preservation, and day/night
transitions. Keep a compact health report and document recovery procedures. A throwaway server must use an isolated
Carbon root and must not share mutable managed assemblies with an updating live instance.
