namespace CSWarfront.Core
{
    /// <summary>基地・施設の種別。Army/Navy/AirForce/MissileBase=従来の軍事基地（生産・収入あり）。
    /// Task101（Update3）で野戦築城・貨物駅を末尾に追加（int値の互換維持）: これらは
    /// FortificationRules.IsFortification==trueで、生産・収入・ミサイル機能を持たない
    /// （SpawnableDomains=None）。各種の規則はFortificationRules参照。</summary>
    public enum BaseType
    {
        Army, Navy, AirForce, MissileBase,
        Bunker, ArtilleryPost, SupplyDepot, Trench, CargoStation
    }
}
