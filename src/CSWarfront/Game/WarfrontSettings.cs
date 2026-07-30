using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// Options指定建物（BaseBuildingDesignation）を建てて軍事基地とする際の「建設先勢力」の選択状態、
    /// および部隊コマンド（Task48）のキー割り当て（Task82: 電力タブの複製プレハブ機構撤去に伴い
    /// コメント文言を現行方式に更新。設定の実体・挙動自体は変更なし）。
    /// 既知のCS落とし穴（設定クラス/ファイル名をアセンブリ名と同名にすると
    /// 「同じキー」例外→設定削除ループになる）を避けるため、永続化はせずメモリ内のみで保持する（MVP）。
    /// クラス名は意図的にアセンブリ名 "CSWarfront" と一致させていない。
    /// Task48のキー割り当ても同じ理由でSavedInt（GameSettings永続化）は使わず、既存のBuildFactionIdと
    /// 同じくメモリ内のみで保持する（セッションをまたいだ既定値へのリセットは許容、MVP）。
    /// </summary>
    public static class WarfrontSettings
    {
        public const int MaxFactions = 5;

        private static int _buildFactionId; // 0..MaxFactions-1, default 0 (Red)

        public static byte BuildFactionId { get { return (byte)_buildFactionId; } }

        public static void SetBuildFactionId(int id)
        {
            if (id < 0) id = 0;
            if (id > MaxFactions - 1) id = MaxFactions - 1;
            _buildFactionId = id;
        }

        /// <summary>MVPではRed/Blueのみが実在勢力。3〜5はUI表示上のプレースホルダ。</summary>
        public static string[] FactionNames
        {
            get { return new[] { "Red", "Blue", "Faction 3", "Faction 4", "Faction 5" }; }
        }

        // --- Task49: ユニット上の勢力アイコン（小さな球、Game/UnitVisuals）表示切り替え ---

        private static bool _showFactionIcons = true; // 既定ON

        public static bool ShowFactionIcons
        {
            get { return _showFactionIcons; }
            set { _showFactionIcons = value; }
        }

        // --- Task48: 部隊コマンドのキー割り当て ---

        /// <summary>ホットキー候補（テンキー中心、MissileDisaster.ModSettings.KeyOptionsと同じ考え方：
        /// バニラ操作と衝突しにくいテンキー/ファンクションキーのみを候補にする）。
        /// OnSettingsUIのドロップダウンはこの配列のインデックスで選択値を管理する。</summary>
        public static readonly KeyCode[] KeyOptions =
        {
            KeyCode.Keypad1, KeyCode.Keypad2, KeyCode.Keypad3, KeyCode.Keypad4, KeyCode.Keypad5,
            KeyCode.Keypad6, KeyCode.Keypad7, KeyCode.Keypad8, KeyCode.Keypad9, KeyCode.Keypad0,
            KeyCode.F5, KeyCode.F6, KeyCode.F7, KeyCode.F8, KeyCode.F9, KeyCode.F10, KeyCode.F11, KeyCode.F12,
        };

        private static KeyCode _freeAdvanceKey = KeyCode.Keypad1;
        private static KeyCode _holdKey = KeyCode.Keypad2;
        private static KeyCode _rallyKey = KeyCode.Keypad3;

        // --- Task76: 部隊選択モードの有効/無効を切り替えるホットキー ---

        private static KeyCode _selectionModeKey = KeyCode.Keypad0;

        /// <summary>押すたびに部隊選択モード（ボックスドラッグでの範囲選択）のON/OFFをトグルする。
        /// ONの間だけドラッグによる範囲選択が働く。単発クリックでの選択（Game/UI/UnitSelection）は
        /// このモードの状態に関わらず常時動作する。既定 Numpad 0。実際のトグル処理は
        /// Game/UI/UnitBoxSelection が持つ（KeyOptionsの一覧から選ぶだけの他のコマンドキーと同じ
        /// パターン、Game/Mod.csのOnSettingsUI参照）。</summary>
        public static KeyCode SelectionModeKey
        {
            get { return _selectionModeKey; }
            set { _selectionModeKey = value; }
        }

        /// <summary>自由進撃（選択部隊を各自の最高速度で最寄りの敵拠点へ進撃させる）。既定 Numpad 1。</summary>
        public static KeyCode FreeAdvanceKey
        {
            get { return _freeAdvanceKey; }
            set { _freeAdvanceKey = value; }
        }

        /// <summary>停止（選択部隊をその場で停止させる。射程内の敵には引き続き応戦する）。既定 Numpad 2。</summary>
        public static KeyCode HoldKey
        {
            get { return _holdKey; }
            set { _holdKey = value; }
        }

        /// <summary>集結待機（右クリックで指定した地点へ選択部隊を移動させ、到着後は停止して受動防御に
        /// 徹する）を起動するキー。押すと「次の右クリックで地点を指定する」モードに入る。既定 Numpad 3。</summary>
        public static KeyCode RallyKey
        {
            get { return _rallyKey; }
            set { _rallyKey = value; }
        }

        // --- Task51: 兵科別射撃音・撃破音の音量設定 ---
        // 他の設定と同じくメモリ内保持のみ（クラス冒頭のコメント参照、セッションをまたいだ既定値への
        // リセットは許容、MVP）。

        private static int _soundVolume = 50; // 0..100、既定50%

        /// <summary>発砲音・撃破音の音量（0=無音〜100=最大）。WarfrontSoundPlayerが
        /// AudioSource.volume = SoundVolume / 100f として毎回参照する。</summary>
        public static int SoundVolume
        {
            get { return _soundVolume; }
            set { _soundVolume = value < 0 ? 0 : (value > 100 ? 100 : value); }
        }

        private static bool _soundMuted; // 既定OFF（鳴らす）

        /// <summary>ONの間はWarfrontSoundPlayerが一切音を再生しない（SoundVolumeの値に関わらず）。</summary>
        public static bool SoundMuted
        {
            get { return _soundMuted; }
            set { _soundMuted = value; }
        }
    }
}
