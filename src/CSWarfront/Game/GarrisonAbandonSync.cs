using System;
using System.Collections.Generic;
using ColossalFramework;
using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// Task139 (Workshop request from lilMobStick: "civilian buildings should probably become abandoned,
    /// and garrisoned by military whenever fighting starts in their proximity"): while infantry are
    /// holding a city building (BuildingGarrisonStep, Task127), that building is shown as abandoned. The
    /// residents have fled the fighting; when the troops move on, the building goes back to normal and
    /// the city repopulates it by itself.
    ///
    /// Why Building.Flags.Abandoned: it is the game's own "this place has been left" state, so it comes
    /// with the derelict look, the empty-building behaviour and the info-view treatment for free. Nothing
    /// here fakes an appearance.
    ///
    /// This writes into the shared CS building buffer, so it follows exactly the discipline
    /// BaseHiddenSync established for Building.Flags.Hidden:
    ///   - sim thread only (called from MilitaryManager.OnSimTick, inside _stateLock);
    ///   - every bit this mod sets is remembered in <see cref="_abandonedIds"/> and cleared by exactly
    ///     this class — a flag is never set on a building that was already abandoned by the city itself,
    ///     and such a building is never cleared either (see <see cref="Apply"/>);
    ///   - cleared before the game writes the building array to the save and re-applied afterwards
    ///     (<see cref="ClearAllForSave"/> / <see cref="ReapplyAfterSave"/>), so a temporary battlefield
    ///     state is never baked into the player's city;
    ///   - cleared on level unload (<see cref="Reset"/>), because after that the sim thread will not run
    ///     again and nothing else would restore the buildings.
    /// The combination is what makes this safe to do to somebody's city at all: the worst case is a
    /// building that looks derelict until the next tick.
    /// </summary>
    internal static class GarrisonAbandonSync
    {
        /// <summary>Buildings this mod has flagged, and only those.</summary>
        private static readonly HashSet<ushort> _abandonedIds = new HashSet<ushort>();

        private static readonly HashSet<ushort> _desired = new HashSet<ushort>();
        private static readonly List<ushort> _scratch = new List<ushort>();

        /// <summary>Sim thread (OnSimTick, inside _stateLock): brings the flagged set in line with who is
        /// currently holding a building.</summary>
        public static void Apply(WarState state)
        {
            try
            {
                if (!WarfrontSettings.GarrisonAbandonsBuildings)
                {
                    if (_abandonedIds.Count > 0) RestoreAll(); // the player just turned it off
                    return;
                }
                if (state == null || !Singleton<BuildingManager>.exists) return;

                _desired.Clear();
                for (int i = 0; i < state.Units.Count; i++)
                {
                    UnitInstance u = state.Units[i];
                    if (!u.IsAlive || u.GarrisonBuildingId == 0) continue;
                    _desired.Add(u.GarrisonBuildingId);
                }
                if (_desired.Count == 0 && _abandonedIds.Count == 0) return;

                Building[] buf = Singleton<BuildingManager>.instance.m_buildings.m_buffer;

                // Release the ones no longer held (and anything demolished under us).
                _scratch.Clear();
                foreach (ushort id in _abandonedIds)
                {
                    if (_desired.Contains(id) && id < buf.Length
                        && (buf[id].m_flags & Building.Flags.Created) != 0) continue;
                    _scratch.Add(id);
                }
                for (int i = 0; i < _scratch.Count; i++)
                {
                    ushort id = _scratch[i];
                    if (id < buf.Length) buf[id].m_flags &= ~Building.Flags.Abandoned;
                    _abandonedIds.Remove(id);
                }

                // Flag the newly held ones.
                foreach (ushort id in _desired)
                {
                    if (_abandonedIds.Contains(id)) continue;
                    if (id >= buf.Length) continue;
                    if ((buf[id].m_flags & Building.Flags.Created) == 0) continue;
                    // Already abandoned, collapsed or burned down by the city itself: leave it alone.
                    // Clearing it later would be us repairing a building we never touched.
                    if ((buf[id].m_flags & (Building.Flags.Abandoned | Building.Flags.Collapsed
                        | Building.Flags.BurnedDown)) != 0) continue;

                    buf[id].m_flags |= Building.Flags.Abandoned;
                    _abandonedIds.Add(id);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("GarrisonAbandonSync.Apply exception: " + e);
            }
        }

        /// <summary>Called right before saving, while holding _stateLock: takes this mod's bits back out
        /// so a battle in progress is not written into the city. The set is kept, and
        /// <see cref="ReapplyAfterSave"/> puts them back.</summary>
        public static void ClearAllForSave()
        {
            try
            {
                if (_abandonedIds.Count == 0 || !Singleton<BuildingManager>.exists) return;
                Building[] buf = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
                foreach (ushort id in _abandonedIds)
                {
                    if (id < buf.Length) buf[id].m_flags &= ~Building.Flags.Abandoned;
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("GarrisonAbandonSync.ClearAllForSave exception: " + e);
            }
        }

        /// <summary>Re-sets what ClearAllForSave removed. By contract the caller defers this until the
        /// game has finished writing Building.m_flags to the stream (see MilitaryManagerPersistence).
        /// Buildings demolished in the meantime are quietly dropped.</summary>
        public static void ReapplyAfterSave()
        {
            try
            {
                if (_abandonedIds.Count == 0 || !Singleton<BuildingManager>.exists) return;
                Building[] buf = Singleton<BuildingManager>.instance.m_buildings.m_buffer;

                _scratch.Clear();
                foreach (ushort id in _abandonedIds)
                {
                    if (id >= buf.Length || (buf[id].m_flags & Building.Flags.Created) == 0)
                    {
                        _scratch.Add(id);
                        continue;
                    }
                    buf[id].m_flags |= Building.Flags.Abandoned;
                }
                for (int i = 0; i < _scratch.Count; i++) _abandonedIds.Remove(_scratch[i]);
            }
            catch (Exception e)
            {
                ModConfig.LogError("GarrisonAbandonSync.ReapplyAfterSave exception: " + e);
            }
        }

        /// <summary>Level unload (MilitaryManager.Reset, main thread): hands every building back before
        /// the sim thread stops running. A failure here is logged and swallowed — the level may already
        /// be tearing down.</summary>
        public static void Reset()
        {
            try
            {
                RestoreAll();
            }
            catch (Exception e)
            {
                ModConfig.LogError("GarrisonAbandonSync.Reset: restore-on-unload failed (level is tearing down): " + e);
            }
            finally
            {
                _abandonedIds.Clear();
                _desired.Clear();
            }
        }

        private static void RestoreAll()
        {
            if (_abandonedIds.Count == 0 || !Singleton<BuildingManager>.exists) { _abandonedIds.Clear(); return; }
            Building[] buf = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            foreach (ushort id in _abandonedIds)
            {
                if (id < buf.Length) buf[id].m_flags &= ~Building.Flags.Abandoned;
            }
            _abandonedIds.Clear();
        }

        /// <summary>How many buildings are currently held (for the diagnostic log).</summary>
        public static int Count { get { return _abandonedIds.Count; } }
    }
}
