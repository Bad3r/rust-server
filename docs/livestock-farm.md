# Livestock farm

`server/carbon/plugins/LivestockFarm.cs` is a local Carbon plugin that builds one farm per server identity and keeps
it running without player upkeep: cattle and sheep in a fenced pen, two chicken coops, four planters watered by a
solar pump, a composter, and a powered fridge. The crops feed the animals, the animals' dung fertilizes the crops,
rain and the river supply the water, and the plugin creates feed or water only when a stock runs low, logging every
such top-up. It was written and tested on Rust buildid 25797215 with Carbon 2.1.1287 and runs on the 25823813 hotfix,
which kept the save protocol; [livestock.md](livestock.md) covers the game mechanics it builds on.

## Site and layout

The default layout sits on the E6 river meadow of Pandora (seed 1739235182, worldsize 4500), pen centre
(-1542.9, 1227.3), about 2.5 m above sea level. A survey of the map's own height, topology, biome, and water layers,
its rock and cliff colliders, and the trees and bushes of the world save chose it for open grass, gentle slope, the
river beside it, at least 50 m to roads and rails, and at least 150 m to monuments. Every entry was then checked
against those layers: ground contact for each prefab's area checks, slope limits, rocks and trees under fences, the
coops' and panels' line to the sun over a year, and the pump's water depth and type.

| Part | Count | Placement |
| --- | --- | --- |
| Animal fence and gate | 63 and 1 | an 18 by 14 segment pen, 52.2 by 40.6 m, gate on the east side |
| Foundation with tool cupboard | 1 | the pen centre; the cupboard is the animals' home |
| Hitch & Trough | 2 | inside the pen, 13 m either side of the centre; no grass is more than 23.8 m from one |
| Small water catcher | 2 | beside each trough |
| Large wooden box | 7 | a row north-east of the gate: milk, wool, eggs, dung, meat, feed, clones |
| Composter, fridge, small battery | 1 each | a second row beside the boxes, fridge and battery on a foundation |
| Chicken coop | 2 | east of the boxes |
| Large planter with a sprinkler | 4 | on the river bank north of the yard |
| Water pump | 1 | in the river, 0.66 to 0.86 m deep, where the water is fresh |
| Large solar panel | 2 | one powers the pump, the other charges the battery that runs the fridge |

On the carved navmesh of the test build, 519 of the pen's 520 cells of 2 m were reachable (the other lies under a
catcher), as were all 24 standing spots at the troughs, while none of 70 points outside was reachable with the gate
closed and all were with it open.

The pen holds 2,086 m2 of grazing grass. A grazed patch regrows after 30 minutes
(`livestock.overgrazedareacooldownminutes`), and 8 adults stripped a 404 m2 test pen in about 8 minutes, which puts
the need at 150 to 250 m2 per grazing adult: this pen feeds 8 to 14 of the 14 animals the caps allow, and the
troughs carry the rest.

The fridge's area checks see only building blocks and large vehicles, never terrain, so a player could not set it
on bare ground and the first physics event nearby would destroy it there. It and the battery stand on a stone
foundation of their own (`platform-01`), whose top sits at the highest ground under it.

The wiring is two circuits. Solar panel 1 feeds the pump, the pump feeds sprinkler 1, and each sprinkler's
passthrough feeds the next. The pump draws 5 power, which the panel gave from about 07:00 to 16:00 in-game in the
test, and makes 85 ml every 10 s, against the 8 ml/s four running sprinklers drain. Solar panel 2 charges the small
battery (24,000 rW s), which runs the fridge (5 power) around the clock: in the worst month the panel delivers 40%
more than the fridge uses, and the battery is full again about 20 minutes after sunrise.

## Building it

`farm.build` (RCON, the server console, or F1 at auth level 2) places the default layout. `farm.build <x> <z> [yaw]`
places it at another centre and rotation, without the terrain checks the default spot had. The build:

- refuses while the identity already has a farm, and takes over a matching standing entity (same prefab, within
  0.5 m, owned by the farm owner or nobody) instead of placing a second one;
