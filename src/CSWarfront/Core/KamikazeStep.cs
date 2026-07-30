namespace CSWarfront.Core
{
    /// <summary>
    /// 自爆ドローン（UnitCategoryFlags.IsKamikaze）専用の交戦ステップ（Task79）。
    ///
    /// 背景: ユーザー報告「自爆ドローンが機銃で攻撃するようなアニメーションになっています。戦闘では
    /// モデルがターゲットに突進して自爆するようにしてください」。旧仕様（Task61）は通常のCombatStep
    /// パイプラインに自爆ドローンを乗せ、射程内でShotEventを発行しながらdtスケールのダメージを与え、
    /// UnitType.IsOneShot=trueにより「ダメージを与えた瞬間に自壊する」方式だった（つまり見た目は
    /// 他の航空ユニットと全く同じ射撃アニメーション）。本タスクでは射撃を完全に廃止し、
    /// 「目標をロック→ダイブで直接突入→体当たりで起爆」というキルチェーンへ置き換える。
    ///
    /// 設計（Acquire/Dive/Detonate）:
    ///   - Acquire: 自身のRangeを「検知半径」として扱い、その範囲内で目標をロックする。
    ///     ユニット目標は既存のTargetSearch.FindNearestHostile（宿敵Nemesis優先を含む通常のルール、
    ///     CanTargetDomainsによる領域フィルタも適用）をそのまま再利用する。ユニット目標が1体も
    ///     見つからなければ、外部脅威（ゴジラ/エイリアン、ThreatCombatStepと全く同じ実効射程
    ///     Range+threat.Radius・ThreatRelations.IsHostile()判定）から最近接の1体をロックする。
    ///     ユニットも外部脅威も見つからなければ、最後に敵対基地（BaseCombatStepと全く同じ規則:
    ///     水平距離Range以内・state.Relations.IsHostile()判定・Nemesis優先、ただし
    ///     CaptureGraceHours>0（占領猶予中）の基地は対象から完全に除外——ロックすらしない）を
    ///     最近接（Nemesis所有を優先）で1つロックする（Task79ギャップ修正: 旧仕様（Task61以前）は
    ///     通常のCombatStepパイプライン経由で基地も削れていたが、Task79のキルチェーン化で基地への
    ///     攻撃手段が完全に失われていた不具合）。
    ///     優先順位はユニット→外部脅威→基地（この3者を同時に狙う意味が無いため、単一の目標だけを
    ///     ロックする設計はTask61から変わらない。基地は「動く敵が1体も射程内にいない時の最後の
    ///     手段」という位置づけ）。
    ///     ロック結果はUnitInstance.TargetId（ユニット）/TargetThreatId（外部脅威）/TargetBaseId
    ///     （基地）へ毎tick書き込み直す（「ロックしたら固定」ではなく、CombatStep.TargetSearchと
    ///     同じく毎tick再探索する。近接するほど「最近接」は自然に安定するため、実質的に同じ相手を
    ///     ロックし続けることになる——決定的シミュレーションを保ちつつ実装を単純に保つ意図的な選択）。
    ///     MovementStepはAdvance(前段)がこのステップより先に実行されるため、ここで書いたロックは
    ///     次tickのダイブ移動から参照される（1tickの遅延は他の意思決定ステップと同じ許容範囲）。
    ///   - Dive: 実際の移動はMovementStep.AdvanceKamikaze（Domain.Air分岐、CruiseAltitudeを離脱し
    ///     目標の現在位置へ3D直線で突入、速度=Speed×DiveSpeedMultiplier）が担う。本ステップは
    ///     ロック状態(TargetId/TargetThreatId/TargetBaseId)を書くだけで、移動そのものには一切関与しない。
    ///   - Detonate: ロックした目標までの3D距離（WorldPos.DistanceTo、高度差込み——ダイブは水平方向
    ///     だけでなく降下もするため）がDetonateDistance以下になったら起爆する。Attack値をdt/命中率
    ///     ロール無しでCombatMath.DamagePerHitに通し1回だけ適用する（自爆弾頭は必中・確定ダメージ
    ///     として扱う、Task79の設計判断）。装甲は通常通り軽減する（対ユニットはtargetType.Armor、
    ///     対脅威はThreatCombatStep.ThreatArmorを共用）。CombatMatchup（兵科相性）は適用しない
    ///     ——自爆弾頭はどんな兵科にも同じ実ダメージを叩き込む「フルの直撃」という単純な仕様にした。
    ///     起爆の瞬間に自身のPositionを目標の現在位置へスナップしてからCurrentHPを0にする
    ///     （死亡判定・KillEvent発行そのものはCombatStepの第2パス「死亡はCurrentHP<=0から導出する」
    ///     という単一の真実源に委ねる、Task61のIsOneShotパターンを踏襲）。KillEvent.Positionは
    ///     このPositionを使うため、爆発エフェクト(KillFx)・撃破音(CombatFx.SpawnKillSounds)が
    ///     着弾地点＝目標の位置で鳴る。SuicideDroneはCombatFx.IsVehicleDestructionCategoryで
    ///     「車両撃破」判定される（Infantry/DroneInfantry以外は全てtrue）ため、Game層は無改造で
    ///     爆発の見た目・音の両方が自動的に付く。基地への起爆も全く同じ経路（自身のPositionを
    ///     基地の位置へスナップ→CurrentHP=0→CombatStep第2パスがKillEvent発行）を通るため、
    ///     Game層側の追加対応は不要（対ユニット/対脅威と同一のFX/音）。基地へのダメージは
    ///     CombatMath.DamagePerHit(Attack, 0f)（BaseCombatStepと同じ装甲0扱い）を1回だけ適用し
    ///     0未満にはクランプする。CurrentHPが0になった基地の占領移管そのものはOccupation.Advance
    ///     が通常通り担う（本ステップは基地のOwnerFactionIdやCaptureGraceHoursを一切書き換えない）。
    ///   - Target lost（撃破/消滅）: 次tickのAcquireで自動的に新しい目標を探す。ロックが外れている間、
    ///     MovementStepのダイブ対象解決はnullを返すため、既存のResolveDomainObjective
    ///     （OrderTargetPos追従・巡航高度復帰のAdvanceAir）へ自然にフォールバックする
    ///     （専用の「離脱シーケンス」処理は不要——CombatStepが目標を見失った通常ユニットを
    ///     Idleへ戻すのと全く同じパターン）。
    /// </summary>
    public static class KamikazeStep
    {
        /// <summary>起爆と判定する、目標までの3D距離（Task79）。ダイブは高度も含めた直線移動なので、
        /// 水平距離ではなく実距離（WorldPos.DistanceTo）で判定する。</summary>
        public const float DetonateDistance = 6f;

        public static void Advance(WarState state, float dt)
        {
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance self = state.Units[i];
                if (!self.IsAlive) continue;
                UnitType type = state.Types.Get(self.TypeKey);
                if (type == null || !type.Category.IsKamikaze()) continue;

                UnitInstance targetUnit = TargetSearch.FindNearestHostile(self, state.Units, state.Relations,
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
                if (dist > DetonateDistance) continue; // まだダイブ中（移動自体はMovementStepが担当）

                Detonate(state, self, type, targetUnit, targetThreat, targetBase);
            }
        }

        /// <summary>ThreatCombatStepと全く同じ規則（実効射程=Range+threat.Radius、水平距離、
        /// ThreatRelations.IsHostile()判定、撃破済み(IsDefeated)は除外）で最近接の1体を返す。
        /// 該当が無ければnull。</summary>
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

        /// <summary>ThreatCombatStepではなくBaseCombatStepと全く同じ規則（水平距離Range以内、
        /// state.Relations.IsHostile()判定＝Nemesisも敵対、CaptureGraceHours>0の基地は完全除外——
        /// ダメージ免除だけでなくロック候補からも外す）で、TargetSearch.FindNearestHostileと同じ
        /// Nemesis優先パターンの最近接1件を返す。該当が無ければnull。</summary>
        private static MilitaryBase FindNearestHostileBase(WarState state, UnitInstance self, UnitType type)
        {
            MilitaryBase bestHostile = null;
            float bestHostileDist = float.MaxValue;
            MilitaryBase bestNemesis = null;
            float bestNemesisDist = float.MaxValue;

            for (int j = 0; j < state.Bases.Count; j++)
            {
                MilitaryBase b = state.Bases[j];
                if (b.CaptureGraceHours > 0f) continue; // 猶予中は対象から完全除外（ロックすらしない）
                if (b.OwnerFactionId == null) continue;
                if (b.OwnerFactionId.Value == self.FactionId) continue;
                Relation r = state.Relations.Get(self.FactionId, b.OwnerFactionId.Value);
                if (!r.IsHostile()) continue;

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

        /// <summary>体当たり起爆: Attackをdt/命中率ロール無しで1回だけ適用し、自身をCurrentHP=0にして
        /// 死亡確定を次段（CombatStepの死亡判定第2パス）へ委ねる。爆発エフェクト/音が着弾地点で鳴る
        /// よう、自身のPositionを目標の現在位置へスナップしてから自壊させる。</summary>
        private static void Detonate(WarState state, UnitInstance self, UnitType type,
            UnitInstance targetUnit, ExternalThreat targetThreat, MilitaryBase targetBase)
        {
            if (targetUnit != null)
            {
                UnitType targetType = state.Types.Get(targetUnit.TypeKey);
                float targetArmor = targetType != null ? targetType.Armor : 0f;
                float dmg = CombatMath.DamagePerHit(type.Attack, targetArmor);

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
                // 基地への体当たり（Task79ギャップ修正）: BaseCombatStepと同じ装甲0扱い・dt/命中率
                // ロール無しの確定ダメージを1回だけ適用する。CombatMatchup（兵科相性）は他の2ケースと
                // 同様に適用しない。
                float dmg = CombatMath.DamagePerHit(type.Attack, 0f);
                targetBase.CurrentHP -= dmg;
                // Task85: 自爆ドローンは航空戦力なので、拠点HPは1で頭打ち（占領は地上戦力のみ）。
                float floor = TargetingRules.BaseHpFloor(type.Domain);
                if (targetBase.CurrentHP < floor) targetBase.CurrentHP = floor;

                state.CombatZones.ReportCombat(targetBase.Position);
                self.Position = targetBase.Position;
            }

            self.CurrentHP = 0f;
        }
    }
}
