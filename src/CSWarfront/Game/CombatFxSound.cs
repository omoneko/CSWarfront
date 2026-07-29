using System;
using System.Collections.Generic;
using CSWarfront.Core;
using CSWarfront.Game.Audio;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// CombatFx（発砲/撃破の見た目エフェクト）に対応する「音」側（Task51、兵科別射撃音・撃破音）。
    /// CombatFx.cs と同じ partial class の別ファイル分割（1ファイルが肥大化しすぎないための整理のみで、
    /// 挙動・スレッド境界はCombatFx本体と完全に同一＝メインスレッド専用、例外は握ってログのみ）。
    ///
    /// PlayShotSound は CombatFx.SpawnOne から発射位置(from)・カメラ位置とともに呼ばれる。
    /// SpawnKillSounds は MilitaryManager.OnMainVisualUpdate から State.RecentKills のスナップショットを
    /// 受け取って呼ばれる（Spawn(shots)と対になる、視覚エフェクトを伴わない「音だけ」のエントリポイント）。
    /// </summary>
    internal static partial class CombatFx
    {
        // Task51: 銃撃バーストのうちどれくらいの頻度で跳弾音(WarfrontSounds.Ricochet)を混ぜるか。
        // 毎発鳴らすとうるさいだけなので、演出のスパイス程度に間引く（銃撃15発につき1回程度）。
        private const int RicochetEveryNGunfireShots = 15;
        private static int _gunfireSoundCounter;

        /// <summary>発砲した兵科に応じた音を発射位置(from)で再生する。視覚エフェクトの距離カリング
        /// (MaxSpawnDistanceFromCamera)を通過した後に呼ばれるが、WarfrontSoundPlayer側でさらに独立した
        /// （より近い）距離カリングと同時再生数の上限を適用するため、視覚とは別に静かに間引かれ得る。
        /// 銃撃(Gunfire)はまれに跳弾音(Ricochet)も重ねる（演出のスパイス、鳴らしすぎない）。</summary>
        private static void PlayShotSound(ShotEvent e, Vector3 from, Vector3? cameraPos)
        {
            string clipName = WarfrontSounds.ShotSoundFor(e.Category);
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
        /// 撃破音(車両撃破時)をキル位置で再生する（メインスレッド専用）。KillEventには視覚エフェクトを
        /// 付けない（音のみ）。Spawn(shots)と同じ「カメラ位置を1回だけ取得してから全件処理する」パターン。
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
                    // Task53: 歩兵・ドローン兵の撃破では「車両撃破時」の爆発音をオミットする
                    // （生身の歩兵が爆発するのは演出として不自然なため）。MechInfantryは車両に
                    // 乗った機械化歩兵なので対象外＝従来どおり爆発音を鳴らす（あえて含めない）。
                    if (k.Category == UnitCategory.Infantry || k.Category == UnitCategory.DroneInfantry)
                        continue;

                    Vector3 pos = new Vector3(k.Position.X, k.Position.Y, k.Position.Z);
                    WarfrontSoundPlayer.PlayKill(pos, cameraPos);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("CombatFx.SpawnKillSounds error: " + e);
            }
        }
    }
}
