namespace CSWarfront.Core
{
    /// <summary>BallisticMissiles.TryLaunchの結果（Task63、ManualProduction.QueueResultと同じenum-styleのAPI）。</summary>
    public enum LaunchResult
    {
        Ok,
        BaseNotFound,
        NotMissileBase,
        NoOwner,
        /// <summary>StockpiledMissilesが0（発射可能な弾が無い）。</summary>
        NoStockpile,
        /// <summary>目標がMissileStep.MaxRangeMetersを超える（基地からの水平距離）。</summary>
        OutOfRange
    }

    /// <summary>飛翔中の弾道ミサイル1発（Task63）。UnityEngine非依存。Game層（MissileVisuals想定）が
    /// From/To/Progressから見た目の弾道弧を描く。Coreはこの構造体を「From→Toの直線をProgress(0..1)で
    /// たどる」という抽象化のみで扱い、見た目の弾道（高高度アーチ等）はGame層の演出に委ねる。</summary>
    public class MissileInFlight
    {
        public uint Id;
        public byte FactionId;
        public WorldPos From;
        public WorldPos To;
        /// <summary>飛行進捗（0..1）。1に達した瞬間に着弾解決される。</summary>
        public float Progress;
        /// <summary>迎撃成功済みか。Trueになった弾はMissileStep.Advance内で即座にリストから除去されるため、
        /// 外部から観測されるのはRecentImpacts（MissileImpactEvent.Intercepted=true）経由のみ
        /// （フィールド自体は将来のデバッグ/テスト向けに残す）。</summary>
        public bool Intercepted;
    }

    /// <summary>ミサイルの着弾/迎撃を表す軽量イベント（Task63）。ShotEvent/KillEventと同じ設計思想:
    /// ダメージ計算そのものには一切影響しない、間引かれた表現専用のトランジェント・イベント。
    /// Intercepted=falseは着弾（大きな爆発演出）、Intercepted=trueは迎撃（閃光のみ・ダメージ無し）を表す。</summary>
    public struct MissileImpactEvent
    {
        public WorldPos Position;
        public bool Intercepted;
        public byte FactionId;

        public MissileImpactEvent(WorldPos position, bool intercepted, byte factionId)
        {
            Position = position; Intercepted = intercepted; FactionId = factionId;
        }
    }

    /// <summary>
    /// 弾道ミサイルの発射・飛翔・迎撃・着弾（Task63、塊6 MVP）。ミサイル災害MOD
    /// （MissileDisaster.Core.InterceptDecision/InterceptorTier）を参考にした「高度帯＋水平射程＋Pk」の
    /// 迎撃判定を、CSWarfrontの決定的シミュレーション方針（乱数不使用、seedハッシュのみ）へ翻訳したもの。
    /// MissileDisasterとの違い: 高度帯を持つ複数の迎撃層ではなく、単一のAntiAir兵科×水平射程のみで
    /// 判定する単純化版（MVPスコープ、conventional弾頭のみ・核/クラスターは対象外）。
    /// UnityEngine非依存。決定的（乱数不使用、ハッシュのみ）。
    /// </summary>
    public static class MissileStep
    {
        /// <summary>発射から着弾までの飛行時間（ゲーム内時間、固定値）。</summary>
        public const float FlightHours = 1.5f;

        /// <summary>発射基地から目標までの最大水平距離。これを超える目標へはTryLaunchが失敗する。</summary>
        public const float MaxRangeMeters = 4000f;

        /// <summary>迎撃可能な水平距離（AntiAirユニットとミサイルの地上投影位置の間）。</summary>
        public const float InterceptRange = 300f;

        /// <summary>迎撃確率（ゲーム内1時間あたり）。1tickあたりの確率はこれにdtを掛けた値
        /// （AntiAir 1体・1tickにつき最大1回のロール）。</summary>
        public const float InterceptRatePerHour = 0.9f;

        /// <summary>着弾ダメージが及ぶ半径（水平距離）。</summary>
        public const float ImpactRadius = 90f;

        /// <summary>着弾によるユニットへのダメージ（装甲無視・固定値）。</summary>
        public const float ImpactDamageUnit = 400f;

