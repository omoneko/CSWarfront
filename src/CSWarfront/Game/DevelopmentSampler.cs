using System.Collections.Generic;
using ColossalFramework;
using CSWarfront.Core;
using UnityEngine;
namespace CSWarfront.Game
{
    /// <summary>
    /// Builds development samples from BuildingManager (called only at the low frequency of the
    /// economy tick).
    /// Threading note: this class is called from the economy tick of OnSimTick (inside
    /// MilitaryManager.OnSimTick) and runs on the sim thread. Reading
    /// BuildingManager.instance.m_buildings.m_buffer targets data owned by the sim thread, and
    /// reading it at OnAfterSimulationTick-equivalent timing (after the sim step) is safe — i.e. it
    /// does not fall under the main-thread-only constraints (CS APIs for vehicle creation, rendering,
    /// UI, etc.).
    /// </summary>
    public static class DevelopmentSampler
    {
        /// <summary>
        /// Intended to be called from the sim thread (via the economy tick of
        /// MilitaryManager.OnSimTick). The BuildingManager building buffer is sim-owned data, so
        /// reading it on the sim thread is safe.
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
                // Development = building level + 1 (MVP simplification; population density etc. to be
                // factored in later).
                float dev = buf[i].m_level + 1;
                list.Add(new DevelopmentSample
                {
                    Position = new WorldPos(p.x, p.y, p.z),
                    Development = dev,
                    Zone = ZoneFor(buf[i].Info.m_class.m_service) // Task99: zone classification for the 3-resource economy
                });
            }
            return list;
        }

        /// <summary>Task99: CS Service kind → zone classification for the 3-resource economy
        /// (residential → manpower, commercial/office → funds, industrial → production). Everything
        /// else (public services etc.) is Other = contributes to no resource.</summary>
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
