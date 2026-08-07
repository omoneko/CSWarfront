using System;
using System.Collections.Generic;
using CSWarfront.Core;
using CSWarfront.Game.Audio;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// The "sound" side that accompanies CombatFx (the visual firing/kill effects) (Task51,
    /// per-category shot sounds and kill sounds). A separate-file split of the same partial class as
    /// CombatFx.cs (purely organizational, to keep any single file from growing too large; behavior
    /// and the thread boundary are exactly identical to the CombatFx core = main thread only,
    /// exceptions are swallowed and only logged).
    ///
    /// PlayShotSound is called from CombatFx.SpawnOne together with the firing position (from) and the
    /// camera position.
    /// SpawnKillSounds is called from MilitaryManager.OnMainVisualUpdate with a snapshot of
    /// State.RecentKills (the counterpart of Spawn(shots): a "sound-only" entry point with no visual
    /// effect attached).
    /// </summary>
    internal static partial class CombatFx
    {
        // Task51: how often a ricochet sound (WarfrontSounds.Ricochet) is mixed into gunfire bursts.
        // Playing it on every round would just be noisy, so it is thinned out to a light garnish
        // (roughly once per 15 gunfire rounds).
        private const int RicochetEveryNGunfireShots = 15;
        private static int _gunfireSoundCounter;

        /// <summary>Plays the sound matching the firing unit's category at the firing position (from).
        /// Called after passing the visual effects' distance culling (MaxSpawnDistanceFromCamera), but
        /// WarfrontSoundPlayer applies its own independent (tighter) distance culling and a cap on
        /// simultaneous playbacks, so sounds may be silently thinned out separately from the visuals.
        /// Gunfire occasionally also layers a ricochet sound (Ricochet) on top (a light garnish, not
        /// overplayed).</summary>
        private static void PlayShotSound(ShotEvent e, Vector3 from, Vector3? cameraPos)
        {
            string clipName = WarfrontSounds.ShotSoundFor(e.Category, e.Kind); // Task90: supports distinct anti-air sounds
            if (clipName != null) WarfrontSoundPlayer.PlayShot(clipName, from, cameraPos);

            if (e.Kind == ShotKind.Gunfire)
            {
                _gunfireSoundCounter++;
                if (_gunfireSoundCounter >= RicochetEveryNGunfireShots)
                {
                    _gunfireSoundCounter = 0;
                    WarfrontSoundPlayer.PlayRicochet(from, cameraPos);
                }
            }
        }

        /// <summary>
        /// Plays the kill sound (on vehicle destruction) at the kill position (main thread only).
        /// KillEvents get no visual effect attached (sound only). Same pattern as Spawn(shots):
        /// "fetch the camera position once, then process all entries".
        /// </summary>
        public static void SpawnKillSounds(List<KillEvent> kills)
        {
            if (kills == null || kills.Count == 0) return;

            try
            {
                Camera cam = Camera.main;
                Vector3? cameraPos = cam != null ? (Vector3?)cam.transform.position : null;

                for (int i = 0; i < kills.Count; i++)
                {
                    KillEvent k = kills[i];
                    // Task53: omit the "vehicle destruction" explosion sound for infantry and drone
                    // infantry kills (flesh-and-blood infantry exploding is unnatural as a presentation).
                    // MechInfantry is mechanized infantry riding vehicles, so it is not excluded = the
                    // explosion sound plays as before (deliberately not included here).
                    if (!IsVehicleDestructionCategory(k.Category)) continue;

                    Vector3 pos = new Vector3(k.Position.X, k.Position.Y, k.Position.Z);
                    WarfrontSoundPlayer.PlayKill(pos, cameraPos);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("CombatFx.SpawnKillSounds error: " + e);
            }
        }

        /// <summary>Whether the killed category is subject to the "vehicle destruction" presentation
        /// (the explosion sound = SpawnKillSounds directly above, and the explosion effect = Task65's
        /// KillFx.Spawn). Both must use exactly the same criterion (per the Task65 spec: if the sound
        /// and the effect judged differently, inconsistencies like "the explosion sound plays but no
        /// fireball appears" would arise), so the decision logic is consolidated and shared in this one
        /// place. Infantry and drone infantry (flesh-and-blood) are false; everything else (all vehicle
        /// types including MechInfantry) is true.</summary>
        internal static bool IsVehicleDestructionCategory(UnitCategory category)
        {
            return category != UnitCategory.Infantry && category != UnitCategory.DroneInfantry;
        }
    }
}
