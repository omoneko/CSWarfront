namespace CSWarfront.Core
{
    /// <summary>
    /// 他MOD（ゴジラ災害/エイリアン侵略）が生成する外部脅威(ExternalThreat)の種別（Task59）。
    /// ExternalThreat.Kind および WarState.ThreatRelations のキーとして使う。
    /// 追加する場合は必ず末尾に追記すること：ThreatRelations.ThreatKindCount とWarStateSerializerの
    /// 永続化ブロックは「0..ThreatKindCount-1」を固定順で読み書きするため、既存の値の並びを変えると
    /// 別の脅威の関係を読み違える（RelationのNemesis追記と同じ注意点）。
    /// </summary>
    public enum ThreatKind { Kaiju, Alien }
}
