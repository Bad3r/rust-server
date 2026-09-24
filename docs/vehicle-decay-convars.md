# Vehicle decay convars

Backs the `server.cfg` change in commit `d660062` (disable vehicle decay). Verified against Rust buildid
25454815 (`server/steamapps/appmanifest_258550.acf`) on 2026-09-24, by decompiling `Assembly-CSharp.dll` with
`ilspycmd` (see CLAUDE.md Host tools) and cross-checking every field against `ConsoleGen.cs`. Re-verify after a
Rust update with `scripts/check-convars.sh`, which automates the `ConsoleGen.cs` half of this table (it confirms
a convar still resolves to a real `[ServerVar]` field, not that the field still means the same thing).

## Mechanism

`BaseBoat.WaterVehicleDecay(entity, decayTickRate, timeSinceLastUsed, outsideDecayMinutes, deepWaterDecayMinutes,
decayStartDelayMinutes, preventDecayIndoors)` is called on a repeating timer (every caller passes `decayTickRate
= 60`). It does nothing while `timeSinceLastUsed < 60 * decayStartDelayMinutes` seconds, otherwise picks a rate:
`outsideDecayMinutes`, blended toward `deepWaterDecayMinutes` between 12 m and 16 m of water depth (full
deep-water rate past 16 m), or positive infinity (no damage at all) when `preventDecayIndoors` is set and the
vehicle is not outside. It then calls `entity.Hurt(entity.MaxHealth() * decayTickRate / 60 / rate)`, which at the
fixed 60 s tick is `Hurt(MaxHealth / rate)` once per tick. `rate = 0` would destroy the vehicle on its first tick
instead of disabling decay, while `1e9` stretches full decay to about 1900 years. Land vehicles and the
helicopter apply the same shape in their own tick methods (no depth blending). `traincar.decayminutes` is
different: it is an idle despawn timer for workcarts and wagons away from their station, not a health-decay rate.

## Convar table

Set in `server.cfg` to `1e9` (see commit `d660062`); "kind" flags a field with reuse or inheritance detail, spelled
out in Class hierarchy notes below.

| Convar | Backing field | Vanilla default | Kind |
| --- | --- | --- | --- |
| `playerhelicopter.insidedecayminutes` | `PlayerHelicopter.insidedecayminutes` | 2880 | own field |
| `playerhelicopter.outsidedecayminutes` | `PlayerHelicopter.outsidedecayminutes` | 480 | own field |
| `playerboat.decayminutes` | `PlayerBoat.decayminutes` | 720 | shared outside/deep-water |
| `motorrowboat.outsidedecayminutes` | `MotorRowboat.outsidedecayminutes` | 180 | inherited + reused |
| `motorrowboat.deepwaterdecayminutes` | `MotorRowboat.deepwaterdecayminutes` | 120 | inherited + reused |
| `tugboat.tugdecayminutes` | `Tugboat.tugdecayminutes` | 2160 | shared outside/deep-water |
| `basesubmarine.outsidedecayminutes` | `BaseSubmarine.outsidedecayminutes` | 180 | reused by DPV |
| `basesubmarine.deepwaterdecayminutes` | `BaseSubmarine.deepwaterdecayminutes` | 120 | reused by DPV |
| `snowmobile.outsidedecayminutes` | `Snowmobile.outsideDecayMinutes` | 1440 | own field |
| `basesiegeweapon.outsidedecayminutes` | `BaseSiegeWeapon.outsideDecayMinutes` | 600 | own field |
| `ridablehorse.decayminutes` | `RidableHorse.decayMinutes` | 180 | own field |
| `bike.outsidedecayminutes` | `Bike.outsideDecayMinutes` | 1440 | own field |
| `hotairballoon.outsidedecayminutes` | `HotAirBalloon.outsidedecayminutes` | 180 | own field |
| `modularcar.outsidedecayminutes` | `ModularCar.outsidedecayminutes` | 864 | own field |
| `traincar.decayminutes` | `TrainCar.decayminutes` | 30 | despawn timer, not health |
| `app.port` | `ConVar.App.port` | 0 (implicit) | unrelated to decay, see CLAUDE.md |

Not set in `server.cfg`; left at the vanilla default on purpose.

| Convar | Backing field | Vanilla default |
| --- | --- | --- |
| `playerboat.decaystartdelayminutes` | `PlayerBoat.decaystartdelayminutes` | 1440 |
| `motorrowboat.decaystartdelayminutes` | `MotorRowboat.decaystartdelayminutes` | 45 |
| `tugboat.tugdecaystartdelayminutes` | `Tugboat.tugdecaystartdelayminutes` | 1440 |
| `traincarunloadable.decayminutesafterunload` | `TrainCarUnloadable.decayminutesafterunload` | 10 |

The start-delay convars only gate when decay begins, not its rate, so leaving them at the vanilla default
still stops a parked vehicle from ever reaching zero health; `traincarunloadable.decayminutesafterunload` must
keep its default, since emptied ore and fuel wagons need to despawn or they fill `traincar.population` and full
wagons stop respawning (see the `server.cfg` paragraph in CLAUDE.md).

## Class hierarchy notes

- `RHIB : MotorRowboat`, `PTBoat : RHIB`: both inherit `MotorRowboat`'s decay fields, so setting
  `motorrowboat.*` covers them with no separate convar.
- `Kayak : BaseBoat` (not `MotorRowboat`), but `Kayak.BoatDecay` calls `BaseBoat.WaterVehicleDecay` passing
  `MotorRowboat.outsidedecayminutes`, `MotorRowboat.deepwaterdecayminutes`, and
  `MotorRowboat.decaystartdelayminutes` directly, so it is covered too.
- `DiverPropulsionVehicle : BaseMountable` (not `BaseBoat` at all), but `DiverPropulsionVehicle.DPVDecay` still
  calls `BaseBoat.WaterVehicleDecay`, passing `BaseSubmarine.outsidedecayminutes` and
  `BaseSubmarine.deepwaterdecayminutes` for the rate and `MotorRowboat.decaystartdelayminutes` for the delay
  (a mix of two other vehicles' statics).
- `PlayerBoat : BaseBoat` is its own class (the type ConsoleGen.cs resolves `playerboat.decayminutes` to); it
  passes its single `decayminutes` field as both the outside and deep-water argument.

## Re-deriving this table

`scripts/check-convars.sh` confirms names and field existence but not vanilla defaults or the call graph above.
To redo that part after a Rust update, decompile the relevant classes and grep for the decay tick call, for
example:

```bash
m=server/RustDedicated_Data/Managed
ilspycmd --disable-updatecheck -r "$m" -t MotorRowboat "$m/Assembly-CSharp.dll" | grep -n 'decayminutes\|ServerVar'
```

`BaseBoat.WaterVehicleDecay`'s signature (`entity, decayTickRate, timeSinceLastUsed, outsideDecayMinutes,
deepWaterDecayMinutes, decayStartDelayMinutes, preventDecayIndoors`) is the anchor: grep each vehicle class for
`WaterVehicleDecay(` to see which statics it actually passes, since (as the Kayak and DiverPropulsionVehicle
rows above show) that is not always the class's own fields.
