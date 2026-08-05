using System;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game.Audio
{
    /// <summary>
    /// Task109: ユニットの移動音（ループ再生）を、そのユニットの見た目GameObjectへ付けた
    /// AudioSourceで鳴らす（3D・距離減衰つき）。クリップの調達はEngineSounds（CS自身の車両効果から借用）。
    ///
    /// 方針:
    ///   - 移動している間だけ鳴らす（停止・駐機・一時停止中は止める＝「移動の際の効果音」）。
    ///   - 音量は爆発/砲撃より控えめ（EngineSounds.EngineVolumeScale）。SoundVolume/SoundMutedに追従。
    ///   - カメラから遠いものは再生しない＋同時再生数に上限を設ける（乱戦でボイスを食い潰さない）。
    /// メインスレッド専用（UnitVisualsのSyncから駆動）。
    /// </summary>
    public static class UnitEngineAudio
    {
        private const float MinDistance = 40f;    // これより近いと最大音量
        private const float MaxDistance = 400f;   // これより遠いと無音（生成・再生もしない）
        private const int MaxActiveSources = 24;  // 同時に鳴らす移動音の上限

        private static int _activeThisFrame;

        /// <summary>毎フレーム、ユニットの走査を始める前に呼ぶ（同時再生数のカウンタをリセット）。</summary>
        public static void BeginFrame()
        {
            _activeThisFrame = 0;
        }

        /// <summary>このTypeKeyに移動音があるなら、rootへ停止状態のループAudioSourceを付けて返す
        /// （無ければnull＝以後この個体は無音）。</summary>
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
                src.spatialBlend = 1f; // 完全3D
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

        /// <summary>毎フレーム（ユニット1体ぶん）: 移動中・可聴距離・非ポーズなら鳴らし、そうでなければ止める。</summary>
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
