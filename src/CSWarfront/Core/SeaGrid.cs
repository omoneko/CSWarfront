using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>
    /// The navigability grid + A* pathfinding for naval units (Task92, user request "real pathfinding
    /// for naval units"). The old straight-line + wall-following detours (MovementStepSea) could not
    /// round complex headlands and inlets; an 8-direction A* over a coarse grid (default 96m cells)
    /// now detours them.
    ///
    /// Same design principles as RoadGraph (the road A*): no UnityEngine dependency, deterministic
    /// (no RNG; ties break to the smaller cell index, a fixed tiebreak). The Game layer's
    /// SeaGridBuilder fills the cells via WaterSampler (draft-aware IsWater) and supplies it
    /// (WarState.SeaNav, runtime-only, never persisted).
    ///
    /// Paths are sequences of cell-center WorldPos (Y=0; the actual water-surface Y is resolved each
    /// step by MovementStep.CommitSeaStep). The grid is coarse, so along each path segment the
    /// traditional water check + wall-following detour keeps serving as the last line of defense
    /// (see MovementStepSea).
    /// </summary>
    public class SeaGrid
    {
        private readonly float _originX;
        private readonly float _originZ;
        private readonly float _cellSize;
        private readonly int _width;
        private readonly int _height;
        private readonly bool[] _navigable;

        public float CellSize { get { return _cellSize; } }
        public int Width { get { return _width; } }
        public int Height { get { return _height; } }

        public SeaGrid(float originX, float originZ, float cellSize, int width, int height)
        {
            _originX = originX;
            _originZ = originZ;
            _cellSize = cellSize;
            _width = width;
            _height = height;
            _navigable = new bool[width * height];
        }

        public void SetNavigable(int cx, int cz, bool value)
        {
            if (cx < 0 || cx >= _width || cz < 0 || cz >= _height) return;
            _navigable[cz * _width + cx] = value;
        }

        public bool IsNavigable(int cx, int cz)
        {
            if (cx < 0 || cx >= _width || cz < 0 || cz >= _height) return false;
            return _navigable[cz * _width + cx];
        }

        /// <summary>World coordinates to cell coordinates (out-of-range values are returned as-is;
        /// callers check the bounds).</summary>
        public void WorldToCell(float x, float z, out int cx, out int cz)
        {
            cx = (int)((x - _originX) / _cellSize);
            cz = (int)((z - _originZ) / _cellSize);
        }

        /// <summary>The cell center in world coordinates (Y=0).</summary>
        public WorldPos CellCenter(int cx, int cz)
        {
            return new WorldPos(
                _originX + (cx + 0.5f) * _cellSize,
                0f,
                _originZ + (cz + 0.5f) * _cellSize);
        }

        /// <summary>Finds a sailing route from→to. Both ends snap to the nearest navigable cell within
        /// snapRadius. Returns the cell centers from just past the start cell to the goal cell; null
        /// on snap failure or no route; an empty list when start cell == goal cell. Deterministic
        /// (the same contract as RoadGraph.FindPath).</summary>
        public List<WorldPos> FindPath(WorldPos from, WorldPos to, float snapRadius)
        {
            int startCx, startCz, goalCx, goalCz;
            if (!TrySnapToNavigable(from, snapRadius, out startCx, out startCz)) return null;
            if (!TrySnapToNavigable(to, snapRadius, out goalCx, out goalCz)) return null;

            int start = startCz * _width + startCx;
            int goal = goalCz * _width + goalCx;
            if (start == goal) return new List<WorldPos>();

            List<int> route = AStar(start, goal);
            if (route == null) return null;

            var result = new List<WorldPos>(route.Count - 1);
            for (int i = 1; i < route.Count; i++)
                result.Add(CellCenter(route[i] % _width, route[i] / _width));
            return result;
        }

        /// <summary>Finds the nearest navigable cell to pos within snapRadius (deterministic: distance
        /// ties go to the smaller index). Returns pos's own cell if it is navigable.</summary>
        private bool TrySnapToNavigable(WorldPos pos, float snapRadius, out int cx, out int cz)
        {
            WorldToCell(pos.X, pos.Z, out cx, out cz);
            if (IsNavigable(cx, cz)) return true;

            int radiusCells = (int)(snapRadius / _cellSize) + 1;
            float bestDistSq = snapRadius * snapRadius;
            int bestCx = -1, bestCz = -1;
            for (int dz = -radiusCells; dz <= radiusCells; dz++)
            {
                for (int dx = -radiusCells; dx <= radiusCells; dx++)
                {
                    int nx = cx + dx, nz = cz + dz;
                    if (!IsNavigable(nx, nz)) continue;
                    WorldPos center = CellCenter(nx, nz);
                    float ddx = center.X - pos.X, ddz = center.Z - pos.Z;
                    float distSq = ddx * ddx + ddz * ddz;
                    if (distSq < bestDistSq ||
                        (distSq == bestDistSq && bestCx >= 0 && (nz * _width + nx) < (bestCz * _width + bestCx)))
                    {
                        bestDistSq = distSq;
                        bestCx = nx; bestCz = nz;
                    }
                }
            }
            if (bestCx < 0) return false;
            cx = bestCx; cz = bestCz;
            return true;
        }

        // The 8-direction visit order (fixed = deterministic). The 4 straights first, the 4 diagonals after.
        private static readonly int[] NeighborDx = { 1, -1, 0, 0, 1, 1, -1, -1 };
        private static readonly int[] NeighborDz = { 0, 0, 1, -1, 1, -1, 1, -1 };
        private const float DiagonalCost = 1.41421356f;

        private List<int> AStar(int start, int goal)
        {
            int goalCx = goal % _width, goalCz = goal / _width;

            var openSet = new List<int> { start };
            var cameFrom = new Dictionary<int, int>();
            var gScore = new Dictionary<int, float> { { start, 0f } };
            var fScore = new Dictionary<int, float> { { start, Heuristic(start, goalCx, goalCz) } };
            var closed = new HashSet<int>();

            while (openSet.Count > 0)
            {
                int current = PickLowestFScore(openSet, fScore);
                if (current == goal) return Reconstruct(cameFrom, current);

                openSet.Remove(current);
                closed.Add(current);

                int ccx = current % _width, ccz = current / _width;
                for (int n = 0; n < 8; n++)
                {
                    int nx = ccx + NeighborDx[n], nz = ccz + NeighborDz[n];
                    if (!IsNavigable(nx, nz)) continue;
                    // Diagonal moves are allowed only when both adjacent straight cells are navigable
                    // too (prevents "slipping diagonally past" a headland corner — a route that would
                    // really graze land).
                    if (n >= 4 && (!IsNavigable(ccx + NeighborDx[n], ccz) || !IsNavigable(ccx, ccz + NeighborDz[n])))
                        continue;

                    int neighbor = nz * _width + nx;
                    if (closed.Contains(neighbor)) continue;

                    float tentative = gScore[current] + (n >= 4 ? DiagonalCost : 1f);
                    float known;
                    if (!gScore.TryGetValue(neighbor, out known) || tentative < known)
                    {
                        cameFrom[neighbor] = current;
                        gScore[neighbor] = tentative;
                        fScore[neighbor] = tentative + Heuristic(neighbor, goalCx, goalCz);
                        if (!openSet.Contains(neighbor)) openSet.Add(neighbor);
                    }
                }
            }
            return null;
        }

        private float Heuristic(int cell, int goalCx, int goalCz)
        {
            int cx = cell % _width, cz = cell / _width;
            int dx = cx > goalCx ? cx - goalCx : goalCx - cx;
            int dz = cz > goalCz ? cz - goalCz : goalCz - cz;
            // Octile distance (the admissible heuristic for 8-direction movement).
            int min = dx < dz ? dx : dz;
            int max = dx < dz ? dz : dx;
            return max - min + DiagonalCost * min;
        }

        private static int PickLowestFScore(List<int> openSet, Dictionary<int, float> fScore)
        {
            int best = openSet[0];
            float bestScore = fScore[best];
            for (int i = 1; i < openSet.Count; i++)
            {
                int candidate = openSet[i];
                float score = fScore[candidate];
                if (score < bestScore || (score == bestScore && candidate < best))
                {
                    best = candidate;
                    bestScore = score;
                }
            }
            return best;
        }

        private static List<int> Reconstruct(Dictionary<int, int> cameFrom, int current)
        {
            var route = new List<int> { current };
            while (cameFrom.TryGetValue(current, out current)) route.Insert(0, current);
            return route;
        }
    }
}