        /// <summary>着弾による基地HPの減少量（固定値）。占領猶予中の基地は無傷（BaseCombatStepと同じ方針）。</summary>
        public const float ImpactDamageBase = 300f;

        /// <summary>着弾による外部脅威（KAIJU/Alien）へのダメージ（固定値）。</summary>
        public const float ImpactDamageThreat = 600f;

        /// <summary>
        /// baseIdの基地からtargetへ1発発射する。判定順序（先に失敗した方を返す）:
        ///  1. 基地が存在するか -&gt; BaseNotFound
        ///  2. b.Type が BaseType.MissileBase か -&gt; NotMissileBase
        ///  3. 所有勢力がいるか -&gt; NoOwner
        ///  4. StockpiledMissiles &gt; 0 か -&gt; NoStockpile
        ///  5. 基地からtargetまでの水平距離が MaxRangeMeters 以下か -&gt; OutOfRange
        /// 全て通ればStockpiledMissilesを1消費し、WarState.MissilesInFlightへ新しい飛翔体を追加してOkを返す。
        /// </summary>
        public static LaunchResult TryLaunch(WarState state, ushort baseId, WorldPos target)
        {
            MilitaryBase b = FindBase(state, baseId);
            if (b == null) return LaunchResult.BaseNotFound;
            if (b.Type != BaseType.MissileBase) return LaunchResult.NotMissileBase;
            if (b.OwnerFactionId == null) return LaunchResult.NoOwner;
            if (b.StockpiledMissiles <= 0) return LaunchResult.NoStockpile;

            float dist = b.Position.HorizontalDistanceTo(target);
            if (dist > MaxRangeMeters) return LaunchResult.OutOfRange;

            b.StockpiledMissiles--;
            var missile = new MissileInFlight
            {
                Id = state.AllocMissileId(),
                FactionId = b.OwnerFactionId.Value,
                From = b.Position,
                To = target,
                Progress = 0f,
                Intercepted = false
            };
            state.MissilesInFlight.Add(missile);
            return LaunchResult.Ok;
        }

        /// <summary>
        /// 飛翔中の全ミサイルを1tick進める（simスレッド、MilitaryManager.OnSimTick経由。
        /// ThreatCombatStepの後・経済tickの前に呼ぶこと）。着弾したもの・迎撃されたものはこの呼び出し内で
        /// WarState.MissilesInFlightから除去され、対応するMissileImpactEventがWarState.RecentImpactsへ
        /// 積まれる。呼び出し元はこのAdvanceより前にRecentImpacts.Clear()を行っていること
        /// （RecentShots/RecentKillsと同じ「呼び出し元が毎tickの先頭でクリアする」契約）。
        /// </summary>
        public static void Advance(WarState state, float dt)
        {
            state.TickCounter++;

            for (int i = state.MissilesInFlight.Count - 1; i >= 0; i--)
            {
                MissileInFlight m = state.MissilesInFlight[i];

                m.Progress += FlightHours > 0f ? dt / FlightHours : 1f;
                if (m.Progress >= 1f)
                {
                    m.Progress = 1f;
                    ResolveImpact(state, m);
                    state.MissilesInFlight.RemoveAt(i);
                    continue;
                }

                // 迎撃判定は降下半分（Progress > 0.5）のみ。上昇/中間段は迎撃対象外という単純化
                // （MissileDisasterの高度帯モデルをAntiAirの水平射程のみへ翻訳した簡略版、Task63仕様）。
                if (m.Progress > 0.5f && TryIntercept(state, m, dt))
                {
                    state.AddImpact(new MissileImpactEvent(GroundTrackPos(m), true, m.FactionId));
                    state.MissilesInFlight.RemoveAt(i);
                }
            }
        }

        /// <summary>Fromからm.Progressに応じて線形補間したミサイルの現在位置（地上投影・見た目の弾道弧は
        /// Game層の演出、Coreは直線補間のみを扱う）。</summary>
        private static WorldPos GroundTrackPos(MissileInFlight m)
        {
            float t = m.Progress;
            return new WorldPos(
                m.From.X + (m.To.X - m.From.X) * t,
                m.From.Y + (m.To.Y - m.From.Y) * t,
                m.From.Z + (m.To.Z - m.From.Z) * t);
        }

