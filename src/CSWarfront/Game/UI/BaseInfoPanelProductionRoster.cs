using System;
using System.Collections.Generic;
using System.Text;
using ColossalFramework.UI;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// BaseInfoPanelProduction のうち、ユニット選択ドロップダウンの構築/内容更新とキュー表示文字列の
    /// 組み立てだけを分離した partial class（BaseInfoPanelProduction.cs 側の500行制限のため分離。
    /// Task34のBaseInfoPanelProduction.cs分離、Task63のBaseInfoPanelMissile.cs分離と同じ方針）。
    /// 全メソッドはメインスレッド専用（Unity UI API呼び出しのため）。
    /// </summary>
    internal static partial class BaseInfoPanel
    {
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

        /// <summary>選択中の基地が生産できる領域（Task61: spawnableDomains、MilitaryBase.SpawnableDomains
        /// の写し）のロスターから、ドロップダウンの表示テキストと発注用TypeKeyの対応配列を構築する
        /// （陸軍基地はLandUnitRoster、海軍基地はNavalUnitRoster、航空基地はAirUnitRoster。
        /// 未所属の基地等でどれとも一致しない場合はLandUnitRosterへフォールバックする）。
        /// unlockedTierを超えるTierの項目には末尾に " [未解禁]" を付ける（Task35：選択自体は可能なままにして、
        /// 生産ボタンがTierLockedを報告する。何が研究で解禁されるか一目で分かるようにするため選択肢からは
        /// 外さない）。unlockedTier・spawnableDomainsのどちらも前回と同じであれば何もしない
        /// （毎フレーム呼ばれてもリストを再構築しない）。</summary>
        private static void EnsureUnitDropdownItemsBuilt(byte unlockedTier, DomainMask spawnableDomains,
            BaseType baseType = BaseType.Army)
        {
            if (_unitDropdownItems != null && _lastUnitDropdownUnlockedTier == unlockedTier &&
                _lastUnitDropdownDomains == spawnableDomains && _lastUnitDropdownBaseType == baseType) return;

            // Task61: ロスターの切り替え時（陸軍→海軍基地など）は選択インデックスを維持する意味が
            // 無い（全く別の兵科リストになるため）ので先頭へ戻す。Tierのみが変わった場合は従来通り
            // 選択位置を維持する。上書き前の値をここで捕まえておく。
            bool rosterChanged = _lastUnitDropdownDomains != spawnableDomains;

            IEnumerable<UnitType> roster;
            if (DomainMaskUtil.Contains(spawnableDomains, Domain.Sea)) roster = NavalUnitRoster.All();
            else if (DomainMaskUtil.Contains(spawnableDomains, Domain.Air)) roster = AirUnitRoster.All();
            else roster = LandUnitRoster.All();

            var items = new List<string>();
            var keys = new List<string>();
            foreach (UnitType t in roster)
            {
                // Task103: 兵科単位の生産可否（軍用列車は貨物駅のみ・貨物駅は列車のみ。
                // 軍事拠点のメニューに列車を出さない）。
                if (!FortificationRules.CanProduceUnit(baseType, t.Category)) continue;

                string label = t.TypeKey + "  (¥" + t.Cost.ToString("0") + ")";
                if (t.Tier > unlockedTier) label += " [Locked]";
                items.Add(label);
                keys.Add(t.TypeKey);
            }
            _unitDropdownItems = items.ToArray();
            _unitDropdownTypeKeys = keys.ToArray();
            _lastUnitDropdownUnlockedTier = unlockedTier;
            _lastUnitDropdownDomains = spawnableDomains;
            _lastUnitDropdownBaseType = baseType;

            if (_unitDropdown != null)
            {
                int prevSelected = _unitDropdown.selectedIndex;
                _unitDropdown.items = _unitDropdownItems;
                if (!rosterChanged && prevSelected >= 0 && prevSelected < _unitDropdownItems.Length)
                    _unitDropdown.selectedIndex = prevSelected;
                else if (_unitDropdownItems.Length > 0)
                    _unitDropdown.selectedIndex = 0;
            }
        }

        /// <summary>"キュー: Tank_T3(生産中) → Infantry_T2 → …" 形式の1行を組み立てる。
        /// index 0 は常に生産中（先頭）で "(生産中)" を付ける。QueueDisplayMax件を超える分は "…" で省略する。</summary>
        private static string BuildQueueDisplayText(string[] queuedTypeKeys)
        {
            if (queuedTypeKeys == null || queuedTypeKeys.Length == 0) return "Queue: none";

            StringBuilder sb = _queueBuilder;
            sb.Length = 0;
            sb.Append("Queue: ");

            int shown = Math.Min(queuedTypeKeys.Length, QueueDisplayMax);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) sb.Append(" -> ");
                sb.Append(queuedTypeKeys[i]);
                if (i == 0) sb.Append("(producing)");
            }
            if (queuedTypeKeys.Length > shown) sb.Append(" → …");

            return sb.ToString();
        }
    }
}
