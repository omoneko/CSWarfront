using System;
using System.Collections.Generic;
using UnityEngine;

namespace CSWarfront.Game.Audio
{
    /// <summary>
    /// Plays loaded AudioClips in 3D at a given world position (Task51, main thread only).
    /// Same proven pattern as MissileDisaster.Game.Audio.SoundPlayer (temporary GameObject with
    /// spatialBlend=1 and linear rolloff), plus extra defenses for large-scale battles:
    ///   - Concurrency cap per sound category: prevents dozens of overlapping copies of the same sound
    ///     from exhausting the audio voices.
    ///   - Camera distance culling: shots/kills that are too far away never even get an AudioSource
    ///     (relying on Unity's linear rolloff alone still piles up GameObjects at inaudible distances).
    ///   - No playback while the game is paused (SimulationManager.instance.SimulationPaused).
    ///   - WarfrontSettings.SoundVolume/SoundMuted are consulted on every call.
    /// </summary>
    public static class WarfrontSoundPlayer
    {
        // Task51: concurrency cap for firing sounds (rifle/heavy MG/cannon/AA missile). Kept
        // conservative (about 8 as per the requirements) so that even up to 200 ShotEvents per tick in
        // a melee cannot flood the voices.
        private const int MaxConcurrentShotSounds = 8;
        private const float ShotMinDistance = 20f;   // full volume when closer than this
        private const float ShotMaxDistance = 600f;  // silent beyond this (also used for camera distance culling)

        // Task51: kill sounds (vehicle destroyed) are far less frequent than firing, so the cap is
        // looser and they carry a bit farther.
        private const int MaxConcurrentKillSounds = 4;
        private const float KillMinDistance = 20f;
        private const float KillMaxDistance = 800f;

        // Task51: the ricochet sound is pure presentation spice (occasionally plays on a gunfire
        // burst), so its cap is minimal.
        private const int MaxConcurrentRicochetSounds = 2;

        private static readonly List<float> _shotExpiry = new List<float>();
        private static readonly List<float> _killExpiry = new List<float>();
        private static readonly List<float> _ricochetExpiry = new List<float>();

        public static void PlayShot(string clipName, Vector3 position, Vector3? cameraPos)
        {
            Play(clipName, position, cameraPos, _shotExpiry, MaxConcurrentShotSounds, ShotMinDistance, ShotMaxDistance);
        }

        public static void PlayKill(Vector3 position, Vector3? cameraPos)
        {
            Play(WarfrontSounds.VehicleDestroyed, position, cameraPos, _killExpiry, MaxConcurrentKillSounds,
                KillMinDistance, KillMaxDistance);
        }

        public static void PlayRicochet(Vector3 position, Vector3? cameraPos)
        {
            Play(WarfrontSounds.Ricochet, position, cameraPos, _ricochetExpiry, MaxConcurrentRicochetSounds,
                ShotMinDistance, ShotMaxDistance);
        }

        private static void Play(string clipName, Vector3 position, Vector3? cameraPos, List<float> activeExpiry,
            int maxConcurrent, float minDistance, float maxDistance)
        {
            try
            {
                if (string.IsNullOrEmpty(clipName)) return;
                if (WarfrontSettings.SoundMuted || WarfrontSettings.SoundVolume <= 0) return;
                if (IsGamePaused()) return;

                if (cameraPos.HasValue)
                {
                    float distSqr = (position - cameraPos.Value).sqrMagnitude;
                    if (distSqr > maxDistance * maxDistance) return;
                }

                Prune(activeExpiry);
                if (activeExpiry.Count >= maxConcurrent) return;

                AudioClip clip = WarfrontSounds.Get(clipName);
                if (clip == null) return; // silent if not loaded yet or loading failed (combat processing continues)

                var go = new GameObject("CSWarfrontSound_" + clipName);
                go.transform.position = position;
                var src = go.AddComponent<AudioSource>();
                src.clip = clip;
                src.volume = Mathf.Clamp01(WarfrontSettings.SoundVolume / 100f);
                src.spatialBlend = 1f; // fully 3D
                src.rolloffMode = AudioRolloffMode.Linear;
                src.minDistance = minDistance;
                src.maxDistance = maxDistance;
                src.dopplerLevel = 0f;
                src.playOnAwake = false;
                src.Play();

                UnityEngine.Object.Destroy(go, clip.length + 0.5f);
                activeExpiry.Add(Time.realtimeSinceStartup + clip.length);
            }
            catch (Exception e)
            {
                ModConfig.LogError("WarfrontSoundPlayer.Play(" + clipName + ") error: " + e);
            }
        }

        /// <summary>Removes expired (finished playing) slots. There is no separate Update-style hook;
        /// this is evaluated lazily the next time a sound of that category is about to play (cheap,
        /// main thread only).</summary>
        private static void Prune(List<float> activeExpiry)
        {
            float now = Time.realtimeSinceStartup;
            for (int i = activeExpiry.Count - 1; i >= 0; i--)
                if (activeExpiry[i] <= now) activeExpiry.RemoveAt(i);
        }

        private static bool IsGamePaused()
        {
            try { return SimulationManager.instance.SimulationPaused; }
            catch (Exception e)
            {
                ModConfig.LogError("WarfrontSoundPlayer.IsGamePaused error: " + e);
                return false;
            }
        }
    }
}
