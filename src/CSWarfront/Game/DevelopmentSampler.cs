using System.Collections.Generic;
using ColossalFramework;
using CSWarfront.Core;
using UnityEngine;
namespace CSWarfront.Game
{
    /// <summary>BuildingManagerから発展度サンプルを作る（経済tickの低頻度でのみ呼ぶ）。</summary>
    public static class DevelopmentSampler
    {
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
                list.Add(new DevelopmentSample { Position = new WorldPos(p.x, p.y, p.z), Development = dev });
            }
            return list;
        }
    }
}
