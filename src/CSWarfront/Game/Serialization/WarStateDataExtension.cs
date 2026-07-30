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
                // _stateLock を保持したままシリアライズする（OnSimTick等によるState.Units変更中の
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
                LandUnitRoster.RegisterAll(types); // 陸上7兵種×Tier1〜5（Task28）。旧セーブのTank_T1も同じキーで解決される。
                NavalUnitRoster.RegisterAll(types); // 海上2種×Tier1〜5（Task61）。海上/航空ユニットを含むセーブの復元に必要。
                AirUnitRoster.RegisterAll(types);   // 航空3種×Tier1〜5（Task61）。
                WarState restored = WarStateSerializer.Deserialize(bytes, types);
                // Task88: 勢力名は表示専用のMOD定義（色名）なので、セーブに残っている旧名
                // （"Faction 3"等）は常に現行のWarfrontSettings.FactionNamesで上書きする。
                string[] names = WarfrontSettings.FactionNames;
                for (int i = 0; i < restored.Factions.Count; i++)
                {
                    var f = restored.Factions[i];
                    if (f.Id < names.Length) f.Name = names[f.Id];
                }
                // State差し替えと表現（車両）再生成を同一ロック内で行う（MilitaryManager参照）。
                MilitaryManager.LoadAndRebuild(restored);
            }
            catch (System.Exception e) { ModConfig.LogError("Load: " + e); }
        }
    }
}
