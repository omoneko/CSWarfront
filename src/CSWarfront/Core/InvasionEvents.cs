using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>
    /// 外部からの襲来イベント（Task94、Workshopコメント要望「敵ユニットがランダムなタイミングで
    /// 都市の外からスポーンして攻めてくるオプション。敵基地を手で建てる代わりに、自分の基地を建てて
    /// 都市を防衛する」）。
    ///
    /// Optionsのトグル（Game層WarfrontSettings.InvasionEventsEnabled）がONの間、CheckIntervalHoursごとに
    /// 決定的ハッシュで襲来判定を行い、当選したらマップ端のランダムな地点（陸地）へ襲撃部隊を
    /// スポーンする。部隊の所属は「基地を最も持っていない勢力」（＝プレイヤーが使っていない勢力を
    /// 侵略者役として使い回す。基地0の勢力を優先）で、スポーン時に防衛側（基地所有勢力）との関係を
    /// Hostileへ設定する（Nemesis設定は上書きしない）。以後は通常のAI（InvasionOrders.AssignAdvance）が
    /// 最寄りの敵基地へ進軍させるため、専用の攻撃ロジックは不要。
    ///
    /// 部隊規模・Tierは防衛側の最高解禁Tierに追従する（ゲームが進むほど強い襲撃が来る）。
    /// 乱数不使用（TickCounterと通し番号からの決定的ハッシュ、AntiAirCombat.RollHitと同じfmix32）。
    /// </summary>
    public static class InvasionEvents
    {
        /// <summary>襲来判定の間隔（ゲーム内時間）。</summary>
        public const float CheckIntervalHours = 6f;

        /// <summary>頻度設定（Options: Low/Medium/High）ごとの、1判定あたりの当選確率。
        /// 期待値: Low≈5日に1回、Medium≈2.5日に1回、High≈1.2日に1回。</summary>
        public static readonly float[] ChancePerCheck = { 0.05f, 0.10f, 0.21f };

        /// <summary>スポーン地点のマップ中心からの距離（辺上）。SeaGrid/プレイアブル境界の内側。</summary>
        public const float SpawnEdgeDistance = 4300f;

        /// <summary>スポーン候補が水域だった場合に内側へずらして再試行する回数と1回あたりの距離。</summary>
        private const int LandSearchSteps = 8;
        private const float LandSearchStepDistance = 300f;

        /// <summary>襲来判定を1tickぶん進める。スポーンが発生した場合はスポーンしたユニット数を返す
        /// （Game層が通知トーストに使う）。0=何も起きていない。</summary>
        public static int Advance(WarState state, float dt, bool enabled, int frequencyIndex)
        {
            if (!enabled) return 0;

            state.InvasionCheckAccum += dt;
            if (state.InvasionCheckAccum < CheckIntervalHours) return 0;
            state.InvasionCheckAccum -= CheckIntervalHours;

            if (frequencyIndex < 0) frequencyIndex = 0;
            if (frequencyIndex >= ChancePerCheck.Length) frequencyIndex = ChancePerCheck.Length - 1;

            float roll = Hash01(state.TickCounter, 0xA11ACEu);
            if (roll >= ChancePerCheck[frequencyIndex]) return 0;

            return SpawnWave(state);
        }

        /// <summary>襲撃部隊を1回スポーンする（テストからも直接呼べる）。戻り値はスポーンしたユニット数。
        /// 防衛側（基地所有勢力）が1つも無い、または陸地のスポーン地点が見つからない場合は0。</summary>
        public static int SpawnWave(WarState state)
        {
            // 防衛側 = 基地を1つ以上所有する勢力。いなければ攻める意味が無い。
            var defenders = new List<byte>();
            int[] baseCounts = new int[state.Factions.Count];
            for (int i = 0; i < state.Bases.Count; i++)
            {
                MilitaryBase b = state.Bases[i];
                if (b.OwnerFactionId == null) continue;
                byte owner = b.OwnerFactionId.Value;
                if (owner < baseCounts.Length) baseCounts[owner]++;
            }
            for (int f = 0; f < state.Factions.Count; f++)
            {
                if (baseCounts[f] > 0) defenders.Add(state.Factions[f].Id);
            }
            if (defenders.Count == 0) return 0;

            // 侵略者役 = 基地が最も少ない勢力（同数なら若いId）。通常は基地0の未使用勢力が選ばれる。
            Faction attacker = null;
            int fewest = int.MaxValue;
            for (int f = 0; f < state.Factions.Count; f++)
            {
                int count = baseCounts[f];
                if (count < fewest)
                {
                    fewest = count;
                    attacker = state.Factions[f];
                }
            }
            if (attacker == null) return 0;

            // 全防衛勢力と敵対させる（既にNemesisならそのまま。侵略者が防衛側自身になる縮退ケース
            // ＝全勢力が基地持ち、では自分以外とだけ敵対させる）。
            for (int d = 0; d < defenders.Count; d++)
            {
                byte def = defenders[d];
                if (def == attacker.Id) continue;
                if (!state.Relations.Get(attacker.Id, def).IsHostile())
                    state.Relations.Set(attacker.Id, def, Relation.Hostile);
            }

            // スポーン地点: マップ端の1辺上の決定的ランダム点。水域なら内側へずらして陸地を探す。
            WorldPos? spawn = FindLandSpawnPoint(state);
            if (!spawn.HasValue) return 0;

            // 部隊Tier = 防衛側の最高解禁Tier（ゲーム進行に追従）。
            byte tier = 1;
            for (int d = 0; d < defenders.Count; d++)
            {
                Faction df = state.FindFaction(defenders[d]);
                if (df != null && df.UnlockedTier > tier) tier = df.UnlockedTier;
            }

            // 編成: 諸兵科連合の襲撃部隊（Tierが上がると戦車が増える）。
            var composition = new List<UnitCategory>
            {
                UnitCategory.Tank, UnitCategory.Tank,
                UnitCategory.MechInfantry, UnitCategory.MechInfantry,
                UnitCategory.Apc, UnitCategory.Artillery, UnitCategory.AntiAir
            };
            for (int extra = 1; extra < tier; extra++) composition.Add(UnitCategory.Tank);

            int spawned = 0;
            for (int i = 0; i < composition.Count; i++)
            {
                string key = LandUnitRoster.TypeKey(composition[i], tier);
                UnitType type = state.Types.Get(key);
                if (type == null) continue;

                // 密集し過ぎないよう決定的な小オフセットで散らす。
                float ox = (Hash01(state.TickCounter, (uint)(i * 2 + 1)) - 0.5f) * 60f;
                float oz = (Hash01(state.TickCounter, (uint)(i * 2 + 2)) - 0.5f) * 60f;
                var u = new UnitInstance(state.AllocInstanceId(), key, attacker.Id, type.MaxHP,
                    new WorldPos(spawn.Value.X + ox, spawn.Value.Y, spawn.Value.Z + oz));
                u.State = UnitState.Moving; // 次のAssignAdvanceが目標基地と経路を与える
                state.Units.Add(u);
                spawned++;
            }
            return spawned;
        }

        /// <summary>マップ端の決定的ランダム点から、必要なら内側へずらして陸地を探す。
        /// water==null（テスト等）は常に陸地扱い。見つからなければnull（このtickの襲来は不成立）。</summary>
        private static WorldPos? FindLandSpawnPoint(WarState state)
        {
            int side = (int)(Hash01(state.TickCounter, 0xED6Eu) * 4f) & 3;
            float t = (Hash01(state.TickCounter, 0x5EEDu) * 2f - 1f) * SpawnEdgeDistance;

            float x, z, ix, iz; // 辺上の位置と「マップ内側へ向かう」単位方向
            switch (side)
            {
                case 0: x = t; z = SpawnEdgeDistance; ix = 0f; iz = -1f; break;   // 北端
                case 1: x = t; z = -SpawnEdgeDistance; ix = 0f; iz = 1f; break;   // 南端
                case 2: x = SpawnEdgeDistance; z = t; ix = -1f; iz = 0f; break;   // 東端
                default: x = -SpawnEdgeDistance; z = t; ix = 1f; iz = 0f; break;  // 西端
            }

            IWaterSampler water = state.Water;
            IHeightSampler height = state.Height;
            for (int step = 0; step <= LandSearchSteps; step++)
            {
                float cx = x + ix * step * LandSearchStepDistance;
                float cz = z + iz * step * LandSearchStepDistance;
                if (water != null && water.IsWater(cx, cz)) continue; // 海上には陸上部隊を降ろさない

                float y = 0f;
                float h;
                if (height != null && height.TrySampleHeight(cx, cz, out h)) y = h;
                return new WorldPos(cx, y, cz);
            }
            return null;
        }

        /// <summary>fmix32ベースの決定的[0,1)ハッシュ（AntiAirCombat.RollHitと同じ手法）。</summary>
        private static float Hash01(uint a, uint b)
        {
            unchecked
            {
                uint h = a * 2654435761u + b;
                h ^= h >> 16;
                h *= 0x85ebca6bu;
                h ^= h >> 13;
                h *= 0xc2b2ae35u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / (float)0x1000000;
            }
        }
    }
}
