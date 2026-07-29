using System;
using System.Collections.Generic;
using UnityEngine;

namespace CSWarfront.Game.Audio
{
    /// <summary>
    /// 読込済み AudioClip を指定ワールド座標で3D再生する（Task51、メインスレッド専用）。
    /// MissileDisaster.Game.Audio.SoundPlayer と同じ実績パターン（spatialBlend=1・線形ロールオフの
    /// 一時GameObject）に、大規模乱戦向けの追加防御を載せている:
    ///   - 同時再生数の上限（種別ごと）: 同じ音が何十発も重なって鳴り、音声ボイスを食い潰すのを防ぐ。
    ///   - カメラからの距離カリング: 遠すぎる発砲/撃破はそもそもAudioSourceを生成しない
    ///     （Unityの線形ロールオフだけに任せると、聞こえない距離でもGameObjectが積み上がる）。
    ///   - 一時停止中は再生しない（SimulationManager.instance.SimulationPaused）。
    ///   - WarfrontSettings.SoundVolume/SoundMuted を毎回参照する。
    /// </summary>
    public static class WarfrontSoundPlayer
    {
        // Task51: 発砲音（銃撃/重機関銃/砲撃/対空ミサイル）の同時再生数上限。乱戦で毎tick最大200件の
        // ShotEventが来てもボイスが埋め尽くされないよう、控えめな値にする（要件どおり目安8件）。
        private const int MaxConcurrentShotSounds = 8;
        private const float ShotMinDistance = 20f;   // これより近いと最大音量
        private const float ShotMaxDistance = 600f;  // これより遠いと無音（カメラ距離カリングにも使う）

        // Task51: 撃破音（車両撃破時）は発砲より遥かに低頻度なので上限は緩め、少し遠くまで届かせる。
        private const int MaxConcurrentKillSounds = 4;
        private const float KillMinDistance = 20f;
        private const float KillMaxDistance = 800f;

        // Task51: 跳弾音は演出専用のスパイス（銃撃バーストのたびにまれに鳴る）なので上限は最小限。
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
                if (clip == null) return; // 未読込/失敗時は無音（戦闘処理は継続）

                var go = new GameObject("CSWarfrontSound_" + clipName);
                go.transform.position = position;
                var src = go.AddComponent<AudioSource>();
                src.clip = clip;
                src.volume = Mathf.Clamp01(WarfrontSettings.SoundVolume / 100f);
                src.spatialBlend = 1f; // 完全3D
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

        /// <summary>期限切れ（再生完了済み）のスロットを取り除く。Update系のフックを別途持たず、
        /// 次にこの種別の音を鳴らそうとしたタイミングで遅延評価する（安価・メインスレッド専用）。</summary>
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
