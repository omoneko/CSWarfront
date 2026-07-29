using UnityEngine;
namespace CSWarfront.Game
{
    /// <summary>デバッグログの薄ラッパ。常時ログは残さない（規約）。</summary>
    public static class ModConfig
    {
        public const string Tag = "[CSWarfront] ";

        /// <summary>MODディレクトリ直下、発砲音・撃破音(*.wav)を配置するサブフォルダ名（Task51）。
        /// build.ps1がsrc\CSWarfront\Sounds\*.wavをここへ配置し、WarfrontSoundsが実行時に読み込む。</summary>
        public const string SoundsFolderName = "Sounds";

        public static void Log(string msg) { Debug.Log(Tag + msg); }
        public static void LogError(string msg) { Debug.LogError(Tag + msg); }
    }
}
