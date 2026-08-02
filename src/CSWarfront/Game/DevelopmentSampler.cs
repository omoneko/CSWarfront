using System.Collections.Generic;
using ColossalFramework;
using CSWarfront.Core;
using UnityEngine;
namespace CSWarfront.Game
{
    /// <summary>
    /// BuildingManagerから発展度サンプルを作る（経済tickの低頻度でのみ呼ぶ）。
    /// スレッド注記: このクラスは OnSimTick の経済tick（MilitaryManager.OnSimTick内）から呼ばれ、
    /// simスレッド上で実行される。BuildingManager.instance.m_buildings.m_buffer の読み取りは
    /// simスレッドが所有するデータであり、OnAfterSimulationTick相当のタイミング（sim step後）で
    /// 読むぶんには安全＝メインスレッド専用の制約（車両生成・描画・UI等のCS API）には該当しない。
    /// </summary>
    public static class DevelopmentSampler
    {
        /// <summary>
        /// sim スレッドから呼ばれる想定（MilitaryManager.OnSimTick の経済tick経由）。
        /// BuildingManager の建物バッファはsim側が所有するデータのため、simスレッドでの読み取りは安全。
        /// </summary>
        public static List<DevelopmentSample> Sample()
        {
            var list = new List<DevelopmentSample>();
            BuildingManager bm = Singleton<BuildingManager>.instance;
            Building[] buf = bm.m_buildings.m_buffer;
            for (int i = 1; i < buf.Length; i++)
            {
                if ((buf[i].m_flags & Building.Flags.Created) == 0) continue;
                if (buf[i].Info == null) continue;
                Vector3 p = buf[i].m_position;
                // 発展度＝建物レベル+1（MVP簡略。人口密度等は後日加味）。
                float dev = buf[i].m_level + 1;
                list.Add(new DevelopmentSample
                {
                    Position = new WorldPos(p.x, p.y, p.z),
                    Development = dev,
                    Zone = ZoneFor(buf[i].Info.m_class.m_service) // Task99: 3資源経済のゾーン分類
                });
            }
            return list;
        }

        /// <summary>Task99: CSのService種別→3資源経済のゾーン分類（住宅→人的資源、商業/オフィス→資金、
        /// 工業→生産力）。それ以外（公共サービス等）はOther＝どの資源にも寄与しない。</summary>
        private static ZoneKind ZoneFor(ItemClass.Service service)
        {
            switch (service)
            {
                case ItemClass.Service.Residential: return ZoneKind.Residential;
                case ItemClass.Service.Commercial: return ZoneKind.CommercialOffice;
                case ItemClass.Service.Office: return ZoneKind.CommercialOffice;
                case ItemClass.Service.Industrial: return ZoneKind.Industrial;
                default: return ZoneKind.Other;
            }
        }
    }
}
