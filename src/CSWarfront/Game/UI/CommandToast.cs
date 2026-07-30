using System;
using ColossalFramework.UI;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Task62（Mount&amp;Blade風の指示フィードバック 2/2）: 部隊コマンドが発行される/変化するたびに、
    /// 画面中央上部へ短時間だけ表示する簡易トースト。UnitCommandInput の各コマンド発行箇所
    /// （自由進撃/停止/集結待機の武装/集結地点確定/集結キャンセル）から Show(message) を呼ぶだけの
    /// 一方向API。表示内容の意味づけ（「×12」等の対象数を含めるか）は呼び出し側の責務。
    ///
    /// 生成/表示更新はUnitBoxSelectionの矩形パネル（EnsureCreated冪等生成＋毎フレームUpdate）と
    /// 同じ方式: UIView直下に1つだけUILabelを生成し使い回す。Show()が呼ばれるたびに文字列と
    /// 消去タイマー(Time.realtimeSinceStartup基準、ポーズ中でも進む)をリセットする。Update()は
    /// 毎フレーム呼ばれ、残り時間がFadeDurationSeconds未満になったら不透明度を線形に落とし、
    /// 0になったら非表示にする。
    ///
    /// メインスレッド専用（Unity/ColossalFramework UI API呼び出しのため）。WarfrontThreadingExtension.
    /// OnUpdate から、他のUI更新と同様に毎フレーム EnsureCreated()→Update() の順で呼ぶこと。
    /// </summary>
    public static class CommandToast
    {
        private const string LabelName = "CSWarfrontCommandToast";

        private const float DisplaySeconds = 2.5f;
        private const float FadeDurationSeconds = 0.5f; // 消える直前にこの秒数だけ不透明度を線形フェードする
        private const float TopOffset = 70f; // 画面上端からの距離（バニラの上部ツールバーと被らない程度）
        private const float LabelTextScale = 1.3f;

        private static UILabel _label;
        private static float _hideAtRealtime;
        private static bool _visible;

        /// <summary>冪等。ラベルをUIViewが準備できた時点で一度だけ生成する（他パネルと同じ方式）。</summary>
        public static void EnsureCreated()
        {
            try
            {
                if (!PanelChrome.IsGameReadyForUi()) return; // Task56: ロード/アンロード中はUIライブラリに触れない
                if (_label != null) return;
                UIView view = PanelChrome.GetCachedView();
                if (view == null) return;
                if (view.FindUIComponent<UILabel>(LabelName) != null) return;

                UILabel label = view.AddUIComponent(typeof(UILabel)) as UILabel;
                if (label == null)
                {
                    ModConfig.LogError("CommandToast.EnsureCreated: failed to create UILabel");
                    return;
                }
                label.name = LabelName;
                label.textScale = LabelTextScale;
                label.textColor = new Color32(255, 235, 180, 255);
                label.textAlignment = UIHorizontalAlignment.Center;
                label.autoSize = true;
                label.isInteractive = false; // クリック/ドラッグを横取りしない
                label.isVisible = false;
                label.opacity = 1f;
                _label = label;
            }
            catch (Exception e)
            {
                ModConfig.LogError("CommandToast.EnsureCreated error: " + e);
            }
        }

        /// <summary>コマンドイベントを表示する。既に表示中でも上書きし、消去タイマーをリセットする
        /// （連続してコマンドを出した場合は常に最新のメッセージを表示し続ける）。</summary>
        public static void Show(string message)
        {
            try
            {
                if (_label == null) return; // 未生成（ロード中等）。このイベントは静かに捨てる。
                _label.text = message ?? "";
                _label.opacity = 1f;
                CenterLabel();
                _label.Show();
                _label.BringToFront();
                _visible = true;
                _hideAtRealtime = Time.realtimeSinceStartup + DisplaySeconds;
            }
            catch (Exception e)
            {
                ModConfig.LogError("CommandToast.Show error: " + e);
            }
        }

        /// <summary>毎メインスレッドフレーム呼ぶ。フェード/非表示の時間経過管理のみを行う。</summary>
        public static void Update()
        {
            try
            {
                if (_label == null || !_visible) return;

                if (!PanelChrome.IsGameReadyForUi() || PanelChrome.IsGameMenuOpen())
                {
                    // Task62: ロード中・Escメニュー表示中は一時的に隠す（トグル状態は保持しない、
                    // 単に見た目を消すだけ。閉じた後にまだ猶予が残っていれば自動的に再度見えるようにはせず、
                    // 単純化のためここで表示自体を終了する）。
                    HideNow();
                    return;
                }

                float remaining = _hideAtRealtime - Time.realtimeSinceStartup;
                if (remaining <= 0f)
                {
                    HideNow();
                    return;
                }

                _label.opacity = remaining < FadeDurationSeconds ? Mathf.Clamp01(remaining / FadeDurationSeconds) : 1f;
            }
            catch (Exception e)
            {
                ModConfig.LogError("CommandToast.Update error: " + e);
            }
        }

        /// <summary>レベルアンロード時（MilitaryManager.Reset経由）に呼ぶ。ラベルを破棄し静的状態を残さない。</summary>
        public static void Destroy()
        {
            try
            {
                if (_label != null) UnityEngine.Object.Destroy(_label.gameObject);
            }
            catch (Exception e)
            {
                ModConfig.LogError("CommandToast.Destroy error: " + e);
            }
            finally
            {
                _label = null;
                _visible = false;
            }
        }

        private static void HideNow()
        {
            if (_label != null && _label.isVisible) _label.Hide();
            _visible = false;
        }

        private static void CenterLabel()
        {
            UIView view = PanelChrome.GetCachedView();
            if (view == null || _label == null) return;
            Vector2 res = view.GetScreenResolution();
            float x = (res.x - _label.width) * 0.5f;
            _label.relativePosition = new Vector3(x, TopOffset);
        }
    }
}
