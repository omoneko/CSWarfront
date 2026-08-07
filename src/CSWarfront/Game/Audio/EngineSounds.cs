using System;
using System.Collections.Generic;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game.Audio
{
    /// <summary>
    /// Task109 (user request "movement sound effects for each unit"): resolves the movement (engine)
    /// sound clips.
    ///
    /// Sound sources:
    ///   - Ground vehicles / fighters / bombers / helicopters ... user-supplied wavs (Sounds\engine_*.wav)
    ///   - Military freight train ... the looping sound owned by CS's own train prefab
    ///     (user request "freight trains should use CS's default sound")
    /// Infantry (on foot) and naval units get no movement sound (as per requirements; ships only have
    /// gunfire sounds).
    ///
    /// The train sound lookup is just "recursively search VehicleType.Train prefabs for a looping
    /// SoundEffect (EffectInfo)" — no hard-coded prefab name, so it survives CS version differences
    /// and other mods that strip effects (Vehicle Effects etc.); if nothing is found, only trains stay
    /// silent. ResolveAll is called once after level load (once prefabs are available). Main thread
    /// only.
    /// </summary>
    public static class EngineSounds
    {
        /// <summary>Base volume for movement sounds (multiplied by WarfrontSettings.SoundVolume). Kept
        /// quieter than explosions/gunfire (user request "the volume can be lower than the explosion
        /// sounds").</summary>
        public const float EngineVolumeScale = 0.35f;

        private static AudioClip _trainClip;
        private static bool _resolved;

        /// <summary>Once at level load. Only resolves the train sound borrowed from CS (everything else
        /// uses WarfrontSounds' wavs).</summary>
        public static void ResolveAll()
        {
            if (_resolved) return;
            _resolved = true;
            _trainClip = null;
            TryResolveTrainClip();
        }

        /// <summary>At level unload (re-resolve in the next session).</summary>
        public static void Reset()
        {
            _resolved = false;
            _trainClip = null;
        }

        /// <summary>Movement sound clip for this unit category (false if none = no movement sound).</summary>
        public static bool TryGetClip(UnitCategory category, out AudioClip clip)
        {
            clip = null;

            switch (category)
            {
                // Ground vehicle types (infantry/drone infantry = on foot, excluded).
                case UnitCategory.MechInfantry:
                case UnitCategory.Apc:
                case UnitCategory.Tank:
                case UnitCategory.Artillery:
                case UnitCategory.AntiAir:
                case UnitCategory.SupplyTruck:
                    clip = WarfrontSounds.Get(WarfrontSounds.EngineGround);
                    break;

                case UnitCategory.AirSuperiority:
                    clip = WarfrontSounds.Get(WarfrontSounds.EngineFighter);
                    break;

                case UnitCategory.TacticalBomber:
                    clip = WarfrontSounds.Get(WarfrontSounds.EngineBomber);
                    break;

                case UnitCategory.AttackHelicopter:
                case UnitCategory.TransportHelicopter:
                    clip = WarfrontSounds.Get(WarfrontSounds.EngineHelicopter);
                    break;

                case UnitCategory.MilitaryTrain:
                    clip = _trainClip; // CS's train sound (user request)
                    break;

                default:
                    return false; // Infantry/DroneInfantry/ships/suicide drones = no movement sound
            }

            return clip != null;
        }

        private static void TryResolveTrainClip()
        {
            try
            {
                int count = PrefabCollection<VehicleInfo>.LoadedCount();
                for (int i = 0; i < count; i++)
                {
                    VehicleInfo info = PrefabCollection<VehicleInfo>.GetLoaded((uint)i);
                    if (info == null || info.m_vehicleType != VehicleInfo.VehicleType.Train) continue;
                    if (info.m_effects == null) continue;

                    for (int e = 0; e < info.m_effects.Length; e++)
                    {
                        AudioClip clip = FindLoopingClip(info.m_effects[e].m_effect, 0);
                        if (clip == null) continue;

                        _trainClip = clip;
                        ModConfig.Log("EngineSounds: train movement sound = '" + clip.name +
                            "' (from vehicle prefab '" + info.name + "')");
                        return;
                    }
                }
                ModConfig.Log("EngineSounds: no looping train sound found (military trains stay silent)");
            }
            catch (Exception ex)
            {
                ModConfig.LogError("EngineSounds.TryResolveTrainClip error: " + ex);
            }
        }

        /// <summary>Walks the EffectInfo tree looking for a looping SoundEffect's clip (handles MultiEffect).</summary>
        private static AudioClip FindLoopingClip(EffectInfo effect, int depth)
        {
            if (effect == null || depth > 4) return null;

            SoundEffect sound = effect as SoundEffect;
            if (sound != null)
            {
                if (sound.m_audioInfo == null || sound.m_audioInfo.m_clip == null) return null;
                return sound.m_audioInfo.m_loop ? sound.m_audioInfo.m_clip : null;
            }

            MultiEffect multi = effect as MultiEffect;
            if (multi != null && multi.m_effects != null)
            {
                for (int i = 0; i < multi.m_effects.Length; i++)
                {
                    AudioClip clip = FindLoopingClip(multi.m_effects[i].m_effect, depth + 1);
                    if (clip != null) return clip;
                }
            }
            return null;
        }
    }
}