- kills bushes, trees, and resource piles whose colliders overlap a footprint, plus 0.5 m;
- places the foundation (stone, top at the highest ground under it), the cupboard on it with the owner authorized,
  the fences and gate rolled to the terrain along their length, and every other deployable aligned to the terrain;
- gives every entity the owner's SteamID, so Auto Doors closes the gate 5 s after it opens;
- wires both circuits, puts one GGYYYY clone per planter slot plus a reserve into the clone box, and 15 s later
  spawns the starter herd 11 m north of the pen centre: 2 cows, a bull, and a female calf; 3 ewes, a ram, and a
  male lamb.

The owner is `Owner SteamID` in the config or, at 0, the first owner in `cfg/users.cfg`, recorded at build time.

## Upkeep

Every 30 s the plugin runs these steps in order, each entity in its own guard, so one failure skips only that entity:

1. Structures: re-place any missing layout entry (`Auto repair missing structures`), re-authorize the owner on the
   cupboard, and close the gate when no player is within 10 m. Fences and the gate come back on every pass. Other
   entries wait 10 minutes after a failed placement, or after a loss within 10 minutes of their last repair, and the
   wait doubles up to 2 hours each time that repeats. Any entry waits while a player or an animal stands on its spot.
2. Wiring: re-make any missing link, except a pump link when the pump's water is not fresh.
3. Sweep: dropped dung and eggs within 50 m go into their boxes.
4. Herd: full trust (900 s) for the owner, the farm cupboard as home, milking and shearing straight into storage,
   and the needs net: fullness, hydration, or personal space below 0.5 is set to 0.8 and logged. A wild animal
   standing inside the pen joins the herd while its species is under the cap. Wild means no player has ever bonded
   it (`DecayStarted` false, which the game sets for good once an animal can be led), so an animal bought from the
   Livestock Vendor or tamed by a player never joins, even after its trust fades. A farm animal outside the fence
   for 5 minutes and not on a lead or a vehicle goes back to the pen, from any distance, through the same move the
   game uses to unload an animal from a vehicle. One a player leads or drives out past 50 m from the centre leaves
   the farm for good, and the plugin never touches it again.
5. Sex safety: a species with no male or no female gets a juvenile of that sex, and a species with no animals gets
   its starter herd again, each at most every 10 minutes, doubling up to 2 hours while the reseeded animals die or
   when a release left the gap.
6. Breeding: a species' mating is held while births could take it 2 past its cap, or while its due cull cannot run
   (Herd). Otherwise, after 60 minutes without a new pregnancy while under the cap, an eligible female is made
   pregnant by an eligible male, retried every 10 minutes.
7. Crops: hold soil water at the planter's ideal 60%, fertilize, harvest ripe plants, take clones from saplings while
   the clone stock is short, clear dying plants, and replant empty slots.
8. Compost: dung into the composter, fertilizer out to the dung box.
9. Chickens: eggs out, feed, water, and petting; a coop short of chickens hatches a stored egg.
10. Troughs: feed and water.
11. Spoiled produce: to the coops first (chickens eat it), then the composter.
12. Cull: above the cap, one surplus adult is killed and butchered into storage; juveniles are never culled.

## Feed, water, and fertilizer

| Stock | Target | Created below | Sources, in order |
| --- | --- | --- | --- |
| Trough feed (Food items) | 120 | 20 | spoiling produce, oldest first; fresh produce; wheat |
| Trough water (ml) | 5,000 | 1,000 | the catchers; the pump, below the minimum only; created water |
| Coop feed (items) | 40 | 5 | spoiled produce, then the same as the troughs |
| Coop water (ml) | 1,000 | 200 | the catchers; the pump, below the minimum only; created water |

The pump keeps at least 500 ml for the sprinklers, which go dry when it empties. With four sprinklers running its
store stays around 0 to 250 ml in daylight, so in practice the troughs and coops drink rain from the catchers and
created water.

Created feed is wheat (`Fallback feed shortname`), which never spoils. A cow or sheep living off a trough eats up to
14 potatoes an hour of daylight. Even with no gene bonus, the four planters give about 10,000 calories an hour, 20,000
with the server's 2x gather, against about 12,300 for 6 cattle and 8 sheep with no grass at all.

