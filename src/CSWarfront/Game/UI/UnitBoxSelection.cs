using System;
using System.Collections.Generic;
using ColossalFramework.UI;
using CSWarfront.Game;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// ユニットの範囲選択（Task48）。既存の単発クリック選択（Game/UI/UnitSelection.Update、
    /// GetMouseButtonDownの立ち上がりフレームで即raycastし、命中すればSelectedInstanceIdへ反映する）は
    /// 一切変更しない。本クラスは「そのクリックが実はドラッグの始まりだった」場合にのみ、mouse upの
    /// 時点で選択を範囲選択の結果へ上書きする。こうすることで
    /// 「ドラッグにならない普通のクリックは既存の単発選択挙動のまま」という要件を、既存のraycast
    /// ロジックを一切複製・変更せずに満たす（UnitSelection.Update自体は従来通り毎フレーム呼ばれ続ける）。
    ///
    /// ドラッグの成立条件: 左ボタンが押し下げられた瞬間にカーソルがUI上（UIInput.hoveredComponent!=null）
    /// またはバニラのEscメニューが開いていれば、そのドラッグ全体を無視する（矩形も出さないし選択も
    /// 変更しない）。押し下げ位置からDragThresholdPixelsを超えて動いた時点で初めて「ドラッグ確定」とみなし
    /// 矩形の描画を開始する。それに満たないまま離された場合は「ただのクリック」としてUnitSelection.Update
    /// の結果をそのまま残す。
    ///
    /// 選択結果の反映先: SelectedIds（範囲選択した全ID、コマンド入力 Game/UI/UnitCommandInput が使う）と
    /// UnitSelection.SelectedInstanceId（先頭の1件、既存のユニット情報パネルが単一ユニット向けに参照する。
    /// 0件なら0＝未選択）の両方。
    ///
    /// 矩形の当たり判定はスクリーン座標系（Camera.WorldToScreenPoint と Input.mousePosition、どちらも
    /// 左下原点・実ピクセル）で完結させ、ColossalFramework の UIView 仮想GUI解像度（UIスケール設定次第で
    /// 実ピクセルと一致しない）を一切経由しない。これにより選択判定自体はUIスケール設定に左右されない。
    /// 矩形の「見た目」（下記UpdateRectVisual）だけは、既存の描画APIを再利用するためUIView.ScreenPointToGUI
    /// を経由しており、そちらはUIスケールが既定(100%)以外だと見た目の位置に多少のズレが出ることがある
    /// （選択の正しさには影響しない、見た目のみの既知の制約）。
    ///
    /// 選択ユニットのハイライト: 各選択ユニットの位置へ毎フレーム追従する薄い円柱プリミティブ
    /// （コライダーは除去し、Physics.Raycastによるクリック判定を一切邪魔しない）。安価な視覚的合図として
    /// 採用した。共有マテリアルを1つだけ生成して全ハイライトで使い回す（ユニット本体のマテリアル/勢力色は
    /// 一切変更しない）。
    ///
    /// メインスレッド専用（Unity/ColossalFramework UI API呼び出しのため）。WarfrontThreadingExtension.OnUpdate
    /// から、位置同期（MilitaryManager.OnMainVisualUpdate）より後・UnitInfoPanelより前に呼ぶこと。
    /// </summary>
    public static class UnitBoxSelection
    {
        private const string RectPanelName = "CSWarfrontBoxSelectRect";

        /// <summary>この距離（実スクリーンピクセル）を超えて動いて初めて「ドラッグ」とみなす。
        /// 手ぶれ程度の移動を伴う「ただのクリック」を誤ってドラッグ扱いしないための遊び。
        /// Task62: 実機ログで「selected 0 unit(s) via drag」が連発していた根本原因がこの値。
        /// 旧値(6px)は高DPI環境やマウスセンサーのジッタで通常のクリックですら容易に超えてしまい、
        /// 普通の単発クリックが誤ってドラッグ判定され、範囲内に何も無ければ選択を巻き添えで
        /// 消していた（後述のFinishBoxSelectの「空振りドラッグでは選択を消さない」ルールと合わせて
        /// 二重に対策する）。8px以上を推奨値として10pxへ引き上げる。</summary>
        private const float DragThresholdPixels = 10f;

        private const float MaxCameraDistanceCheck = 100000f; // WorldToScreenPointのz>0判定にのみ使用（距離クランプ無し）

        // ハイライト（選択マーカー）の見た目定数。UnitVisuals.AttachVisibilityMarkerと同系統だが
        // 独立した薄い円柱にすることでユニット本体のマーカー/メッシュと視覚的に区別する。
        private const float HighlightRadius = 5f;
        private const float HighlightThinHeight = 0.15f;
        private const float HighlightYOffset = 0.3f;

        public static readonly List<uint> SelectedIds = new List<uint>();

        private static UIPanel _rectPanel;

        private static bool _pendingDragCandidate; // mouse down がUI外で起きた＝ドラッグに発展しうる
        private static bool _dragging;              // DragThresholdPixelsを超えて確定した
        private static Vector2 _dragStartScreen;

        /// <summary>直前フレーム終了時点の UnitSelection.SelectedInstanceId（Task48）。mouse down 時点で
        /// これと異なっていれば「この押し下げでUnitSelection.Update（同フレーム内で先に実行済み）が
        /// 新たにユニットへ命中した」と判定できる。単発クリックでもSelectedIdsを追従させるために使う
        /// （下のUpdate冒頭のコメント参照）。</summary>
        private static uint _lastSeenSelectedInstanceId;

        private static readonly List<uint> _idBuffer = new List<uint>();
        private static readonly List<Vector3> _posBuffer = new List<Vector3>();
        private static readonly List<uint> _foundBuffer = new List<uint>(); // Task62: FinishBoxSelectの結果を確定前に貯めておくワーク領域（GC回避）

        private static readonly Dictionary<uint, GameObject> _highlightMarkers = new Dictionary<uint, GameObject>();
        private static readonly List<uint> _staleHighlightIds = new List<uint>();
        private static Material _highlightMaterial;

        /// <summary>冪等。矩形パネルをUIViewが準備できた時点で一度だけ生成する（他パネルと同じ方式）。</summary>
        public static void EnsureCreated()
        {
            try
            {
                if (!PanelChrome.IsGameReadyForUi()) return; // Task56: ロード/アンロード中はUIライブラリに触れない
                if (_rectPanel != null) return;
                UIView view = PanelChrome.GetCachedView();
                if (view == null) return;
                if (view.FindUIComponent<UIPanel>(RectPanelName) != null) return;

                UIPanel panel = view.AddUIComponent(typeof(UIPanel)) as UIPanel;
                if (panel == null)
                {
                    ModConfig.LogError("UnitBoxSelection.EnsureCreated: UIPanel の生成に失敗");
                    return;
                }
                panel.name = RectPanelName;
                // "EmptySprite": バニラUIアトラスに含まれる単色1x1スプライト。colorで着色し半透明矩形として使う
                // （CS modding で単色矩形オーバーレイに広く使われる定番の組み合わせ）。万一このスプライト名が
                // 環境によって存在しなくても、ColossalFramework は単に何も描画しないだけで例外にはならない
                // ため、矩形が見えないだけで選択ロジック（スクリーン座標の当たり判定）自体は影響を受けない。
                panel.backgroundSprite = "EmptySprite";
                panel.color = new Color32(120, 170, 255, 90);
                panel.isInteractive = false; // クリック/ドラッグを横取りしない
                panel.isVisible = false;
                _rectPanel = panel;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitBoxSelection.EnsureCreated error: " + e);
            }
        }

        /// <summary>毎メインスレッドフレーム呼ぶ。UnitSelection.Update と同じフレームで、その後に呼ぶこと
        /// （呼び出し順序はWarfrontThreadingExtension.OnUpdateが保証する）。</summary>
        public static void Update()
        {
            try
            {
                if (!PanelChrome.IsGameReadyForUi())
                {
                    // Task56: ロード/アンロード中はUIライブラリに触れない。進行中のドラッグ候補も破棄する。
                    CancelDrag();
                    return;
                }

                if (PanelChrome.IsGameMenuOpen())
                {
                    CancelDrag();
                    _lastSeenSelectedInstanceId = UnitSelection.SelectedInstanceId;
                    return;
                }

                if (Input.GetMouseButtonDown(0))
                {
                    // UI上で始まった押し下げはドラッグ候補にしない（UnitSelection.Updateと同じガード）。
                    _pendingDragCandidate = UIInput.hoveredComponent == null;
                    _dragging = false;
                    _dragStartScreen = Input.mousePosition;

                    if (_pendingDragCandidate)
                    {
                        // UnitSelection.Update は同フレーム内でこれより先に実行済み。今回の押し下げが
                        // 新たにユニットへ命中していれば（前フレーム終了時点の値と異なっていれば）、
                        // 最終的にドラッグへ発展しなかった場合でもSelectedIdsを追従させる（単発クリックでも
                        // SelectedIds/SelectedInstanceId の整合を保つため）。命中しなかった/前回と同じ場合は
                        // 何もしない＝UnitSelection本来の「空振りは現在の選択を維持する」契約をそのまま守る。
                        uint clicked = UnitSelection.SelectedInstanceId;
                        if (clicked != 0 && clicked != _lastSeenSelectedInstanceId)
                        {
                            SelectedIds.Clear();
                            SelectedIds.Add(clicked);
                        }
                    }
                }
                else if (_pendingDragCandidate && Input.GetMouseButton(0))
                {
                    Vector2 cur = Input.mousePosition;
                    if (!_dragging && Vector2.Distance(cur, _dragStartScreen) >= DragThresholdPixels)
                    {
                        _dragging = true;
                    }
                    if (_dragging) UpdateRectVisual(_dragStartScreen, cur);
                }
                else if (Input.GetMouseButtonUp(0))
                {
                    if (_pendingDragCandidate && _dragging)
                    {
                        FinishBoxSelect(_dragStartScreen, Input.mousePosition);
                    }
                    // ドラッグが確定しなかった（ただのクリック）場合はここで何もしない＝
                    // 上のmouse down分岐で既に反映済みの単発選択（またはUnitSelectionの空振り時の
                    // 「現在の選択を維持」）をそのまま残す。
                    CancelDrag();
                }

                SyncHighlights();
                _lastSeenSelectedInstanceId = UnitSelection.SelectedInstanceId;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitBoxSelection.Update error: " + e);
                CancelDrag();
            }
        }

        /// <summary>レベルアンロード時（MilitaryManager.Reset経由）に呼ぶ。パネル/マーカーを破棄し静的状態を残さない。</summary>
        public static void Destroy()
        {
            try
            {
                if (_rectPanel != null) UnityEngine.Object.Destroy(_rectPanel.gameObject);
                foreach (var kv in _highlightMarkers)
                {
                    if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitBoxSelection.Destroy error: " + e);
            }
            finally
            {
                _rectPanel = null;
                _highlightMarkers.Clear();
                SelectedIds.Clear();
                _pendingDragCandidate = false;
                _dragging = false;
                _lastSeenSelectedInstanceId = 0;
            }
        }

        private static void CancelDrag()
        {
            _pendingDragCandidate = false;
            _dragging = false;
            if (_rectPanel != null && _rectPanel.isVisible) _rectPanel.Hide();
        }

        private static void UpdateRectVisual(Vector2 startScreen, Vector2 curScreen)
        {
            if (_rectPanel == null) return;
            UIView view = PanelChrome.GetCachedView(); // Task56: 毎フレーム呼ばれるためキャッシュ済みアクセサを使う
            if (view == null) return;

            Vector2 a = view.ScreenPointToGUI(startScreen);
            Vector2 b = view.ScreenPointToGUI(curScreen);

            float x = Mathf.Min(a.x, b.x);
            float y = Mathf.Min(a.y, b.y);
            float w = Mathf.Abs(a.x - b.x);
            float h = Mathf.Abs(a.y - b.y);

            _rectPanel.relativePosition = new Vector3(x, y);
            _rectPanel.width = Mathf.Max(1f, w);
            _rectPanel.height = Mathf.Max(1f, h);
            if (!_rectPanel.isVisible) _rectPanel.Show();
            _rectPanel.BringToFront();
        }

        /// <summary>ドラッグ終了時に一度だけ呼ばれる。スクリーン矩形内に投影されるユニットをSelectedIdsへ
        /// 反映し、UnitSelection.SelectedInstanceId も先頭のIDで上書きする。
        ///
        /// Task62: 決定事項 — 矩形内に1体も見つからない「空振りドラッグ」は、既存の選択を
        /// サイレントに消さない（何もせず直前の選択をそのまま残す）。理由: DragThresholdPixels引き上げ後も
        /// 手ぶれ等で意図せずドラッグが確定することはあり得るうえ、コマンド待機中（UnitCommandInput.
        /// IsAwaitingRallyClick等）に選択が空振りドラッグで消えると、直後に出すコマンドが対象0件で
        /// 空振りする実害が出る。矩形が実際に1体以上を捉えた場合のみSelectedIdsを置き換える
        /// （範囲を変えて選び直す通常のドラッグ操作はこれまで通り機能する）。選択を明示的に0件へ戻す
        /// UI操作は本タスクの対象外（既存にも無い）。</summary>
        private static void FinishBoxSelect(Vector2 startScreen, Vector2 endScreen)
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            float minX = Mathf.Min(startScreen.x, endScreen.x);
            float maxX = Mathf.Max(startScreen.x, endScreen.x);
            float minY = Mathf.Min(startScreen.y, endScreen.y);
            float maxY = Mathf.Max(startScreen.y, endScreen.y);

            UnitVisuals.CollectVisible(_idBuffer, _posBuffer);

            _foundBuffer.Clear();
            for (int i = 0; i < _idBuffer.Count; i++)
            {
                Vector3 sp = cam.WorldToScreenPoint(_posBuffer[i]);
                if (sp.z <= 0f || sp.z > MaxCameraDistanceCheck) continue; // カメラ後方は対象外
                if (sp.x < minX || sp.x > maxX || sp.y < minY || sp.y > maxY) continue;
                _foundBuffer.Add(_idBuffer[i]);
            }

            if (_foundBuffer.Count == 0) return; // Task62: 空振りドラッグは既存の選択を維持する（上記コメント参照）。

            SelectedIds.Clear();
            SelectedIds.AddRange(_foundBuffer);
            UnitSelection.Set(SelectedIds[0]);
            ModConfig.Log("UnitBoxSelection: selected " + SelectedIds.Count + " unit(s) via drag"); // Task62: 0件はもう発生しないためログはcount>0のみ
        }

        /// <summary>選択中ユニットへ毎フレーム追従する簡易ハイライト（薄い円柱、コライダー無し）を
        /// 宣言的に同期する（UnitVisuals.Syncと同じreconcileパターン）。</summary>
        private static void SyncHighlights()
        {
            _staleHighlightIds.Clear();
            foreach (var kv in _highlightMarkers)
            {
                if (!SelectedIds.Contains(kv.Key)) _staleHighlightIds.Add(kv.Key);
            }
            for (int i = 0; i < _staleHighlightIds.Count; i++)
            {
                GameObject stale;
                if (_highlightMarkers.TryGetValue(_staleHighlightIds[i], out stale) && stale != null)
                    UnityEngine.Object.Destroy(stale);
                _highlightMarkers.Remove(_staleHighlightIds[i]);
            }

            for (int i = 0; i < SelectedIds.Count; i++)
            {
                uint id = SelectedIds[i];
                Vector3 pos;
                if (!UnitVisuals.TryGetPosition(id, out pos)) continue; // 見た目未生成/破棄済み。次フレーム再試行。

                GameObject marker;
                if (!_highlightMarkers.TryGetValue(id, out marker) || marker == null)
                {
                    marker = CreateHighlightMarker();
                    if (marker == null) continue;
                    _highlightMarkers[id] = marker;
                }
                marker.transform.position = new Vector3(pos.x, pos.y + HighlightYOffset, pos.z);
            }
        }

        private static GameObject CreateHighlightMarker()
        {
            try
            {
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                Collider col = go.GetComponent<Collider>();
                if (col != null) UnityEngine.Object.Destroy(col); // クリック判定を邪魔しない
                go.transform.localScale = new Vector3(HighlightRadius, HighlightThinHeight, HighlightRadius);
                MeshRenderer renderer = go.GetComponent<MeshRenderer>();
                if (renderer != null) renderer.sharedMaterial = GetHighlightMaterial();
                return go;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitBoxSelection.CreateHighlightMarker error: " + e);
                return null;
            }
        }

        private static Material GetHighlightMaterial()
        {
            if (_highlightMaterial == null)
            {
                Shader shader = Shader.Find("Standard");
                if (shader == null) shader = Shader.Find("Diffuse");
                _highlightMaterial = new Material(shader);
                _highlightMaterial.color = new Color(0.35f, 1f, 0.4f, 1f);
            }
            return _highlightMaterial;
        }
    }
}
