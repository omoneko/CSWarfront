using System;
using CSWarfront.Game;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Partial class that splits out from OptionsModelAssignPage only the change-notification
    /// handlers of the faction / unit type / asset kind / search field / copy scope / asset selection
    /// controls (newly created in Task70 because the OptionsModelAssignPage.cs side reached the
    /// 500-line limit; pure code move only, no logic changes).
    /// The fields (_suppressEvents/_customOnly/_filteredAssetNames etc.) are all declared on the
    /// OptionsModelAssignPage.cs side (fine, since a partial class shares private members across all
    /// parts).
    /// All methods are main-thread only (they call Unity UI APIs).
    /// </summary>
    internal static partial class OptionsModelAssignPage
    {
        private static void OnFactionChanged(int value)
        {
            try
            {
                if (_suppressEvents) return;
                RefreshTypeKeyLabels(_typeKeyDropdown != null ? _typeKeyDropdown.selectedIndex : 0);
                RefreshCurrentBinding();
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsModelAssignPage.OnFactionChanged error: " + e);
            }
        }

        private static void OnTypeKeyChanged(int value)
        {
            try
            {
                if (_suppressEvents) return;
                RefreshCurrentBinding();
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsModelAssignPage.OnTypeKeyChanged error: " + e);
            }
        }

        private static void OnAssetKindChanged(int value)
        {
            try
            {
                if (_suppressEvents) return;
                RefreshAssetDropdown();
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsModelAssignPage.OnAssetKindChanged error: " + e);
            }
        }

        private static void OnCustomOnlyChanged(bool value)
        {
            try
            {
                _customOnly = value;
                RefreshAssetDropdown();
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsModelAssignPage.OnCustomOnlyChanged error: " + e);
            }
        }

        private static void OnSearchTextChanged(string value)
        {
            try
            {
                if (_suppressEvents) return;
                RefreshAssetDropdown();
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsModelAssignPage.OnSearchTextChanged error: " + e);
            }
        }

        /// <summary>No-op for AddTextfield's OnTextSubmitted (no extra handling is needed on Enter
        /// confirmation; OnTextChanged already filters on every change).</summary>
        private static void OnSearchTextSubmitted(string value)
        {
        }

        /// <summary>No-op for AddDropdown's eventCallback (for the copy-target scope it is sufficient
        /// to read selectedIndex at the moment OnCopyApplyClick is pressed).</summary>
        private static void OnCopyScopeChanged(int value)
        {
        }

        private static void OnAssetSelected(int value)
        {
            try
            {
                if (_suppressEvents) return;

                if (value >= 1 && value - 1 < _filteredAssetNames.Count) RefreshThumbnail(SelectedAssetKind, _filteredAssetNames[value - 1]);
                else RefreshCurrentBinding();
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsModelAssignPage.OnAssetSelected error: " + e);
            }
        }
    }
}
