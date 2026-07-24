using UnityEngine;
namespace CSWarfront.Game
{
    /// <summary>デバッグログの薄ラッパ。常時ログは残さない（規約）。</summary>
    public static class ModConfig
    {
        public const string Tag = "[CSWarfront] ";
        public static void Log(string msg) { Debug.Log(Tag + msg); }
        public static void LogError(string msg) { Debug.LogError(Tag + msg); }
    }
}