Crops follow `Planter slot plan`: planters 1 and 2 grow potatoes, planter 3 corn and pumpkins in alternate slots,
planter 4 wheat. With the GGYYYY genes and the 2x, a plant at full quality gave about 41 potatoes, 20 corn, 20
wheat, or 10 pumpkins in the test, where plants grew 30 times faster and the feed box filled in 15 to 25 minutes,
about 8 to 13 hours of normal uptime. While it is full, ripe crops wait in their slots, die after 24 hours, and are
replanted.

Planted crops never give seeds, so every replant comes from clones. A plant reaching the sapling stage is cloned
while the clone box holds fewer of that crop than its slots, and the clones keep its genes. A crop with no clones
left and a slot empty for 10 minutes gets fresh GGYYYY clones, logged. Harvests go through the `OnGrowableGathered`
hook, so Carbon's GatherManager gives them its 2x.

Sprinklers would keep the soil at 100%, which costs up to 29% of growth and yield, so the plugin holds it at the
planter's ideal 60% and fills new planters to that level. A planter under 25% for 10 minutes without rising, as when
the pump or a sprinkler fails, is filled to 60% directly, and the log names what it found, such as an unpowered pump
or a missing link.

The composter turns one dung into 10 fertilizer every 60 s, and the plugin feeds it only while the dung box has room
for what it will make, since a full composter drops its output on the ground. Each plant takes one fertilizer.

## Herd

`Cattle cap` 6 and `Sheep cap` 8 count adults and juveniles, except a juvenile that sex safety spawned, which counts
only once it grows up. Above a cap the plugin culls one adult per pass: while the heads exceed the cap, an adult male
beyond one, oldest first; while the adults alone exceed it, the oldest open adult female. It never culls a pregnant
female, a ewe that has just lambed, the last adult of a sex, or a juvenile: the `calf`, `calfmale`, and `lamb` corpses
carry an empty `ResourceDispenser`, so butchering one yields nothing. A species over its cap only by its juveniles
waits for them to grow up. It kills the animal the way `DieOffNavmesh` does, waits for the corpse, and butchers it
through the `OnDispenserGather` and `OnDispenserBonus` hooks, so GatherManager's 2x applies: a cow gives 40 beef, 200
bone fragments, 80 animal fat, 200 leather, and 60 cloth; a sheep 24 mutton, 100 bone fragments, 40 fat, 40 leather,
and 20 cloth, plus twice any wool it carried. A cull waits until storage has room for the whole yield.

Births can take a species past its cap while its young grow up, so the plugin holds a species' mating while its heads
plus the young its pregnancies can bear (1 each, plus the mother's Fertility gene scale above 1, rounded up, at most
`livestock.maxlittersize`) reach its cap plus 2, and while a due cull cannot run because storage has no room for the
yield or no adult may be culled. A hold gives every open adult female a 90 s breeding cooldown (3 upkeep intervals),
renewed every pass, and `CanBePregnant` refuses a female on cooldown, so no mating path, vanilla or the fallback, starts
a pregnancy. A longer cooldown of the animal's own is left alone, pregnancies already begun go on, and the cooldowns
lapse within 90 s of the hold ending. The log notes when a hold starts and ends, and `farm.status` names the next cull
of a species over its cap or what it waits for, and why a species' mating is held.

Aging is untouched: juveniles grow up, and adults die of old age, which goes through `Die` directly, so the damage
block does not stop it. Neglect does not go through damage either; the needs net keeps contentment at 0.5 or more,
where a condition below breeding's 0.6 pause cannot set in.

## Products and storage

Milk, eggs, and raw meat go into the fridge, whose prefab stops spoilage while powered, and into their boxes when it
is full. Wool, dung, leather, fat, bones, and cloth go into their boxes. The plugin never drops an item: when the
target container is full it skips the action, so a ripe crop stays unharvested and milk or wool stays on the animal,
and logs that at most once an hour per container. Vanilla still destroys a coop's eggs when its egg slot is full.

## Protection

