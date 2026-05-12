using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace REPO_Active.Runtime
{
    internal sealed class ExtractionPointScanner
    {
        private readonly ExtractionPointStateTracker _stateTracker;
        private readonly List<ExtractionPoint> _cached = new List<ExtractionPoint>();
        private readonly HashSet<int> _cachedIds = new HashSet<int>();
        private readonly HashSet<int> _activatedIds = new HashSet<int>();
        private readonly HashSet<int> _discovered = new HashSet<int>();
        private readonly Dictionary<int, float> _spawnPathCache = new Dictionary<int, float>();
        private readonly HashSet<int> _spawnPathInvalid = new HashSet<int>();
        private readonly Dictionary<long, float> _edgePathCache = new Dictionary<long, float>();
        private readonly HashSet<long> _edgePathInvalid = new HashSet<long>();

        private Vector3? _spawnPos;
        private int? _firstAnchorId;
        private int? _lastAnchorId;

        public int CachedCount => _cached.Count;

        public int DiscoveredCount => _discovered.Count;

        public ExtractionPointScanner(ExtractionPointStateTracker stateTracker)
        {
            _stateTracker = stateTracker;
        }

        public bool RegisterPoint(ExtractionPoint point)
        {
            if (!point)
            {
                return false;
            }

            PruneInvalidCachedPoints();
            int id = point.GetInstanceID();
            if (_cachedIds.Contains(id))
            {
                return false;
            }

            _cachedIds.Add(id);
            _cached.Add(point);
            return true;
        }

        public List<ExtractionPoint> GetAllPoints()
        {
            PruneInvalidCachedPoints();
            return new List<ExtractionPoint>(_cached);
        }

        public Vector3 GetReferencePos()
        {
            try
            {
                var local = SemiFunc.PlayerAvatarLocal();
                if (local && local.transform)
                {
                    return local.transform.position;
                }
            }
            catch
            {
            }

            try
            {
                if (Camera.main)
                {
                    return Camera.main.transform.position;
                }
            }
            catch
            {
            }

            try
            {
                var player = GameObject.FindWithTag("Player");
                if (player)
                {
                    return player.transform.position;
                }
            }
            catch
            {
            }

            return Vector3.zero;
        }

        public void MarkAllDiscovered(List<ExtractionPoint> allPoints)
        {
            for (int i = 0; i < allPoints.Count; i++)
            {
                var point = allPoints[i];
                if (point)
                {
                    _discovered.Add(point.GetInstanceID());
                }
            }
        }

        public void UpdateDiscovered(Vector3 refPos, float radius)
        {
            if (_cached.Count == 0)
            {
                return;
            }

            float radiusSquared = radius * radius;
            for (int i = 0; i < _cached.Count; i++)
            {
                var point = _cached[i];
                if (!point)
                {
                    continue;
                }

                int id = point.GetInstanceID();
                if (_discovered.Contains(id))
                {
                    continue;
                }

                if ((refPos - point.transform.position).sqrMagnitude <= radiusSquared)
                {
                    _discovered.Add(id);
                }
            }
        }

        public List<ExtractionPoint> FilterDiscovered(List<ExtractionPoint> allPoints)
        {
            var list = new List<ExtractionPoint>();
            for (int i = 0; i < allPoints.Count; i++)
            {
                var point = allPoints[i];
                if (point && _discovered.Contains(point.GetInstanceID()))
                {
                    list.Add(point);
                }
            }

            return list;
        }

        public Vector3 GetSpawnPos()
        {
            return _spawnPos ?? Vector3.zero;
        }

        public void CaptureSpawnPosIfNeeded(Vector3 refPos)
        {
            if (_spawnPos.HasValue || refPos == Vector3.zero)
            {
                return;
            }

            _spawnPos = refPos;
        }

        public bool TryGetRoundAnchors(
            List<ExtractionPoint> allPoints,
            Vector3 spawnPos,
            out ExtractionPoint? firstAnchor,
            out ExtractionPoint? tailAnchor)
        {
            return TryGetGlobalAnchorsNoCache(allPoints, spawnPos, out firstAnchor, out tailAnchor);
        }

        public bool TryGetGlobalAnchorsNoCache(
            List<ExtractionPoint> allPoints,
            Vector3 spawnPos,
            out ExtractionPoint? firstAnchor,
            out ExtractionPoint? tailAnchor)
        {
            firstAnchor = null;
            tailAnchor = null;
            if (allPoints == null || allPoints.Count == 0 || spawnPos == Vector3.zero)
            {
                return false;
            }

            var reachable = new List<(ExtractionPoint point, int id, float spawnPath)>();
            for (int i = 0; i < allPoints.Count; i++)
            {
                var point = allPoints[i];
                if (!point)
                {
                    continue;
                }

                if (TryGetSpawnPathLength(point, spawnPos, _spawnPathCache, _spawnPathInvalid, out var distance))
                {
                    reachable.Add((point, point.GetInstanceID(), distance));
                }
            }

            if (reachable.Count == 0)
            {
                return false;
            }

            var firstPick = PickByMinSpawn(reachable);
            firstAnchor = firstPick.point;

            var rest = reachable.Where(item => item.id != firstPick.id).ToList();
            if (rest.Count == 0)
            {
                return true;
            }

            tailAnchor = PickByMinSpawn(rest).point;
            return true;
        }

        public void ResetForNewRound()
        {
            _cached.Clear();
            _cachedIds.Clear();
            _activatedIds.Clear();
            _discovered.Clear();
            _spawnPos = null;
            _spawnPathCache.Clear();
            _spawnPathInvalid.Clear();
            _edgePathCache.Clear();
            _edgePathInvalid.Clear();
            _firstAnchorId = null;
            _lastAnchorId = null;
        }

        public void MarkActivated(ExtractionPoint point)
        {
            if (point)
            {
                _activatedIds.Add(point.GetInstanceID());
            }
        }

        public ExtractionPoint.State? ReadState(ExtractionPoint point)
        {
            return _stateTracker.TryGetState(point);
        }

        public string ReadStateName(ExtractionPoint point)
        {
            return _stateTracker.ReadStateName(point);
        }

        private void PruneInvalidCachedPoints()
        {
            for (int i = _cached.Count - 1; i >= 0; i--)
            {
                var point = _cached[i];
                if (point)
                {
                    continue;
                }

                _cached.RemoveAt(i);
            }

            if (_cachedIds.Count == _cached.Count)
            {
                return;
            }

            _cachedIds.Clear();
            for (int i = 0; i < _cached.Count; i++)
            {
                var point = _cached[i];
                if (point)
                {
                    _cachedIds.Add(point.GetInstanceID());
                }
            }
        }

        public bool TryGetActivatingInfo(List<ExtractionPoint> allPoints, out string info, out int busyCount)
        {
            info = "";
            busyCount = 0;
            if (allPoints == null || allPoints.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < allPoints.Count; i++)
            {
                var point = allPoints[i];
                if (!point)
                {
                    continue;
                }

                var state = ReadState(point);
                if (!state.HasValue || !IsBlockingState(state.Value))
                {
                    continue;
                }

                busyCount++;
                if (string.IsNullOrEmpty(info))
                {
                    info = $"{point.gameObject.name} state={state.Value}";
                }
            }

            return busyCount > 0;
        }

        internal static bool IsIdleLikeState(ExtractionPoint.State? state)
        {
            return state == ExtractionPoint.State.None ||
                   state == ExtractionPoint.State.Idle ||
                   state == ExtractionPoint.State.Cancel;
        }

        internal static bool IsCompletedLikeState(ExtractionPoint.State? state)
        {
            return state == ExtractionPoint.State.Success || state == ExtractionPoint.State.Complete;
        }

        internal static bool IsBlockingState(ExtractionPoint.State? state)
        {
            return state == ExtractionPoint.State.Active ||
                   state == ExtractionPoint.State.Warning ||
                   state == ExtractionPoint.State.Extracting ||
                   state == ExtractionPoint.State.Surplus ||
                   state == ExtractionPoint.State.TaxReturn;
        }

        public List<ExtractionPoint> BuildStage1PlannedList(
            List<ExtractionPoint> allPoints,
            Vector3 spawnPos,
            bool skipActivated)
        {
            var result = new List<ExtractionPoint>();
            if (allPoints == null || allPoints.Count == 0 || spawnPos == Vector3.zero)
            {
                return result;
            }

            var candidates = BuildEligibleCandidates(allPoints, skipActivated);
            if (candidates.Count == 0)
            {
                return result;
            }

            var reachable = GetReachableFromSpawn(candidates, spawnPos);
            if (reachable.Count == 0)
            {
                return result;
            }

            int firstIndex = FindIndexById(reachable, _firstAnchorId);
            var firstPick = firstIndex >= 0 ? reachable[firstIndex] : PickByMinSpawn(reachable);
            if (!_firstAnchorId.HasValue)
            {
                _firstAnchorId = firstPick.id;
            }

            result.Add(firstPick.point);

            var restForLast = reachable.Where(item => item.id != firstPick.id).ToList();
            if (restForLast.Count == 0)
            {
                return result;
            }

            int lastIndex = FindIndexById(restForLast, _lastAnchorId);
            var lastPick = lastIndex >= 0 ? restForLast[lastIndex] : PickByMinSpawn(restForLast);
            if (!_lastAnchorId.HasValue)
            {
                _lastAnchorId = lastPick.id;
            }

            var middle = restForLast
                .Where(item => item.id != lastPick.id)
                .Select(item => item.point)
                .ToList();

            var orderedMiddle = FindBestMiddleOrder(firstPick.point, middle, lastPick.point);
            if (orderedMiddle == null)
            {
                return new List<ExtractionPoint>();
            }

            result.AddRange(orderedMiddle);
            result.Add(lastPick.point);
            return result;
        }

        public List<ExtractionPoint> BuildPlanDiscoverAllFixedAnchors(
            List<ExtractionPoint> allPoints,
            Vector3 spawnPos,
            bool skipActivated)
        {
            var result = new List<ExtractionPoint>();
            if (allPoints == null || allPoints.Count == 0 || spawnPos == Vector3.zero)
            {
                return result;
            }

            if (!TryGetGlobalAnchorsNoCache(allPoints, spawnPos, out var globalFirst, out var globalTail) ||
                globalFirst == null ||
                !globalFirst)
            {
                return result;
            }

            var candidates = BuildEligibleCandidates(allPoints, skipActivated);
            if (candidates.Count == 0)
            {
                return result;
            }

            var reachable = GetReachableFromSpawn(candidates, spawnPos);
            if (reachable.Count == 0)
            {
                return result;
            }

            int globalFirstId = globalFirst.GetInstanceID();
            int globalTailId = globalTail != null && globalTail ? globalTail.GetInstanceID() : int.MinValue;

            bool hasGlobalFirst = reachable.Any(item => item.id == globalFirstId);
            bool hasGlobalTail = globalTail != null && globalTail && globalTailId != globalFirstId && reachable.Any(item => item.id == globalTailId);

            (ExtractionPoint point, int id, float spawnPath) firstEntry;
            if (hasGlobalFirst)
            {
                firstEntry = reachable.First(item => item.id == globalFirstId);
            }
            else
            {
                var firstPool = hasGlobalTail
                    ? reachable.Where(item => item.id != globalTailId).ToList()
                    : reachable;
                if (firstPool.Count == 0)
                {
                    firstPool = reachable;
                }

                firstEntry = PickByMinSpawn(firstPool);
            }

            result.Add(firstEntry.point);

            bool tailUsable = hasGlobalTail && globalTailId != firstEntry.id;
            ExtractionPoint? tail = tailUsable ? reachable.First(item => item.id == globalTailId).point : null;
            var middle = reachable
                .Where(item => item.id != firstEntry.id)
                .Where(item => !tailUsable || item.id != globalTailId)
                .Select(item => item.point)
                .ToList();

            var orderedMiddle = FindBestMiddleOrder(firstEntry.point, middle, tail);
            if (orderedMiddle == null)
            {
                return new List<ExtractionPoint>();
            }

            result.AddRange(orderedMiddle);
            if (tail != null && tail)
            {
                result.Add(tail);
            }

            return result;
        }

        private List<ExtractionPoint> BuildEligibleCandidates(List<ExtractionPoint> allPoints, bool skipActivated)
        {
            var candidates = new List<ExtractionPoint>(allPoints.Count);
            for (int i = 0; i < allPoints.Count; i++)
            {
                var point = allPoints[i];
                if (!point)
                {
                    continue;
                }

                if (IsCompletedLikeState(ReadState(point)))
                {
                    continue;
                }

                if (skipActivated && IsMarkedActivated(point))
                {
                    continue;
                }

                candidates.Add(point);
            }

            return candidates;
        }

        private bool IsMarkedActivated(ExtractionPoint point)
        {
            return point && _activatedIds.Contains(point.GetInstanceID());
        }

        private List<(ExtractionPoint point, int id, float spawnPath)> GetReachableFromSpawn(
            List<ExtractionPoint> candidates,
            Vector3 spawnPos)
        {
            var reachable = new List<(ExtractionPoint point, int id, float spawnPath)>();
            for (int i = 0; i < candidates.Count; i++)
            {
                var point = candidates[i];
                if (!point)
                {
                    continue;
                }

                if (TryGetSpawnPathLength(point, spawnPos, _spawnPathCache, _spawnPathInvalid, out var distance))
                {
                    reachable.Add((point, point.GetInstanceID(), distance));
                }
            }

            return reachable;
        }

        private List<ExtractionPoint>? FindBestMiddleOrder(
            ExtractionPoint first,
            List<ExtractionPoint> middleCandidates,
            ExtractionPoint? tail)
        {
            if (!first)
            {
                return null;
            }

            if (middleCandidates.Count == 0)
            {
                if (tail == null || !tail)
                {
                    return new List<ExtractionPoint>();
                }

                return TryGetEdgePathLength(first, tail, _edgePathCache, _edgePathInvalid, out _)
                    ? new List<ExtractionPoint>()
                    : null;
            }

            const float tieEpsilon = 0.0001f;
            var used = new bool[middleCandidates.Count];
            var current = new List<ExtractionPoint>(middleCandidates.Count);
            List<ExtractionPoint>? best = null;
            float bestTotal = float.MaxValue;

            void Search(ExtractionPoint previous, float costSoFar)
            {
                if (costSoFar > bestTotal + tieEpsilon)
                {
                    return;
                }

                if (current.Count == middleCandidates.Count)
                {
                    float total = costSoFar;
                if (tail != null && tail)
                    {
                        if (!TryGetEdgePathLength(previous, tail, _edgePathCache, _edgePathInvalid, out var tailCost))
                        {
                            return;
                        }

                        total += tailCost;
                    }

                    if (total + tieEpsilon < bestTotal ||
                        Mathf.Abs(total - bestTotal) <= tieEpsilon && IsBetterSameCostOrder(current, best))
                    {
                        bestTotal = total;
                        best = new List<ExtractionPoint>(current);
                    }

                    return;
                }

                for (int i = 0; i < middleCandidates.Count; i++)
                {
                    if (used[i])
                    {
                        continue;
                    }

                    var next = middleCandidates[i];
                    if (!next ||
                        !TryGetEdgePathLength(previous, next, _edgePathCache, _edgePathInvalid, out var edgeCost))
                    {
                        continue;
                    }

                    used[i] = true;
                    current.Add(next);
                    Search(next, costSoFar + edgeCost);
                    current.RemoveAt(current.Count - 1);
                    used[i] = false;
                }
            }

            Search(first, 0f);
            return best;
        }

        private static bool IsBetterSameCostOrder(List<ExtractionPoint> current, List<ExtractionPoint>? best)
        {
            if (best == null)
            {
                return true;
            }

            int count = Math.Min(current.Count, best.Count);
            for (int i = 0; i < count; i++)
            {
                int a = current[i].GetInstanceID();
                int b = best[i].GetInstanceID();
                if (a != b)
                {
                    return a < b;
                }
            }

            return current.Count < best.Count;
        }

        private static (ExtractionPoint point, int id, float spawnPath) PickByMinSpawn(
            List<(ExtractionPoint point, int id, float spawnPath)> list)
        {
            const float tieEpsilon = 0.0001f;
            var best = list[0];
            for (int i = 1; i < list.Count; i++)
            {
                var current = list[i];
                if (current.spawnPath + tieEpsilon < best.spawnPath ||
                    Mathf.Abs(current.spawnPath - best.spawnPath) <= tieEpsilon && current.id < best.id)
                {
                    best = current;
                }
            }

            return best;
        }

        private static int FindIndexById(List<(ExtractionPoint point, int id, float spawnPath)> list, int? id)
        {
            if (!id.HasValue)
            {
                return -1;
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].id == id.Value)
                {
                    return i;
                }
            }

            return -1;
        }

        private static long MakeDirectedKey(int fromId, int toId)
        {
            return ((long)(uint)fromId << 32) | (uint)toId;
        }

        private bool TryGetPathLength(Vector3 from, Vector3 to, out float length)
        {
            length = 0f;
            try
            {
                if (!TrySampleNavPoint(from, out var fromOnNav) ||
                    !TrySampleNavPoint(to, out var toOnNav))
                {
                    return false;
                }

                var path = new NavMeshPath();
                if (!NavMesh.CalculatePath(fromOnNav, toOnNav, NavMesh.AllAreas, path) ||
                    path.status != NavMeshPathStatus.PathComplete)
                {
                    return false;
                }

                var corners = path.corners;
                if (corners == null || corners.Length == 0)
                {
                    return false;
                }

                if (corners.Length == 1)
                {
                    length = 0f;
                    return true;
                }

                float sum = 0f;
                for (int i = 1; i < corners.Length; i++)
                {
                    sum += Vector3.Distance(corners[i - 1], corners[i]);
                }

                length = sum;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TrySampleNavPoint(Vector3 pos, out Vector3 sampled)
        {
            sampled = pos;

            if (NavMesh.SamplePosition(pos, out var hit, 3f, NavMesh.AllAreas))
            {
                sampled = hit.position;
                return true;
            }

            if (NavMesh.SamplePosition(pos, out hit, 10f, NavMesh.AllAreas))
            {
                sampled = hit.position;
                return true;
            }

            return false;
        }

        private bool TryGetSpawnPathLength(
            ExtractionPoint point,
            Vector3 spawnPos,
            Dictionary<int, float> cache,
            HashSet<int> invalid,
            out float length)
        {
            length = 0f;
            if (!point)
            {
                return false;
            }

            int id = point.GetInstanceID();
            if (cache.TryGetValue(id, out length))
            {
                return true;
            }

            if (invalid.Contains(id))
            {
                return false;
            }

            if (TryGetPathLength(spawnPos, point.transform.position, out length))
            {
                cache[id] = length;
                return true;
            }

            invalid.Add(id);
            return false;
        }

        private bool TryGetEdgePathLength(
            ExtractionPoint from,
            ExtractionPoint to,
            Dictionary<long, float> cache,
            HashSet<long> invalid,
            out float length)
        {
            length = 0f;
            if (!from || !to)
            {
                return false;
            }

            int fromId = from.GetInstanceID();
            int toId = to.GetInstanceID();
            if (fromId == toId)
            {
                length = 0f;
                return true;
            }

            long key = MakeDirectedKey(fromId, toId);
            if (cache.TryGetValue(key, out length))
            {
                return true;
            }

            if (invalid.Contains(key))
            {
                return false;
            }

            if (TryGetPathLength(from.transform.position, to.transform.position, out length))
            {
                cache[key] = length;
                return true;
            }

            invalid.Add(key);
            return false;
        }
    }
}
