using System;
using System.Collections.Generic;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game.Audio
{
    /// <summary>
    /// Task109（ユーザー要望「各ユニットの移動時の効果音」）: 移動音（エンジン音）のクリップ解決。
    ///
    /// 音源の内訳:
    ///   - 地上車両 / 戦闘機 / 爆撃機 / ヘリコプター … ユーザー提供のwav（Sounds\engine_*.wav）
    ///   - 軍用貨物列車 … CS自身の列車プレハブが持つループ音（ユーザー要望「貨物列車はCSのデフォルトのもの」）
    /// 歩兵（生身）と海上ユニットには移動音を付けない（要件どおり。艦艇は砲撃音のみ）。
    ///
    /// 列車音の探索は「VehicleType.Trainのプレハブから、ループ再生のSoundEffect（EffectInfo）を
    /// 再帰的に探す」だけ——プレハブ名を決め打ちしないので、CSのバージョン差や、効果を削る他MOD
    /// （Vehicle Effects等）が入っていても壊れず、見つからなければ列車だけ無音になる。
    /// レベルロード後（プレハブが揃った後）にResolveAllを1回呼ぶ。メインスレッド専用。
    /// </summary>
    public static class EngineSounds
    {
        /// <summary>移動音の基準音量（WarfrontSettings.SoundVolumeに掛かる）。爆発・砲撃より小さく
        /// 抑える（ユーザー要望「音量は爆発音に比べたら小さめでいい」）。</summary>
        public const float EngineVolumeScale = 0.35f;

        private static AudioClip _trainClip;
        private static bool _resolved;

        /// <summary>レベルロード時に1回。CSから借りる列車音だけを解決する（他はWarfrontSoundsのwav）。</summary>
        public static void ResolveAll()
        {
            if (_resolved) return;
            _resolved = true;
            _trainClip = null;
            TryResolveTrainClip();
        }

        /// <summary>レベルアンロード時（次のセッションで解決し直す）。</summary>
        public static void Reset()
        {
            _resolved = false;
            _trainClip = null;
        }

        /// <summary>このユニット種別の移動音クリップ（無ければfalse＝移動音なし）。</summary>
        public static bool TryGetClip(UnitCategory category, out AudioClip clip)
        {
            clip = null;

            switch (category)
            {
                // 地上の車両系（歩兵・ドローン兵＝生身は対象外）。
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
                    clip = _trainClip; // CSの列車音（ユーザー要望）
                    break;

                default:
                    return false; // Infantry/DroneInfantry/艦艇/自爆ドローン＝移動音なし
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

        /// <summary>EffectInfoの木を辿ってループ再生のSoundEffectのクリップを探す（MultiEffect対応）。</summary>
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
