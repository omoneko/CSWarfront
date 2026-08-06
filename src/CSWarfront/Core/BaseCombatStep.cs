using System;

namespace CSWarfront.Core
{
    /// <summary>Units attacking hostile bases in range (pure logic).</summary>
    public static class BaseCombatStep
    {
        /// <summary>Accuracy floor for sieging bases (Task38). A stationary building is easier to hit
        /// than a moving unit, so categories whose raw accuracy is lower (e.g. Artillery at 0.35) are
        /// raised to this value against bases. This preserves artillery's role as a siege weapon:
        /// "hard to land on units, still devastating against bases".</summary>
        public const float SiegeAccuracyFloor = 0.8f;

        /// <summary>Task89 (user request "base HP should regenerate gradually"): natural base HP
        /// regeneration per in-game hour. Once attacks stop, a base heads back to full over time, and
        /// bases ground down to the floor (1) by air/sea/missiles recover if left alone. Establishing a
        /// capture requires sustained ground attack exceeding this regen rate (a Tank_T1's siege DPS of
        /// 32/h &gt; 20/h, so even a single tank nets damage, while a single infantry (16/h) loses to the
        /// regen — see TargetingRulesTests).</summary>
        public const float BaseRegenPerHour = 20f;

        public static void Advance(WarState state, float dt)
        {
            // Consume the capture grace of new bases first. Bases in grace are excluded entirely from this
            // tick's damage loop (prevents one side being captured before the player finishes placing both
            // sides).
            // Task89: natural regen is also applied here first (regen → attack order; within a tick the
            // net grind is "attack DPS − regen"). Bases at HP 0 (awaiting capture processing) do not
            // regenerate — if they did, captures could never complete.
            for (int j = 0; j < state.Bases.Count; j++)
            {
                var b = state.Bases[j];

                if (b.OwnerFactionId != null && b.CurrentHP > 0f && b.CurrentHP < b.MaxHP)
                {
                    b.CurrentHP += BaseRegenPerHour * dt;
                    if (b.CurrentHP > b.MaxHP) b.CurrentHP = b.MaxHP;
                }

                if (b.CaptureGraceHours <= 0f) continue;
                b.CaptureGraceHours -= dt;
                if (b.CaptureGraceHours < 0f) b.CaptureGraceHours = 0f;
            }

            for (int i = 0; i < state.Units.Count; i++)
            {
                var u = state.Units[i];
                if (!u.IsAlive) continue;
                var type = state.Types.Get(u.TypeKey);
                if (type == null) continue;

                // Task79: suicide drones never do the continuous in-range bombardment of bases (formerly:
                // one damage application then IsOneShot self-destruct) — keeping Task79's "no ShotEvents"
                // contract consistent against bases too. The ability to attack bases is not lost:
                // KamikazeStep covers it separately as "only when no unit or external threat is in range,
                // as a last resort, dive into a hostile base and detonate". (The rework initially dropped
                // base attacks entirely, which regressed suicide drones into never attacking bases at all;
                // KamikazeStep picked it back up — see its class comment.)
                if (type.Category.IsKamikaze()) continue;

                // Task85: fighters (air-superiority specialists) and carriers (platform specialists) never
                // attack bases.
                if (!TargetingRules.CanAttackBase(type.Category)) continue;

                // Task99: out of ammo also stops base attacks (same convention as CombatStep's
                // anti-unit fire).
                if (!AmmoRules.HasAmmo(u, type)) continue;

                bool firedAtAnyBase = false;
                for (int j = 0; j < state.Bases.Count; j++)
                {
                    var b = state.Bases[j];
                    if (!FortificationRules.IsTargetable(b.Type)) continue; // Task101: trenches cannot be attacked
                    if (b.CaptureGraceHours > 0f) continue; // invulnerable during grace
                    if (b.OwnerFactionId == null) continue;
                    if (b.OwnerFactionId.Value == u.FactionId) continue;
                    if (!state.Relations.Get(u.FactionId, b.OwnerFactionId.Value).IsHostile()) continue; // Task59: Nemesis counts as hostile
                    if (u.Position.HorizontalDistanceTo(b.Position) > type.Range) continue;

                    // Task88: never attack a base already at this attacker's HP floor (air/sea = 1)
                    // (prevents endless zero-damage bombing that only spews muzzle effects — the fix for
                    // the playtest report "bombers keep attacking even when the base is at 1 HP").
                    float hpFloor = TargetingRules.BaseHpFloor(type.Domain);
                    if (b.CurrentHP <= hpFloor) continue;
                    // Attack is damage per in-game hour. The applied damage scales with elapsed in-game
                    // time (dt) and accuracy (Task38). A stationary building is a sitting target compared
                    // with a moving unit, so accuracy gets the SiegeAccuracyFloor (0.8) lower bound (e.g.
                    // 0.35-accuracy artillery is treated as 0.8 in sieges = artillery stays an effective
                    // siege weapon despite its low raw accuracy).
                    float accuracy = CombatSynergy.AccuracyFor(state, u, type);
                    float siegeAccuracy = Math.Max(accuracy, SiegeAccuracyFloor);
                    // Task86: air (bombers) spend less time in range due to pass movement; compensated by
                    // the AirCombat multiplier.
                    b.CurrentHP -= CombatMath.DamagePerHit(type.Attack, 0f) * dt * siegeAccuracy
                        * AirCombat.DamageMultiplier(type);
                    // Task85: only ground forces can grind a base to HP 0 (= capture). Air and sea attacks
                    // stop at HP 1 (the final point must always be taken by land troops).
                    float floor = TargetingRules.BaseHpFloor(type.Domain);
                    if (b.CurrentHP < floor) b.CurrentHP = floor;

                    // Task54: base-siege hit locations are reported as combat zones too (same reason as
                    // CombatStep).
                    state.CombatZones.ReportCombat(b.Position);

                    // Muzzle-effect throttling (Task42; consolidated into FireEffects.EmitThrottled in
                    // Task58). The same FireCooldown accumulator is shared with CombatStep, so if this
                    // attacker already fired at a unit this tick, nothing extra is emitted here (keeping
                    // the contract "visually at most one shot per FireIntervalHours per attacker"
                    // consistent whether the target is a unit or a base). TargetId=0: bases have no
                    // logical unit id (Task43; the Game layer treats TargetId==0 as "not a unit" and uses
                    // the default base impact height).
                    FireEffects.EmitThrottled(state, u, type, b.Position, 0, dt);
                    firedAtAnyBase = true;
                }

                // Task99: if at least one base was attacked this tick, consume ammo (attacking several
                // bases at once still consumes one tick's worth = matching the anti-unit consumption
                // rate).
                if (firedAtAnyBase) AmmoRules.ConsumeFire(u, type, dt);
            }
        }
    }
}
