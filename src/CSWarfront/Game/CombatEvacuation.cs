using System;
using ColossalFramework;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// Task150 (Workshop feedback from bob — "people just walk and drive like normal even though there
    /// are tanks and explosions right next to them" — and the user's own idea: send them to the nearest
    /// shelter): declares each combat zone an evacuation area, so civilians clear out of a battle.
    ///
    /// This does not implement evacuation. The game already has it, and calling it is enough:
    ///   DisasterManager.AddEvacuationArea(position, radius)
    /// Finding a shelter with room, routing the citizens, running the evacuation buses — all of that is
    /// vanilla and therefore works with whatever shelters the player has, the Natural Disasters ones and
    /// any custom asset built on ShelterAI alike. Nothing here needs to know what a shelter is.
    ///
    /// Nothing needs undoing either, which is what makes this safe to do to somebody's city. The
    /// evacuation map holds two bits per cell; AddEvacuationArea sets the low one, and every 256 frames
    /// DisasterManager sweeps the map with `map[i] = (map[i] &amp; 0x55555555) &lt;&lt; 1`, shifting the low bit
    /// up and dropping the high one (verified by decompiling DisasterManager.SimulationStepImpl). A cell
    /// therefore stops evacuating about two sweeps after the last call. Keep calling while the fighting
    /// lasts, stop when it ends, and the game clears up by itself — and a real disaster's own evacuation
    /// is untouched, because it re-declares its areas the same way.
    ///
    /// EvacuateAll is deliberately not used: it empties the shelters, which is the opposite of the point.
    ///
    /// Sim thread only, called from MilitaryManager.OnSimTick beside CombatCollateral.
    /// </summary>
    internal static class CombatEvacuation
    {
        /// <summary>How often the areas are re-declared. Far more often than the map's own fade, so a
        /// zone never flickers out from under the citizens while the shooting is still going on.</summary>
        public const float RefreshIntervalHours = 0.25f;

        /// <summary>Evacuation radius as a multiple of the combat zone's own radius. Wider than the
        /// fighting itself: the people worth moving are the ones about to be in it, not only those
        /// already under fire.</summary>
        public const float RadiusScale = 1.5f;

        private static float _accum;
        private static bool _noShelterLogged;
        private static bool _activeLogged;

        public static void Advance(WarState state, float dt)
        {
            try
            {
                if (!WarfrontSettings.EvacuateCiviliansFromCombat) return;
                if (state == null) return;

                _accum += dt;
                if (_accum < RefreshIntervalHours) return;
                _accum = 0f;

                var zones = state.CombatZones.Zones;
                if (zones.Count == 0) return;
                if (!Singleton<DisasterManager>.exists) return;

                // Without a shelter there is nowhere to send anyone, and an evacuation order with no
                // destination just unsettles a city for no reason. Checked every time rather than cached:
                // the player may build their first shelter at any moment.
                if (!HasAnyShelter())
                {
                    if (!_noShelterLogged)
                    {
                        _noShelterLogged = true;
                        ModConfig.Log("CombatEvacuation: no shelter in the city, so civilians are not "
                            + "ordered to evacuate (build one, or a custom ShelterAI asset, to enable this)");
                    }
                    return;
                }
                _noShelterLogged = false;

                DisasterManager dm = Singleton<DisasterManager>.instance;
                for (int i = 0; i < zones.Count; i++)
                {
                    CombatZone zone = zones[i];
                    dm.AddEvacuationArea(
                        new Vector3(zone.Center.X, zone.Center.Y, zone.Center.Z),
                        zone.Radius * RadiusScale);
                }

                if (!_activeLogged)
                {
                    _activeLogged = true;
                    ModConfig.Log("CombatEvacuation: civilians are being evacuated from " + zones.Count
                        + " combat zone(s) to the city's shelters");
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("CombatEvacuation.Advance exception: " + e);
            }
        }

        /// <summary>Whether the city has anything citizens could be evacuated to. Shelters are Disaster
        /// service buildings running ShelterAI — the same test DisasterManager.EvacuateAll uses to find
        /// them, so a custom asset that works for a disaster works here too.</summary>
        private static bool HasAnyShelter()
        {
            if (!Singleton<BuildingManager>.exists) return false;
            BuildingManager bm = Singleton<BuildingManager>.instance;
            FastList<ushort> shelters = bm.GetServiceBuildings(ItemClass.Service.Disaster);
            if (shelters == null) return false;

            for (int i = 0; i < shelters.m_size; i++)
            {
                ushort id = shelters.m_buffer[i];
                if (id == 0) continue;
                BuildingInfo info = bm.m_buildings.m_buffer[id].Info;
                if (info != null && info.m_buildingAI is ShelterAI) return true;
            }
            return false;
        }

        /// <summary>Level unload: nothing to hand back (the evacuation map fades on its own), only the
        /// throttling and the once-only logs.</summary>
        public static void Reset()
        {
            _accum = 0f;
            _noShelterLogged = false;
            _activeLogged = false;
        }
    }
}
