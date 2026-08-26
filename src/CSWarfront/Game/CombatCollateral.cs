using System;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// Occasionally triggers fires and building collapses near unit-vs-unit combat zones
    /// (State.CombatZones, Task54) (Task65, per the user request "I'd like unit-vs-unit combat to also
    /// cause random fires and building collapses nearby. A rare frequency is fine").
    ///
    /// Thread boundary: DisasterHelpers is a sim-thread-only API, just as in the missile disaster MOD
    /// (MissileDisaster.Game.ImpactResolver, C:\Users\omone\Desktop\G\
    /// [missile disaster project folder]\src\MissileDisaster\Game\ImpactResolver.cs),
    /// so we align with exactly the same thread boundary as CombatRoadBlocker (expected to be called
    /// from within MilitaryManager.OnSimTick's _stateLock, in the same sequence as the other
    /// sim-only processing that touches the CS building buffers).
    ///
    /// The DisasterHelpers.DestroyStuff usage is carried over verbatim from ImpactResolver.ApplyBlast:
    ///   DestroyStuff(seed, area, position, preRadius, totalRadius, removeRadius,
    ///                destroyMin, destroyMax, burnMin, burnMax)
    ///   - Buildings in the destroyMin/destroyMax band collapse probabilistically; buildings in the
    ///     burnMin/burnMax band catch fire instead of collapsing (behavior verified in
    ///     docs/superpowers/plans/2026-07-15-fire-and-contamination.md = the "burn band").
    ///   - preRadius/totalRadius are the processing outer bounds (we carry over and avoid the known
    ///     ImpactResolver pitfall that the outer area is not scanned unless these match the larger of
    ///     destroyMax/burnMax).
    ///   - removeRadius (strong destruction that removes foundations and roads too) is always 0: here
    ///     we aim for small collateral damage on the order of a "stray shell", not area saturation
    ///     like a missile impact.
    ///   - DisasterHelpers.MakeCrater (terrain cratering) is never called: terrain deformation is a
    ///     missile-class effect and would be far too dramatic as collateral from ordinary combat, so
    ///     it was deliberately excluded.
    ///   - The seed argument itself (used internally by DestroyStuff for building selection) is taken
    ///     from SimulationManager's randomizer, same as ImpactResolver (this is an internal argument
    ///     required by the CS API; the "does it happen / where does it happen" decisions below use
    ///     neither System.Random nor even this randomizer = a separate concern).
    /// </summary>
    internal static class CombatCollateral
    {
        /// <summary>Roll interval per combat zone (in-game time). Same "throttling" pattern as
        /// CombatRoadBlocker.BlockUpdateIntervalHours (do not evaluate all zones every tick).</summary>
        public const float CollateralCheckIntervalHours = 0.5f;

        /// <summary>Probability of a fire per roll (1 combat zone x 1 check). Low value, per the "rare" requirement.</summary>
        public const float FireChancePerCheck = 0.06f;

        /// <summary>Probability of a building collapse per roll. Even rarer than fire.</summary>
        public const float CollapseChancePerCheck = 0.015f;

        /// <summary>Cap on collateral events (fires + collapses combined) allowed per in-game day (24h).
        /// A safety valve so that log and processing costs do not grow without bound even in
        /// multi-front wars with several combat zones existing at once (a defensive cap in the same
        /// spirit as CombatZoneTracker.MaxZones).</summary>
        public const int MaxEventsPerDay = 6;

        private const float HoursPerDay = 24f;

        /// <summary>Radius of a fire (burn band only, no collapse). Kept small, on the order of
        /// "a stray shell sets a nearby building on fire" (deliberately chosen to be much smaller
        /// than MissileDisaster's conventional-warhead BurnRadius=40: that one is damage from the
        /// impact itself, whereas this is positioned as combat collateral).</summary>
        private const float FireBurnRadius = 22f;

        /// <summary>Radius of a building collapse (destroy band only, no burning). Kept small, on the
        /// order of "one building gets caught up in it", so it reads as a "stray round" rather than
        /// bombardment (per the requirement).</summary>
        private const float CollapseDestroyRadius = 14f;

        private static float _checkAccum;
        private static float _dayAccum;
        private static int _eventsToday;
        private static uint _checkCounter;

        /// <summary>
        /// Called every tick from MilitaryManager.OnSimTick (inside _stateLock, in the same sequence
        /// position as CombatRoadBlocker.Advance). Only performs the actual roll once per
        /// CollateralCheckIntervalHours. Exceptions are not propagated outward and are limited to
        /// logging (same policy as CombatRoadBlocker.Advance = never stop the sim loop).
        /// </summary>
        public static void Advance(WarState state, float dt)
        {
            try
            {
                _dayAccum += dt;
                if (_dayAccum >= HoursPerDay)
                {
                    _dayAccum -= HoursPerDay;
                    if (_dayAccum < 0f) _dayAccum = 0f;
                    _eventsToday = 0; // Reset the cap once per day
                }

                _checkAccum += dt;
                if (_checkAccum < CollateralCheckIntervalHours) return;
                _checkAccum -= CollateralCheckIntervalHours;
                if (_checkAccum < 0f) _checkAccum = 0f;

                // Task146: the player's chosen intensity. At 0 the whole system is off; the shipped
                // default is the historical rate, and the heavy setting is what makes a battle visibly
                // wreck the place it is fought in.
                float scale = WarfrontSettings.BattleDamageScale;
                if (scale <= 0f) return;
                float fireChance = FireChancePerCheck * scale;
                float collapseChance = CollapseChancePerCheck * scale;
                int dailyCap = (int)(MaxEventsPerDay * scale);
                if (dailyCap < 1) dailyCap = 1;

                var zones = state.CombatZones.Zones;
                if (zones.Count == 0) return;

                _checkCounter++; // Deterministic seed uniquely identifying this "check" itself

                for (int i = 0; i < zones.Count; i++)
                {
                    if (_eventsToday >= dailyCap) return;

                    CombatZone zone = zones[i];

                    // Fire roll (a hash mixing zoneIndex+checkCounter+salt, no System.Random).
                    if (Roll(i, _checkCounter, 1u) < fireChance)
                    {
                        Vector3 pos = RandomPointIn(zone, i, _checkCounter, 2u);
                        IgniteFire(pos);
                        _eventsToday++;
                        if (_eventsToday >= dailyCap) return;
                    }

                    // Building collapse roll (independent of fire; hash separated via a different salt).
                    if (Roll(i, _checkCounter, 3u) < collapseChance)
                    {
                        Vector3 pos = RandomPointIn(zone, i, _checkCounter, 4u);
                        CollapseBuilding(pos);
                        _eventsToday++;
                    }
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("CombatCollateral.Advance exception: " + e);
            }
        }

        /// <summary>Calls DestroyStuff with the burn band only (destroyMin=destroyMax=0) = buildings catch fire without collapsing.</summary>
        private static void IgniteFire(Vector3 pos)
        {
            int seed = (int)SimulationManager.instance.m_randomizer.Int32(1000000u);
            DisasterHelpers.DestroyStuff(seed, null, pos, FireBurnRadius, FireBurnRadius, 0f,
                0f, 0f, 0f, FireBurnRadius);
            ModConfig.Log("CombatCollateral: fire at (" + pos.x.ToString("0") + ", " + pos.z.ToString("0") + ")");
        }

        /// <summary>Calls DestroyStuff with the destroy band only (burnMin=burnMax=0) = collapses buildings
        /// without spreading fire. Because removeRadius=0, foundations and roads remain (keeping the
        /// visual to roughly "one building gets crushed").</summary>
        private static void CollapseBuilding(Vector3 pos)
        {
            int seed = (int)SimulationManager.instance.m_randomizer.Int32(1000000u);
            DisasterHelpers.DestroyStuff(seed, null, pos, CollapseDestroyRadius, CollapseDestroyRadius, 0f,
                0f, CollapseDestroyRadius, 0f, 0f);
            ModConfig.Log("CombatCollateral: collapse at (" + pos.x.ToString("0") + ", " + pos.z.ToString("0") + ")");
        }

        /// <summary>Returns one "random but reproducible" point inside the combat zone in polar
        /// coordinates (no System.Random; both angle and radius are deterministic pseudo-random values
        /// via Hash).</summary>
        private static Vector3 RandomPointIn(CombatZone zone, int zoneIndex, uint checkCounter, uint salt)
        {
            float angle01 = Roll(zoneIndex, checkCounter, salt);
            float radius01 = Roll(zoneIndex, checkCounter, salt + 100u);
            float angle = angle01 * 2f * Mathf.PI;
            float radius = radius01 * zone.Radius;
            float x = zone.Center.X + Mathf.Cos(angle) * radius;
            float z = zone.Center.Z + Mathf.Sin(angle) * radius;
            return new Vector3(x, zone.Center.Y, z);
        }

        /// <summary>Deterministic pseudo-random value in 0..1. Independently adopts the same technique
        /// as BallisticMissiles.HashSeed/Hash (equivalent to the MurmurHash3 finalizer) here as well
        /// (mixing the three values zoneIndex/checkCounter/salt). System.Random is never used: the same
        /// inputs (zoneIndex, checkCounter, salt) always yield the same result.</summary>
        private static float Roll(int zoneIndex, uint checkCounter, uint salt)
        {
            uint seed = HashSeed((uint)zoneIndex, checkCounter, salt);
            return (seed % 1000000u) / 1000000f;
        }

        private static uint HashSeed(uint zoneIndex, uint checkCounter, uint salt)
        {
            unchecked
            {
                uint h = zoneIndex;
                h = h * 2654435761u + checkCounter;
                h = h * 2654435761u + salt;
                return Hash(h);
            }
        }

        private static uint Hash(uint x)
        {
            unchecked
            {
                x ^= x >> 16;
                x *= 0x7feb352dU;
                x ^= x >> 15;
                x *= 0x846ca68bU;
                x ^= x >> 16;
                return x;
            }
        }

        /// <summary>On level unload (called from MilitaryManager.Reset): clears the internal throttling
        /// state. Touches no CS entities at all, so it is plain assignments only (cannot throw).</summary>
        public static void Reset()
        {
            _checkAccum = 0f;
            _dayAccum = 0f;
            _eventsToday = 0;
            _checkCounter = 0;
        }
    }
}
