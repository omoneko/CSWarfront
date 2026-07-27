using System;
using System.Collections.Generic;
using System.Text;
using ColossalFramework.UI;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// BaseInfoPanel のうち、プレイヤーによる手動生産（自動生産切替・発注・取消、Task34）に関わる
    /// 部分だけを分離した partial class。BaseInfoPanel.cs 側の500行制限のため分離した
    /// （Task30のBaseUiSnapshotBuilder分離、Task34のMilitaryManagerManualProduction.cs分離と同じ方針）。
    ///
    /// このクラス自身は _panel / _statusLabel / _currentBaseId / _collapsed 等の BaseInfoPanel.cs 側の
    /// private static フィールドへ直接アクセスする（partial class は private メンバーも全パーツで共有する
    /// ため問題ない）。呼び出しは必ず BaseInfoPanel.cs 側のBuild/Destroy/ApplyCollapsedState/RefreshContents
    /// から行われ、このクラス単体では状態を持ち回らない。全メソッドはメインスレッド専用（Unity UI API）。
    /// </summary>
    internal static partial class BaseInfoPanel
    {
        private const float ProductionRowGap = 6f;
        private const float ToggleButtonWidth = 150f;
        private const float ToggleButtonHeight = 22f;
        private const float SmallLabelHeight = 16f;
        private const float ProductionButtonHeight = 24f;

        /// <summary>キュー表示で先頭以降を何件まで並べるか。それ以上は「…」で省略する（Task34仕様）。</summary>
        private const int QueueDisplayMax = 3;

        /// <summary>「研究投資」ボタン1クリックあたりの投資額（Task35）。</summary>
        private const float ResearchInvestAmount = 50f;

        private static UIButton _autoProduceButton;
        private static UILabel _autoProduceHintLabel;
        private static UIDropDown _unitDropdown;
        private static UIButton _queueButton;
        private static UIButton _cancelButton;
        private static UIButton _investButton;
        private static UIButton _unlockButton;
        private static UILabel _productionMessageLabel;
        private static UILabel _queueLabel;

        /// <summary>直近のスナップショットのAutoProduce値。クリック時に「反転した値」を計算するために覚えておく
        /// （TryEnqueue等と違いUI側は現在値を保持していないと即座にトグルできないため）。</summary>
        private static bool _lastAutoProduce = true;

        /// <summary>直近のスナップショットの所属勢力ID（Task35）。研究投資/Tier解禁ボタンのクリック時に
        /// MilitaryManagerへ渡すfactionIdとして使う（BaseUiSnapshotはOwnerFactionIdだけでなく研究値も
        /// 持つが、ボタンのクリックハンドラはスナップショット全体を保持しないためここに覚えておく）。</summary>
        private static byte? _lastOwnerFactionId;

        /// <summary>直近のスナップショットの所属勢力UnlockedTier（Task35）。Tier解禁ボタンの失敗理由
        /// （研究点不足 か 既に最大Tier か）を判定するために覚えておく。</summary>
        private static byte _lastOwnerUnlockedTier = 1;

        /// <summary>生産セクションの最下端Y（RecomputeExpandedHeightが全体パネル高さの算出に使う）。</summary>
        private static float _productionBottomY;

        /// <summary>ドロップダウン表示用テキスト（例: "Tank_T3  (¥153)" / 未解禁なら
        /// "Tank_T4  (¥168) [未解禁]"、Task35）。所属勢力のUnlockedTierに依存するため、値が変わった
        /// ときだけ再構築する（_lastUnitDropdownUnlockedTierで判定）。</summary>
        private static string[] _unitDropdownItems;
        /// <summary>_unitDropdownItems と同じ並びのTypeKey（実際の発注に使う値）。</summary>
        private static string[] _unitDropdownTypeKeys;
        /// <summary>_unitDropdownItemsを最後に構築したときのUnlockedTier。0は「まだ構築していない」
        /// を表すセンチネル（有効なTierは1..5のため衝突しない、Task35）。</summary>
        private static byte _lastUnitDropdownUnlockedTier;

        private static readonly StringBuilder _queueBuilder = new StringBuilder(128);

        /// <summary>ステータスラベルの下に生産セクションの各コントロールを生成する（Build()から一度だけ呼ばれる）。
        /// ここでの relativePosition は仮値でよい（RefreshProductionSectionが毎フレーム正しい位置へ更新する。
        /// 初回表示前＝_panel.isVisible=falseの間にRefreshContentsが最低1回走るため実害はない）。</summary>
        private static void BuildProductionSection(float width)
        {
            if (_panel == null) return;

            EnsureUnitDropdownItemsBuilt(1); // 初期表示は未所属/UnlockedTier既定値(1)相当。実値は初回RefreshContentsで反映される。

            _autoProduceButton = _panel.AddUIComponent<UIButton>();
            _autoProduceButton.size = new Vector2(ToggleButtonWidth, ToggleButtonHeight);
            _autoProduceButton.textScale = 0.75f;
            _autoProduceButton.normalBgSprite = "ButtonMenu";
            _autoProduceButton.hoveredBgSprite = "ButtonMenuHovered";
            _autoProduceButton.pressedBgSprite = "ButtonMenuPressed";
            _autoProduceButton.text = "自動生産: ON";
            _autoProduceButton.relativePosition = new Vector3(Pad, 0f);
            _autoProduceButton.eventClick += OnAutoProduceClick;

            _autoProduceHintLabel = _panel.AddUIComponent<UILabel>();
            _autoProduceHintLabel.textScale = 0.65f;
            _autoProduceHintLabel.textColor = new Color32(180, 180, 180, 255);
            _autoProduceHintLabel.wordWrap = false;
            _autoProduceHintLabel.autoSize = false;
            _autoProduceHintLabel.width = width - ToggleButtonWidth - 6f;
            _autoProduceHintLabel.text = "";
            _autoProduceHintLabel.relativePosition = new Vector3(Pad + ToggleButtonWidth + 6f, 4f);

            _unitDropdown = BuildUnitDropdown(Pad, 0f, width);

            float halfWidth = (width - ProductionRowGap) / 2f;
            _queueButton = _panel.AddUIComponent<UIButton>();
            _queueButton.text = "生産";
            _queueButton.textScale = 0.8f;
            _queueButton.size = new Vector2(halfWidth, ProductionButtonHeight);
            _queueButton.normalBgSprite = "ButtonMenu";
            _queueButton.hoveredBgSprite = "ButtonMenuHovered";
            _queueButton.pressedBgSprite = "ButtonMenuPressed";
            _queueButton.relativePosition = new Vector3(Pad, 0f);
            _queueButton.eventClick += OnQueueClick;

            _cancelButton = _panel.AddUIComponent<UIButton>();
            _cancelButton.text = "取消";
            _cancelButton.textScale = 0.8f;
            _cancelButton.size = new Vector2(halfWidth, ProductionButtonHeight);
            _cancelButton.normalBgSprite = "ButtonMenu";
            _cancelButton.hoveredBgSprite = "ButtonMenuHovered";
            _cancelButton.pressedBgSprite = "ButtonMenuPressed";
            _cancelButton.relativePosition = new Vector3(Pad + halfWidth + ProductionRowGap, 0f);
            _cancelButton.eventClick += OnCancelClick;

            // Task35: 資金→研究点への投資、および研究点によるTier解禁。
            _investButton = _panel.AddUIComponent<UIButton>();
            _investButton.text = "研究投資 (¥" + ResearchInvestAmount.ToString("0") + ")";
            _investButton.textScale = 0.8f;
            _investButton.size = new Vector2(halfWidth, ProductionButtonHeight);
            _investButton.normalBgSprite = "ButtonMenu";
            _investButton.hoveredBgSprite = "ButtonMenuHovered";
            _investButton.pressedBgSprite = "ButtonMenuPressed";
            _investButton.relativePosition = new Vector3(Pad, 0f);
            _investButton.eventClick += OnInvestClick;

            _unlockButton = _panel.AddUIComponent<UIButton>();
            _unlockButton.text = "Tier解禁";
            _unlockButton.textScale = 0.8f;
            _unlockButton.size = new Vector2(halfWidth, ProductionButtonHeight);
            _unlockButton.normalBgSprite = "ButtonMenu";
            _unlockButton.hoveredBgSprite = "ButtonMenuHovered";
            _unlockButton.pressedBgSprite = "ButtonMenuPressed";
            _unlockButton.relativePosition = new Vector3(Pad + halfWidth + ProductionRowGap, 0f);
            _unlockButton.eventClick += OnUnlockClick;

            _productionMessageLabel = _panel.AddUIComponent<UILabel>();
            _productionMessageLabel.textScale = 0.7f;
            _productionMessageLabel.textColor = new Color32(230, 140, 140, 255);
            _productionMessageLabel.wordWrap = false;
            _productionMessageLabel.autoSize = false;
            _productionMessageLabel.width = width;
            _productionMessageLabel.text = "";
            _productionMessageLabel.relativePosition = new Vector3(Pad, 0f);

            _queueLabel = _panel.AddUIComponent<UILabel>();
            _queueLabel.textScale = 0.7f;
            _queueLabel.textColor = new Color32(200, 200, 200, 255);
            _queueLabel.wordWrap = false;
            _queueLabel.autoSize = false;
            _queueLabel.width = width;
            _queueLabel.text = "キュー: なし";
            _queueLabel.relativePosition = new Vector3(Pad, 0f);
        }

        private static UIDropDown BuildUnitDropdown(float x, float y, float width)
        {
            UIDropDown dd = _panel.AddUIComponent<UIDropDown>();
            dd.size = new Vector2(width, DropdownHeight);
            dd.relativePosition = new Vector3(x, y);
            dd.normalBgSprite = "ButtonMenu";
            dd.hoveredBgSprite = "ButtonMenuHovered";
            dd.disabledBgSprite = "ButtonMenuDisabled";
            dd.listBackground = "GenericPanelLight";
            dd.itemHeight = 22;
            dd.itemHover = "ListItemHover";
            dd.itemHighlight = "ListItemHighlight";
            dd.listWidth = (int)width;
            dd.listHeight = 200;
            dd.listPosition = UIDropDown.PopupListPosition.Below;
            dd.textScale = 0.75f;
            dd.textFieldPadding = new RectOffset(8, 8, 6, 0);
            dd.itemPadding = new RectOffset(8, 0, 3, 0);
            dd.popupColor = new Color32(45, 52, 61, 255);
            dd.popupTextColor = new Color32(230, 230, 230, 255);
            dd.foregroundSpriteMode = UIForegroundSpriteMode.Stretch;
            dd.verticalAlignment = UIVerticalAlignment.Middle;
            dd.horizontalAlignment = UIHorizontalAlignment.Left;

            dd.items = _unitDropdownItems;
            dd.selectedIndex = 0;

            UIButton trigger = dd.AddUIComponent<UIButton>();
            trigger.text = "▼";
            trigger.textScale = 0.7f;
            trigger.size = new Vector2(24f, DropdownHeight);
            trigger.relativePosition = new Vector3(width - 24f, 0f);
            trigger.normalBgSprite = "ButtonMenu";
            trigger.hoveredBgSprite = "ButtonMenuHovered";
            trigger.pressedBgSprite = "ButtonMenuPressed";
            dd.triggerButton = trigger;

            return dd;
        }

        /// <summary>陸上ユニットロスター全体（LandUnitRoster、カテゴリ宣言順→Tier1〜5の順）から
        /// ドロップダウンの表示テキストと発注用TypeKeyの対応配列を構築する。unlockedTierを超えるTierの
        /// 項目には末尾に " [未解禁]" を付ける（Task35：選択自体は可能なままにして、生産ボタンが
        /// TierLockedを報告する。何が研究で解禁されるか一目で分かるようにするため選択肢からは外さない）。
        /// unlockedTierが前回と同じであれば何もしない（毎フレーム呼ばれてもリストを再構築しない）。</summary>
        private static void EnsureUnitDropdownItemsBuilt(byte unlockedTier)
        {
            if (_unitDropdownItems != null && _lastUnitDropdownUnlockedTier == unlockedTier) return;

            var items = new List<string>();
            var keys = new List<string>();
            foreach (UnitType t in LandUnitRoster.All())
            {
                string label = t.TypeKey + "  (¥" + t.Cost.ToString("0") + ")";
                if (t.Tier > unlockedTier) label += " [未解禁]";
                items.Add(label);
                keys.Add(t.TypeKey);
            }
            _unitDropdownItems = items.ToArray();
            _unitDropdownTypeKeys = keys.ToArray();
            _lastUnitDropdownUnlockedTier = unlockedTier;

            if (_unitDropdown != null)
            {
                int prevSelected = _unitDropdown.selectedIndex;
                _unitDropdown.items = _unitDropdownItems;
                if (prevSelected >= 0 && prevSelected < _unitDropdownItems.Length)
                    _unitDropdown.selectedIndex = prevSelected;
            }
        }

        /// <summary>_collapsed の反映（BaseInfoPanel.ApplyCollapsedStateから呼ばれる）。</summary>
        private static void ApplyProductionCollapsedState(bool collapsed)
        {
            if (_autoProduceButton != null) _autoProduceButton.isVisible = !collapsed;
            if (_autoProduceHintLabel != null) _autoProduceHintLabel.isVisible = !collapsed;
            if (_unitDropdown != null) _unitDropdown.isVisible = !collapsed;
            if (_queueButton != null) _queueButton.isVisible = !collapsed;
            if (_cancelButton != null) _cancelButton.isVisible = !collapsed;
            if (_investButton != null) _investButton.isVisible = !collapsed;
            if (_unlockButton != null) _unlockButton.isVisible = !collapsed;
            if (_productionMessageLabel != null) _productionMessageLabel.isVisible = !collapsed;
            if (_queueLabel != null) _queueLabel.isVisible = !collapsed;
        }

        /// <summary>ステータスラベルの実際の下端（毎フレーム変動しうる）から、生産セクション各行のY座標を
        /// 再計算して反映する。BaseInfoPanel.RefreshContents から毎フレーム（折りたたみ中を除く）呼ばれる。</summary>
        private static void RefreshProductionSection(BaseUiSnapshot snapshot)
        {
            if (_panel == null || _statusLabel == null) return;

            float width = PanelWidth - Pad * 2f;
            float y = _statusLabel.relativePosition.y + _statusLabel.height + ProductionRowGap;

            _lastAutoProduce = snapshot.AutoProduce;
            _lastOwnerFactionId = snapshot.OwnerFactionId;
            _lastOwnerUnlockedTier = snapshot.OwnerUnlockedTier;
            EnsureUnitDropdownItemsBuilt(snapshot.OwnerUnlockedTier); // Task35: 未解禁Tierの表示更新

            if (_autoProduceButton != null)
            {
                _autoProduceButton.text = snapshot.AutoProduce ? "自動生産: ON" : "自動生産: OFF";
                _autoProduceButton.relativePosition = new Vector3(Pad, y);
            }
            if (_autoProduceHintLabel != null)
            {
                _autoProduceHintLabel.text = snapshot.AutoProduce ? "AIがこの基地を自動管理します" : "";
                _autoProduceHintLabel.relativePosition = new Vector3(Pad + ToggleButtonWidth + 6f, y + 4f);
            }
            y += ToggleButtonHeight + ProductionRowGap;

            if (_unitDropdown != null) _unitDropdown.relativePosition = new Vector3(Pad, y);
            y += DropdownHeight + ProductionRowGap;

            float halfWidth = (width - ProductionRowGap) / 2f;
            if (_queueButton != null) _queueButton.relativePosition = new Vector3(Pad, y);
            if (_cancelButton != null) _cancelButton.relativePosition = new Vector3(Pad + halfWidth + ProductionRowGap, y);
            y += ProductionButtonHeight + ProductionRowGap;

            // Task35: 研究投資／Tier解禁ボタン行。
            if (_investButton != null) _investButton.relativePosition = new Vector3(Pad, y);
            if (_unlockButton != null) _unlockButton.relativePosition = new Vector3(Pad + halfWidth + ProductionRowGap, y);
            y += ProductionButtonHeight + ProductionRowGap;

            if (_productionMessageLabel != null) _productionMessageLabel.relativePosition = new Vector3(Pad, y);
            y += SmallLabelHeight;

            if (_queueLabel != null)
            {
                _queueLabel.text = BuildQueueDisplayText(snapshot.QueuedTypeKeys);
                _queueLabel.relativePosition = new Vector3(Pad, y);
            }
            y += SmallLabelHeight;

            _productionBottomY = y;
        }

        /// <summary>"キュー: Tank_T3(生産中) → Infantry_T2 → …" 形式の1行を組み立てる。
        /// index 0 は常に生産中（先頭）で "(生産中)" を付ける。QueueDisplayMax件を超える分は "…" で省略する。</summary>
        private static string BuildQueueDisplayText(string[] queuedTypeKeys)
        {
            if (queuedTypeKeys == null || queuedTypeKeys.Length == 0) return "キュー: なし";

            StringBuilder sb = _queueBuilder;
            sb.Length = 0;
            sb.Append("キュー: ");

            int shown = Math.Min(queuedTypeKeys.Length, QueueDisplayMax);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) sb.Append(" → ");
                sb.Append(queuedTypeKeys[i]);
                if (i == 0) sb.Append("(生産中)");
            }
            if (queuedTypeKeys.Length > shown) sb.Append(" → …");

            return sb.ToString();
        }

        private static void OnAutoProduceClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                if (_currentBaseId == 0) return;
                bool newValue = !_lastAutoProduce;
                bool ok = MilitaryManager.TrySetAutoProduce(_currentBaseId, newValue);
                if (ok)
                {
                    // 次のRefreshContentsでスナップショットの実値へ上書きされるが、クリック直後の
                    // 見た目の即時反映のためここでも更新しておく。
                    _lastAutoProduce = newValue;
                }
                else
                {
                    ModConfig.LogError("BaseInfoPanel: TrySetAutoProduce failed baseId=" + _currentBaseId);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("BaseInfoPanel.OnAutoProduceClick error: " + e);
            }
        }

        private static void OnQueueClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                if (_currentBaseId == 0 || _unitDropdown == null || _unitDropdownTypeKeys == null) return;
                int idx = _unitDropdown.selectedIndex;
                if (idx < 0 || idx >= _unitDropdownTypeKeys.Length) return;

                QueueResult r = MilitaryManager.TryQueueUnit(_currentBaseId, _unitDropdownTypeKeys[idx]);
                SetProductionMessage(r == QueueResult.Ok ? "" : EnqueueResultMessage(r));
            }
            catch (Exception e)
            {
                ModConfig.LogError("BaseInfoPanel.OnQueueClick error: " + e);
            }
        }

        private static void OnCancelClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                if (_currentBaseId == 0) return;
                QueueResult r = MilitaryManager.TryCancelLastOrder(_currentBaseId);
                SetProductionMessage(r == QueueResult.Ok ? "" : CancelResultMessage(r));
            }
            catch (Exception e)
            {
                ModConfig.LogError("BaseInfoPanel.OnCancelClick error: " + e);
            }
        }

        /// <summary>「研究投資」ボタン（Task35）。_lastOwnerFactionId へ ResearchInvestAmount を投資する。
        /// 失敗理由はResearch.TryInvestの実装上、資金不足のみ（fが見つからない等の防御ケースは除く）。</summary>
        private static void OnInvestClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                if (_currentBaseId == 0 || !_lastOwnerFactionId.HasValue)
                {
                    SetProductionMessage("所有者がいません");
                    return;
                }
                bool ok = MilitaryManager.TryInvestResearch(_lastOwnerFactionId.Value, ResearchInvestAmount);
                SetProductionMessage(ok ? "" : "資金不足");
            }
            catch (Exception e)
            {
                ModConfig.LogError("BaseInfoPanel.OnInvestClick error: " + e);
            }
        }

        /// <summary>「Tier解禁」ボタン（Task35）。_lastOwnerFactionId の次Tierを解禁する。失敗時は
        /// 直近スナップショットのUnlockedTierから「既に最大Tier」か「研究点不足」かを区別して表示する
        /// （MilitaryManager.TryUnlockNextTierはbool一つしか返さないため、失敗理由はUI側が持つ直近の
        /// 状態から判断する）。</summary>
        private static void OnUnlockClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                if (_currentBaseId == 0 || !_lastOwnerFactionId.HasValue)
                {
                    SetProductionMessage("所有者がいません");
                    return;
                }
                bool ok = MilitaryManager.TryUnlockNextTier(_lastOwnerFactionId.Value);
                if (ok) SetProductionMessage("");
                else SetProductionMessage(_lastOwnerUnlockedTier >= 5 ? "最大Tier" : "研究点不足");
            }
            catch (Exception e)
            {
                ModConfig.LogError("BaseInfoPanel.OnUnlockClick error: " + e);
            }
        }

        private static void SetProductionMessage(string text)
        {
            if (_productionMessageLabel != null) _productionMessageLabel.text = text;
        }

        /// <summary>QueueResult -> 発注失敗理由の短い日本語文言（Task34仕様の例: 資金不足／キューが一杯。
        /// Task35でTierLockedを追加）。</summary>
        private static string EnqueueResultMessage(QueueResult r)
        {
            switch (r)
            {
                case QueueResult.BaseNotFound: return "基地が見つかりません";
                case QueueResult.NoOwner: return "所有者がいません";
                case QueueResult.UnknownType: return "不明な種別です";
                case QueueResult.QueueFull: return "キューが一杯";
                case QueueResult.NotAffordable: return "資金不足";
                case QueueResult.TierLocked: return "未解禁のTierです";
                default: return "";
            }
        }

        /// <summary>QueueResult -> 取消失敗理由の短い日本語文言。QueueFullはManualProduction.TryCancelLast側の
        /// コメントの通り「取消可能な注文が無い」（空／唯一の注文が進行中）の意味で流用されている。</summary>
        private static string CancelResultMessage(QueueResult r)
        {
            switch (r)
            {
                case QueueResult.BaseNotFound: return "基地が見つかりません";
                case QueueResult.NoOwner: return "所有者がいません";
                case QueueResult.QueueFull: return "取消できる注文がありません";
                default: return "";
            }
        }

        /// <summary>BaseInfoPanel.Destroyから呼ばれる。イベント購読解除とフィールドのリセットのみ行う
        /// （UnityEngine.Object.Destroyでの実際のGameObject破棄は_panelごと呼び出し元が行う）。</summary>
        private static void DestroyProductionSection()
        {
            if (_autoProduceButton != null) _autoProduceButton.eventClick -= OnAutoProduceClick;
            if (_queueButton != null) _queueButton.eventClick -= OnQueueClick;
            if (_cancelButton != null) _cancelButton.eventClick -= OnCancelClick;
            if (_investButton != null) _investButton.eventClick -= OnInvestClick;
            if (_unlockButton != null) _unlockButton.eventClick -= OnUnlockClick;

            _autoProduceButton = null;
            _autoProduceHintLabel = null;
            _unitDropdown = null;
            _queueButton = null;
            _cancelButton = null;
            _investButton = null;
            _unlockButton = null;
            _productionMessageLabel = null;
            _queueLabel = null;
            _lastAutoProduce = true;
            _lastOwnerFactionId = null;
            _lastOwnerUnlockedTier = 1;
            _productionBottomY = 0f;
            // Task35: 表示テキストはUnlockedTierに依存するようになったため、次セッションで確実に
            // 再構築させるためセンチネルへ戻す（_unitDropdownItems自体はLandUnitRoster由来で内容は
            // 不変のため保持したままでよく、EnsureUnitDropdownItemsBuiltがTier不一致から再構築する）。
            _lastUnitDropdownUnlockedTier = 0;
        }
    }
}
