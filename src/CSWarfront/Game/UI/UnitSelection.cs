using System;
using ColossalFramework.UI;
using CSWarfront.Game;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// ユニットのクリック選択（Task31）。メインスレッド専用。マウス左クリックの立ち上がりフレーム
    /// （Input.GetMouseButtonDown(0)）のみraycastを行い、毎フレームのraycastは行わない（コスト最小化）。
    ///
    /// UI上のクリックは無視する: ColossalFramework.UI.UIInput.hoveredComponent
    /// （ColossalManaged.dll をリフレクションで確認済み。public static プロパティ、戻り値型
    /// ColossalFramework.UI.UIComponent。裏付けフィールドは private static UIComponent m_HoveredComponent）
    /// がnullでない＝カーソルが何らかのUIコンポーネント上にある、と判定しraycast自体をスキップする。
    /// これにより自パネルはもちろん、バニラの全UI（電力タブ、建物パネル等）の上のクリックも
    /// 3D世界へは一切透過しない。
    ///
    /// バニラ入力との共存: ヒットしたGameObjectが本MODのユニット表現（UnitVisuals.TryGetInstanceId
    /// 経由）であると判定できた場合にのみ選択状態を更新する。それ以外（建物・地形・道路・何もない場所
    /// へのクリック、あるいはraycast自体が何もヒットしない場合）は一切何もしない
    /// （選択解除もInput消費もしない）。Physics.Raycastは判定のみに使い、イベントを消費（Input無効化等）
    /// する操作は行っていないため、バニラの建物選択・ツール操作は完全にそのまま動作し続ける。
    /// </summary>
    public static class UnitSelection
    {
        // マップ全域をカバーするのに十分な距離（CSのマップは概ね数kmオーダー）。
        private const float MaxRaycastDistance = 10000f;

        public static uint SelectedInstanceId { get; private set; }

        public static void Clear()
        {
            SelectedInstanceId = 0;
        }

        /// <summary>毎メインスレッドフレーム呼ぶ。左クリックの立ち上がりフレームのみ処理する。</summary>
        public static void Update()
        {
            try
            {
                if (!Input.GetMouseButtonDown(0)) return;

                // カーソルがUI上にあるクリックは3D世界のraycastへ渡さない。
                if (UIInput.hoveredComponent != null) return;

                Camera cam = Camera.main;
                if (cam == null) return; // カメラ未準備（レベルロード中等）。次回クリックで再試行。

                Ray ray = cam.ScreenPointToRay(Input.mousePosition);
                RaycastHit hit;
                if (!Physics.Raycast(ray, out hit, MaxRaycastDistance)) return;

                GameObject hitGo = hit.collider != null ? hit.collider.gameObject : null;
                uint instanceId;
                if (UnitVisuals.TryGetInstanceId(hitGo, out instanceId))
                {
                    SelectedInstanceId = instanceId;
                }
                // ヒットが本MODユニットでなければ何もしない＝現在の選択を維持し、
                // バニラのクリック処理（建物選択等）へそのまま委ねる。
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitSelection.Update error: " + e);
            }
        }
    }
}
