using System;
using ColossalFramework.UI;
using CSWarfront.Core;
using CSWarfront.Game;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// 部隊コマンドのホットキー入力（Task48）。WarfrontSettings.FreeAdvanceKey/HoldKey/RallyKey を
    /// 毎フレームポーリングし、Game/UI/UnitBoxSelection.SelectedIds を対象に MilitaryManager の
    /// コマンドラッパー（Game/MilitaryManagerUnitCommands.cs）を呼ぶ。
    ///
    /// RallyKeyは即座に命令を出さず、「次の右クリックで集結地点を指定する」ターゲティングモードへ入る
    /// （プレイヤーがまず地点を選ぶ必要があるため）。ターゲティング中はEscでキャンセルできる。
    /// 地点のワールド座標は UnitSelection.Update / Game/UI/UnitSelection と同じ
    /// Camera.main.ScreenPointToRay + Physics.Raycast を使う（CSの地形/建物/道路コライダーは
    /// このMOD内の既存のraycast経路で既に反応することを確認済み、Game/UI/UnitSelection.cs参照）。
    /// TerrainManager等CS固有APIは使わず、ヒットした物体の種類を問わず hit.point をそのまま採用する
    /// （建物の屋根等にヒットした場合は屋根の高さになるが、集結地点としては十分実用的なためMVPとして許容）。
    ///
    /// ホットキー/右クリックのどちらも、バニラのEscメニューが開いている間・何らかのテキスト入力欄に
    /// フォーカスがある間は完全に無視する（ColossalFramework.UI.UIView.HasInputFocus()、
    /// ColossalManaged.dll をリフレクションで確認済みの public static bool メソッド）。
    ///
    /// メインスレッド専用。WarfrontThreadingExtension.OnUpdate から、UnitBoxSelection.Update の後に呼ぶこと
    /// （同じフレームで確定した選択を対象にコマンドを出せるようにするため）。
    /// </summary>
    public static class UnitCommandInput
    {
        private const float MaxRaycastDistance = 10000f; // Game/UI/UnitSelectionと同じ値

        private static bool _awaitingRallyClick;

        /// <summary>集結地点のターゲティング中か（Game/UI/UnitInfoPanel等がヒント表示に使ってよい、Task48時点は未使用）。</summary>
        public static bool IsAwaitingRallyClick { get { return _awaitingRallyClick; } }

        public static void Update()
        {
            try
            {
                if (PanelChrome.IsGameMenuOpen())
                {
                    _awaitingRallyClick = false; // メニューが開いたらターゲティングは打ち切る
                    return;
                }
                if (UIView.HasInputFocus()) return; // テキスト入力欄にフォーカスがある間はホットキーを一切拾わない

                if (_awaitingRallyClick)
                {
                    HandleRallyTargeting();
                    return; // ターゲティング中は他のホットキーを無視（誤操作防止）
                }

                if (Input.GetKeyDown(WarfrontSettings.FreeAdvanceKey))
                {
                    IssueFreeAdvance();
                }
                else if (Input.GetKeyDown(WarfrontSettings.HoldKey))
                {
                    IssueHold();
                }
                else if (Input.GetKeyDown(WarfrontSettings.RallyKey))
                {
                    BeginRallyTargeting();
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitCommandInput.Update error: " + e);
                _awaitingRallyClick = false;
            }
        }

        /// <summary>レベルアンロード時（MilitaryManager.Reset経由）に呼ぶ。ターゲティング状態を残さない。</summary>
        public static void Reset()
        {
            _awaitingRallyClick = false;
        }

        private static void IssueFreeAdvance()
        {
            if (UnitBoxSelection.SelectedIds.Count == 0) return;
            MilitaryManager.CommandFreeAdvance(UnitBoxSelection.SelectedIds);
        }

        private static void IssueHold()
        {
            if (UnitBoxSelection.SelectedIds.Count == 0) return;
            MilitaryManager.CommandHold(UnitBoxSelection.SelectedIds);
        }

        private static void BeginRallyTargeting()
        {
            if (UnitBoxSelection.SelectedIds.Count == 0) return;
            _awaitingRallyClick = true;
            ModConfig.Log("UnitCommandInput: rally targeting armed for " + UnitBoxSelection.SelectedIds.Count +
                " unit(s) - right-click a destination (Esc cancels)");
        }

        private static void HandleRallyTargeting()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                _awaitingRallyClick = false;
                ModConfig.Log("UnitCommandInput: rally targeting cancelled");
                return;
            }

            if (!Input.GetMouseButtonDown(1)) return; // 右クリック待ち。それまでターゲティング状態を維持する。
            if (UIInput.hoveredComponent != null) return; // UI上の右クリックは無視、ターゲティングは継続

            Camera cam = Camera.main;
            if (cam == null) return; // カメラ未準備。次回のクリックで再試行。

            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            RaycastHit hit;
            if (!Physics.Raycast(ray, out hit, MaxRaycastDistance)) return; // 何もヒットしなければターゲティング継続

            WorldPos point = new WorldPos(hit.point.x, hit.point.y, hit.point.z);
            MilitaryManager.CommandRally(UnitBoxSelection.SelectedIds, point);
            _awaitingRallyClick = false;
        }
    }
}
