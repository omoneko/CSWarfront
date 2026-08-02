namespace CSWarfront.Core
{
    /// <summary>交戦tick（純ロジック）。射程内の敵を探し、毎tick DamagePerHit を適用する。</summary>
    public static class CombatStep
    {
        public static void Advance(WarState state, float dt)
        {
            // Task97: ターゲット探索の総当たりO(N²)を避けるため、このtickのユニット配置で
            // 空間グリッドを組み直す（O(N)。結果は総当たり版と完全に同一、UnitSpatialGrid参照）。
            state.UnitGrid.Build(state.Units);

            // 1) ターゲット選定と発火（ダメージは後段で一括適用しないシンプル版：都度適用）
            for (int i = 0; i < state.Units.Count; i++)
            {
                var self = state.Units[i];
                if (!self.IsAlive) continue;
                var type = state.Types.Get(self.TypeKey);
                if (type == null) continue;

                // Task79: 自爆ドローン（UnitCategoryFlags.IsKamikaze）は通常の射撃パイプラインに
                // 一切乗らない（ShotEventも発行しない・dtスケールのダメージも与えない）。
                // 目標のロック・突進・体当たり起爆はKamikazeStep（MovementStepと連携）が完結して扱う。
                // このユニットのCurrentHPがKamikazeStepの起爆で0になった場合の死亡判定・KillEvent発行は、
                // 下の第2パス（死亡はCurrentHP<=0から導出するという単一の真実源）がそのまま拾う。
                if (type.Category.IsKamikaze()) continue;

                // Task99: 補給トラックは非武装（CanTargetDomains=Noneのため探索しても候補が空になるが、
                // 探索コスト自体を省くためここで早期スキップする）。
                if (type.Category == UnitCategory.SupplyTruck) continue;

                // Task99: 弾切れ（Ammo<=0）は射撃停止のみ——ターゲットを解除して非交戦へ戻す
                // （移動・占領は可能なまま）。補給（ResupplyStep/SupplyTruckStep）で回復すれば
                // 次tickから通常どおり交戦に復帰する。Invader・弾薬制対象外はHasAmmoが常にtrue。
                if (!AmmoRules.HasAmmo(self, type))
                {
                    if (self.State == UnitState.Engaging) self.State = UnitState.Idle;
                    self.TargetId = null;
                    continue;
                }

                // ターゲット選定は依然として単純な「射程内最近接の敵」（TargetSearch）。
                // 有利な相性（CombatMatchup）を優先してターゲットを選ぶ賢いAIは将来の拡張課題とする。
                // Task61: type.CanTargetDomainsで領域フィルタをかける（AntiAir以外の陸上兵科は航空ユニットを
                // 一切狙わない、艦艇は航空ユニットを狙わない、航空ユニットは全領域を狙える、等）。
                var target = TargetSearch.FindNearestHostile(self, state.UnitGrid, state.Relations, type.Range,
                    type.CanTargetDomains, state.Types);
                if (target == null)
                {
                    if (self.State == UnitState.Engaging) self.State = UnitState.Idle;
                    self.TargetId = null;
                    continue;
                }
                self.State = UnitState.Engaging;
                self.TargetId = target.InstanceId;
                var targetType = state.Types.Get(target.TypeKey);
                float targetArmor = targetType != null ? targetType.Armor : 0f;
                float matchup = targetType != null
                    ? CombatMatchup.Multiplier(type.Category, targetType.Category)
                    : 1.0f;

                // Task90（ユーザー要望）: 対空兵科の対航空攻撃は離散的な1発ごとの命中ロールで解決する
                // （自爆ドローンには機銃、戦闘機・爆撃機には対空ミサイル。Tier別命中率で外れもある）。
                // 期待値方式の連続ダメージ・FireEffectsの間引きはどちらも使わない（AntiAirCombat参照）。
                if (type.Category == UnitCategory.AntiAir && targetType != null && targetType.Domain == Domain.Air)
                {
                    AdvanceAntiAirShot(state, self, type, target, targetType, targetArmor, matchup, dt);
                    continue;
                }
                // Attack はゲーム内1時間あたりのダメージ量。実際に適用するダメージは経過ゲーム内時間(dt)と
                // 兵科相性倍率(CombatMatchup、Task29)、命中率(CombatSynergy.AccuracyFor、Task38)に比例する。
                // 命中率は乱数抽選ではなく期待値ダメージ倍率として適用する（決定的シミュレーションを保つため）。
                float accuracy = CombatSynergy.AccuracyFor(state, self, type);
                // Task86: 航空ユニットはパス移動で射程内滞在時間が減るぶん、AirCombat.DamageMultiplierで補正する。
                float dmg = CombatMath.DamagePerHit(type.Attack, targetArmor) * dt * matchup * accuracy
                    * AirCombat.DamageMultiplier(type);
                // 撃破報酬（Task35）: このダメージでHPが初めて0以下へ落ちた瞬間だけ、攻撃側(self)の勢力へ
                // Research.KillReward を与える。wasAlive フラグで「このダメージがとどめだったか」を判定し、
                // 同tick内で同じ的へ複数回ダメージが入っても二重付与しない。
                bool wasAlive = target.CurrentHP > 0f;
                target.CurrentHP -= dmg;
                if (wasAlive && target.CurrentHP <= 0f) AwardKillReward(state, self.FactionId, targetType);

                // Task54: 被弾地点を「戦闘域」として報告する（民間交通の迂回用）。近傍への報告は
                // CombatZoneTracker側でマージされるため、ここでは間引き不要。
                state.CombatZones.ReportCombat(target.Position);

                // 発砲エフェクトの間引き（Task42、Task58でFireEffects.EmitThrottledへ集約）: 実際に
                // ダメージを与えたこのtickでのみFireCooldownをdt分だけ減算する。0以下になった瞬間だけ
                // ShotEventを1つ積み、FireIntervalHoursへリセットする（乱数不使用・決定的）。
                FireEffects.EmitThrottled(state, self, type, target.Position, target.InstanceId, dt);

                // Task99: 射撃したtickだけ弾薬を消費する（連続ダメージ方式に合わせた連続消費）。
                AmmoRules.ConsumeFire(self, type, dt);
            }
            // 2) 死亡判定
            // Task51: ここが「ユニットが実際にDeadへ遷移する、まさにその瞬間」なので、撃破音の
            // トリガーとしてKillEventを1件だけ積む（State != Deadを条件にしているため二重には積まない）。
            // Task53: KillEventに被撃破ユニットのUnitCategoryを積む（歩兵系の撃破音オミット判定用）。
            // UnitTypeが見つからない防御的ケースではdefault(UnitCategory)（Tank=0、撃破音は鳴る側）にする。
            for (int i = 0; i < state.Units.Count; i++)
            {
                var u = state.Units[i];
                if (u.State != UnitState.Dead && u.CurrentHP <= 0f)
                {
                    u.State = UnitState.Dead;
                    var uType = state.Types.Get(u.TypeKey);
                    var category = uType != null ? uType.Category : default(UnitCategory);
                    state.AddKill(new KillEvent(u.Position, u.FactionId, category));
                }
            }
        }

        /// <summary>Task90: 対空の対航空・離散射撃。FireCooldownをdtで消化し、0以下になった瞬間に
        /// 1発発射する: AntiAirCombat.RollHit（決定的ハッシュ、Tier別命中率）で命中/外れをロールし、
        /// 命中時のみ「Attack × FireIntervalHours × 相性」の一括ダメージ（＝連続方式の1発射間隔ぶんの
        /// ダメージをまとめて適用する形。期待DPS = 命中率×Attack×相性）。外れはノーダメージで、
        /// Missed=trueのShotEventだけを積む（Game層のフレア/回避演出用）。ShotKindは機銃=Gunfire、
        /// 対空ミサイル=SamMissile（UnitTypeのShotKindは使わない——目標の種類で撃ち分けるため）。</summary>
        private static void AdvanceAntiAirShot(WarState state, UnitInstance self, UnitType type,
            UnitInstance target, UnitType targetType, float targetArmor, float matchup, float dt)
        {
            self.FireCooldown -= dt;
            if (self.FireCooldown > 0f) return;
            self.FireCooldown = type.FireIntervalHours;

            bool usesMissile = AntiAirCombat.UsesMissileAgainst(targetType.Category);
            float chance = AntiAirCombat.HitChanceFor(type.Tier, targetType.Category);
            bool hit = AntiAirCombat.RollHit(self.InstanceId, target.InstanceId, state.TickCounter, chance);

            if (hit)
            {
                float dmg = CombatMath.DamagePerHit(type.Attack, targetArmor) * type.FireIntervalHours * matchup;
                bool wasAlive = target.CurrentHP > 0f;
                target.CurrentHP -= dmg;
                if (wasAlive && target.CurrentHP <= 0f) AwardKillReward(state, self.FactionId, targetType);
                state.CombatZones.ReportCombat(target.Position);
            }

            state.AddShot(new ShotEvent(self.Position, target.Position,
                usesMissile ? ShotKind.SamMissile : ShotKind.Gunfire,
                self.FactionId, self.InstanceId, target.InstanceId, type.Category, !hit));

            // Task99: 1発撃つたびに発射間隔ぶんの弾薬を消費（外れても消費する。期待消費速度は
            // 連続方式のdt消費と一致する——1発/FireIntervalHoursの離散射撃のため）。
            AmmoRules.ConsumeFire(self, type, type.FireIntervalHours);
        }

        /// <summary>撃破報酬（Task35）を killerFactionId の勢力へ加算する。victimType/勢力が見つからなければ
        /// 何もしない（防御的：整合性が崩れているケースで例外にしないため）。
        /// internal（Task79）: KamikazeStepの体当たり起爆でも同じ撃破報酬ロジックを再利用するため、
        /// ロジックを複製せずここを直接呼べるようにアクセス修飾子をprivateから緩めた
        /// （同一アセンブリ内のみ、公開APIの増加はない）。</summary>
        internal static void AwardKillReward(WarState state, byte killerFactionId, UnitType victimType)
        {
            if (victimType == null) return;
            Faction killer = state.FindFaction(killerFactionId);
            if (killer == null) return;
            killer.AddResearchPoints(Research.KillReward(victimType));
        }
    }
}
