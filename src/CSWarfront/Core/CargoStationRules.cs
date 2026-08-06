using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>Task101: military cargo station operability (design §3). A station counts as "connected"
    /// only while a rail-network node (WarState.Rails) lies within RailSnapRadius, and only then is it
    /// used for rail transport (TrainStep). Stock (StoredSupplies) and capture work even unconnected.</summary>
    public static class CargoStationRules
    {
        /// <summary>Maximum distance to a rail node for the station to count as on the rails (m).</summary>
        public const float RailSnapRadius = 100f;

        /// <summary>Task108: maximum distance when looking for an entry point on the main network (m).
        /// Wider than RailSnapRadius so that when the track right beside the station is "its own siding —
        /// a small component severed from the main line", the slightly farther main-line node is grabbed
        /// instead (the countermeasure for the in-game case where no route ever formed).</summary>
        public const float RailEntryRadius = 300f;

        /// <summary>Re-resolves every cargo station's RailConnected / RailEntry (the point on the rails
        /// where trains actually call) — called by the Game layer every time the rail network is
        /// (re)built.
        ///
        /// Task110 (user report "trains run off the rails and shuttle toward the map edge"): the entry
        /// choice changed from "the largest connected component" to "the component the stations share".
        /// Once curve sampling made node counts proportional to track length, the long pre-placed
        /// mainline (running to the outside connections) always won as the largest component, sucking
        /// every station's entry onto it — so trains ran the pre-placed line toward the outside instead
        /// of the city's military rail.
        ///
        /// Selection rules (deterministic):
        ///  1. Collect, per station, each component's nearest node within RailEntryRadius.
        ///  2. Pick the component reached by the most stations (= the network actually linking them).
        ///  3. On a tie, the component with the smallest total distance to the stations (= the one
        ///     running right beside them).
        /// Stations that cannot reach the winning component fall back to their plain nearest node within
        /// RailSnapRadius as before (which is what keeps a faction with stations on a separate network
        /// working).</summary>
        public static void RefreshConnectivity(WarState state)
        {
            var stations = new List<MilitaryBase>();
            for (int i = 0; i < state.Bases.Count; i++)
            {
                MilitaryBase b = state.Bases[i];
                if (b.Type != BaseType.CargoStation) continue;
                b.RailEntry = null;
                b.RailConnected = false;
                stations.Add(b);
            }
            if (state.Rails == null || stations.Count == 0) return;

            // 1. Tally, per station, "component → (nearest node, distance)" plus per-component votes and
            //    distance sums.
            var perStation = new Dictionary<int, KeyValuePair<ushort, float>>[stations.Count];
            var votes = new Dictionary<int, int>();
            var distanceSum = new Dictionary<int, float>();
            for (int i = 0; i < stations.Count; i++)
            {
                perStation[i] = state.Rails.FindNearestNodePerComponent(stations[i].Position, RailEntryRadius);
                foreach (var kv in perStation[i])
                {
                    int c;
                    votes.TryGetValue(kv.Key, out c);
                    votes[kv.Key] = c + 1;
                    float sum;
                    distanceSum.TryGetValue(kv.Key, out sum);
                    distanceSum[kv.Key] = sum + kv.Value.Value;
                }
            }

            // 2-3. Deterministic pick: most votes → smallest distance sum → smallest component id.
            int winner = -1;
            foreach (var kv in votes)
            {
                if (winner < 0) { winner = kv.Key; continue; }
                int cmp = kv.Value.CompareTo(votes[winner]);
                if (cmp > 0 || (cmp == 0 && distanceSum[kv.Key] < distanceSum[winner])
                    || (cmp == 0 && distanceSum[kv.Key] == distanceSum[winner] && kv.Key < winner))
                {
                    winner = kv.Key;
                }
            }

            for (int i = 0; i < stations.Count; i++)
            {
                MilitaryBase b = stations[i];
                ushort nodeId;
                KeyValuePair<ushort, float> hit;
                if (winner >= 0 && perStation[i].TryGetValue(winner, out hit))
                {
                    nodeId = hit.Key;
                }
                else if (!state.Rails.TryFindNearestNode(b.Position, RailSnapRadius, out nodeId))
                {
                    continue; // reaches no network at all: stays unconnected
                }

                WorldPos entry;
                if (state.Rails.TryGetNodePosition(nodeId, out entry))
                {
                    b.RailEntry = entry;
                    b.RailConnected = true;
                }
            }
        }

        /// <summary>Task108: the point on the rails where trains call at this station (the entry point;
        /// the station's own position while unresolved).</summary>
        public static WorldPos RailPointOf(MilitaryBase b)
        {
            return b.RailEntry.HasValue ? b.RailEntry.Value : b.Position;
        }

        /// <summary>Whether this station is usable for rail transport (owned, rail-connected, HP left).</summary>
        public static bool IsOperational(MilitaryBase b)
        {
            return b != null && b.Type == BaseType.CargoStation
                && b.OwnerFactionId != null && b.RailConnected && b.CurrentHP > 0f;
        }
    }
}
