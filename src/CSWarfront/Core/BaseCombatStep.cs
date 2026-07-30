using System;

namespace CSWarfront.Core
{
    /// <summary>ユニットが敵対基地を射程内で攻撃する（純ロジック）。</summary>
    public static class BaseCombatStep
    {
        /// <summary>基地攻めにおける命中率の下限（Task38）。静止した建物は動くユニットより狙いやすいため、
        /// 素の命中率がこれより低い兵科（例: 命中率0.35のArtillery）でも、基地相手にはこの値まで
        /// 底上げする。これにより砲兵は「対ユニットでは当てにくいが、対基地では依然強力」という
        /// 攻城兵器としての立ち位置を保つ。</summary>
        public const float SiegeAccuracyFloor = 0.8f;

        /// <summary>Task89（ユーザー要望「各基地HPは徐々に回復する仕様に」）: 基地HPの自然回復量
        /// （ゲーム内1時間あたり）。攻撃が止めば拠点は時間とともに全快へ向かい、航空・海上・ミサイルが
        /// 床(1)まで削った拠点も放置すれば回復する。占領を成立させるには、この回復速度を上回る
        /// 継続的な地上攻撃が必要（Tank_T1の攻城DPS 32/h &gt; 20/h なので戦車1両でも正味では削れるが、
        /// 歩兵1体（16/h）では回復に負ける、という調整。TargetingRulesTests参照）。</summary>
        public const float BaseRegenPerHour = 20f;

        public static void Advance(WarState state, float dt)
        {
            // 新設基地の占領猶予を先に消化する。猶予中の基地はこのtickのダメージループから完全に除外する
            // （プレイヤーが両陣営を配置し終える前に一方的に占領されるのを防ぐ）。
            // Task89: あわせて自然回復もここで先に適用する（回復→攻撃の順。同tick内では
            // 「攻撃DPS − 回復」が正味の削り量になる）。HP0（占領処理待ち）の基地は回復させない
            // ——回復させると占領が二度と成立しなくなる。
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

                // Task79: 自爆ドローンは基地への継続的な射程内砲撃（旧: ダメージ1回でIsOneShot自壊）を
                // 一切行わない（ShotEventを出さないというTask79の契約を対基地でも一貫させる）。
                // 対拠点への攻撃自体は失っていない——KamikazeStepが「ユニット/外部脅威が1体も射程内に
                // いない時に限り、最後の手段として敵対基地へ突進・体当たり起爆する」形で別途担う
                // （このリワーク当初は対基地攻撃を丸ごと見送っていたが、それにより自爆ドローンが基地を
                // 一切攻撃できなくなる回帰が生じたため、KamikazeStep側で拾い直した。詳細はKamikazeStep
                // のクラス冒頭コメント参照）。
                if (type.Category.IsKamikaze()) continue;

                // Task85: 戦闘機（対空専任）・空母（プラットフォーム専任）は拠点を一切攻撃しない。
                if (!TargetingRules.CanAttackBase(type.Category)) continue;

                for (int j = 0; j < state.Bases.Count; j++)
                {
                    var b = state.Bases[j];
                    if (b.CaptureGraceHours > 0f) continue; // 猶予中は無敵
                    if (b.OwnerFactionId == null) continue;
                    if (b.OwnerFactionId.Value == u.FactionId) continue;
                    if (!state.Relations.Get(u.FactionId, b.OwnerFactionId.Value).IsHostile()) continue; // Task59: Nemesisも敵対として扱う
                    if (u.Position.HorizontalDistanceTo(b.Position) > type.Range) continue;

                    // Task88: 既にこの攻撃側のHP床（航空/海上=1）に達している拠点へは攻撃しない
                    // （ダメージ0の爆撃を延々と続け、発砲エフェクトだけが出続けるのを防ぐ。
                    // 実機報告「爆撃機が敵拠点HPが1になっても攻撃をやめない」の修正）。
                    float hpFloor = TargetingRules.BaseHpFloor(type.Domain);
                    if (b.CurrentHP <= hpFloor) continue;
                    // Attack はゲーム内1時間あたりのダメージ量。実際に適用するダメージは経過ゲーム内時間(dt)と
                    // 命中率(Task38)に比例する。ただし静止した建物は動くユニットより狙いやすい格好の的なので、
                    // 命中率にはSiegeAccuracyFloor(0.8)の下限を設ける（例: 命中率0.35の砲兵でも基地攻めでは
                    // 0.8として扱う＝素の命中率が低くても砲兵は依然として有効な攻城兵器のままにする）。
                    float accuracy = CombatSynergy.AccuracyFor(state, u, type);
                    float siegeAccuracy = Math.Max(accuracy, SiegeAccuracyFloor);
                    // Task86: 航空（爆撃機）はパス移動で射程内滞在時間が減るぶんAirCombat側の倍率で補正する。
                    b.CurrentHP -= CombatMath.DamagePerHit(type.Attack, 0f) * dt * siegeAccuracy
                        * AirCombat.DamageMultiplier(type);
                    // Task85: 拠点をHP0（＝占領）まで削れるのは地上戦力のみ。航空・海上の攻撃は
                    // HP1で頭打ちにする（最後の1は必ず陸上部隊が削る）。
                    float floor = TargetingRules.BaseHpFloor(type.Domain);
                    if (b.CurrentHP < floor) b.CurrentHP = floor;

                    // Task54: 被弾地点（基地攻め）も戦闘域として報告する（CombatStepと同じ理由）。
                    state.CombatZones.ReportCombat(b.Position);

                    // 発砲エフェクトの間引き（Task42、Task58でFireEffects.EmitThrottledへ集約）。
                    // CombatStepと同じFireCooldownアキュムレータを共有するため、同tick内で既に
                    // ユニット攻撃側で発砲済みなら、ここでは重ねて出さない（攻撃側1体につき見た目は
                    // 最大1発/FireIntervalHoursという契約を、対象がユニットか基地かによらず一貫させる
                    // ため）。TargetId=0: 基地には論理ユニットIDが無い（Task43。Game層はTargetId==0を
                    // 「ユニットでない対象」として扱い、基地用の既定の着弾高さを使う）。
                    FireEffects.EmitThrottled(state, u, type, b.Position, 0, dt);
                }
            }
        }
    }
}
