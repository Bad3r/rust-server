# Livestock

Rust buildid 25797215 (installed 2026-10-09, save protocol 289) adds farm livestock: cattle (cow, bull, and calves)
and sheep (sheep and lambs), tamed by trust, bred with genes, and kept for milk, wool, dung, and meat, with a Livestock
Vendor at the stables, animal fences, and livestock feeding at the Hitch & Trough. Chickens and the chicken coop
predate this build. Verified on 2026-10-09 from an `ilspycmd` decompile of `Assembly-CSharp.dll` (see CLAUDE.md Host
tools), the prefab and asset data in `server/Bundles/` read with UnityPy, read-only RCON queries on the live `pve`
server, and spawn tests on a throwaway vanilla server. Verification at the end separates what ran from what was read.

## Animals

| Animal | `spawn` name | Prefab | Class | Notes |
| --- | --- | --- | --- | --- |
| Cow | `cow` | `assets/rust.ai/agents/cow/cow.prefab` | `Rust.Ai.Gen2.Cow` | adult female, 450 HP, milk |
| Bull | `bull` | `assets/rust.ai/agents/bull/bull.prefab` | `Cow` | adult male, 450 HP |
| Calf | `calf` | `assets/rust.ai/agents/calf/calf.prefab` | `Cow` | female, grows into a cow |
| Male calf | `calfmale` | `assets/rust.ai/agents/calf/calfmale.prefab` | `Cow` | grows into a bull |
| Sheep | `sheep` | `assets/rust.ai/agents/sheep/sheep.prefab` | `Rust.Ai.Gen2.Sheep` | adult of either sex, 150 HP, wool |
| Lamb | `lamb` | `assets/rust.ai/agents/sheep/lamb.prefab` | `Sheep` | either sex, grows into a sheep |
| Chicken | `chicken` | `assets/rust.ai/agents/chicken/chicken.prefab` | `Rust.Ai.Gen2.Chicken` | wild, lays eggs, not saved |
| Coop chicken | none | `assets/prefabs/deployable/chickencoop/simplechicken.entity.prefab` | `FarmableAnimal` | hatches in a coop |

`Cow` and `Sheep` derive from `LivestockAnimal : BaseNPC2`, and everything below about trust, needs, breeding, and
products applies to both. `assets/rust.ai/agents/cow/cow.species.asset` ties cow, bull, calf, and male calf together;
`sheep.species.asset` uses `sheep.prefab` and `lamb.prefab` for both sexes, so a spawned sheep or lamb rolls its sex
50/50 (`livestock.swapgender` flips it). The wild chicken is not a `LivestockAnimal` and has none of these systems.

## Spawning

