using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Partial class splitting out of BaseInfoPanel only the formatting of display labels and the
    /// recomputation of the panel height (split off because of the 500-line limit on BaseInfoPanel.cs;
    /// same policy as the Task34 BaseInfoPanelProduction.cs split and the Task63
    /// BaseInfoPanelMissile.cs split).
    /// All methods are main-thread only (they call Unity UI APIs).
    /// </summary>
    internal static partial class BaseInfoPanel
    {
        /// <summary>Task61: BaseType -&gt; display label (for the panel).</summary>
        private static string BaseTypeLabel(BaseType type)
        {
            switch (type)
            {
                case BaseType.Army: return WarfrontStrings.BaseInfo_TypeArmyBase;
                case BaseType.Navy: return WarfrontStrings.BaseInfo_TypeNavalBase;
                case BaseType.AirForce: return WarfrontStrings.BaseInfo_TypeAirBase;
                case BaseType.MissileBase: return WarfrontStrings.BaseInfo_TypeMissileBase;
                default: return type.ToString();
            }
        }

        /// <summary>Task61: DomainMask -&gt; display label (for the panel; lists "Land"/"Sea"/"Air" per bit).</summary>
        private static string SpawnableDomainsLabel(DomainMask mask)
        {
            var parts = new System.Collections.Generic.List<string>(3);
            if (DomainMaskUtil.Contains(mask, Domain.Land)) parts.Add(WarfrontStrings.BaseInfo_DomainLand);
            if (DomainMaskUtil.Contains(mask, Domain.Sea)) parts.Add(WarfrontStrings.BaseInfo_DomainSea);
            if (DomainMaskUtil.Contains(mask, Domain.Air)) parts.Add(WarfrontStrings.BaseInfo_DomainAir);
            return parts.Count > 0 ? string.Join("/", parts.ToArray()) : WarfrontStrings.Common_None;
        }

        /// <summary>
        /// Task33: derives the total expanded panel height from the actual height of the status label
        /// (autoHeight enabled) and updates the _expandedHeight cache. So that collapse -&gt; re-expand
        /// can restore exactly to the latest value computed here, this calculation is redone on every
        /// content update rather than using a hard-coded constant.
        /// _panel.height / _expandedHeight are rewritten only when the value changed (to avoid wasteful
        /// per-frame layout recomputation — not a cross-thread issue, but an unnecessary redraw cost).
        /// </summary>
        private static void RecomputeExpandedHeight()
        {
            if (_statusLabel == null || _panel == null) return;

            // Task34: if the production section has been built, use its bottom edge
            // (_productionBottomY, updated by RefreshProductionSection/BuildProductionSection) as the
            // reference.
            // Task63: the missile-base-only section (_missileSectionBottomY) is mutually exclusive with
            // it, so depending on the selected base type exactly one of them is nonzero at any time
            // (simply take the larger with Math.Max).
            // If neither is built (theoretically impossible, but defensively) fall back to the bottom
            // edge of the status label.
            float sectionBottom = Mathf.Max(_productionBottomY, _missileSectionBottomY);
            float contentBottom = sectionBottom > 0f
                ? sectionBottom
                : _statusLabel.relativePosition.y + _statusLabel.height;
            // Give vertical headroom beyond the content's actual size (user request: the building window
            // should be "1.5x taller in size only"). Text size and width are unchanged; only the panel
            // height is multiplied by VerticalScale to relieve the cramped look.
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
