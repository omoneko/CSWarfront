using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// 電力タブから軍事基地を建設する際の「建設先勢力」の選択状態、および部隊コマンド（Task48）の
    /// キー割り当て。
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
    }
}
