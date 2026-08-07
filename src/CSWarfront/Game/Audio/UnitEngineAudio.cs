using System;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game.Audio
{
    /// <summary>
    /// Task109: plays each unit's movement sound (looped) through an AudioSource attached to the
    /// unit's visual GameObject (3D with distance attenuation). Clip sourcing is handled by
    /// EngineSounds (borrowed from CS's own vehicle effects).
    ///
    /// Policy:
    ///   - Play only while the unit is moving (stop when halted, parked, or paused = "sound effects
    ///     while moving").
    ///   - Volume is quieter than explosions/gunfire (EngineSounds.EngineVolumeScale). Tracks
    ///     SoundVolume/SoundMuted.
    ///   - Skip playback for units far from the camera, and cap the number of simultaneous sources
    ///     (so a melee cannot exhaust the audio voices).
    /// Main thread only (driven from UnitVisuals' Sync).
    /// </summary>
    public static class UnitEngineAudio
    {
        private const float MinDistance = 40f;    // full volume when closer than this
        private const float MaxDistance = 400f;   // silent beyond this (no creation/playback either)
        private const int MaxActiveSources = 24;  // cap on simultaneously playing movement sounds

        private static int _activeThisFrame;

        /// <summary>Called every frame before iterating over units (resets the concurrency counter).</summary>
        public static void BeginFrame()
        {
            _activeThisFrame = 0;
        }

        /// <summary>If this TypeKey has a movement sound, attaches a stopped looping AudioSource to
        /// root and returns it (null if none = this instance stays silent from then on).</summary>
        public static AudioSource TryAttach(GameObject root, string typeKey)
        {
            try
            {
                UnitCategory category;
                byte tier;
                if (!TypeKeyParser.TryParse(typeKey, out category, out tier)) return null;

                AudioClip clip;
                if (!EngineSounds.TryGetClip(category, out clip)) return null;

                AudioSource src = root.AddComponent<AudioSource>();
                src.clip = clip;
                src.loop = true;
                src.playOnAwake = false;
                src.spatialBlend = 1f; // fully 3D
                src.rolloffMode = AudioRolloffMode.Linear;
                src.minDistance = MinDistance;
                src.maxDistance = MaxDistance;
                src.dopplerLevel = 0f;
                src.volume = 0f;
                return src;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitEngineAudio.TryAttach error: " + e);
                return null;
            }
        }

        /// <summary>Every frame (per unit): play if moving, within audible range, and not paused; stop otherwise.</summary>
        public static void Update(AudioSource src, bool moving, Vector3 position, Vector3? cameraPos)
        {
            if (src == null) return;

            try
            {
                bool audible = moving
                    && !WarfrontSettings.SoundMuted
                    && WarfrontSettings.SoundVolume > 0
                    && _activeThisFrame < MaxActiveSources
                    && !IsGamePaused();

                if (audible && cameraPos.HasValue)
                {
                    float distSqr = (position - cameraPos.Value).sqrMagnitude;
                    if (distSqr > MaxDistance * MaxDistance) audible = false;
                }

                if (!audible)
                {
                    if (src.isPlaying) src.Stop();
                    return;
                }

                _activeThisFrame++;
                src.volume = Mathf.Clamp01(WarfrontSettings.SoundVolume / 100f) * EngineSounds.EngineVolumeScale;
                if (!src.isPlaying) src.Play();
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitEngineAudio.Update error: " + e);
            }
        }

        private static bool IsGamePaused()
        {
            try { return SimulationManager.instance.SimulationPaused; }
            catch (Exception) { return false; }
        }
    }
}
