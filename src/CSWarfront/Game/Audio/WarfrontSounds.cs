using System.Collections.Generic;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game.Audio
{
    /// <summary>
    /// Loads Sounds/*.wav at runtime and caches the AudioClips (Task51, per-branch firing/kill sounds).
    /// Same proven pattern as the Missile Disaster mod (MissileDisaster.Game.Audio.SoundLibrary):
    /// Initialize(modPath) is called from WarfrontLoadingExtension.OnLevelLoaded, and a
    /// WarfrontSoundLoaderBehaviour on a hidden DontDestroyOnLoad host GameObject performs the actual
    /// loading in a coroutine (only once). Everything runs on the main thread.
    ///
    /// Note: CS (Unity 5.6) does not support runtime MP3 decoding (WWW.GetAudioClip(AudioType.MPEG)
    /// returns null, confirmed in-game with MissileDisaster). Therefore the user-supplied mp3 originals
    /// are converted to WAV at build time and deployed/loaded as Sounds/*.wav (see build.ps1; the mp3
    /// originals are also kept in src\CSWarfront\Sounds\, but only the *.wav files are deployed).
    /// </summary>
    public static class WarfrontSounds
    {
        // Base names (without extension) of the wav files placed in the Sounds folder.
        public const string Rifle1 = "rifle1";
        public const string Rifle2 = "rifle2";
        public const string Rifle3 = "rifle3";
        public const string Rifle4 = "rifle4";
        public const string Mg1 = "mg1";
        public const string Mg2 = "mg2";
        public const string Cannon1 = "cannon1";
        public const string Cannon2 = "cannon2";
        public const string Cannon3 = "cannon3";
        public const string AaMissile = "aa_missile";
        public const string Ricochet = "ricochet";
        public const string VehicleDestroyed = "vehicle_destroyed";

        // Task109: engine/movement sounds (looped playback). User-supplied mp3s converted to mono
        // 22.05kHz WAV (mono is required for 3D spatialization). Only the military freight train
        // borrows CS's own train sound, so it is not listed here (see EngineSounds).
        public const string EngineGround = "engine_ground";
        public const string EngineFighter = "engine_fighter";
        public const string EngineBomber = "engine_bomber";
        public const string EngineHelicopter = "engine_helicopter";

        private static readonly string[] RifleVariants = { Rifle1, Rifle2, Rifle3, Rifle4 };
        private static readonly string[] MgVariants = { Mg1, Mg2 };
        private static readonly string[] CannonVariants = { Cannon1, Cannon2, Cannon3 };

        public static readonly string[] FileNames =
        {
            Rifle1, Rifle2, Rifle3, Rifle4, Mg1, Mg2, Cannon1, Cannon2, Cannon3,
            AaMissile, Ricochet, VehicleDestroyed,
            EngineGround, EngineFighter, EngineBomber, EngineHelicopter // Task109
        };

        private static bool _loadStarted;
        private static readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>();

        // Task51: rotation counters for the per-branch sound variants. No System.Random; each call just
        // advances through 0..length-1 in order (cheap and deterministic). This is purely Game-layer
        // presentation state and never touches the Core simulation's determinism contract (no RNG use).
        private static int _rifleIndex, _mgIndex, _cannonIndex;

        /// <summary>
        /// Called from WarfrontLoadingExtension.OnLevelLoaded. Creates the resident DontDestroyOnLoad
        /// host and immediately starts loading Sounds/*.wav (never started twice). Main thread only.
        /// </summary>
        public static void Initialize(string modDir)
        {
            if (_loadStarted) return;
            if (string.IsNullOrEmpty(modDir))
            {
                ModConfig.LogError("WarfrontSounds.Initialize: modDir is empty");
                return;
            }
            _loadStarted = true;
            try
            {
                var go = new GameObject("CSWarfrontAudioLoader");
                Object.DontDestroyOnLoad(go);
                var loader = go.AddComponent<WarfrontSoundLoaderBehaviour>();
                loader.Begin(modDir);
                ModConfig.Log("WarfrontSounds initialized: " + modDir);
            }
            catch (System.Exception e)
            {
                ModConfig.LogError("WarfrontSounds.Initialize error: " + e);
            }
        }

        public static void Register(string name, AudioClip clip)
        {
            if (!string.IsNullOrEmpty(name) && clip != null) _clips[name] = clip;
        }

        /// <summary>Returns the AudioClip if it has been loaded; null if not loaded yet or loading failed.</summary>
        public static AudioClip Get(string name)
        {
            AudioClip c;
            return !string.IsNullOrEmpty(name) && _clips.TryGetValue(name, out c) ? c : null;
        }

        /// <summary>
        /// Deterministically rotates through the firing-sound variants for each branch (Task51).
        /// Infantry/MechInfantry: rifle fire (4 variants); Apc/DroneInfantry: heavy machine gun (2);
        /// Tank/Artillery: cannon fire (3); AntiAir: AA missile (single). Unmapped branches (naval/air
        /// units etc., currently unimplemented) return null, and the caller (CombatFx) continues
        /// silently.
        /// </summary>
        public static string ShotSoundFor(UnitCategory category)
        {
            return ShotSoundFor(category, ShotKind.Gunfire);
        }

        /// <summary>Task90: overload that also considers ShotKind. Anti-air (AntiAir) supports distinct
        /// sounds — the anti-drone machine gun (Gunfire) uses the heavy machine gun sound, while
        /// SamMissile against fighters/bombers uses the AA missile sound.
        /// For all other branches ShotKind is ignored (still determined by category only, as before).</summary>
        public static string ShotSoundFor(UnitCategory category, ShotKind kind)
        {
            switch (category)
            {
                case UnitCategory.Infantry:
                case UnitCategory.MechInfantry:
                    return RifleVariants[NextIndex(ref _rifleIndex, RifleVariants.Length)];
                case UnitCategory.Apc:
                case UnitCategory.DroneInfantry:
                    return MgVariants[NextIndex(ref _mgIndex, MgVariants.Length)];
                case UnitCategory.Tank:
                case UnitCategory.Artillery:
                case UnitCategory.Destroyer: // Task88: destroyers' naval guns/missiles also get the cannon sound (previously unmapped = silent)
                    return CannonVariants[NextIndex(ref _cannonIndex, CannonVariants.Length)];
                case UnitCategory.AntiAir:
                    return kind == ShotKind.SamMissile
                        ? AaMissile
                        : MgVariants[NextIndex(ref _mgIndex, MgVariants.Length)];
                default:
                    return null;
            }
        }

        private static int NextIndex(ref int counter, int length)
        {
            int idx = counter;
            counter++;
            if (counter >= length) counter = 0;
            return idx;
        }
    }
}