`OnEntityTakeDamage` cancels damage to farm entities and their children (chickens, plants, trough water ends) unless
a real player deals it, `OnNpcTarget` stops NPCs and predators targeting farm animals, and `OnEntityGroundMissing`
keeps farm entities standing. A player's attacks, F1 kills, old age, and neglect still go through. Farm structures
cannot be picked up with a hammer or taken down with Remover Tool's `/remove` (`CanPickupEntity`, `canRemove`, and
`CanAdminRemove`): auto-repair would put a new one in place, so the player would keep a free copy. Coop chickens'
unhappiness damage uses `Hurt`, so they cannot die of neglect.

## Commands

| Command | Effect |
| --- | --- |
| `farm.build [x z [yaw]]` | place the farm and schedule the starter herd |
| `farm.status` | herd, chickens, planters, pump, wiring, container contents, totals, missing parts, events |
| `farm.tick` | run one upkeep pass now |
| `farm.relink` | find farm animals again after something replaced them |
| `farm.repair` | respawn every missing or failed layout entry, take over matching standing entities, and rewire |
| `farm.remove confirm` | drop every container's items as loot bags, list them, and remove the farm and its animals |

Each needs RCON, the server console, or auth level 2. While the server is still loading the world, before the plugin
has checked its record against it, every command but `farm.status` refuses and says so.

## Config

`server/carbon/configs/LivestockFarm.json` holds the owner, the upkeep interval (30 s), the farm radius (50 m), the
starter herd, the caps, the breeding fallback timing, the needs threshold (0.5) and restore value (0.8), the stock
targets above, the water sources, the planter slot plan, the clone genes, `Auto repair missing structures` (true),
and the whole layout: 94 entries with farm-local positions, plus the 7 power and water links. A config that does not
parse or validate disables the plugin: it keeps the defaults in memory, never saves over the file, logs the reason
every 10 minutes, and every `farm.*` command replies with it.

## Records, wipes, and recovery

The record is `server/carbon/data/LivestockFarm/<identity>.json`, written only when the world saves and when the
plugin unloads, so it never runs ahead of the `.sav`. It stores each structure and animal by network id and prefab,
and the world's save file name and creation time. Every lookup checks the prefab and owner, so an id the server
reissued after a crash never counts as part of the farm.

- A wipe (`OnNewSave`) or a record from another world archives the record as `<identity>.stale-<time>.json` and
  starts clean; the farm has to be built again with `farm.build`. On the same seed the E6 terrain stays the same.
- A restored backup older than the build leaves a record whose cupboard and most fences no longer exist; the plugin
  archives it the same way. While the cupboard is missing the plugin never reseeds and asks for `farm.repair`.
- A corrupt record is moved to `<identity>.corrupt-<time>.json` and logged.
- Upkeep replaces a destroyed structure on its next pass, and `farm.repair` does the same at once; a foundation
  comes back before the cupboard on it, since the game removes a cupboard left without a building block. To drop a
  part of the farm for good, delete its entry from the config's layout.
- `farm.build` over a standing farm whose record was lost takes the standing parts over.

## Limits

- One farm per identity. The layout was checked at the default spot only.
- The pump runs only while its panel gives 5 power, about 07:00 to 16:00 in-game. Corn, pumpkin, and wheat grow
  below full temperature quality on this temperate site, which lowers their yield; an electric heater would fix that
  but needs power the panels do not spare.
- The south fence is 63 m from a trail and coop 2 is 60 m from a side road, inside a road Bradley's 100 m sight. The
  farm takes no damage from it, but a player there can draw its fire.
- The farm holds no SteamID in its config by default; the owner comes from `cfg/users.cfg`.

## Verification

On 2026-10-09 the plugin ran on a copy of the pve map under a throwaway identity with its own Carbon root (CLAUDE.md,
Validating changes), driven over RCON through an 18-section test plan, with a second plugin that measured the built
farm: every entry within 0.01 m of its spot and passing its own ground checks, the coops' sun and both panels' output
by hour, the water chain, the navmesh reachability above, and containment with the gate open and closed. The runs
covered restarts, a new world, a restored backup, a corrupt record, hot reloads over a running farm, and 30-minute
soaks at normal speed that logged no failure and no exception. The steps that need a connected client did not run:
releasing a led animal, the pickup and `/remove` refusals, and the gate's swing direction.
