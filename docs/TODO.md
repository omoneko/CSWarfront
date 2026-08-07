# CSWarfront Remaining Tasks

Last updated: 2026-08-04 (as of the Update 3 implementation, Core 850/850 green)

## Update 3 (implemented 2026-08-04, pre-release) — field fortifications / helicopters / rail

- **Five field fortifications** (Options-designated-building scheme, BaseType extension): Bunker (fires
  as three infantry, no shooting through buildings, goes defunct at 0 HP) / Artillery position (one
  artillery's worth, 30m splash) / Supply depot (300 stock, 200m auto-resupply, stock seized on
  capture) / Trench (untargetable, +50% infantry defense friend or foe, new model) / Cargo station
  (500 stock, 100m rail snap)
- **Defense bonus**: infantry on a trench/bunker take damage ÷ 1.5. Infantry AI auto-moves to a
  fortification within 300m when an enemy is within 600m
- **Supply network**: bases (faction pool) → trucks/transport helicopters/trains → depots & stations
  (stock) → the front. Trucks reload at depots and haul stock to depots when otherwise idle
- **Helicopters**: attack helicopter (air-base production, ground-attack only, hovers; only
  tanks/AA/fighters can shoot it down) / transport helicopter (army bases auto-maintain 6; airlifts
  60 supplies + 3 infantry; carried units die with it if shot down)
- **Rail**: RailGraph (rebuilt every 12h); per cargo-station pair (2km+ and connected) one train
  (4 per faction) hauls 200 supplies + land units that would get 1km+ closer to the front
- Save v10 (stock/fort ammo/rail connectivity/boarding). 8 new models (the trench replaces the old
  one). Not yet playtested in-game


## Implemented (highlights)

- **Update 1 (released 2026-08-02)**: outside-incursion events (dedicated Invader faction = ID 5,
  moss green, always hostile, targets bases, defenders scramble), Missile Disaster impact damage to
  units, spatial-grid engagement checks + 150-unit faction cap (performance), auto-despawn of stuck
  units (speed-proportional threshold)
- **Update 2 (implemented 2026-08-03, pre-release)**: three-resource economy + supply logistics
  - Per-zone development within 1km of a base → residential = manpower / commercial & office =
    funds / industrial = production (economy tick)
  - Unit production = manpower + production (production shortfall substitutable with funds ×2).
    Research and missiles stay funds-only
  - Ammo gauge (per-category continuous-fire hours; dry = fire stops, nothing more.
    Invader/carriers/suicide drones/trucks are infinite)
  - Supply stockpile (auto-produced from production, cap 1000) → auto-resupply 25%/h within 200m of
    a base/carrier
  - Supply trucks (new category, unarmed, separate 30-truck cap per faction, auto-maintained by army
    bases) deliver to the front at 50%/h
  - Aircraft return to base/carrier when dry → rearm → auto-sortie again. Save v9 (resources, ammo,
    cargo)

- 5 factions (color names: Red/Blue/Green/Yellow/Magenta), relation matrix (hostile/neutral/allied/
  **nemesis**, set in Options; nemeses are attacked first)
- **KAIJU/Alien pseudo-factions**: relation rows appear only when the Godzilla/Alien MODs are
  installed. Nemesis designation sorties at unlimited range. **The Godzilla beam / tripod laser
  damages units too** (segment check, instant-death/critical grade)
- 7 land classes × tiers 1–5 / naval (missile destroyer, carrier = dedicated flight platform) / air
  (fighter, bomber, suicide drone)
- **Realistic fire rates** (small arms = frequent & weak vs guns & missiles = rare & heavy);
  subscribers can tune base stats via `unit-stats.xml` (template auto-generated)
- **Engagement rules**: capture (0 HP) by ground forces only. Air, naval and ballistic missiles can
  only take base HP down to 1. Fighters = anti-air only, bombers = ground & naval targets,
  destroyers = anti-ground/anti-ship (no AA). Base HP regenerates 20/h = bases fall only to
  sustained ground assault that outpaces regen
- **AA rework**: per-shot tier-based hit chance (vs drones = gun 0.70–0.90, vs fighters/bombers =
  SAM 0.55–0.83). SAMs are homing projectiles with exhaust trails; target aircraft pop flares and
  jink (ported from MissileDisaster)
- **Air pass movement**: bombers = hit-and-run with a bomb-drop model, fighters = crossing-pass
  dogfights (racetrack passes, DPS compensation ×3)
- **Movement**: global 1.25× speed. Land = road A* (nemesis pursuit stays on roads where possible),
  sea = **SeaGrid A\* (96m grid, 2m draft)** + wall-following detours, air = cruise altitude 120 +
  return home (Idle → nearest air base/carrier, ships → navy base)
- **Ballistic missiles**: stockpile production → launch (**auto-production/auto-launch toggleable
  per base**, manual button available) → AA interception → impact. **In-flight missiles and unit
  orders persist in saves (v8)**
- All player-facing strings are in English. All 19 models (12 units + 4 bases + bomb/ballistic
  missile/interceptor) are exported from models.blend
- Kill explosion = CS stock effect; bombing and destroyer gun sounds included. Units face their
  attack direction (aircraft excluded)

## Remaining tasks (post-release update candidates)

### Features
1. **Missile expansion** (the full version of design §4.7): warhead kinds (cluster/nuclear etc.) with
   damage factors, nukes priced high = deterrence, city-building destruction on impact
   (`DisasterHelpers`, reused from MissileDisaster), stockpile seizure on capture
2. Dedicated firing sounds for naval/air units (currently reusing existing audio: destroyer = gun
   sound, fighter = rifle sound)
8. ~~Dedicated supply-truck model~~ done (2026-08-03, 20_Supply_Truck → Unit_SupplyTruck)

### Improvements / tuning
3. In-game balance tuning of the numbers (subscribers can now tune via unit-stats.xml too)
4. Use props for cover (currently buildings only)
5. Resolve `MilitaryManager.cs` exceeding the 500-line rule
6. Aiming cursor for rally/missile targeting modes (currently toast-only)
7. In-game tuning of SeaGrid cell resolution/extent (currently 96m, ±4800m)
