using System.Collections;
using System.IO;
using UnityEngine;

namespace CSWarfront.Game.Audio
{
    /// <summary>
    /// Sounds/*.wav を WWW で読み込んで WarfrontSounds に登録するコルーチンホスト（メインスレッド、Task51）。
    /// MissileDisaster.Game.Audio.SoundLoaderBehaviour と同じ実績パターン。DontDestroyOnLoad の隠し
    /// GameObject に付与され、1回だけ全ファイルを読み込む。WAVを読むのはCS(Unity 5.6)がランタイムMP3
    /// デコードに非対応なため（WarfrontSounds冒頭のコメント参照）。
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
                    ModConfig.LogError("WarfrontSoundLoader: ファイルなし " + path);
                    continue;
                }

                string url = "file:///" + path.Replace("\\", "/");
                WWW www = new WWW(url);
                yield return www;

                if (!string.IsNullOrEmpty(www.error))
                {
                    ModConfig.LogError("WarfrontSoundLoader: 読込失敗 " + name + " : " + www.error);
                    continue;
                }

                AudioClip clip = null;
                try { clip = www.GetAudioClip(false, false, AudioType.WAV); }
                catch (System.Exception e) { ModConfig.LogError("WarfrontSoundLoader: デコード失敗 " + name + " : " + e); }
                if (clip == null)
                {
                    ModConfig.LogError("WarfrontSoundLoader: GetAudioClip が null " + name);
                    continue;
                }

                // 非同期デコードの完了を待つ（最大5秒）。
                float t = 0f;
                while (clip.loadState == AudioDataLoadState.Loading && t < 5f)
                {
                    t += Time.unscaledDeltaTime;
                    yield return null;
                }
                if (clip.loadState == AudioDataLoadState.Failed)
                {
                    ModConfig.LogError("WarfrontSoundLoader: ロード状態=Failed " + name);
                    continue;
                }

                clip.name = name;
                WarfrontSounds.Register(name, clip);
                ModConfig.Log("WarfrontSoundLoader: 読込完了 " + name + " (" + clip.length.ToString("0.0") + "s)");
            }
        }
    }
}
