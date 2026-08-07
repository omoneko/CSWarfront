using System.Collections;
using System.IO;
using UnityEngine;

namespace CSWarfront.Game.Audio
{
    /// <summary>
    /// Coroutine host that loads Sounds/*.wav via WWW and registers them in WarfrontSounds (main
    /// thread, Task51). Same proven pattern as MissileDisaster.Game.Audio.SoundLoaderBehaviour.
    /// Attached to a hidden DontDestroyOnLoad GameObject; loads all files exactly once. WAV is used
    /// because CS (Unity 5.6) does not support runtime MP3 decoding (see the comment at the top of
    /// WarfrontSounds).
    /// </summary>
    public class WarfrontSoundLoaderBehaviour : MonoBehaviour
    {
        private string _modDir;

        public void Begin(string modDir)
        {
            _modDir = modDir;
            StartCoroutine(LoadAll());
        }

        private IEnumerator LoadAll()
        {
            string folder = Path.Combine(_modDir, ModConfig.SoundsFolderName);
            for (int i = 0; i < WarfrontSounds.FileNames.Length; i++)
            {
                string name = WarfrontSounds.FileNames[i];
                string path = Path.Combine(folder, name + ".wav");
                if (!File.Exists(path))
                {
                    ModConfig.LogError("WarfrontSoundLoader: file not found " + path);
                    continue;
                }

                string url = "file:///" + path.Replace("\\", "/");
                WWW www = new WWW(url);
                yield return www;

                if (!string.IsNullOrEmpty(www.error))
                {
                    ModConfig.LogError("WarfrontSoundLoader: load failed " + name + " : " + www.error);
                    continue;
                }

                AudioClip clip = null;
                try { clip = www.GetAudioClip(false, false, AudioType.WAV); }
                catch (System.Exception e) { ModConfig.LogError("WarfrontSoundLoader: decode failed " + name + " : " + e); }
                if (clip == null)
                {
                    ModConfig.LogError("WarfrontSoundLoader: GetAudioClip returned null " + name);
                    continue;
                }

                // Wait for asynchronous decoding to finish (up to 5 seconds).
                float t = 0f;
                while (clip.loadState == AudioDataLoadState.Loading && t < 5f)
                {
                    t += Time.unscaledDeltaTime;
                    yield return null;
                }
                if (clip.loadState == AudioDataLoadState.Failed)
                {
                    ModConfig.LogError("WarfrontSoundLoader: load state=Failed " + name);
                    continue;
                }

                clip.name = name;
                WarfrontSounds.Register(name, clip);
                ModConfig.Log("WarfrontSoundLoader: loaded " + name + " (" + clip.length.ToString("0.0") + "s)");
            }
        }
    }
}
