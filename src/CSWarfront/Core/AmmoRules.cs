namespace CSWarfront.Core
{
    /// <summary>
    /// Task99: 弾薬ゲージ（設計: 2026-08-03-economy-supply-design.md §3）。
    ///
    /// 全戦闘ユニットが Ammo（0..1）を持ち、射撃しているtickだけ dt/AmmoCombatHours ずつ減る。
    /// 弾切れ（Ammo<=0）は射撃停止のみ（移動・占領は可能）。回復は基地圏内自動補給（ResupplyStep）
    /// と補給トラック（SupplyTruckStep）。
    ///
    /// 例外（HasAmmoが常にtrue）:
    ///  - AmmoCombatHours<=0 の兵科（空母=プラットフォーム、自爆ドローン=体当たりが本体、補給トラック）
    ///  - Invader勢力（外部襲来のイベント敵。時間経過で襲来が無力化するのを防ぐ、ユーザー決定）
    /// </summary>
    public static class AmmoRules
    {
        /// <summary>兵科ごとの既定の連続射撃可能時間（ゲーム内時間、Tier不問）。0=弾薬無限。
        /// unit-stats.xmlのammoCombatHoursで上書き可能（UnitStatOverrides.AmmoHours）。</summary>
        public static float DefaultCombatHours(UnitCategory category)
        {
            switch (category)
            {
                case UnitCategory.Infantry:
                case UnitCategory.MechInfantry:
                    return 12f;
                case UnitCategory.Tank:
                case UnitCategory.Apc:
                case UnitCategory.DroneInfantry:
                case UnitCategory.Destroyer:
                    return 8f;
                case UnitCategory.AntiAir:
                    return 6f;
                case UnitCategory.Artillery:
                    return 4f;
                case UnitCategory.AirSuperiority:
                case UnitCategory.TacticalBomber:
                    return 3f; // 2〜3ソーティ相当（弾切れ→基地/空母で再武装→再出撃のループを作る）
                default:
                    return 0f; // Carrier / SuicideDrone / SupplyTruck / 未実装カテゴリ = 弾薬無限
            }
        }

        /// <summary>射撃できるか。弾薬制の対象外（AmmoCombatHours<=0）とInvader勢力は常にtrue。</summary>
        public static bool HasAmmo(UnitInstance u, UnitType t)
        {
            if (t == null || t.AmmoCombatHours <= 0f) return true;
            if (u.FactionId == Faction.InvaderFactionId) return true;
            return u.Ammo > 0f;
        }

        /// <summary>射撃したtickの弾薬消費。弾薬制の対象外・Invaderは何もしない。</summary>
        public static void ConsumeFire(UnitInstance u, UnitType t, float dt)
        {
            if (t == null || t.AmmoCombatHours <= 0f) return;
            if (u.FactionId == Faction.InvaderFactionId) return;
            u.Ammo -= dt / t.AmmoCombatHours;
            if (u.Ammo < 0f) u.Ammo = 0f;
        }
    }
}
