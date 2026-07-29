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

        /// <summary>MODディレクトリ直下、兵科別・軍事基地の既定モデル(*.obj/*.mtl)を配置する
        /// サブフォルダ名（Task57）。build.ps1がsrc\CSWarfront\Models\*.obj,*.mtlをここへ配置し、
        /// Game/Models/WarfrontModelProviderが実行時に読み込む。</summary>
        public const string ModelsFolderName = "Models";

        /// <summary>Task69: 既定(built-in)モデルのマルチマテリアル描画（WarfrontMeshBuilder.TryBuild）で
        /// 使う Standard シェーダのパラメータと、.mtl 側にそのスロットの色が無かった場合のフォールバック色。
        /// MissileDisaster.Game.ModConfig の同名定数と同じ値（実績のある値をそのまま踏襲）。</summary>
        public const float ObjMetallic = 0.6f;
        public const float ObjGlossiness = 0.5f;
        public static readonly Color ObjFallbackColor = new Color(0.25f, 0.25f, 0.25f, 1f);

        public static void Log(string msg) { Debug.Log(Tag + msg); }
        public static void LogError(string msg) { Debug.LogError(Tag + msg); }
    }
}
