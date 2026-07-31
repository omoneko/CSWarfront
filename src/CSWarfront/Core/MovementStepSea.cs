namespace CSWarfront.Core
{
    /// <summary>MovementStepの続き（Task78: 海上ユニット(Domain.Sea)の移動則）。500行/ファイルの
    /// 上限に収めるため、AdvanceSea本体と迂回ロジックだけをこのファイルへ分離した
    /// （MilitaryBase.cs/MilitaryManager*.csと同じpartial classパターン）。
    ///
    /// 陸地に阻まれて直進できない海上ユニットが、いつまでも波打ち際で足止めされ続ける
    /// （ユーザー報告「海上ユニットが敵拠点へ移動せず自拠点にこもったまま」）不具合の対策。
    /// 真の水上経路探索（A*等）はまだ無いMVPの簡易的な「壁沿い歩き」: 直進の着地点が水域でなければ
    /// SeaDetourAnglesDegの順に決定的な迂回方向を試し、最初に水域へ着地する方向を採用する。
    /// 岬・半島の付け根程度は回り込めるが、完全に閉じた入り江や内陸の目標までは辿り着けないことが
    /// ある——その場合はSeaBlockedHoursが育ちSeaBlockedIdleHoursで安全にIdleへ諦める。
    /// 1tickあたり最大でもSeaDetourAnglesDegの長さ(6)回しか水域判定を行わないため、探索コストは
    /// 常に一定で頭打ちになる。</summary>
    public static partial class MovementStep
    {
        private static readonly float[] SeaDetourAnglesDeg = { 30f, -30f, 60f, -60f, 90f, -90f };

        /// <summary>Task78: 海上ユニットが直進・迂回のいずれの方向にも一歩も進めない状態が
        /// このゲーム内時間だけ続いたら、そのtickでState=Idleへ遷移し、以後は目的地
        /// (OrderTargetPos/RallyPoint)が変わるまで移動を一切試みない（毎tick迂回を探索し続けて
        /// 見た目がスピンし続ける／CPUを浪費し続けるのを防ぐ）。目的地が変われば
        /// UnitInstance.SeaBlockedHoursは即座に0へリセットされ、また新たにこの時間だけ迂回を試みる。</summary>
        public const float SeaBlockedIdleHours = 6f;

        /// <summary>目的地が実質的に同じとみなす閾値（水平距離）。これ未満の差はSeaBlockedHoursの
        /// リセット判定において「同じ命令が継続している」として扱う。</summary>
        private const float SeaObjectiveChangeEpsilon = 0.5f;

        /// <summary>Task61/Task78: 海上ユニットの移動。RoadGraph/CoverMapを一切使わず目的地へ直線移動を
        /// 試みる。直進の着地点が水域でなければ（陸地/岬に阻まれれば）、SeaDetourAnglesDegの順に
        /// 決定的な迂回方向を試し、最初に水域へ着地する方向を採用する（簡易wall-follow）。
        /// いずれも水域へ着地できなければ、そのtickは一切移動せずSeaBlockedHoursへdtを積算する
        /// （クラス冒頭のIWaterSamplerコメント参照。海軍専用の経路探索がまだ無いMVPの既知の制約——
        /// 完全に陸に囲まれた目標へは物理的に到達できないことがある）。Yは水面サンプラーが返す値を
        /// そのまま採用する（サンプリングに失敗すれば従来のYを維持）。water==nullの場合は「常に水上」
        /// とみなし自由に移動する（Height同様、Game層未供給時のテスト容易性のための安全側フォールバック、
        /// この場合迂回/足止めのロジックは一切発生しない＝直進のみで常に成功する）。</summary>
        /// <summary>Task92: SeaGrid経路のウェイポイントへの到達判定距離。セルサイズ（96m）より
        /// やや小さく取り、通過しながら次のウェイポイントへ滑らかに切り替える。</summary>
        public const float SeaWaypointArrivalDistance = 60f;

        private static void AdvanceSea(UnitInstance u, float stepLen, WorldPos objective, IWaterSampler water, float dt)
        {
            bool objectiveChanged = !u.SeaLastObjective.HasValue ||
                System.Math.Abs(u.SeaLastObjective.Value.X - objective.X) >= SeaObjectiveChangeEpsilon ||
                System.Math.Abs(u.SeaLastObjective.Value.Z - objective.Z) >= SeaObjectiveChangeEpsilon;
            if (objectiveChanged)
            {
                u.SeaLastObjective = objective;
                u.SeaBlockedHours = 0f;
            }
            else if (u.SeaBlockedHours >= SeaBlockedIdleHours)
            {
                // Task78: 同じ目的地に対してこれ以上探索しても無駄と分かっている。命令が変わるまで
                // 一切移動を試みない（次tickからはResolveDomainObjectiveがState!=Movingで弾くため
                // このメソッド自体呼ばれなくなる）。
                u.State = UnitState.Idle;
                return;
            }

            // Task92: SeaGrid経路（InvasionOrders/ApplyRallyが張る）があればウェイポイントを順に辿る。
            // 各歩の水域チェック・壁沿い迂回・足止めカウンタは従来どおり機能する（グリッドは粗いため
            // 最終防衛線として残す）。経路が尽きたら本来の目的地への直線に戻る。
            WorldPos steer = objective;
            if (u.Path != null)
            {
                while (u.PathIndex < u.Path.Count &&
                       u.Position.HorizontalDistanceTo(u.Path[u.PathIndex]) <= SeaWaypointArrivalDistance)
                    u.PathIndex++;
                if (u.PathIndex < u.Path.Count) steer = u.Path[u.PathIndex];
            }
            objective = steer;

            float dist = u.Position.HorizontalDistanceTo(objective);
            if (dist <= 0.01f) { u.SeaBlockedHours = 0f; return; } // 既に到達済み。

            bool arriving = dist <= stepLen;
            float nx, nz;
            if (arriving) { nx = objective.X; nz = objective.Z; }
            else
            {
                float t = stepLen / dist;
                nx = u.Position.X + (objective.X - u.Position.X) * t;
                nz = u.Position.Z + (objective.Z - u.Position.Z) * t;
            }

            if (water == null || water.IsWater(nx, nz))
            {
                CommitSeaStep(u, nx, nz, water);
                u.SeaBlockedHours = 0f;
                return;
            }

            // Task78: 直進が陸地に阻まれた。到達直前（arriving）は迂回すると行き過ぎてしまうため
            // 対象外とし、それ以外の場合のみ決定的な迂回方向を順に試す。
            if (!arriving && TryFindSeaDetourStep(u, objective, stepLen, water, out nx, out nz))
            {
                CommitSeaStep(u, nx, nz, water);
                u.SeaBlockedHours = 0f;
                return;
            }

            // 直進・迂回のいずれも水域へ着地できなかった＝このtickは完全に足止め。
            u.SeaBlockedHours += dt;
            if (u.SeaBlockedHours >= SeaBlockedIdleHours)
                u.State = UnitState.Idle;
        }

        /// <summary>SeaDetourAnglesDegの順に、現在位置から目的地への進行方向をその角度だけ回転させた
        /// 同じ歩幅(stepLen)の着地点を試し、最初に水域と判定されたものをnx/nzへ返す（true）。
        /// いずれも水域でなければfalse（nx/nzは未定義のまま）。waterはこの時点で必ず非null
        /// （呼び出し元のAdvanceSeaがwater==nullの場合は既に直進側で処理を終えている）。</summary>
        private static bool TryFindSeaDetourStep(UnitInstance u, WorldPos objective, float stepLen, IWaterSampler water, out float nx, out float nz)
        {
            nx = 0f; nz = 0f;
            float dx = objective.X - u.Position.X;
            float dz = objective.Z - u.Position.Z;
            float mag = (float)System.Math.Sqrt(dx * dx + dz * dz);
            if (mag <= 0.0001f) return false;
            dx /= mag; dz /= mag; // 単位方向ベクトル

            for (int i = 0; i < SeaDetourAnglesDeg.Length; i++)
            {
                double rad = SeaDetourAnglesDeg[i] * System.Math.PI / 180.0;
                float cos = (float)System.Math.Cos(rad);
                float sin = (float)System.Math.Sin(rad);
                float rdx = dx * cos - dz * sin;
                float rdz = dx * sin + dz * cos;
                float tx = u.Position.X + rdx * stepLen;
                float tz = u.Position.Z + rdz * stepLen;
                if (water.IsWater(tx, tz))
                {
                    nx = tx; nz = tz;
                    return true;
                }
            }
            return false;
        }

        /// <summary>実際に位置を更新する（Yは水面サンプラーが返す値、失敗すれば従来のYを維持）。
        /// 直進・迂回どちらの着地点でも共通で使う（AdvanceSea参照）。</summary>
        private static void CommitSeaStep(UnitInstance u, float nx, float nz, IWaterSampler water)
        {
            float ny = u.Position.Y;
            float level;
            if (water != null && water.TrySampleWaterLevel(nx, nz, out level))
                ny = level;

            u.Position = new WorldPos(nx, ny, nz);
        }
    }
}
