using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// 基地/ユニット情報パネル向けのUI-facingラッパー（所有権変更・UIスナップショット取得）向けの
    /// MilitaryManager 追加メンバー。MilitaryManager.cs の500行制限のため分離した partial class
    /// （Task34のMilitaryManagerManualProduction、Task49のMilitaryManagerRelations等と同じ方針。
    /// 勢力関係/研究/生産/ミサイル/部隊コマンド向けのラッパーは既にそれぞれ専用partialへ分離済みのため
    /// ここでは重複させず、基地所有権と基地/ユニットのUIスナップショット取得のみを持つ）。
    /// _stateLock / State は MilitaryManager.cs 側で宣言された private static メンバーで、
    /// partial class なのでこちらからもそのままアクセスできる。
    ///
    /// 呼び出し元（Game/UI配下の各パネル）はメインスレッドから呼ぶ。各メソッドは _stateLock を
    /// 短時間だけ保持するだけの薄いラッパーで、Unity API には一切触れない（ロック保持中にUnity APIを
    /// 呼ばないという既定の規約に従う）。
    /// </summary>
    public static partial class MilitaryManager
    {
        /// <summary>
        /// 基地情報パネル（Game/UI/BaseInfoPanel）から呼ばれる、基地の所属勢力変更（Task25）。
        /// メインスレッドから呼ばれる想定だが、simスレッド（OnSimTick）も同じ _stateLock を取るため
        /// 排他は保証される。HQ整合性は BasePlacementWatcher.ReassignHqIfCleared を共有利用する
        /// （解体経路と重複させないため）。
        /// </summary>
        /// <returns>baseId の基地または factionId の勢力が見つからない場合は false。</returns>
        public static bool TrySetBaseOwner(ushort baseId, byte factionId)
        {
            lock (_stateLock)
            {
                if (State == null) return false;

                MilitaryBase mb = null;
                for (int i = 0; i < State.Bases.Count; i++)
                {
                    if (State.Bases[i].BaseId == baseId) { mb = State.Bases[i]; break; }
                }
                if (mb == null) return false;

                Faction newFaction = State.FindFaction(factionId);
                if (newFaction == null) return false;

                byte? oldOwner = mb.OwnerFactionId;
                if (oldOwner.HasValue && oldOwner.Value == factionId) return true; // 変更なし

                bool wasHq = mb.IsHeadquarters;
                mb.OwnerFactionId = factionId;
                mb.IsHeadquarters = false;

                // 旧所有勢力のHQだった場合はクリアして、その勢力が他に持つ基地があれば昇格する。
                if (oldOwner.HasValue && wasHq)
                {
                    BasePlacementWatcher.ReassignHqIfCleared(State, oldOwner.Value, baseId);
                }

                // 新所有勢力がまだHQを持たない場合、この基地をHQにする。
                if (!newFaction.HomeBaseId.HasValue)
                {
                    newFaction.HomeBaseId = baseId;
                    mb.IsHeadquarters = true;
                }

                ModConfig.Log("MilitaryManager: base " + baseId + " owner changed " +
                    (oldOwner.HasValue ? oldOwner.Value.ToString() : "none") + " -> " + factionId +
                    (mb.IsHeadquarters ? " (new HQ)" : ""));
                return true;
            }
        }

        /// <summary>
        /// Task66: 指定勢力が指定種別の拠点を1つでも所有しているか（AssetAssignPanel/OptionsModelAssignPage
        /// が「基地種別ごとのモデル割り当て」を適用する際、割り当て対象の拠点が現時点で1つも無い場合に
        /// ユーザーへヒントを出すために使う。バグ調査で判明した通り、割り当て自体は正しく保存されていても、
        /// 対応する拠点が存在しなければ見た目には何も反映されないため、ユーザーには「反映されていない」
        /// ように見えてしまう——このメソッドはその状況を明示的に案内するためのものであり、割り当ての
        /// 保存/適用ロジック自体には一切影響しない）。
        /// </summary>
        public static bool HasOwnedBaseOfType(byte factionId, BaseType type)
        {
            lock (_stateLock)
            {
                if (State == null) return false;
                for (int i = 0; i < State.Bases.Count; i++)
                {
                    MilitaryBase b = State.Bases[i];
                    if (b.Type == type && b.OwnerFactionId.HasValue && b.OwnerFactionId.Value == factionId) return true;
                }
                return false;
            }
        }

        /// <summary>
        /// 基地情報パネル表示用の値をロック内でコピーして返す（UIが WarState へ直接触れないため、Task25）。
        /// </summary>
        public static bool TryGetBaseSnapshot(ushort baseId, out BaseUiSnapshot snapshot)
        {
            lock (_stateLock)
            {
                snapshot = default(BaseUiSnapshot);
                if (State == null) return false;

                for (int i = 0; i < State.Bases.Count; i++)
                {
                    MilitaryBase mb = State.Bases[i];
                    if (mb.BaseId != baseId) continue;

                    snapshot = BaseUiSnapshotBuilder.Build(mb, State);
                    return true;
                }
                return false;
            }
        }

        /// <summary>ユニット情報パネル表示用の値をロック内でコピーして返す（Task31。TryGetBaseSnapshotと
        /// 同じパターン）。死亡済みはまだ残っている可能性があるため見つからない扱いにする。</summary>
        public static bool TryGetUnitSnapshot(uint instanceId, out UnitUiSnapshot snapshot)
        {
            lock (_stateLock)
            {
                snapshot = default(UnitUiSnapshot);
                if (State == null) return false;

                UnitInstance unit = State.FindUnit(instanceId);
                if (unit == null || unit.State == UnitState.Dead) return false;

                UnitType type = State.Types.Get(unit.TypeKey);
                snapshot = UnitUiSnapshotBuilder.Build(State, unit, type);
                return true;
            }
        }
    }
}
