using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// Read-only snapshot for the unit info panel (Game/UI/UnitInfoPanel) (Task31).
    /// So the UI never touches WarState / UnitInstance / UnitType directly,
    /// MilitaryManager.TryGetUnitSnapshot copies the values inside _stateLock and hands them over.
    /// Follows the same pattern as BaseUiSnapshot (Game/BaseUiSnapshot.cs, Task25/30).
    /// </summary>
    public struct UnitUiSnapshot
    {
        public string TypeKey;
        public byte Tier;
        public byte FactionId;
        public float CurrentHP;
        public float MaxHP;
        public float Attack;
        public float Range;
        public float Armor;
        /// <summary>UnitType.Speed (map distance / in-game time) converted back to km/h via
        /// SpeedCalibration.KmhFromUnitsPerGameHour (uses the Task26 calibration constant; display
        /// only).</summary>
        public float SpeedKmh;
        /// <summary>Effective accuracy (Task38). Not UnitType.Accuracy itself but the value after the
        /// drone-observation-support buff, obtained through CombatSynergy.AccuracyFor. This lets the
        /// player see the effect of drone observation support in the UI.</summary>
        public float Accuracy;
        /// <summary>Whether Accuracy has been raised above the base value by CombatSynergy (drone
        /// observation support) (Task38). Used by UnitInfoPanel to decide whether to show the
        /// "Accuracy: 85% (spotting)" annotation.</summary>
        public bool AccuracyBoosted;
        public UnitState State;
        public uint? TargetId;
        /// <summary>Index of the next element in Path. 0 if no Path is set (straight-line movement
        /// fallback).</summary>
        public int PathIndex;
        /// <summary>Number of elements in Path. 0 if no Path is set (the UI uses this to switch to the
        /// "straight line" display).</summary>
        public int PathCount;
        /// <summary>The player's command order (Task48). UnitInfoPanel displays it as one of free
        /// advance / hold / rally-and-wait / AI.</summary>
        public UnitOrder Order;

        /// <summary>Task99: ammo gauge (0..1). Not shown when HasAmmoGauge=false (branches with
        /// unlimited ammo).</summary>
        public float Ammo;
        public bool HasAmmoGauge;

        /// <summary>Task99: supply truck load (0..1). Shown only when IsSupplyTruck=true.</summary>
        public float SupplyLoad;
        public bool IsSupplyTruck;
    }

    /// <summary>
    /// Assembly logic for UnitUiSnapshot (Task31). Intended to be called from inside _stateLock in
    /// MilitaryManager.TryGetUnitSnapshot — the caller must hold the lock (this class itself does not
    /// lock). Split out because of the 500-line limit on MilitaryManager.cs (same reason as
    /// BaseUiSnapshotBuilder, following Task30).
    /// </summary>
    internal static class UnitUiSnapshotBuilder
    {
        /// <summary>Even when type is null (abnormal cases such as an unregistered type), returns
        /// zero-filled values instead of throwing. state is used to compute Accuracy (Task38, the
        /// effective accuracy via CombatSynergy.AccuracyFor).</summary>
        public static UnitUiSnapshot Build(WarState state, UnitInstance unit, UnitType type)
        {
            float effectiveAccuracy = type != null ? CombatSynergy.AccuracyFor(state, unit, type) : 0f;
            return new UnitUiSnapshot
            {
                TypeKey = unit.TypeKey,
                Tier = type != null ? type.Tier : (byte)0,
                FactionId = unit.FactionId,
                CurrentHP = unit.CurrentHP,
                MaxHP = type != null ? type.MaxHP : 0f,
                Attack = type != null ? type.Attack : 0f,
                Range = type != null ? type.Range : 0f,
                Armor = type != null ? type.Armor : 0f,
                // Task83: display the effective speed (including the global multiplier). Showing
                // type.Speed alone would disagree with the actual movement.
                SpeedKmh = type != null
                    ? SpeedCalibration.KmhFromUnitsPerGameHour(type.Speed * MovementStep.GlobalSpeedMultiplier)
                    : 0f,
                Accuracy = effectiveAccuracy,
                // Being higher than the base accuracy (type.Accuracy) means CombatSynergy (drone
                // observation support) is in effect (because AccuracyFor is specified to return
                // type.Accuracy unchanged when not applicable).
                AccuracyBoosted = type != null && effectiveAccuracy > type.Accuracy,
                State = unit.State,
                TargetId = unit.TargetId,
                PathIndex = unit.PathIndex,
                PathCount = unit.Path != null ? unit.Path.Count : 0,
                Order = unit.Order,
                Ammo = unit.Ammo, // Task99
                // Task100: Invaders are now on the ammo system too (forage/local-procurement style),
                // so show the gauge.
                HasAmmoGauge = type != null && type.AmmoCombatHours > 0f,
                SupplyLoad = unit.SupplyLoad,
                IsSupplyTruck = type != null && type.Category == UnitCategory.SupplyTruck
            };
        }
    }
}
