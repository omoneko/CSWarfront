using System.Collections.Generic;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game.Audio
{
    /// <summary>
    /// Sounds/*.wav を実行時に読み込んで AudioClip をキャッシュする（Task51、兵科別射撃音・撃破音）。
    /// ミサイル災害MOD(MissileDisaster.Game.Audio.SoundLibrary)と同じ実績パターン:
    /// Initialize(modPath) を WarfrontLoadingExtension.OnLevelLoaded から呼び、DontDestroyOnLoad の
    /// 隠しホスト GameObject 上の WarfrontSoundLoaderBehaviour がコルーチンで実読込を行う（1回だけ）。
    /// すべてメインスレッド。
    ///
    /// 注意: CS(Unity 5.6)はランタイムMP3デコード非対応（WWW.GetAudioClip(AudioType.MPEG)がnullを返す、
    /// MissileDisasterで実機確認済み）。そのためユーザーが用意したmp3原本はビルド時にWAVへ変換し、
    /// Sounds/*.wav として配置・読込する（build.ps1参照。mp3原本もsrc\CSWarfront\Sounds\に残しているが、
    /// デプロイ対象は*.wavのみ）。
    /// </summary>
    public static class WarfrontSounds
    {
        // Sounds フォルダに置く wav のベース名（拡張子なし）。
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

        private static readonly string[] RifleVariants = { Rifle1, Rifle2, Rifle3, Rifle4 };
        private static readonly string[] MgVariants = { Mg1, Mg2 };
        private static readonly string[] CannonVariants = { Cannon1, Cannon2, Cannon3 };

        public static readonly string[] FileNames =
        {
            Rifle1, Rifle2, Rifle3, Rifle4, Mg1, Mg2, Cannon1, Cannon2, Cannon3,
            AaMissile, Ricochet, VehicleDestroyed
        };

        private static bool _loadStarted;
        private static readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>();

        // Task51: 兵科別バリアントのローテーション用カウンタ。System.Randomは使わず、呼ばれるたびに
        // 0..length-1を順番に進めるだけ（安価・決定的）。あくまでGame層の演出状態であり、
        // Coreのシミュレーション決定性（乱数不使用の契約）には一切関与しない。
        private static int _rifleIndex, _mgIndex, _cannonIndex;

        /// <summary>
        /// WarfrontLoadingExtension.OnLevelLoaded から呼ぶ。DontDestroyOnLoad の常駐ホストを作り、
        /// Sounds/*.wav の読込を即開始する（多重起動しない）。メインスレッドから。
        /// </summary>
        public static void Initialize(string modDir)
        {
            if (_loadStarted) return;
            if (string.IsNullOrEmpty(modDir))
            {
                ModConfig.LogError("WarfrontSounds.Initialize: modDir が空");
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

        /// <summary>読込済みなら AudioClip を返す。未読込/失敗なら null。</summary>
        public static AudioClip Get(string name)
        {
            AudioClip c;
            return !string.IsNullOrEmpty(name) && _clips.TryGetValue(name, out c) ? c : null;
        }

        /// <summary>
        /// 兵科ごとの発砲音バリアントを決定的にローテーションして選ぶ（Task51）。
        /// Infantry/MechInfantry→銃撃音(4種)、Apc/DroneInfantry→重機関銃(2種)、Tank/Artillery→砲撃音(3種)、
        /// AntiAir→対空ミサイル(単一)。マッピング対象外の兵科（海空ユニット等、現状未実装）はnullを返し、
        /// 呼び出し側（CombatFx）は無音のまま処理を継続する。
        /// </summary>
        public static string ShotSoundFor(UnitCategory category)
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
                case UnitCategory.Destroyer: // Task88: 駆逐艦の艦砲/ミサイルにも砲撃音を当てる（従来は未マッピング＝無音）
                    return CannonVariants[NextIndex(ref _cannonIndex, CannonVariants.Length)];
                case UnitCategory.AntiAir:
                    return AaMissile;
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
