namespace CSWarfront.Core
{
    /// <summary>
    /// Task101（Update3）: 野戦築城・貨物駅の集中規則
    /// （設計: 2026-08-04-fortifications-heli-rail-design.md §1）。
    ///
    /// 5種の位置づけ:
    ///  - Bunker（掩蔽壕）: 歩兵3体分の自動射撃（FortCombatStep、建物非貫通）＋歩兵守備+50%。
    ///    HP0で機能停止（Owner=null化。占領不可・再稼働なし。地形ボーナスだけは残る）。
    ///  - ArtilleryPost（砲兵陣地）: 砲兵1体分の範囲射撃。HP0の扱いはBunkerと同じ。
    ///  - SupplyDepot（補給拠点）: 独自備蓄（StoredSupplies）＋200m自動補給＋トラック積出。
    ///    基地と同じく占領可（備蓄ごと奪取——StoredSuppliesは基地オブジェクトに載ったまま移る）。
    ///  - Trench（塹壕）: 攻撃対象外の地形効果（歩兵守備+50%、敵味方不問）。
    ///  - CargoStation（貨物駅）: 鉄道輸送の端点＋備蓄。占領可。レール未接続なら輸送には使われない。
    /// </summary>
    public static class FortificationRules
    {
        public static bool IsFortification(BaseType type)
        {
            return type == BaseType.Bunker || type == BaseType.ArtilleryPost
                || type == BaseType.SupplyDepot || type == BaseType.Trench
                || type == BaseType.CargoStation;
        }

        /// <summary>攻撃対象になるか。Trenchのみfalse（BaseCombatStep/ミサイル着弾/自爆ドローンの
        /// 対象から除外。AIの進軍目標（ChooseTargetBase）からも除外する）。</summary>
        public static bool IsTargetable(BaseType type)
        {
            return type != BaseType.Trench;
        }

        /// <summary>HP0で占領されるか。Bunker/ArtilleryPostのみfalse＝HP0でOwner=null化（機能停止）。
        /// Occupation.ResolveCapturesが分岐する。</summary>
        public static bool IsCapturable(BaseType type)
        {
            return type != BaseType.Bunker && type != BaseType.ArtilleryPost;
        }

        /// <summary>配置時の最大HP。Trenchは事実上無敵（攻撃対象外なので通常減らないが防御的に巨大値）。</summary>
        public static float DefaultMaxHP(BaseType type)
        {
            switch (type)
            {
                case BaseType.Bunker: return 300f;
                case BaseType.ArtilleryPost: return 250f;
                case BaseType.SupplyDepot: return 400f;
                case BaseType.CargoStation: return 400f;
                case BaseType.Trench: return 1000000000f;
                default: return 500f; // 通常基地の既定（MilitaryBaseのフィールド初期値と同値）
            }
        }

        /// <summary>備蓄（StoredSupplies）の上限。Depot300/Station500、他は備蓄を持たない。</summary>
        public static float StoredSupplyCap(BaseType type)
        {
            if (type == BaseType.SupplyDepot) return 300f;
            if (type == BaseType.CargoStation) return 500f;
            return 0f;
        }
    }
}