        /// <summary>迎撃者側（発射勢力にとって敵対=Hostile/Nemesis）のAntiAirユニットで、ミサイルの
        /// 現在の地上投影位置からInterceptRange以内にいるものそれぞれが、決定的ハッシュから導いた
        /// ロールで迎撃を試みる。いずれか1体でも成功すればtrue（複数のAAが同時に有効射程内にいても
        /// 迎撃成功は1発で十分＝2回目以降のロールは行わない）。</summary>
        private static bool TryIntercept(WarState state, MissileInFlight m, float dt)
        {
            WorldPos pos = GroundTrackPos(m);
            float chance = InterceptRatePerHour * dt;

            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive) continue;

                UnitType type = state.Types.Get(u.TypeKey);
                if (type == null || type.Category != UnitCategory.AntiAir) continue;

                if (!state.Relations.Get(m.FactionId, u.FactionId).IsHostile()) continue;

                float d = pos.HorizontalDistanceTo(u.Position);
                if (d > InterceptRange) continue;

                uint seed = HashSeed(m.Id, u.InstanceId, state.TickCounter);
                float roll = (seed % 1000000u) / 1000000f; // 決定的な0..1の疑似乱数値
                if (roll < chance) return true;
            }
            return false;
        }

        /// <summary>着弾解決: ImpactRadius以内の全ての自軍・非自軍のユニット/基地/外部脅威（＝勢力を問わない、
        /// CS都市建物は対象外＝スコープ外）にダメージを与える。基地は占領猶予中(CaptureGraceHours&gt;0)なら
        /// 無傷（BaseCombatStepの猶予保護と同じ方針）。</summary>
        private static void ResolveImpact(WarState state, MissileInFlight m)
        {
            WorldPos pos = m.To;

            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive) continue;
                if (pos.HorizontalDistanceTo(u.Position) > ImpactRadius) continue;

                u.CurrentHP -= ImpactDamageUnit;
                if (u.State != UnitState.Dead && u.CurrentHP <= 0f)
                {
                    u.CurrentHP = 0f;
                    u.State = UnitState.Dead;
                    UnitType uType = state.Types.Get(u.TypeKey);
                    UnitCategory category = uType != null ? uType.Category : default(UnitCategory);
                    state.AddKill(new KillEvent(u.Position, u.FactionId, category));
                }
            }

            for (int j = 0; j < state.Bases.Count; j++)
            {
                MilitaryBase b = state.Bases[j];
                if (b.OwnerFactionId == null) continue;
                if (b.CaptureGraceHours > 0f) continue; // 猶予中は無敵（BaseCombatStepと同じ方針）
                if (pos.HorizontalDistanceTo(b.Position) > ImpactRadius) continue;

                b.CurrentHP -= ImpactDamageBase;
                if (b.CurrentHP < 0f) b.CurrentHP = 0f;
            }

            for (int k = 0; k < state.Threats.Count; k++)
            {
                ExternalThreat t = state.Threats[k];
                if (t.IsDefeated) continue;
                if (pos.HorizontalDistanceTo(t.Position) > ImpactRadius) continue;

                t.CurrentHP -= ImpactDamageThreat;
                if (t.CurrentHP < 0f) t.CurrentHP = 0f;
            }

            state.AddImpact(new MissileImpactEvent(pos, false, m.FactionId));
        }

        /// <summary>決定的な整数ハッシュ入力の合成（AiProductionPolicy.MakeSeedと同じ手法:
        /// missileId/unitId/tickの3値を素数近似定数で混ぜてからfinalizerへ渡す）。</summary>
        private static uint HashSeed(uint missileId, uint unitId, uint tick)
        {
            unchecked
            {
                uint h = missileId;
                h = h * 2654435761u + unitId;
                h = h * 2654435761u + tick;
                return Hash(h);
            }
        }

        /// <summary>決定的な整数ハッシュ（MurmurHash3のfinalizer相当、AiProductionPolicy.Hashと同一手法）。</summary>
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

        private static MilitaryBase FindBase(WarState state, ushort baseId)
        {
            for (int i = 0; i < state.Bases.Count; i++)
                if (state.Bases[i].BaseId == baseId) return state.Bases[i];
            return null;
        }
    }
}
