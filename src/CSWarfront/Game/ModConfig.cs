using UnityEngine;
namespace CSWarfront.Game
{
    /// <summary>Thin wrapper over debug logging. No always-on logging is left in (per convention).</summary>
    public static class ModConfig
    {
        public const string Tag = "[CSWarfront] ";

        /// <summary>Name of the subfolder, directly under the mod directory, holding the firing/kill
        /// sounds (*.wav) (Task51). build.ps1 places src\CSWarfront\Sounds\*.wav here, and
        /// WarfrontSounds loads them at runtime.</summary>
        public const string SoundsFolderName = "Sounds";

        /// <summary>Name of the subfolder, directly under the mod directory, holding the default
        /// per-branch and military-base models (*.obj/*.mtl) (Task57). build.ps1 places
        /// src\CSWarfront\Models\*.obj,*.mtl here, and Game/Models/WarfrontModelProvider loads them at
        /// runtime.</summary>
        public const string ModelsFolderName = "Models";

        /// <summary>Task69: Standard-shader parameters used for multi-material rendering of the default
        /// (built-in) models (WarfrontMeshBuilder.TryBuild), plus the fallback color used when the .mtl
        /// has no color for that slot. Same values as the same-named constants in
        /// MissileDisaster.Game.ModConfig (proven values carried over as-is).</summary>
        public const float ObjMetallic = 0.6f;
        public const float ObjGlossiness = 0.5f;
        public static readonly Color ObjFallbackColor = new Color(0.25f, 0.25f, 0.25f, 1f);

        public static void Log(string msg) { Debug.Log(Tag + msg); }
        public static void LogError(string msg) { Debug.LogError(Tag + msg); }
    }
}