F1 `spawn <name>` is a client command: the client adds the point under the crosshair and the view direction, and the
server runs `entity.spawn` (`ConVar.Entity.svspawn`, admin only). The pve command history shows what the server
receives: `spawn "ridablehorse" "(-1365.20, 2.18, -1706.75)" "(-0.99, 0.00, -0.04)"`. Over RCON, pass both
explicitly: `entity.spawn cow "x,y,z" "0,0,1"`. Without the direction the reply is only Unity's warning `Look rotation
viewing vector is zero`, though the entity still spawns.

`Entity.GetSpawnEntityFromName` matches the name, ignoring case, as a substring of the file names in
`GameManifest.entities` (2408 prefabs in this build). One hit spawns it; among several, an exact file name wins;
otherwise the reply is `Unknown entity - could be:` with the candidates, and nothing spawns. `cow` therefore picks
`cow.prefab` over `cow.corpse` and `cowbell.weapon`, and every name in the tables here is exact.

| Entity | `spawn` name | Prefab | Class |
| --- | --- | --- | --- |
| Livestock Vendor | `livestockvendor_stables` | `assets/prefabs/npc/ranch/livestockvendor_stables.prefab` | `LivestockVendor` |
| Hitch & Trough | `hitchtrough.deployed` | `assets/prefabs/deployable/hitch & trough/hitchtrough.deployed.prefab` | `HitchTrough` |
| Chicken coop | `chickencoop` | `assets/prefabs/deployable/chickencoop/chickencoop.deployed.prefab` | `ChickenCoop` |
| Animal fence | `animalfence.deployed` | `assets/prefabs/deployable/animal fence/animalfence.deployed.prefab` | `AnimalFence` |
| Animal fence gate | `animalfencegate` | `assets/prefabs/deployable/animal fence/animalfencegate.deployed.prefab` | `AnimalFenceGate` |

`hitchtrough` and `animalfence` alone are ambiguous. A trough brings its water end as a child
(`hitchtrough.waterstorage.prefab`, a 5000 ml `LiquidContainer`). Deployables placed from items go through the normal
placement checks and record the player as owner, which a spawned one does not, so give items for everything except the
animals. Do not spawn `simplechicken.entity` on its own: outside a coop it cannot move, eat, or drink.

## How players get livestock

- Wild herds. `Cow.Population` spawns `cow.prefab` and `bull.prefab` in clusters of 1 to 4, and `Sheep.Population`
  spawns `sheep.prefab` in clusters of 2 to 6, each at 2 per square km (`cow.population`, `sheep.population`). The
  filter wants grass or forest ground in the temperate or tundra biome, away from cliffs, beaches, roads, rivers,
  lakes, swamps, mountains, monuments, and buildings. No population spawns calves or lambs. On Pandora `spawn.report`
  showed `Cow.Population` 38/40 and `Sheep.Population` 35/40, and `livestock.census` counted 73 adults (38 cattle, 35
  sheep). They are tamed through trust. 0.5% of population spawns (`livestock.specialchance`) become a named special.
- Livestock Vendor. A `livestockvendor_stables` NPC stands at the stables monuments (Ranch `stables_a`, Large Barn
  `stables_b`); Pandora has two, each near a horse `stables_shopkeeper`. Its conversation sells a male or female
  calf for 300 scrap and a male or female lamb for 150 scrap (`LivestockVendor.OnConversationAction`). The animal
  appears 2 m in front of the vendor, already on the buyer's lead, with the buyer's trust at 300 s. The vendor also buys
  an adult, non-pregnant animal its seller leads up to him (three offers, paid from `LivestockVendor.SaleTable` in items
  such as high quality metal ore, low grade fuel, weapons, and keycards; the animal is removed) and wool in lots of 25
  to 400. Animals no player bred sell for 15% less (`livestock.pricewildpenalty`).
- Breeding (see Life cycle).
- Chickens. About every 30 s a wild chicken drops an `egg` with a 49% chance while a player is within 100 m, and eggs
  hatch in a chicken coop (see Chickens).

## Trust

Each animal remembers up to 8 players' trust in seconds. It gains a second per second while it senses the player
within 20 m (`livestock.familiarityradius`), times `livestock.familiarityrate`, times 1 plus the player's
`LivestockHandling` modifier; a sleeping player gains nothing. Away from it a player loses 1 s per 3 s
(`livestock.familiarityforgetscale`) until the full bond, which never fades. Hurting the animal costs 60 s, hurting
its herd 30 s, and a hurt animal counts its attacker's trust as 0 for 45 s (`livestock.grudgeduration`).

| Trust | Tier | Unlocks |
| --- | --- | --- |
| 5 s (`livestock.trusttofollow`) | known | a curious animal (20%, `livestock.curiouschance`) follows without a lead; adult males never do |
| 30 s (`livestock.trusttotolerate`) | tolerated | bulls, rams, and calf-guarding mothers stop squaring up at the player |
| 300 s (`livestock.trusttolead`) | bonded | lead and milk it; it starts aging, can starve, and drops dung |
| 900 s (`livestock.maxtrust`) | herd | full bond: it breeds, adopts the player's cupboard as home, never forgets, and a bull defends the player |

The animal credits at least 60 s (`livestock.trustfloor`) to players authorized on its home cupboard and to team mates
of a player who can lead it. Shepherds Pie (`pie.mutton`) gives `LivestockHandling` 1.0 for 900 s, doubling the gain
and counting as 60 s while it lasts (`livestock.calmedtrustfloor`); Rich Shepherds Pie (`pie.mutton.rich`) gives 1.5.

Leading and milking are RPCs sent from the animal's interaction prompt within 3 m (client UI, not readable in the
IL2CPP client); shearing is a melee hit:

- Lead (`RPC_Lead`) needs bonded trust. The lead slows the player once it stretches past 3.5 m and breaks past 7 m
  (`livestock.leadbreakdistance`) or when the animal is hurt. A led animal can be loaded into a vehicle flatbed that
  carries a livestock mount (`livestock.allowmounting`).
- Milk (`Cow.MilkCow`) works on an adult cow with bonded trust and gives 1 `milk` (2 with a strong Yield gene), then
  waits 300 s divided by the Yield scale. A new cow starts ready. Without trust the attempt only startles her.
- Shear by hitting an adult sheep with a cutting tool, a melee weapon whose flesh gathering has damage and no destroy
  fraction: `knife.combat`, `knife.bone`, `knife.butcher`, `knife.skinning`, `hatchet`, `axe.salvaged`, `sickle`, and
  their variants (`knife.bone.obsidian`, `sunken.knife`, `frontier_hatchet`, `diverhatchet`, `lumberjack.hatchet`).
  Other melee weapons, such as `stonehatchet`, rocks, pickaxes, the machete, swords, and the cleaver, wound it
  instead. Each hit takes one of four fleece quarters (2 or 3 `wool` of 10 per sheep, scaled by Yield), needs no
  trust but startles an untrusting sheep, and the fleece regrows 300 s (divided by Yield) after the last cut.

## Needs and condition

Contentment is the lowest of three needs, each 0 to 1:

- Fullness drains 0.001 per second only while the animal stands in daylight (all six prefabs set `SleepingDecayScale`
  0, so nothing drains at night, resting, or asleep): full to empty in about 17 minutes of that. Grazing refills
  0.004 per second on ground whose dominant splat is grass or forest, in any biome but arctic, on field or forest
  topology, off building and deployable colliders, within 25 m. A 5 m patch grazed twice turns bare for 30 minutes
  (`livestock.grazesuntilovergrazed`, `livestock.overgrazedareacooldownminutes`). Only when no grass is found does the
  animal eat from a Hitch & Trough within 25 m, taking chicken feed (`ItemModConsumable.chickenCoopFood`) worth 0.0021
  fullness per calorie up to a ceiling of 0.8: `bread.brioche.loaf` 190 calories, `bread.loaf` 150, `potato` 125,
  `cakefiveyear` 100, `pumpkin` 80, `corn` 75, `wheat` 75, `granolabar` 60, `black.raspberries` 40, `blueberries` 30,
  `apple` 30, berries and `honeycomb` 5, `grub` 3, `worm` 1. An animal living off a trough needs up to about 1700
  calories (14 potatoes) per hour of standing daylight.
- Hydration drains 0.00001 per second the same way, so one drink lasts about 5000 s of standing daylight. The animal
  drinks fresh water 0.05 to 0.5 m deep on a river or lake shore, or from any `LiquidContainer` holding fresh water
  within 25 m (water barrel, water catcher, the trough's water end), taking 200 ml times its missing hydration.
- Room never suffers from being alone. It falls when more than 8 herd mates are within 20 m (to 0.3 at 12), or more
  than 2 adult equivalents stand within 2.5 m (a calf or lamb counts 0.5; 0.3 at 5, 0 at 9).

Condition is the animal's health fraction. In daylight while awake it moves toward 1 when contentment is at least 0.5
(divided by the Hardiness scale) and toward lower values below that, at full health per 3600 s either way
(`livestock.conditionfallseconds`, `livestock.conditionriseseconds`). At 0 the animal dies of neglect. An animal no
player has ever reached bonded trust with never falls below 0.5 (`livestock.wildconditionfloor`). Breeding pauses at
0.6 or below and resumes at 0.7 (`livestock.breedpausecondition`, `livestock.breedresumecondition`).

## Life cycle and products

- Aging starts the first time any player reaches bonded trust and never stops (`DecayStarted`, saved). Until then the
  animal neither grows nor ages. Calves and lambs grow up after 3600 s (plus or minus 10%), keeping name, trust, and
  home. Adults die of old age after 172800 s scaled by Longevity (0.65 to 1.5) and plus or minus 10%: 28 to 79 hours,
  48 for an average animal. All of it scales with `livestock.agescale` (0 stops aging), and the timers save their
  remaining time, so they run only while the server runs.
- Breeding needs a fully bonded adult male not on a lead and a fully bonded adult female, both willing, with the
  species under `livestock.maxperspecies` (300, cattle and sheep counted apart). The male looks 30 m for a mate. The
  pregnancy lasts 600 s, the mother lies down for the last 45 s, and the litter is 1, or 2 with a good Fertility gene
  (`livestock.maxlittersize` 3). A newborn rolls its sex, inherits genes from both parents, the mother's home, and her
  players' trust capped at 300 s, so it ages from birth. The father waits 600 s divided by Fertility, the mother about
  66 minutes (her calf's grow time times 1.1). `livestock.breedonlyathome 1` restricts breeding to animals with a home.
- Genes: Dung, Longevity, Yield, Fertility, and Hardiness, two alleles each of Bad, Ok, or Good (wild rolls 20% Good,
  30% Bad). Bad scales the matching rate to 0.6 to 0.7 of average and Good to 1.5 to 1.6 times it
  (`livestock.genedungbad` and its siblings). On an inbred animal, whose two lineage markers match, every gene's
  position is multiplied by 0.5 (`livestock.inbredscale`): Good performs as Ok and Ok falls halfway to Bad.
- Home: a fully bonded animal standing inside the building privilege of a cupboard its bonded player is authorized on
  adopts that cupboard, checked every 5 s while it senses the player. It then roams within 25 m of the cupboard and
  beds down there at night. Without one it roams around its wild anchor, the spot where it spawned
  (`livestock.sendhome` moves the anchor to the caller).
- Dung: an adult drops `horsedung` every 600 s (plus or minus 20%, divided by Dung, up to 3 times slower at low
  condition) once someone has bonded trust, unless it is starving or below 0.15 condition.
- Corpses: a cow or bull yields 20 `beefmeat`, 100 `bone.fragments`, 40 `fat.animal`, 100 `leather`, and 30 `cloth`; a
  sheep yields 12 `muttonmeat`, 50 bone fragments, 20 animal fat, 20 leather, 10 cloth, and its unshorn wool; calves
  and lambs yield nothing.
- Milk stored in a fridge separates into `cream` and `skimmedmilk`, cream enriches the rich pies and creamy teas, and
  milk the milky teas (item descriptions; not traced in code).

## Chickens

The coop (`chickencoop`: 300 wood, 1 rope, 75 metal fragments, workbench level 1) has an egg slot, a food slot that
takes chicken feed (the trough list above plus `spoiled.produce`), a water slot that takes only fresh `water`, and an
output slot. An egg in the egg slot, submitted from the coop's loot panel (`SubmitEggForHatching`), hatches 2 minutes
later (the prefab sets 2; the class default is 30), one egg at a time and up to 4 chickens per coop.
`server.fillchickencoop` fills every coop within 5 m to 4 chickens at once.

A coop chicken starts each of hunger, thirst, love, and sunlight at 20 to 40 of 100. Per hour, hunger drops 5, thirst
8, love 3, and sunlight 3, while sunlight rises 300 whenever the coop's sun sampler sees the sun. A chicken eats one
feed item (40 hunger) at most every 15 s, drinks 1 ml of water (10 thirst) at most every 10 s, and gains 15 love per
petting. Below 50% average happiness it loses up to 3 of its 100 HP every 10 s. Every 4 to 5 minutes a chicken above
75% health lays an `egg` into the output slot, with a chance that rises from 0 at 75% happiness to 1 at 100%.
`farmableanimal.simhours <hours>` runs that many hours of needs on the coop chickens within 10 m.

## Items and deployables

| Item | Shortname | Use | Recipe |
| --- | --- | --- | --- |
| Hitch & Trough | `hitchtroughcombo` | trough feed when grass runs out, 5000 ml water end, horse hitch | 200 wood, default blueprint |
| Animal Fence | `wall.animal.fence` | pen wall with a navmesh obstacle | 250 wood, researchable |
| Animal Fence Gate | `wall.animal.fence.gate` | pen gate | 500 wood, researchable |
| Chicken Coop | `chickencoop` | hatches eggs, houses 4 chickens | see Chickens |
| Water Barrel | `water.barrel` | 20000 ml drinking water within 25 m | existing item |
| Shepherds Pie | `pie.mutton` | trust gain doubled for 15 minutes | cooking workbench |
| Products | `milk`, `wool`, `horsedung`, `beefmeat`, `muttonmeat`, `egg` | | |

On this server, Carbon's Auto Doors closes an owned animal fence gate 5 s after it opens (`configs/AutoDoors.json`
lists `wall.animal.fence.gate`), and `decay.scale 0` keeps fences from decaying.

## Commands

Server commands under `livestock.` act on the animal in view, or failing that the nearest within 20 m (the optional
radius argument). Most need a player and answer `This command has to be run by a player.` over RCON; `census`,
`censusreset`, `cullexcess`, and `specials` work there too.

| Command | Effect |
| --- | --- |
| `livestock.setfamiliarity <s> [r]` | set the caller's trust with one animal; 900 is the full bond |
| `livestock.setfamiliarityall <s> [r]` | the same for every animal within 30 m (default) |
| `livestock.readytomilk`, `livestock.readytoshear`, `livestock.dung` | skip the milk or wool cooldown, drop dung now |
| `livestock.makepregnant`, `livestock.completepregnancy`, `livestock.forcebreed` | breed without waiting |
| `livestock.ageup` | an infant grows up; an adult dies of old age |
| `livestock.fillfullness`, `fillhydration`, `fillsocial`, `setcondition <0-1>` | set needs and condition |
| `livestock.showgenes`, `setgene <Gene> <a> <b>`, `setinbred <0/1>`, `showcoat` | genes, alleles `Bad`/`Ok`/`Good` |
| `livestock.rename <name>`, `swapgender`, `setcurious <0/1>` | identity |
| `livestock.sendhome [r]`, `livestock.showhome` | move the wild anchor here, draw the way home |
| `livestock.overgrazehere`, `livestock.cleargrazing [r]` | mark or clear bare grass |
| `livestock.census [r]` | head count, needs, condition, births, deaths, dung, trough use |
| `livestock.cullexcess [n]` | kill livestock above `maxperspecies` (or n), untamed first |
| `livestock.spawnspecial <Name> [species]`, `livestock.specials` | spawn or list named specials |
| `ai.showstate` | toggle an overlay with each animal's state, needs, trust, and home |

Specials, by exact case: cows `Melk` and `Moostera`; bulls `Mooprint`, `CoconutBeef`, and `Sleth`; ewes `Woolyn` and
`Sheeppee`; rams `Wooljum`, `Oilrams`, and `Shadowfleece`; and `Hoofcamper`, a male of either species that needs
`Cow.Species` or `Sheep.Species` as the second argument. Only one of each name lives at a time.

Convars (default): `cow.population` 2, `sheep.population` 2, `chicken.population` 3, `livestock.agescale` 1,
`breedingenabled` true, `breedonlyathome` false, `maxperspecies` 300, `familiarityrate` 1, `familiarityradius` 20,
`trusttofollow` 5, `trusttotolerate` 30, `trusttolead` 300, `maxtrust` 900, `leadbreakdistance` 7,
`overgrazingenabled` true, `conditionfallseconds` 3600, `wildconditionfloor` 0.5, `genesenabled` true, `specialchance`
0.005. Status panel extras are off by default except age: `livestock.paneltrust`, `panelyield`, `panelgender`,
`paneldung`, `panelgenes`, `panelalleles`, and `panelage`. Those, `breedpausecondition`, `breedresumecondition`,
`allowmounting`, and `inbredscale` are saved convars that `quit` writes to `serverauto.cfg`; the rest reset on restart
unless set in `server.cfg`.

## Test farm

From F1 as an admin. Give the supplies (repeat the fence line for more; amounts stay within one stack):

```text
inventory.give cupboard.tool 1
inventory.give wall.animal.fence 10
inventory.give wall.animal.fence.gate 1
inventory.give hitchtroughcombo 1
inventory.give water.barrel 1
inventory.give potato 20
inventory.give corn 20
inventory.give knife.combat 1
```

On open grass outside the arctic, ideally beside a river or lake, place and authorize the cupboard, fence a pen around
it, and put the trough and barrel inside with the potatoes and corn in the trough. Fill the barrel or the trough's water
end with fresh water, or rely on the shore; hydration falls so slowly that it barely matters in a short test. Then look
at the ground inside the pen and spawn the herd:

```text
spawn cow
spawn bull
spawn calf
spawn sheep
spawn sheep
spawn lamb
```

Stand among them and bond with all of them, then optionally show the panel extras:

```text
livestock.setfamiliarityall 900 30
livestock.paneltrust 1
livestock.panelyield 1
livestock.panelgender 1
```

From then on they can be led, milked, sheared, and bred. Each one standing inside the cupboard's building range adopts
that cupboard as home within seconds while the caller, authorized on it, stays within 20 m (`livestock.showhome`
confirms). Shortcuts, run next to the animal:

```text
livestock.readytomilk
livestock.readytoshear
livestock.makepregnant
livestock.completepregnancy
livestock.census 50
ai.showstate
```

Chickens: `inventory.give chickencoop 1` and `inventory.give egg 4`, place the coop in the sun, then either hatch the
eggs or stand within 5 m and run `server.fillchickencoop`. Give it feed (`inventory.give corn 20`) and fresh water.

## Caveats

- Ownership is trust, not `OwnerID`. A spawned animal belongs to nobody and starts as a stranger. Until a player
  reaches 300 s it does not age, grow, or drop dung, cannot be milked, and never drops below half health from neglect.
  Once bonded it ages for good and can die of neglect or old age, so a test herd needs grass or a stocked trough.
  `livestock.agescale 0` stops aging; it is not a saved convar, so it lasts until the next restart unless set in
  `server.cfg`.
- `livestock.ageup` on an adult kills it of old age.
- Spawned livestock never despawn and survive restarts. They are not members of a spawn population, so population
  limits never touch them. Wild-caught animals stay members: if `cow.population` or `sheep.population` is lowered,
  `SpawnHandler.EnforceLimits` kills members above the new target at the next load, tamed or not.
- `spawn chicken` makes a wild chicken, which is not saved (`enableSaving` 0) and is gone after a restart.
- Aim at walkable ground. Livestock move on the navmesh only; a cow spawned 40 m in the air hung there frozen.
- No biome restricts spawning, but grazing does: on snow, sand, dirt, rock, gravel, or a floor, or anywhere in the
  arctic biome, the herd eats only from a trough.
- Bulls, adult rams, and mothers guarding young square up and kick players under 30 s of trust; bond first or use
  `global.god`. Nothing hurts livestock inside a safe zone, and animals there threaten no one.
- At 300 head of one species (`livestock.maxperspecies`) all breeding of it stops; `livestock.cullexcess` removes the
  surplus.

## Verification

Live, on a throwaway vanilla server (identity `livestocktest`, worldsize 1000, seed 12345) on 2026-10-09:

- `entity.spawn` of `cow`, `bull`, `calf`, `calfmale`, `sheep` (twice), `lamb`, `chicken`, `hitchtrough.deployed`,
  `chickencoop`, and `livestockvendor_stables` created the prefabs listed above, the trough with its
  `hitchtrough.waterstorage` child. `hitchtrough` and `animalfence` answered with their two candidates.
- The animals snapped to the navmesh and grazed; `livestock.census` counted 7 head (4 cattle, 3 sheep, 3 infants).
- After `save`, `quit`, and a restart, all seven livestock and both deployables returned with their entity IDs; the
  chicken did not.
- With `livestock.agescale 0.001`, which would grow an aging calf in about 4 s, untamed calves stayed calves for 45 s.
- A cow spawned 40 m above the ground stayed frozen in mid air for over 10 minutes.

Read-only on the live `pve` server: `livestock.census`, `spawn.report`, the population convars, and
`entity.find_entity livestockvendor` and `stables`. The F1 `spawn` format comes from the pve command history. The rest,
including trust, needs, handling, breeding, vendor trades, and chickens, is read from code and prefab data; no client
was connected to test leading, milking, shearing, or buying.

## Re-deriving

The decompile holds the logic and the class defaults, but the prefabs override several (`MilkAmount` 1 against a class
default of 10, `ChickenHatchTimeMinutes` 2 against 30), so read values from the prefabs.

```bash
m=server/RustDedicated_Data/Managed
a="$m/Assembly-CSharp.dll"
ilspycmd --disable-updatecheck -r "$m" -t ConVar.Livestock "$a" | grep -n 'ServerVar\|ReplicatedVar'
ilspycmd --disable-updatecheck -r "$m" -t Rust.Ai.Gen2.LivestockAnimal "$a" >/tmp/livestock.cs
jq -r '.Scenes[].IncludedAssets[]' server/Bundles/AssetSceneManifest.json |
  grep -i 'rust.ai/agents/\(cow\|bull\|calf\|sheep\)/'
```

The prefabs live as GameObjects in the `AssetScene-prefabs` scene of `server/Bundles/shared/assetscenes.bundle`;
reading the typetrees of their root MonoBehaviours with UnityPy, as `scripts/game-modes.py` does for the game modes,
gives the field values. The species, population, sale table, and specials assets are in `content.bundle` under
`assets/rust.ai/agents/` and `assets/prefabs/npc/ranch/`, and `assets/manifest.asset` there holds the
`GameManifest.entities` list that `spawn` searches.
