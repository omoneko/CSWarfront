namespace CSWarfront.Core
{
    /// <summary>
    /// Dedicated engagement step for suicide drones (UnitCategoryFlags.IsKamikaze) (Task79).
    ///
    /// Background: user report "suicide drones animate as if strafing with a machine gun; in combat the
    /// model should charge into the target and blow itself up". The old design (Task61) ran suicide drones
    /// through the normal CombatStep pipeline — emitting ShotEvents in range while dealing dt-scaled damage,
    /// with UnitType.IsOneShot=true self-destructing them "the moment they deal damage" (so visually they
    /// fired exactly like other aircraft). This task removes shooting entirely and replaces it with the kill
    /// chain "lock a target → dive straight in → detonate on impact".
    ///
    /// Design (Acquire/Dive/Detonate):
    ///   - Acquire: the unit's own Range acts as its detection radius; targets are locked within it.
    ///     Unit targets reuse the existing TargetSearch.FindNearestHostile as-is (the normal rules including
    ///     Nemesis priority and the CanTargetDomains domain filter). With no unit target, the nearest
    ///     external threat (Godzilla/aliens; the exact ThreatCombatStep rules — effective range
    ///     Range+threat.Radius, ThreatRelations.IsHostile()) is locked. With neither, finally a hostile
    ///     base (the exact BaseCombatStep rules: horizontal distance within Range,
    ///     state.Relations.IsHostile(), Nemesis priority — but bases with CaptureGraceHours&gt;0 (capture
    ///     grace) are excluded outright, not even locked) is locked, nearest first (Nemesis-owned
    ///     preferred). (Task79 gap fix: before Task79 the normal CombatStep pipeline also chipped bases, but
    ///     the kill-chain rework had removed every way to attack bases.)
    ///     Priority is unit → external threat → base (there is no point aiming at all three at once; the
    ///     single-target lock design is unchanged from Task61. Bases are the "last resort when no moving
    ///     enemy is in range").
    ///     The lock is rewritten every tick into UnitInstance.TargetId (unit) / TargetThreatId (external
    ///     threat) / TargetBaseId (base) — not "lock once and hold" but a per-tick re-search exactly like
    ///     CombatStep.TargetSearch. As the drone closes in, "nearest" naturally stabilizes, so in practice
    ///     the same victim stays locked — a deliberate choice keeping the implementation simple while
    ///     preserving deterministic simulation.
    ///     MovementStep's Advance runs before this step, so a lock written here is consumed by the dive
    ///     movement on the NEXT tick (the one-tick latency is within the same tolerance as the other
    ///     decision steps).
    ///   - Dive: the actual movement is owned by MovementStep.AdvanceKamikaze (the Domain.Air branch —
    ///     leaves CruiseAltitude and flies a 3D straight line into the target's current position at
    ///     Speed×DiveSpeedMultiplier). This step only writes the lock state
    ///     (TargetId/TargetThreatId/TargetBaseId) and never touches movement.
    ///   - Detonate: when the 3D distance to the locked target (WorldPos.DistanceTo, altitude included —
    ///     the dive descends as well as closes) drops to DetonateDistance, detonate. Attack is passed once
    ///     through CombatMath.DamagePerHit with no dt scaling and no accuracy roll (a suicide warhead is
    ///     treated as a guaranteed, always-hitting hit — the Task79 design decision). Armor mitigates as
    ///     usual (targetType.Armor for units; ThreatCombatStep.ThreatArmor shared for threats).
    ///     CombatMatchup (category matchups) does NOT apply — a suicide warhead lands the same raw damage
    ///     on any category, a deliberately simple "full direct hit" rule. At the moment of detonation the
    ///     drone's own Position snaps to the target's current position before CurrentHP is zeroed (death
    ///     resolution and KillEvent emission are left to CombatStep's second pass — "death derives from
    ///     CurrentHP&lt;=0" as the single source of truth, following Task61's IsOneShot pattern).
    ///     KillEvent.Position uses this Position, so the explosion effect (KillFx) and the kill sound
    ///     (CombatFx.SpawnKillSounds) play at the impact point = the target's location. SuicideDrone counts
    ///     as a "vehicle destruction" in CombatFx.IsVehicleDestructionCategory (everything except
    ///     Infantry/DroneInfantry does), so the Game layer needs no changes for the explosion visuals and
    ///     audio. Base detonations flow through the very same path (snap own Position to the base → set
    ///     CurrentHP=0 → CombatStep's second pass emits the KillEvent), so no extra Game-side handling is
    ///     needed (identical FX/audio to the unit/threat cases). Base damage applies
    ///     CombatMath.DamagePerHit(Attack, 0f) once (the same zero-armor treatment as BaseCombatStep),
    ///     clamped to not go below zero. The ownership handover of a base whose HP reached 0 is handled by
    ///     Occupation.Advance as usual (this step never writes a base's OwnerFactionId or
    ///     CaptureGraceHours).
    ///   - Target lost (destroyed/despawned): the next tick's Acquire finds a new target automatically.
    ///     While unlocked, MovementStep's dive-target resolution returns null and behavior falls back
    ///     naturally to the existing ResolveDomainObjective (following OrderTargetPos and returning to
    ///     cruise altitude via AdvanceAir) — no dedicated "break-off sequence" needed, the same pattern as
    ///     CombatStep returning an ordinary unit to Idle when it loses its target.
    /// </summary>
    public static class KamikazeStep
    {
        /// <summary>3D distance to the target that counts as detonation (Task79). The dive moves in a
        /// straight 3D line including altitude, so the real distance (WorldPos.DistanceTo) is used rather
        /// than the horizontal one.</summary>
        public const float DetonateDistance = 6f;

        public static void Advance(WarState state, float dt)
        {
            // Task97: search via the spatial grid as CombatStep does (avoids the O(N²) sweep with
            // drone-heavy compositions). This step runs after MovementStep and before CombatStep, and
            // positions do not change within it, so one Build at the top suffices.
            state.UnitGrid.Build(state.Units);

            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance self = state.Units[i];
                if (!self.IsAlive) continue;
                UnitType type = state.Types.Get(self.TypeKey);
                if (type == null || !type.Category.IsKamikaze()) continue;

                UnitInstance targetUnit = TargetSearch.FindNearestHostile(self, state.UnitGrid, state.Relations,
                    type.Range, type.CanTargetDomains, state.Types);
                ExternalThreat targetThreat = targetUnit == null ? FindNearestHostileThreat(state, self, type) : null;
                MilitaryBase targetBase = (targetUnit == null && targetThreat == null)
                    ? FindNearestHostileBase(state, self, type) : null;

                if (targetUnit == null && targetThreat == null && targetBase == null)
                {
                    if (self.State == UnitState.Engaging) self.State = UnitState.Idle;
                    self.TargetId = null;
                    self.TargetThreatId = null;
                    self.TargetBaseId = null;
                    continue;
                }

                self.State = UnitState.Engaging;
                self.TargetId = targetUnit != null ? targetUnit.InstanceId : (uint?)null;
                self.TargetThreatId = targetThreat != null ? targetThreat.Id : (uint?)null;
                self.TargetBaseId = targetBase != null ? (ushort?)targetBase.BaseId : null;

                WorldPos targetPos = targetUnit != null ? targetUnit.Position
                    : (targetThreat != null ? targetThreat.Position : targetBase.Position);
                float dist = self.Position.DistanceTo(targetPos);
                if (dist > DetonateDistance) continue; // still diving (movement itself is MovementStep's job)

                Detonate(state, self, type, targetUnit, targetThreat, targetBase);
            }
        }

        /// <summary>Returns the nearest external threat under exactly the ThreatCombatStep rules
        /// (effective range = Range+threat.Radius, horizontal distance, ThreatRelations.IsHostile(),
        /// defeated (IsDefeated) excluded). Null when none qualifies.</summary>
        private static ExternalThreat FindNearestHostileThreat(WarState state, UnitInstance self, UnitType type)
        {
            ExternalThreat best = null;
            float bestDist = float.MaxValue;
            for (int j = 0; j < state.Threats.Count; j++)
            {
                ExternalThreat threat = state.Threats[j];
                if (threat.IsDefeated) continue;
                if (!state.ThreatRelations.Get(self.FactionId, threat.Kind).IsHostile()) continue;

                float effectiveRange = type.Range + threat.Radius;
                float d = self.Position.HorizontalDistanceTo(threat.Position);
                if (d > effectiveRange) continue;

                if (d < bestDist) { bestDist = d; best = threat; }
            }
            return best;
        }

        /// <summary>Returns the nearest hostile base under exactly the BaseCombatStep rules (not
        /// ThreatCombatStep's): horizontal distance within Range, state.Relations.IsHostile() (Nemesis is
        /// hostile too), bases with CaptureGraceHours&gt;0 excluded outright — not just damage-exempt but
        /// dropped from lock candidates — with the same Nemesis-priority pattern as
        /// TargetSearch.FindNearestHostile. Null when none qualifies.</summary>
        private static MilitaryBase FindNearestHostileBase(WarState state, UnitInstance self, UnitType type)
        {
            MilitaryBase bestHostile = null;
            float bestHostileDist = float.MaxValue;
            MilitaryBase bestNemesis = null;
            float bestNemesisDist = float.MaxValue;

            for (int j = 0; j < state.Bases.Count; j++)
            {
                MilitaryBase b = state.Bases[j];
                if (!FortificationRules.IsTargetable(b.Type)) continue; // Task101: trenches cannot be attacked
                if (b.CaptureGraceHours > 0f) continue; // in capture grace: fully excluded (not even locked)
                if (b.OwnerFactionId == null) continue;
                if (b.OwnerFactionId.Value == self.FactionId) continue;
                Relation r = state.Relations.Get(self.FactionId, b.OwnerFactionId.Value);
                if (!r.IsHostile()) continue;
                // Task88: never ram a base already at its HP floor (1 for aircraft, which suicide drones
                // are) — prevents losing the airframe to a zero-damage detonation (the same stop-firing
                // rule as BaseCombatStep).
                if (b.CurrentHP <= TargetingRules.BaseHpFloor(type.Domain)) continue;

                float d = self.Position.HorizontalDistanceTo(b.Position);
                if (d > type.Range) continue;

                if (r == Relation.Nemesis)
                {
                    if (d < bestNemesisDist) { bestNemesisDist = d; bestNemesis = b; }
                }
                else
                {
                    if (d < bestHostileDist) { bestHostileDist = d; bestHostile = b; }
                }
            }
            return bestNemesis != null ? bestNemesis : bestHostile;
        }

        /// <summary>Ramming detonation: applies Attack exactly once (no dt scaling, no accuracy roll), then
        /// sets self CurrentHP=0 and leaves the death resolution to the next stage (CombatStep's
        /// death-check second pass). The drone's own Position snaps to the target's current position first
        /// so the explosion effect/sound plays at the impact point.</summary>
        private static void Detonate(WarState state, UnitInstance self, UnitType type,
            UnitInstance targetUnit, ExternalThreat targetThreat, MilitaryBase targetBase)
        {
            if (targetUnit != null)
            {
                UnitType targetType = state.Types.Get(targetUnit.TypeKey);
                float targetArmor = targetType != null ? targetType.Armor : 0f;
                float dmg = CombatMath.DamagePerHit(type.Attack, targetArmor);
                dmg *= FortDefenseBonus.Multiplier(state, targetUnit, targetType); // Task101: infantry on trenches/bunkers

                bool wasAlive = targetUnit.CurrentHP > 0f;
                targetUnit.CurrentHP -= dmg;
                if (wasAlive && targetUnit.CurrentHP <= 0f)
                    CombatStep.AwardKillReward(state, self.FactionId, targetType);

                state.CombatZones.ReportCombat(targetUnit.Position);
                self.Position = targetUnit.Position;
            }
            else if (targetThreat != null)
            {
                float dmg = CombatMath.DamagePerHit(type.Attack, ThreatCombatStep.ThreatArmor);
                targetThreat.CurrentHP -= dmg;
                if (targetThreat.CurrentHP < 0f) targetThreat.CurrentHP = 0f;

                state.CombatZones.ReportCombat(targetThreat.Position);
                self.Position = targetThreat.Position;
            }
            else
            {
                // Ramming a base (Task79 gap fix): the same zero-armor treatment as BaseCombatStep, one
                // guaranteed application with no dt scaling or accuracy roll. CombatMatchup is not applied,
                // same as the other two cases.
                float dmg = CombatMath.DamagePerHit(type.Attack, 0f);
                targetBase.CurrentHP -= dmg;
                // Task85: suicide drones are air power, so base HP floors at 1 (capturing is ground-only).
                float floor = TargetingRules.BaseHpFloor(type.Domain);
                if (targetBase.CurrentHP < floor) targetBase.CurrentHP = floor;

                state.CombatZones.ReportCombat(targetBase.Position);
                self.Position = targetBase.Position;
            }

            self.CurrentHP = 0f;
        }
    }
}
