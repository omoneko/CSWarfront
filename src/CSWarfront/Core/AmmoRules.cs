namespace CSWarfront.Core
{
    /// <summary>
    /// Task99: 弾薬ゲージ（設計: 2026-08-03-economy-supply-design.md §3）。
    ///
    /// 全戦闘ユニットが Ammo（0..1）を持ち、射撃しているtickだけ dt/AmmoCombatHours ずつ減る。
    /// 弾切れ（Ammo<=0）は射撃停止のみ（移動・占領は可能）。回復は基地圏内自動補給（ResupplyStep）
    /// と補給トラック（SupplyTruckStep）。
    ///
    /// 例外（HasAmmoが常にtrue）: AmmoCombatHours<=0 の兵科（空母=プラットフォーム、
    /// 自爆ドローン=体当たりが本体、補給トラック）。
    ///
    /// Invader勢力（外部襲来）: 当初は弾薬無限としていたが、実機で「侵攻部隊側が有利すぎる」
    /// （ユーザーフィードバック2026-08-03）ため現地調達方式へ変更——スポーン時の初期弾薬（満タン）
    /// だけを持ち、以後は敵ユニットを撃破するたびにInvaderAmmoPerKillだけ回復する
    /// （RewardInvaderKill、CombatStepの撃破判定から呼ばれる）。基地圏内補給・補給トラックは
    /// 一切受けられない（ResupplyStep/SupplyTruckStepがInvaderを除外）。撃破し続けられない波は
    /// 弾切れで射撃不能になり、防衛側が掃討できる。
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

        /// <summary>Invaderユニットが敵を1体撃破するたびに回復する弾薬量（現地調達。
        /// 0.25=戦車換算で2時間ぶんの射撃）。クラスコメント参照。</summary>
        public const float InvaderAmmoPerKill = 0.25f;

        /// <summary>射撃できるか。弾薬制の対象外（AmmoCombatHours<=0）は常にtrue。</summary>
        public static bool HasAmmo(UnitInstance u, UnitType t)
        {
            if (t == null || t.AmmoCombatHours <= 0f) return true;
            return u.Ammo > 0f;
        }

        /// <summary>射撃したtickの弾薬消費。弾薬制の対象外は何もしない。</summary>
        public static void ConsumeFire(UnitInstance u, UnitType t, float dt)
        {
            if (t == null || t.AmmoCombatHours <= 0f) return;
            u.Ammo -= dt / t.AmmoCombatHours;
            if (u.Ammo < 0f) u.Ammo = 0f;
        }

        /// <summary>撃破報酬（Invaderのみ）: 敵ユニットを倒した瞬間にInvaderAmmoPerKillだけ弾薬を
        /// 回復する（上限1）。CombatStepの「このダメージがとどめだったか」判定
        /// （AwardKillRewardと同じ箇所）から呼ばれる。通常勢力は対象外（補給網で賄う）。</summary>
        public static void RewardInvaderKill(UnitInstance killer, UnitType killerType)
        {
            if (killer == null || killer.FactionId != Faction.InvaderFactionId) return;
            if (killerType == null || killerType.AmmoCombatHours <= 0f) return;
            killer.Ammo += InvaderAmmoPerKill;
            if (killer.Ammo > 1f) killer.Ammo = 1f;
        }
    }
}
