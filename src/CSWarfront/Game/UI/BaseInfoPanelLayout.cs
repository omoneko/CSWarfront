using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// BaseInfoPanel のうち、表示用ラベル整形とパネル高さの再計算だけを分離した partial class
    /// （BaseInfoPanel.cs 側の500行制限のため分離。Task34のBaseInfoPanelProduction.cs分離、
    /// Task63のBaseInfoPanelMissile.cs分離と同じ方針）。
    /// 全メソッドはメインスレッド専用（Unity UI API呼び出しのため）。
    /// </summary>
    internal static partial class BaseInfoPanel
    {
        /// <summary>Task61: BaseType→日本語ラベル（パネル表示用）。</summary>
        private static string BaseTypeLabel(BaseType type)
        {
            switch (type)
            {
                case BaseType.Army: return "Army Base";
                case BaseType.Navy: return "Naval Base";
                case BaseType.AirForce: return "Air Base";
                case BaseType.MissileBase: return "Missile Base";
                default: return type.ToString();
            }
        }

        /// <summary>Task61: DomainMask→日本語ラベル（パネル表示用、"陸上"/"海上"/"航空"をビットごとに列挙）。</summary>
        private static string SpawnableDomainsLabel(DomainMask mask)
        {
            var parts = new System.Collections.Generic.List<string>(3);
            if (DomainMaskUtil.Contains(mask, Domain.Land)) parts.Add("Land");
            if (DomainMaskUtil.Contains(mask, Domain.Sea)) parts.Add("Sea");
            if (DomainMaskUtil.Contains(mask, Domain.Air)) parts.Add("Air");
            return parts.Count > 0 ? string.Join("/", parts.ToArray()) : "None";
        }

        /// <summary>
        /// Task33: ステータスラベル（autoHeight有効）の実際の高さから展開時の全体パネル高さを算出し、
        /// _expandedHeight キャッシュを更新する。折りたたみ→再展開時にここで求めた最新値へ正確に戻せる
        /// よう、ハードコードされた定数ではなくこの計算を毎回の内容更新のたびにやり直す。
        /// 値が変化した場合のみ _panel.height / _expandedHeight を書き換える（毎フレームの無駄な
        /// レイアウト再計算・スレッド跨ぎではないが不要な再描画コストを避けるため）。
        /// </summary>
        private static void RecomputeExpandedHeight()
        {
            if (_statusLabel == null || _panel == null) return;

            // Task34: 生産セクションが構築済みなら、その最下端（_productionBottomY、
            // RefreshProductionSection/BuildProductionSectionが更新）を基準にする。
            // Task63: ミサイル基地専用セクション（_missileSectionBottomY）とは互いに排他表示なので、
            // 選択中の基地種別に応じて常に片方だけが非0になる（Math.Maxで単純に大きい方を採る）。
            // どちらも未構築（理論上は起きないが防御的に）ならステータスラベルの下端にフォールバックする。
            float sectionBottom = Mathf.Max(_productionBottomY, _missileSectionBottomY);
            float contentBottom = sectionBottom > 0f
                ? sectionBottom
                : _statusLabel.relativePosition.y + _statusLabel.height;
            // 内容の実寸に対して縦方向へ余裕を持たせる（ユーザー要望: 建物ウィンドウは「大きさだけ縦に1.5倍」）。
            // 文字サイズ・幅は変えず、パネルの高さのみ VerticalScale 倍にして窮屈さを解消する。
            float newExpandedHeight = (contentBottom + Pad) * VerticalScale;
            if (Mathf.Abs(newExpandedHeight - _expandedHeight) > 0.01f)
            {
                _expandedHeight = newExpandedHeight;
            }

            if (!_collapsed && Mathf.Abs(_panel.height - _expandedHeight) > 0.01f)
            {
                _panel.height = _expandedHeight;
            }
        }
    }
}
