using ColossalFramework.UI;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// BaseInfoPanel のうち、バニラパネルへの自動追従位置決めと、Task40で追加した
    /// 「タイトル行ドラッグ開始 → 自動追従の切り離し」だけを分離した partial class
    /// （BaseInfoPanel.cs 側の500行制限のため。BaseInfoPanelProduction/BaseInfoPanelModelButton と
    /// 同じ方針）。全メソッドはメインスレッド専用（Unity UI API呼び出しのため）。
    /// </summary>
    internal static partial class BaseInfoPanel
    {
        /// <summary>Task40: タイトル行(ドラッグハンドル)への最初のマウスダウンで「切り離し」モードに入る。
        /// 以降はPositionNextToVanillaによる自動追従を止め、UIDragHandle自身がパネルを自由に動かせる
        /// ようにする（毎フレームの位置上書きとドラッグ操作の競合を避けるため）。Hide()（バニラパネルが
        /// 閉じる等の「閉じる」相当）で false に戻る。</summary>
        private static void OnTitleBarMouseDown(UIComponent component, UIMouseEventParameter eventParam)
        {
            _detachedFromVanilla = true;
        }

        /// <summary>バニラパネルの絶対位置の右（画面外なら左）に自前パネルを追従させる。画面下端もクランプする。</summary>
        private static void PositionNextToVanilla(CityServiceWorldInfoPanel vanilla)
        {
            UIComponent vc = vanilla.component;
            UIView view = UIView.GetAView();
            if (vc == null || _panel == null || view == null) return;

            Vector3 abs = vc.absolutePosition;
            float x = abs.x + vc.width + VanillaGap;
            float y = abs.y;

            Vector2 res = view.GetScreenResolution();
            if (x + _panel.width > res.x) x = Mathf.Max(0f, abs.x - _panel.width - VanillaGap);
            if (y + _panel.height > res.y) y = Mathf.Max(0f, res.y - _panel.height);
            if (y < 0f) y = 0f;

            _panel.relativePosition = new Vector3(x, y);
        }
    }
}
