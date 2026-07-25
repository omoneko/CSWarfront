namespace CSWarfront.Game
{
    /// <summary>
    /// 電力タブから軍事基地を建設する際の「建設先勢力」の選択状態。
    /// 既知のCS落とし穴（設定クラス/ファイル名をアセンブリ名と同名にすると
    /// 「同じキー」例外→設定削除ループになる）を避けるため、永続化はせずメモリ内のみで保持する（MVP）。
    /// クラス名は意図的にアセンブリ名 "CSWarfront" と一致させていない。
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
    }
}
