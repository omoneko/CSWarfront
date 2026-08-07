using UnityEngine;
namespace CSWarfront.Game
{
    /// <summary>
    /// Identification tag attached to a unit's root GameObject (Task31). A pure data tag used only so
    /// that click selection (Game/UI/UnitSelection) can back-resolve which logical unit (InstanceId)
    /// a raycast hit (including the case where it is a child visibility marker) belongs to. Carries no
    /// logic at all.
    /// The ID is held on the GameObject itself so there is no need to keep and synchronize a second
    /// dictionary (GameObject to InstanceId).
    /// </summary>
    public class UnitVisualTag : MonoBehaviour
    {
        public uint InstanceId;
    }
}
