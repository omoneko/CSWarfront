using ICities;
using CSWarfront.Core;
namespace CSWarfront.Game.Serialization
{
    /// <summary>WarStateをセーブデータへ永続化。ロード時に状態復元＋表現再生成。</summary>
    public class WarStateDataExtension : SerializableDataExtensionBase
    {
        private const string DataId = "CSWarfront.WarState.v1";

        public override void OnSaveData()
        {
            try
            {
                // _stateLock を保持したままシリアライズする（OnMainUpdate等によるState.Units変更中の
                // 「Collection was modified」例外＝セーブ静かに失敗＝データ消失を防ぐ）。
                byte[] bytes = MilitaryManager.SerializeLocked();
                serializableDataManager.SaveData(DataId, bytes);
            }
            catch (System.Exception e) { ModConfig.LogError("Save: " + e); }
        }

        public override void OnLoadData()
        {
            try
            {
                byte[] bytes = serializableDataManager.LoadData(DataId);
                if (bytes == null || bytes.Length == 0) return; // 新規ゲームは既定初期化に任せる
                var types = new UnitTypeRegistry();
                types.Register(MvpUnitTypes.Tank_T1());
                WarState restored = WarStateSerializer.Deserialize(bytes, types);
                // State差し替えと表現（車両）再生成を同一ロック内で行う（MilitaryManager参照）。
                MilitaryManager.LoadAndRebuild(restored);
            }
            catch (System.Exception e) { ModConfig.LogError("Load: " + e); }
        }
    }
}
