namespace CSWarfront.Core
{
    /// <summary>
    /// 全ドメイン共通の兵科カタログ。Task61（海上/航空戦力の追加）で実際に実装したのは
    /// Destroyer/Carrier（海上）とAirSuperiority/TacticalBomber/SuicideDrone（航空）の5種のみ
    /// （ユーザー要望によりスコープを絞った）。それ以外のSea/Air系メンバーは将来拡張用の未実装プレースホルダ
    /// （NavalUnitRoster/AirUnitRosterに定義が無いため、UnitTypeRegistryには登録されず選択できない）。
    ///
    /// 永続化の注意（Task61で検証済み）: この列挙子自体はWarStateSerializerで直接シリアライズされない
    /// （ユニットはTypeKey文字列でのみ永続化され、UnitCategoryへはUnitTypeRegistry.Get経由の実行時解決
    /// でしか到達しない。KillEvent.CategoryやShotEvent.Categoryはこの列挙の値を持つが、どちらも
    /// WarState.RecentKills/RecentShotsという非永続化のトランジェント・バッファにしか入らない）。
    /// そのためSuicideDroneをここに追加しても既存セーブとの互換性は壊れない。ただし将来同様の追加を
    /// する際も、この列挙のint値をどこかで直接シリアライズし始めていないか要確認のうえ、
    /// 新規メンバーは必ず末尾へ追加すること。
    /// </summary>
    public enum UnitCategory
    {
        Tank, Apc, MechInfantry, Artillery, DroneInfantry, Infantry, AntiAir,
        Carrier, Cruiser, Destroyer, Frigate, Minelayer, Minesweeper, Submarine,
        FastBoat, SuicideBoat, SeaDrone,
        AirSuperiority, GroundAttack, TacticalBomber, StrategicBomber, ElectronicWarfare, Awacs,
        /// <summary>自爆ドローン（Task61で追加、Task79で「射撃してから自壊」→「突進して体当たり起爆」へ
        /// 再設計）。UnitCategoryFlags.IsKamikaze()がtrueを返す唯一のカテゴリで、通常の射撃パイプライン
        /// （CombatStep/BaseCombatStep/ThreatCombatStep）には一切乗らず、専用のKamikazeStepが
        /// 交戦フロー全体（目標ロック→ダイブ→体当たり起爆）を扱う。既存メンバーの並びを一切変更せず
        /// 末尾に追加（列挙のint値に依存する永続化が万一存在しても既存値を破壊しないため）。</summary>
        SuicideDrone,
        /// <summary>補給トラック（Task99: 経済・補給システム）。非武装（Attack0・CanTargetDomains=None）で
        /// 通常の射撃パイプラインに乗らず、専用のSupplyTruckStepが積載→配送→転送→帰還を扱う。
        /// AI進軍（InvasionOrders.AssignAdvance）・戦闘編成カウント（ProductionPlanning.MaxUnitsPerFaction）
        /// の対象外で、台数はSupplyTruckStep.MaxTrucksPerFactionで別枠管理。末尾追加（列挙値の互換維持）。</summary>
        SupplyTruck,
        /// <summary>輸送ヘリ（Task101）: 非武装の自動維持兵站ユニット（TransportHeliStep）。
        /// 基地→補給拠点の物資輸送＋歩兵の前線空輸。対ヘリ規則はTargetingRules.CanTargetHelicopter。</summary>
        TransportHelicopter,
        /// <summary>攻撃ヘリ（Task101）: 航空基地の通常生産兵科。地上ユニット専任（対基地・対脅威不可）、
        /// ホバリング型（レーストラック航過なし・低空60m）。</summary>
        AttackHelicopter,
        /// <summary>軍用貨物列車（Task101）: 非武装・レール専用移動（TrainStep）。物資・陸上ユニットを
        /// 貨物駅間で輸送する。</summary>
        MilitaryTrain
    }
}
