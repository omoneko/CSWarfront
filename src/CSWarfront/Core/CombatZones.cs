using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>
    /// 1件の「戦闘域」。CombatZoneTracker.ReportCombatで生成/更新される、UnityEngine非依存の値型
    /// （Task54: 戦闘付近の民間交通の迂回）。
    /// </summary>
    public struct CombatZone
    {
        public readonly WorldPos Center;
        public readonly float Radius;
        public readonly float RemainingHours;

        public CombatZone(WorldPos center, float radius, float remainingHours)
        {
            Center = center;
            Radius = radius;
            RemainingHours = remainingHours;
        }
    }

    /// <summary>
    /// 発砲/被弾地点の報告から「戦闘域」を追跡する（Task54）。CombatStep/BaseCombatStepが
    /// ダメージを実適用したタイミングで対象位置をReportCombatへ渡すことで、近接した報告は
    /// 1つのゾーンへマージ・延長され、離れた報告は新規ゾーンになる。Game層のCombatRoadBlockerが
    /// このゾーン集合を読み取り、範囲内の道路セグメントを一時的に封鎖する。
    /// UnityEngine非依存・決定的・O(n)（nはZones件数、MaxZonesで上限されるためO(1)相当）。
    /// </summary>
    public class CombatZoneTracker
    {
        /// <summary>戦闘地点の周囲この半径（マップ単位＝概ねメートル）を「戦闘域」とする。
        /// ReportCombatの近傍マージ判定にも同じ半径を使う（「同じ戦闘」とみなす距離のしきい値）。</summary>
        public const float ZoneRadius = 120f;

        /// <summary>最後の発砲/被弾報告からこの時間（ゲーム内時間）でゾーンを解除する。</summary>
        public const float ZoneLingerHours = 2f;

        /// <summary>同時に追跡するゾーン数の上限。大規模乱戦でO(n²)的なゾーン管理コストが
        /// 際限なく増えないための防御的上限。上限到達時は最も期限の近いゾーンを1つ落として道を空ける
        /// （決定的：同点はより小さいインデックスを優先）。</summary>
        public const int MaxZones = 16;

        private readonly List<CombatZone> _zones = new List<CombatZone>();

        public IList<CombatZone> Zones => _zones;

        /// <summary>
        /// 発砲/被弾地点を1件報告する。ZoneRadius以内に既存ゾーンがあれば、そのうち最も近いものへ
        /// マージする（中心を単純平均、残り時間をZoneLingerHoursへ延長=リセット）。無ければ新設する。
        /// MaxZonesに達している状態で新設が必要な場合、最も残り時間が少ない（＝最も期限が近い）
        /// ゾーンを1つ削除してから追加する。
        /// </summary>
        public void ReportCombat(WorldPos position)
        {
            int nearestIndex = -1;
            float nearestDist = float.MaxValue;
            for (int i = 0; i < _zones.Count; i++)
            {
                float d = _zones[i].Center.HorizontalDistanceTo(position);
                if (d <= ZoneRadius && d < nearestDist)
                {
                    nearestDist = d;
                    nearestIndex = i;
                }
            }

            if (nearestIndex >= 0)
            {
                CombatZone existing = _zones[nearestIndex];
                WorldPos mergedCenter = new WorldPos(
                    (existing.Center.X + position.X) * 0.5f,
                    (existing.Center.Y + position.Y) * 0.5f,
                    (existing.Center.Z + position.Z) * 0.5f);
                _zones[nearestIndex] = new CombatZone(mergedCenter, ZoneRadius, ZoneLingerHours);
                return;
            }

            if (_zones.Count >= MaxZones)
            {
                int soonestIndex = 0;
                float soonestRemaining = _zones[0].RemainingHours;
                for (int i = 1; i < _zones.Count; i++)
                {
                    if (_zones[i].RemainingHours < soonestRemaining)
                    {
                        soonestRemaining = _zones[i].RemainingHours;
                        soonestIndex = i;
                    }
                }
                _zones.RemoveAt(soonestIndex);
            }

            _zones.Add(new CombatZone(position, ZoneRadius, ZoneLingerHours));
        }

        /// <summary>全ゾーンの残り時間をdt分だけ減算し、0以下になったものを除去する。
        /// MilitaryManager.OnSimTickから毎tick、CombatStep/BaseCombatStepと同じdtで呼ばれる想定。</summary>
        public void Advance(float dt)
        {
            for (int i = _zones.Count - 1; i >= 0; i--)
            {
                CombatZone z = _zones[i];
                float remaining = z.RemainingHours - dt;
                if (remaining <= 0f)
                {
                    _zones.RemoveAt(i);
                    continue;
                }
                _zones[i] = new CombatZone(z.Center, z.Radius, remaining);
            }
        }
    }
}
