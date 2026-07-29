namespace CSWarfront.Core
{
    /// <summary>
    /// 他MOD（ゴジラ災害/エイリアン侵略）が生成する怪獣・侵略者に対するCSWarfront側の戦闘状態（Task58）。
    ///
    /// 相手MODはHPや被弾・撃破APIを一切公開していない（Godzilla.Game.GodzillaManager /
    /// AlienInvasion.Game.InvasionManagerは位置と生死しか教えてくれない）ため、HPはCSWarfrontが
    /// 独自に持つ。0になったらGame層（Game/ExternalThreatBridge）がリフレクション経由で相手MODの
    /// despawn（Defeat/ForceDespawn、無ければResetForNewLevel）を呼び、この脅威を除去する。
    ///
    /// WarState.Threatsは実行時のみ・非永続化：Game層が毎tick、生きている他MODの状態から
    /// 再同期する（RoadGraph/CoverMapと同じパターン）。
    /// </summary>
    public class ExternalThreat
    {
        public uint Id;

        /// <summary>Kaiju(ゴジラ) / Alien（Game層のExternalThreatBridgeが設定する）。Task59:
        /// WarState.ThreatRelationsの検索キーとして使うため文字列からThreatKind enumへ変更した。</summary>
        public ThreatKind Kind;

        public WorldPos Position;

        /// <summary>当たり半径（水平）。大型なので通常のユニット同士の交戦より広めに取る
        /// （ThreatCombatStepが unitType.Range + Radius を実効射程として扱う）。</summary>
        public float Radius;

        public float MaxHP;
        public float CurrentHP;

        public bool IsDefeated { get { return CurrentHP <= 0f; } }
    }
}
