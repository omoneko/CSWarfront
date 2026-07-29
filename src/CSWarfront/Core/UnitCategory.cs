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
        /// <summary>自爆ドローン（Task61）。UnitType.IsOneShot=trueで運用される唯一のカテゴリ。
        /// 既存メンバーの並びを一切変更せず末尾に追加（列挙のint値に依存する永続化が万一存在しても
        /// 既存値を破壊しないため）。</summary>
        SuicideDrone
    }
}
