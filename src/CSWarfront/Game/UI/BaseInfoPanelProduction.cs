using System;
using System.Collections.Generic;
using System.Text;
using ColossalFramework.UI;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Partial class splitting out only the parts of BaseInfoPanel that concern player-driven manual
    /// production (auto-produce toggle / ordering / cancelling, Task34). Split off because of the
    /// 500-line limit on BaseInfoPanel.cs (same policy as the Task30 BaseUiSnapshotBuilder split and the
    /// Task34 MilitaryManagerManualProduction.cs split).
    ///
    /// This class itself accesses the private static fields on the BaseInfoPanel.cs side directly
    /// (_panel / _statusLabel / _currentBaseId / _collapsed etc.; fine because a partial class shares
    /// private members across all its parts). Calls always come from Build/Destroy/ApplyCollapsedState/
    /// RefreshContents on the BaseInfoPanel.cs side; this class does not carry state on its own. All
    /// methods are main-thread only (Unity UI APIs).
    /// </summary>
    internal static partial class BaseInfoPanel
    {
        private const float ProductionRowGap = 6f;
        private const float ToggleButtonWidth = 150f;
        private const float ToggleButtonHeight = 22f;
        private const float SmallLabelHeight = 16f;
        private const float ProductionButtonHeight = 24f;

        /// <summary>How many entries after the head to list in the queue display. Beyond this, elide
        /// with "…" (Task34 spec).</summary>
        private const int QueueDisplayMax = 3;

        /// <summary>Amount invested per click of the "Invest in Research" button (Task35).</summary>
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

        /// <summary>AutoProduce value from the most recent snapshot. Remembered so the click handler can
        /// compute "the inverted value" (unlike TryEnqueue etc., the UI side cannot toggle instantly
        /// without holding the current value).</summary>
        private static bool _lastAutoProduce = true;

        /// <summary>Owning faction id from the most recent snapshot (Task35). Used as the factionId
        /// passed to MilitaryManager when the research-invest / tier-unlock buttons are clicked
        /// (BaseUiSnapshot carries not just OwnerFactionId but also the research values, but the button
        /// click handlers do not retain the whole snapshot, so it is remembered here).</summary>
        private static byte? _lastOwnerFactionId;

        /// <summary>Owning faction's UnlockedTier from the most recent snapshot (Task35). Remembered to
        /// determine the tier-unlock button's failure reason (insufficient research points vs already at
        /// max tier).</summary>
        private static byte _lastOwnerUnlockedTier = 1;

        /// <summary>Bottom Y of the production section (used by RecomputeExpandedHeight to derive the
        /// overall panel height).</summary>
        private static float _productionBottomY;

        /// <summary>Display texts for the dropdown (e.g. "Tank_T3  (¥153)", or if locked
        /// "Tank_T4  (¥168) [locked]", Task35). They depend on the owning faction's UnlockedTier, so
        /// they are rebuilt only when the value changes (checked via
        /// _lastUnitDropdownUnlockedTier).</summary>
        private static string[] _unitDropdownItems;
        /// <summary>TypeKeys in the same order as _unitDropdownItems (the values used for the actual order).</summary>
        private static string[] _unitDropdownTypeKeys;
        /// <summary>The UnlockedTier at the time _unitDropdownItems was last built. 0 is a sentinel
        /// meaning "not built yet" (valid tiers are 1..5 so there is no collision, Task35).</summary>
        private static byte _lastUnitDropdownUnlockedTier;

        /// <summary>The SpawnableDomains at the time _unitDropdownItems was last built (Task61). Used to
        /// switch the dropdown contents to the land/sea/air roster when the base type changed and the
        /// selected base was swapped (e.g. army -&gt; naval base). The default DomainMask.None is a
        /// "not built yet" sentinel (a valid base always has at least one of the Land/Sea/Air bits set,
        /// so there is no collision).</summary>
        private static DomainMask _lastUnitDropdownDomains = DomainMask.None;

        /// <summary>Task103: same as above, the BaseType at the time of the last build (to rebuild the
        /// list on a cargo-station &lt;-&gt; army-base switch; both have SpawnableDomains=Land, so the
        /// domain alone cannot distinguish them).</summary>
        private static BaseType _lastUnitDropdownBaseType = BaseType.Army;

        private static readonly StringBuilder _queueBuilder = new StringBuilder(128);

        /// <summary>Creates the production section controls below the status label (called once from
        /// Build()). The relativePosition values here may be placeholders (RefreshProductionSection
        /// updates them to the correct positions every frame; RefreshContents runs at least once before
        /// the first display — while _panel.isVisible=false — so no harm is done).</summary>
        private static void BuildProductionSection(float width)
        {
            if (_panel == null) return;

            EnsureUnitDropdownItemsBuilt(1, DomainMask.Land); // Initial display assumes unaffiliated / default UnlockedTier (1) / army-base equivalent. Real values are applied by the first RefreshContents.

            _autoProduceButton = _panel.AddUIComponent<UIButton>();
            _autoProduceButton.size = new Vector2(ToggleButtonWidth, ToggleButtonHeight);
            _autoProduceButton.textScale = 0.75f;
            _autoProduceButton.normalBgSprite = "ButtonMenu";
            _autoProduceButton.hoveredBgSprite = "ButtonMenuHovered";
            _autoProduceButton.pressedBgSprite = "ButtonMenuPressed";
            _autoProduceButton.text = WarfrontStrings.BaseInfo_AutoProduceOn;
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
            _queueButton.text = WarfrontStrings.BaseInfo_ProduceButton;
            _queueButton.textScale = 0.8f;
            _queueButton.size = new Vector2(halfWidth, ProductionButtonHeight);
            _queueButton.normalBgSprite = "ButtonMenu";
            _queueButton.hoveredBgSprite = "ButtonMenuHovered";
            _queueButton.pressedBgSprite = "ButtonMenuPressed";
            _queueButton.relativePosition = new Vector3(Pad, 0f);
            _queueButton.eventClick += OnQueueClick;

            _cancelButton = _panel.AddUIComponent<UIButton>();
            _cancelButton.text = WarfrontStrings.BaseInfo_CancelButton;
            _cancelButton.textScale = 0.8f;
            _cancelButton.size = new Vector2(halfWidth, ProductionButtonHeight);
            _cancelButton.normalBgSprite = "ButtonMenu";
            _cancelButton.hoveredBgSprite = "ButtonMenuHovered";
            _cancelButton.pressedBgSprite = "ButtonMenuPressed";
            _cancelButton.relativePosition = new Vector3(Pad + halfWidth + ProductionRowGap, 0f);
            _cancelButton.eventClick += OnCancelClick;

            // Task35: investment of funds into research points, and tier unlocking with research points.
            _investButton = _panel.AddUIComponent<UIButton>();
            _investButton.text = string.Format(WarfrontStrings.BaseInfo_InvestButton, ResearchInvestAmount.ToString("0"));
            _investButton.textScale = 0.8f;
            _investButton.size = new Vector2(halfWidth, ProductionButtonHeight);
            _investButton.normalBgSprite = "ButtonMenu";
            _investButton.hoveredBgSprite = "ButtonMenuHovered";
            _investButton.pressedBgSprite = "ButtonMenuPressed";
            _investButton.relativePosition = new Vector3(Pad, 0f);
            _investButton.eventClick += OnInvestClick;

            _unlockButton = _panel.AddUIComponent<UIButton>();
            _unlockButton.text = WarfrontStrings.BaseInfo_UnlockTierButton;
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
            _queueLabel.text = WarfrontStrings.BaseInfo_QueueNone;
            _queueLabel.relativePosition = new Vector3(Pad, 0f);
        }

        /// <summary>Applies _collapsed (called from BaseInfoPanel.ApplyCollapsedState).</summary>
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

        /// <summary>Recomputes and applies the Y coordinates of each production-section row from the
        /// actual bottom of the status label (which can change every frame). Called every frame (except
        /// while collapsed) from BaseInfoPanel.RefreshContents.</summary>
        private static void RefreshProductionSection(BaseUiSnapshot snapshot)
        {
            if (_panel == null || _statusLabel == null) return;

            // Task63: for missile bases, BaseInfoPanelMissile.RefreshMissileSection shows a different
            // section instead of unit production (mutually exclusive). Because this runs every frame
            // (except while collapsed), it tracks correctly even on the frame where the selected base
            // was swapped.
            bool isMissileBase = snapshot.Type == BaseType.MissileBase;
            ApplyProductionCollapsedState(isMissileBase);
            if (isMissileBase)
            {
                _productionBottomY = 0f;
                return;
            }

            float width = PanelWidth - Pad * 2f;
            float y = _statusLabel.relativePosition.y + _statusLabel.height + ProductionRowGap;

            _lastAutoProduce = snapshot.AutoProduce;
            _lastOwnerFactionId = snapshot.OwnerFactionId;
            _lastOwnerUnlockedTier = snapshot.OwnerUnlockedTier;
            // Task35: refresh the display of locked tiers. Task61: switch between the Land/Sea/Air rosters according to the base's producible domains.
            EnsureUnitDropdownItemsBuilt(snapshot.OwnerUnlockedTier, snapshot.SpawnableDomains, snapshot.Type); // Task103

            if (_autoProduceButton != null)
            {
                _autoProduceButton.text = snapshot.AutoProduce ? WarfrontStrings.BaseInfo_AutoProduceOn : WarfrontStrings.BaseInfo_AutoProduceOff;
                _autoProduceButton.relativePosition = new Vector3(Pad, y);
            }
            if (_autoProduceHintLabel != null)
            {
                _autoProduceHintLabel.text = snapshot.AutoProduce ? WarfrontStrings.BaseInfo_AutoProduceHint : "";
                _autoProduceHintLabel.relativePosition = new Vector3(Pad + ToggleButtonWidth + 6f, y + 4f);
            }
            y += ToggleButtonHeight + ProductionRowGap;

            if (_unitDropdown != null) _unitDropdown.relativePosition = new Vector3(Pad, y);
            y += DropdownHeight + ProductionRowGap;

            float halfWidth = (width - ProductionRowGap) / 2f;
            if (_queueButton != null) _queueButton.relativePosition = new Vector3(Pad, y);
            if (_cancelButton != null) _cancelButton.relativePosition = new Vector3(Pad + halfWidth + ProductionRowGap, y);
            y += ProductionButtonHeight + ProductionRowGap;

            // Task35: the research-invest / tier-unlock button row.
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

        private static void OnAutoProduceClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                if (_currentBaseId == 0) return;
                bool newValue = !_lastAutoProduce;
                bool ok = MilitaryManager.TrySetAutoProduce(_currentBaseId, newValue);
                if (ok)
                {
                    // The next RefreshContents overwrites this with the snapshot's real value, but update
                    // here too so the visual state reflects the click immediately.
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

        /// <summary>The "Invest in Research" button (Task35). Invests ResearchInvestAmount for
        /// _lastOwnerFactionId. By Research.TryInvest's implementation the only failure reason is
        /// insufficient funds (aside from defensive cases such as the faction not being found).</summary>
        private static void OnInvestClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                if (_currentBaseId == 0 || !_lastOwnerFactionId.HasValue)
                {
                    SetProductionMessage(WarfrontStrings.BaseInfo_MsgNoOwner);
                    return;
                }
                bool ok = MilitaryManager.TryInvestResearch(_lastOwnerFactionId.Value, ResearchInvestAmount);
                SetProductionMessage(ok ? "" : WarfrontStrings.BaseInfo_MsgInsufficientFunds);
            }
            catch (Exception e)
            {
                ModConfig.LogError("BaseInfoPanel.OnInvestClick error: " + e);
            }
        }

        /// <summary>The "Unlock Tier" button (Task35). Unlocks the next tier for _lastOwnerFactionId. On
        /// failure, distinguish "already at max tier" from "insufficient research points" using the
        /// UnlockedTier from the most recent snapshot (MilitaryManager.TryUnlockNextTier returns only a
        /// single bool, so the failure reason is judged from the most recent state the UI holds).</summary>
        private static void OnUnlockClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                if (_currentBaseId == 0 || !_lastOwnerFactionId.HasValue)
                {
                    SetProductionMessage(WarfrontStrings.BaseInfo_MsgNoOwner);
                    return;
                }
                bool ok = MilitaryManager.TryUnlockNextTier(_lastOwnerFactionId.Value);
                if (ok) SetProductionMessage("");
                else SetProductionMessage(_lastOwnerUnlockedTier >= 5 ? WarfrontStrings.BaseInfo_MsgMaxTier : WarfrontStrings.BaseInfo_MsgInsufficientResearch);
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

        /// <summary>QueueResult -&gt; short message for the order-failure reason (Task34-spec examples:
        /// insufficient funds / queue full. TierLocked added in Task35).</summary>
        private static string EnqueueResultMessage(QueueResult r)
        {
            switch (r)
            {
                case QueueResult.BaseNotFound: return WarfrontStrings.BaseInfo_MsgBaseNotFound;
                case QueueResult.NoOwner: return WarfrontStrings.BaseInfo_MsgNoOwner;
                case QueueResult.UnknownType: return WarfrontStrings.BaseInfo_MsgUnknownType;
                case QueueResult.QueueFull: return WarfrontStrings.BaseInfo_MsgQueueFull;
                case QueueResult.NotAffordable: return WarfrontStrings.BaseInfo_MsgInsufficientFunds;
                case QueueResult.TierLocked: return WarfrontStrings.BaseInfo_MsgTierLocked;
                case QueueResult.WrongDomain: return WarfrontStrings.BaseInfo_MsgWrongDomain; // Task61
                default: return "";
            }
        }

        /// <summary>QueueResult -&gt; short message for the cancel-failure reason. As stated in the
        /// comments on ManualProduction.TryCancelLast, QueueFull is repurposed to mean "no cancellable
        /// order" (queue empty, or the only order is in progress).</summary>
        private static string CancelResultMessage(QueueResult r)
        {
            switch (r)
            {
                case QueueResult.BaseNotFound: return WarfrontStrings.BaseInfo_MsgBaseNotFound;
                case QueueResult.NoOwner: return WarfrontStrings.BaseInfo_MsgNoOwner;
                case QueueResult.QueueFull: return WarfrontStrings.BaseInfo_MsgNoOrdersToCancel;
                default: return "";
            }
        }

        /// <summary>Called from BaseInfoPanel.Destroy. Only unsubscribes events and resets fields
        /// (the actual GameObject destruction via UnityEngine.Object.Destroy is done by the caller
        /// together with _panel).</summary>
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
            // Task35: the display texts now depend on UnlockedTier, so return to the sentinel to
            // guarantee a rebuild in the next session (_unitDropdownItems itself comes from
            // LandUnitRoster and its contents are immutable, so it may be kept;
            // EnsureUnitDropdownItemsBuilt rebuilds from the tier mismatch).
            _lastUnitDropdownUnlockedTier = 0;
            _lastUnitDropdownDomains = DomainMask.None; // Task61: guarantee the roster is rebuilt in the next session.
        }
    }
}
