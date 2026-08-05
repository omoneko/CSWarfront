using System;
using System.Collections.Generic;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game.Audio
{
    /// <summary>
    /// Task109（ユーザー要望「各ユニットの移動時の効果音」）: 移動音（エンジン音）のクリップ解決。
    ///
    /// 移動音は自前のwavを増やさず、CS自身の車両プレハブが持つ「ループするサウンド効果」を借りる:
    ///   - 地上車両  … VehicleInfo.VehicleType.Car のプレハブから
    ///   - 戦闘機/爆撃機 … Plane
    ///   - ヘリコプター … Helicopter
    ///   - 軍用貨物列車 … Train（ユーザー要望「貨物列車はCSのデフォルトのもの」）
    /// 歩兵（生身）と海上ユニットには移動音を付けない（要件どおり。艦艇は砲撃音のみ）。
    ///
    /// 探索は「そのVehicleTypeの最初のプレハブから、ループ再生のSoundEffect（EffectInfo）を再帰的に
    /// 探す」だけ——プレハブ名を決め打ちしないので、CSのバージョン差や、効果を削る他MOD
    /// （Vehicle Effects等）が入っていても壊れず、見つからなければその種別だけ無音になる。
    /// レベルロード後（プレハブが揃った後）にResolveAllを1回呼ぶ。メインスレッド専用。
    /// </summary>
    public static class EngineSounds
    {
        /// <summary>移動音の基準音量（WarfrontSettings.SoundVolumeに掛かる）。爆発・砲撃より小さく
        /// 抑える（ユーザー要望「音量は爆発音に比べたら小さめでいい」）。</summary>
        public const float EngineVolumeScale = 0.35f;

        private enum EngineKind { Ground, Plane, Helicopter, Train }

        private static readonly Dictionary<EngineKind, AudioClip> _clips = new Dictionary<EngineKind, AudioClip>();
        private static bool _resolved;

        /// <summary>レベルロード時に1回。以後はキャッシュを返す（Resetでやり直せる）。</summary>
        public static void ResolveAll()
        {
            if (_resolved) return;
            _resolved = true;
            _clips.Clear();

            TryResolve(EngineKind.Ground, VehicleInfo.VehicleType.Car);
            TryResolve(EngineKind.Plane, VehicleInfo.VehicleType.Plane);
            TryResolve(EngineKind.Helicopter, VehicleInfo.VehicleType.Helicopter);
            TryResolve(EngineKind.Train, VehicleInfo.VehicleType.Train);
        }

        /// <summary>レベルアンロード時（次のセッションで解決し直す）。</summary>
        public static void Reset()
        {
            _resolved = false;
            _clips.Clear();
        }

        /// <summary>このユニット種別の移動音クリップ（無ければfalse＝移動音なし）。</summary>
        public static bool TryGetClip(UnitCategory category, out AudioClip clip)
        {
            clip = null;
            EngineKind kind;
            if (!TryGetKind(category, out kind)) return false;
            return _clips.TryGetValue(kind, out clip) && clip != null;
        }

        /// <summary>ユニット種別 → 移動音の系統。移動音を持たない種別（歩兵・艦艇・自爆ドローン等）はfalse。</summary>
        private static bool TryGetKind(UnitCategory category, out EngineKind kind)
        {
            switch (category)
            {
                // 地上の車両系（歩兵・ドローン兵＝生身は対象外）。
                case UnitCategory.MechInfantry:
                case UnitCategory.Apc:
                case UnitCategory.Tank:
                case UnitCategory.Artillery:
                case UnitCategory.AntiAir:
                case UnitCategory.SupplyTruck:
                    kind = EngineKind.Ground; return true;

                case UnitCategory.AirSuperiority:
                case UnitCategory.TacticalBomber:
                    kind = EngineKind.Plane; return true;

                case UnitCategory.AttackHelicopter:
                case UnitCategory.TransportHelicopter:
                    kind = EngineKind.Helicopter; return true;

                case UnitCategory.MilitaryTrain:
                    kind = EngineKind.Train; return true;

                default:
                    kind = EngineKind.Ground; return false; // Infantry/DroneInfantry/艦艇/自爆ドローン
            }
        }

        private static void TryResolve(EngineKind kind, VehicleInfo.VehicleType vehicleType)
        {
            try
            {
                int count = PrefabCollection<VehicleInfo>.LoadedCount();
                for (int i = 0; i < count; i++)
                {
                    VehicleInfo info = PrefabCollection<VehicleInfo>.GetLoaded((uint)i);
                    if (info == null || info.m_vehicleType != vehicleType) continue;
                    if (info.m_effects == null) continue;

                    for (int e = 0; e < info.m_effects.Length; e++)
                    {
                        AudioClip clip = FindLoopingClip(info.m_effects[e].m_effect, 0);
                        if (clip == null) continue;

                        _clips[kind] = clip;
                        ModConfig.Log("EngineSounds: " + kind + " movement sound = '" + clip.name +
                            "' (from vehicle prefab '" + info.name + "')");
                        return;
                    }
                }
                ModConfig.Log("EngineSounds: no looping sound found for " + kind + " (that category stays silent)");
            }
            catch (Exception ex)
            {
                ModConfig.LogError("EngineSounds.TryResolve(" + kind + ") error: " + ex);
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
