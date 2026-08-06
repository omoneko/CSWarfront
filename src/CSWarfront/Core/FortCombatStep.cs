using System;

namespace CSWarfront.Core
{
    /// <summary>
    /// Task101: 築城の自動射撃（設計: 2026-08-04-fortifications-heli-rail-design.md §1.1）。
    ///
    ///  - Bunker（掩蔽壕）: 歩兵3体分の火力。射線（施設→目標）が市の建物に遮られると撃てない
    ///    （建物非貫通、CoverMap.BlocksLine）。単一目標（射程内の最近接敵対陸上ユニット）。
    ///  - ArtilleryPost（砲兵陣地）: 砲兵1体分の火力。曲射のため射線判定なし。最近接目標の
    ///    周囲Splash内の全敵対陸上ユニットへ同時ダメージ。
    ///
    /// 共通: 稼働中（Owner有り・HP>0）のみ。施設自身の弾薬（MilitaryBase.FortAmmo）を
    /// 射撃tickだけ消費し、弾切れは射撃停止のみ（ResupplyStepが補給圏内で回復させる）。
    /// ダメージは既存CombatStepと同じ期待値方式（命中率×相性×dt）。死亡判定はこのstepでは行わず、
    /// 後続のCombatStep第2パス（HP<=0→Dead+KillEvent）に任せる（KamikazeStepと同じ規約）。
    /// Invaderの部隊にも通常どおり射撃する（関係表がハードコードで敵対のため自然に成立）。
    /// </summary>
    public static class FortCombatStep
    {
        public const float BunkerAttack = 54f;        // 歩兵T1(18)×3
        public const float BunkerRange = 120f;
        public const float BunkerAccuracy = 0.75f;    // 歩兵と同じ
        public const float BunkerAmmoHours = 12f;     // 歩兵と同じ
        public const float BunkerFireIntervalHours = 0.3f;

        public const float ArtAttack = 55f;           // artillery T1
        /// <summary>Task111 (Workshop request): 120 -> 250. A static emplacement should outrange the mobile
        /// artillery it is built from; at 120 it was routinely outranged and never got to fire.</summary>
        public const float ArtRange = 250f;
        public const float ArtSplash = 30f;
        public const float ArtAccuracy = 0.35f;       // same as artillery units
        /// <summary>Task111 (Workshop request "15 shots instead of only 2 before resupply"): 4 -> 24 hours
        /// of firing per full ammo load. Together with the shorter visual fire interval below this yields
        /// about 16 visible shots per resupply.</summary>
        public const float ArtAmmoHours = 24f;
        public const float ArtFireIntervalHours = 1.5f; // Task111: 2.5 -> 1.5 (more visible firing)

        /// <summary>掩蔽壕自身の建物を射線判定から除外する半径（16×16mの対角半径強）。</summary>
        public const float SelfLosIgnoreRadius = 14f;

        public static void Advance(WarState state, float dt)
        {
            for (int j = 0; j < state.Bases.Count; j++)
            {
                MilitaryBase b = state.Bases[j];
                if (b.OwnerFactionId == null || b.CurrentHP <= 0f) continue;
                if (b.Type != BaseType.Bunker && b.Type != BaseType.ArtilleryPost) continue;
                if (b.FortAmmo <= 0f) continue; // 弾切れ=射撃停止のみ

                bool isBunker = b.Type == BaseType.Bunker;
                float range = isBunker ? BunkerRange : ArtRange;

                UnitInstance target = FindNearestHostileLand(state, b, range);
                if (target == null) continue;

                if (isBunker && state.Cover != null &&
                    state.Cover.BlocksLine(b.Position, target.Position, SelfLosIgnoreRadius))
                    continue; // 建物非貫通: 射線が遮られている

                float ammoHours = isBunker ? BunkerAmmoHours : ArtAmmoHours;
                float attack = isBunker ? BunkerAttack : ArtAttack;
                float accuracy = isBunker ? BunkerAccuracy : ArtAccuracy;
                UnitCategory attackerCategory = isBunker ? UnitCategory.Infantry : UnitCategory.Artillery;

                if (isBunker)
                {
                    ApplyFortDamage(state, b, target, attack, accuracy, attackerCategory, dt);
                }
                else
                {
                    // 砲兵陣地: 目標の周囲Splash内の全敵対陸上ユニットへ（目標自身を含む）。
                    for (int i = 0; i < state.Units.Count; i++)
                    {
                        UnitInstance u = state.Units[i];
                        if (!IsHostileLand(state, b, u)) continue;
                        if (target.Position.HorizontalDistanceTo(u.Position) > ArtSplash) continue;
                        ApplyFortDamage(state, b, u, attack, accuracy, attackerCategory, dt);
                    }
                }

                // 弾薬消費（射撃したtickのみ、ユニットのAmmoRules.ConsumeFireと同じ規約）。
                b.FortAmmo -= dt / ammoHours;
                if (b.FortAmmo < 0f) b.FortAmmo = 0f;

                // 発砲エフェクトの間引き（ユニットのFireEffectsと同じ考え方。施設はユニットIDを
                // 持たないためattackerId=0で積む）。
                b.FortFireCooldown -= dt;
                if (b.FortFireCooldown <= 0f)
                {
                    b.FortFireCooldown = isBunker ? BunkerFireIntervalHours : ArtFireIntervalHours;
                    state.AddShot(new ShotEvent(b.Position, target.Position,
                        isBunker ? ShotKind.Gunfire : ShotKind.IndirectFire,
                        b.OwnerFactionId.Value, 0, target.InstanceId, attackerCategory, false));
                }
            }
        }

        private static void ApplyFortDamage(WarState state, MilitaryBase b, UnitInstance target,
            float attack, float accuracy, UnitCategory attackerCategory, float dt)
        {
            UnitType targetType = state.Types.Get(target.TypeKey);
            float armor = targetType != null ? targetType.Armor : 0f;
            float matchup = targetType != null ? CombatMatchup.Multiplier(attackerCategory, targetType.Category) : 1f;
            float dmg = CombatMath.DamagePerHit(attack, armor) * dt * accuracy * matchup;
            dmg *= FortDefenseBonus.Multiplier(state, target, targetType); // 塹壕/掩蔽壕上の歩兵は被ダメ減
            target.CurrentHP -= dmg;
            state.CombatZones.ReportCombat(target.Position);
        }

        private static UnitInstance FindNearestHostileLand(WarState state, MilitaryBase b, float range)
        {
            UnitInstance best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!IsHostileLand(state, b, u)) continue;
                float d = b.Position.HorizontalDistanceTo(u.Position);
                if (d > range) continue;
                if (d < bestDist) { bestDist = d; best = u; }
            }
            return best;
        }

        private static bool IsHostileLand(WarState state, MilitaryBase b, UnitInstance u)
        {
            if (!u.IsAlive || u.IsCarried) return false;
            if (!state.Relations.Get(b.OwnerFactionId.Value, u.FactionId).IsHostile()) return false;
            UnitType t = state.Types.Get(u.TypeKey);
            return t != null && t.Domain == Domain.Land;
        }
    }
}
