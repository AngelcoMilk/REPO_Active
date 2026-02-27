using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace REPO_Active.Runtime
{
    public sealed class ExtractionPointScanner
    {
        private readonly ManualLogSource _log;
        // Verification notes (decompile cross-check):
        // - ExtractionPoint type and member currentState -> VERIFIED in Assembly-CSharp\ExtractionPoint.cs.
        // - UnityEngine.Object.FindObjectsOfType(Type) and Time.* are verified in UnityEngine.CoreModule.
        // - This file does NOT call Photon APIs directly.

        private Type? _epType;
        private readonly List<Component> _cached = new();
        private float _lastScanRealtime;
        private int _lastScanCount = -1;

        private Vector3? _spawnPos;
        private readonly HashSet<int> _activatedIds = new();
        private readonly HashSet<int> _discovered = new();
        // Round-level NavMesh path cache (cleared on new round).
        private readonly Dictionary<int, float> _spawnPathCache = new();
        private readonly HashSet<int> _spawnPathInvalid = new();
        private readonly Dictionary<long, float> _edgePathCache = new();
        private readonly HashSet<long> _edgePathInvalid = new();
        private int? _firstAnchorId;
        private int? _lastAnchorId;
        private static readonly Dictionary<string, bool> _idleCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, bool> _completeCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // =====================
        // Stage1: planning helpers
        // =====================

        public float RescanCooldown { get; set; }
        public Action<string>? DebugLog { get; set; }
        public bool LogReady { get; set; }

        public int CachedCount => _cached.Count;
        public int DiscoveredCount => _discovered.Count;

        public ExtractionPointScanner(ManualLogSource log, float rescanCooldown)
        {
            _log = log;
            RescanCooldown = rescanCooldown;
        }

        public bool EnsureReady()
        {
            if (_epType != null) return true;

            // [VERIFY] ExtractionPoint type exists in decompiled Assembly-CSharp (ExtractionPoint.cs).
            _epType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); }
                    catch { return Array.Empty<Type>(); }
                })
                .FirstOrDefault(t => t != null && t.Name == "ExtractionPoint");

            return _epType != null;
        }

        public void ScanIfNeeded(bool force)
        {
            try
            {
                // [VERIFY] UnityEngine.Time.realtimeSinceStartup (UnityEngine.CoreModule).
                var t0 = Time.realtimeSinceStartup;
                var now = t0;
                if (!force && (now - _lastScanRealtime) < RescanCooldown) return;
                _lastScanRealtime = now;

                if (!EnsureReady()) return;

                // [VERIFY] UnityEngine.Object.FindObjectsOfType(Type) (UnityEngine.CoreModule).
                var found = UnityEngine.Object.FindObjectsOfType(_epType!);
                _cached.Clear();
                _cached.AddRange(found.OfType<Component>().Where(c => c != null));

                var dt = Time.realtimeSinceStartup - t0;
                if (_lastScanCount != _cached.Count)
                {
                    _lastScanCount = _cached.Count;
                    if (LogReady)
                        DebugLog?.Invoke($"[SCAN] count={_cached.Count} dt={dt:0.000}s");
                }
            }
            catch (Exception e)
            {
                _log.LogError($"ScanIfNeeded failed: {e}");
                if (LogReady)
                    DebugLog?.Invoke($"[SCAN][ERR] {e.GetType().Name}: {e.Message}");
            }
        }

        public Vector3 GetReferencePos()
        {
            // [VERIFY] UnityEngine.Camera.main and GameObject.FindWithTag (UnityEngine.CoreModule).
            try
            {
                if (Camera.main != null) return Camera.main.transform.position;
            }
            catch { }

            try
            {
                var p = GameObject.FindWithTag("Player");
                if (p != null) return p.transform.position;
            }
            catch { }

            return Vector3.zero;
        }

        public void MarkAllDiscovered(List<Component> allPoints)
        {
            int before = _discovered.Count;
            for (int i = 0; i < allPoints.Count; i++)
            {
                var ep = allPoints[i];
                if (!ep) continue;
                _discovered.Add(ep.GetInstanceID());
            }
            int added = _discovered.Count - before;
            if (added > 0)
            {
                if (LogReady)
                    DebugLog?.Invoke($"[DISCOVER] mark-all +{added} total={_discovered.Count}");
            }
        }

        public void UpdateDiscovered(Vector3 refPos, float radius)
        {
            UpdateDiscoveredDetailed(refPos, radius, null);
        }

        public int UpdateDiscoveredDetailed(Vector3 refPos, float radius, List<Component>? newly)
        {
            if (_cached.Count == 0) return 0;

            int before = _discovered.Count;
            for (int i = 0; i < _cached.Count; i++)
            {
                var ep = _cached[i];
                if (!ep) continue;
                var id = ep.GetInstanceID();
                if (_discovered.Contains(id)) continue;

                var d2 = (refPos - ep.transform.position).sqrMagnitude;
                if (d2 <= radius * radius)
                {
                    _discovered.Add(id);
                    if (newly != null) newly.Add(ep);
                }
            }
            int added = _discovered.Count - before;
            if (added > 0 && newly == null)
            {
                if (LogReady)
                    DebugLog?.Invoke($"[DISCOVER] +{added} total={_discovered.Count} radius={radius:0.0}");
            }
            return added;
        }

        public List<Component> FilterDiscovered(List<Component> allPoints)
        {
            var list = new List<Component>();
            for (int i = 0; i < allPoints.Count; i++)
            {
                var ep = allPoints[i];
                if (!ep) continue;
                if (_discovered.Contains(ep.GetInstanceID()))
                    list.Add(ep);
            }
            return list;
        }

        public Vector3 GetSpawnPos()
        {
            return _spawnPos ?? Vector3.zero;
        }

        public void CaptureSpawnPosIfNeeded(Vector3 refPos)
        {
            if (_spawnPos != null) return;
            if (refPos == Vector3.zero) return;
            _spawnPos = refPos;
            if (LogReady)
                DebugLog?.Invoke($"[SPAWN] pos={refPos}");
        }

        public List<Component> ScanAndGetAllPoints()
        {
            ScanIfNeeded(force: true);
            return _cached.Where(c => c != null).ToList();
        }

        public bool TryGetRoundAnchors(
            List<Component> allPoints,
            Vector3 spawnPos,
            out Component? firstAnchor,
            out Component? tailAnchor)
        {
            // Legacy API retained for compatibility; no longer mutates round anchors.
            return TryGetGlobalAnchorsNoCache(allPoints, spawnPos, out firstAnchor, out tailAnchor);
        }

        public bool TryGetGlobalAnchorsNoCache(
            List<Component> allPoints,
            Vector3 spawnPos,
            out Component? firstAnchor,
            out Component? tailAnchor)
        {
            firstAnchor = null;
            tailAnchor = null;
            if (allPoints == null || allPoints.Count == 0) return false;
            if (spawnPos == Vector3.zero) return false;

            var reachable = new List<(Component ep, int id, float spawnPath)>();
            for (int i = 0; i < allPoints.Count; i++)
            {
                var ep = allPoints[i];
                if (!ep) continue;
                if (!TryGetSpawnPathLength(ep, spawnPos, _spawnPathCache, _spawnPathInvalid, out var d))
                    continue;
                reachable.Add((ep, ep.GetInstanceID(), d));
            }

            if (reachable.Count == 0) return false;

            const float tieEpsilon = 0.0001f;
            (Component ep, int id, float spawnPath) PickByMinSpawn(List<(Component ep, int id, float spawnPath)> list)
            {
                var best = list[0];
                for (int i = 1; i < list.Count; i++)
                {
                    var cur = list[i];
                    if (cur.spawnPath + tieEpsilon < best.spawnPath
                        || (Mathf.Abs(cur.spawnPath - best.spawnPath) <= tieEpsilon && cur.id < best.id))
                    {
                        best = cur;
                    }
                }
                return best;
            }

            var firstPick = PickByMinSpawn(reachable);
            firstAnchor = firstPick.ep;

            var rest = new List<(Component ep, int id, float spawnPath)>();
            for (int i = 0; i < reachable.Count; i++)
            {
                if (reachable[i].id != firstPick.id)
                    rest.Add(reachable[i]);
            }

            if (rest.Count == 0) return true;

            var tailPick = PickByMinSpawn(rest);
            tailAnchor = tailPick.ep;
            return true;
        }

        public void ResetForNewRound()
        {
            _cached.Clear();
            _activatedIds.Clear();
            _discovered.Clear();
            _spawnPos = null;
            _spawnPathCache.Clear();
            _spawnPathInvalid.Clear();
            _edgePathCache.Clear();
            _edgePathInvalid.Clear();
            _firstAnchorId = null;
            _lastAnchorId = null;
            _lastScanRealtime = 0f;
            _lastScanCount = -1;
        }

        public void MarkActivated(Component ep)
        {
            if (ep == null) return;
            _activatedIds.Add(ep.GetInstanceID());
        }

        private bool IsMarkedActivated(Component ep)
        {
            if (ep == null) return false;
            return _activatedIds.Contains(ep.GetInstanceID());
        }

        public bool ReconcileActivatedMarks(List<Component> allPoints)
        {
            if (allPoints == null || allPoints.Count == 0)
            {
                if (_activatedIds.Count == 0) return false;
                _activatedIds.Clear();
                return true;
            }

            var liveActive = new HashSet<int>();
            for (int i = 0; i < allPoints.Count; i++)
            {
                var ep = allPoints[i];
                if (!ep) continue;
                var st = ReadStateName(ep);
                if (string.IsNullOrEmpty(st)) continue;
                if (IsIdleLikeState(st)) continue;
                if (IsCompletedLikeState(st)) continue;
                liveActive.Add(ep.GetInstanceID());
            }

            bool changed = false;
            if (_activatedIds.Count > 0)
            {
                var remove = _activatedIds.Where(id => !liveActive.Contains(id)).ToList();
                if (remove.Count > 0)
                {
                    for (int i = 0; i < remove.Count; i++)
                        _activatedIds.Remove(remove[i]);
                    changed = true;
                }
            }

            foreach (var id in liveActive)
            {
                if (_activatedIds.Add(id))
                    changed = true;
            }

            if (changed && LogReady)
                DebugLog?.Invoke($"[SYNC] activated marks reconciled cache={_activatedIds.Count} liveActive={liveActive.Count}");

            return changed;
        }

        public bool IsAnyExtractionPointActivating(List<Component> allPoints)
        {
            string _;
            int __;
            return TryGetActivatingInfo(allPoints, out _, out __);
        }

        public bool TryGetActivatingInfo(List<Component> allPoints, out string info, out int busyCount)
        {
            info = "";
            busyCount = 0;
            if (allPoints == null || allPoints.Count == 0) return false;

            for (int i = 0; i < allPoints.Count; i++)
            {
                var ep = allPoints[i];
                if (!ep) continue;

                try
                {
                    var t = ep.GetType();
                    var f = t.GetField("currentState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    var p = t.GetProperty("currentState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    object? v = null;
                    if (p != null) v = p.GetValue(ep, null);
                    if (v == null && f != null) v = f.GetValue(ep);
                    // [VERIFY] In decompiled ExtractionPoint.cs, `currentState` exists as an internal field (not a property).
                    if (v == null) continue;

                    var s = v.ToString() ?? "";
                    if (s.Length == 0) continue;

                    if (IsIdleLikeState(s)) continue;
                    if (IsCompletedLikeState(s)) continue;

                    busyCount++;
                    if (string.IsNullOrEmpty(info))
                        info = $"{ep.gameObject.name} state={s}";
                }
                catch
                {
                    // fail-safe: if can't read, consider it activating
                    if (busyCount == 0) busyCount = 1;
                    if (string.IsNullOrEmpty(info)) info = "state read failed";
                    return true;
                }
            }

            return busyCount > 0;
        }

        internal static bool IsIdleLikeState(string stateName)
        {
            if (string.IsNullOrEmpty(stateName)) return false;
            if (_idleCache.TryGetValue(stateName, out bool cached)) return cached;
            // [VERIFY] ExtractionPoint.State.Idle exists in decompiled ExtractionPoint.cs enum.
            bool res = stateName.IndexOf("Idle", StringComparison.OrdinalIgnoreCase) >= 0;
            _idleCache[stateName] = res;
            return res;
        }

        internal static bool IsCompletedLikeState(string stateName)
        {
            if (string.IsNullOrEmpty(stateName)) return false;
            if (_completeCache.TryGetValue(stateName, out bool cached)) return cached;
            var s = stateName.ToLowerInvariant();
            // [VERIFY] ExtractionPoint.State.Success / Complete exist in decompiled ExtractionPoint.cs enum.
            bool res = s.Contains("success")
                || s.Contains("complete")
                || s.Contains("submitted")
                || s.Contains("finish")
                || s.Contains("done");
            _completeCache[stateName] = res;
            return res;
        }

        public string ReadStateName(Component ep)
        {
            if (!ep) return "";
            try
            {
                var t = ep.GetType();
                var f = t.GetField("currentState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                var p = t.GetProperty("currentState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                object? v = null;
                if (p != null) v = p.GetValue(ep, null);
                if (v == null && f != null) v = f.GetValue(ep);
                // [VERIFY] In decompiled ExtractionPoint.cs, `currentState` exists as an internal field (not a property).
                if (v == null) return "";
                return v.ToString() ?? "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// 鏋勫缓鈥淔3 椤哄簭婵€娲诲垪琛ㄢ€濓細
        /// - 鍥哄畾绗竴涓細绂诲嚭鐢熺偣璺緞鏈€鐭殑鎻愬彇鐐?        /// - 鍥哄畾鏈€鍚庝竴涓細鍦ㄥ墿浣欑偣閲岋紝绂诲嚭鐢熺偣璺緞鏈€鐭?        /// - 涓棿锛氬叏鎺掑垪绌蜂妇锛屾寜鐐归棿 NavMesh 璺緞鎬婚暱搴︽眰鏈€浼?        /// - 璺宠繃宸插畬鎴?宸叉縺娲荤殑鐐?        /// - 涓嶄娇鐢ㄧ帺瀹朵綅缃紝涓嶄娇鐢ㄧ洿绾胯窛绂?        /// </summary>
        public List<Component> BuildStage1PlannedList(
            List<Component> allPoints,
            Vector3 spawnPos,
            bool skipActivated)
        {
            const float tieEpsilon = 0.0001f;

            var t0 = Time.realtimeSinceStartup;
            var result = new List<Component>();
            if (allPoints == null || allPoints.Count == 0) return result;
            if (spawnPos == Vector3.zero)
            {
                if (LogReady)
                    DebugLog?.Invoke("[PLAN][FAIL] spawnPos is zero; planning skipped");
                return result;
            }

            var candidates = new List<Component>(allPoints.Count);
            for (int i = 0; i < allPoints.Count; i++)
            {
                var ep = allPoints[i];
                if (!ep) continue;

                var st = ReadStateName(ep);
                if (IsCompletedLikeState(st))
                {
                    DebugLog?.Invoke($"[PLAN][SKIP] completed name={ep.gameObject.name} id={ep.GetInstanceID()} state={st}");
                    continue;
                }

                if (skipActivated && IsMarkedActivated(ep))
                {
                    DebugLog?.Invoke($"[PLAN][SKIP] activated name={ep.gameObject.name} id={ep.GetInstanceID()}");
                    continue;
                }

                candidates.Add(ep);
            }

            if (candidates.Count == 0) return result;

            var reachable = new List<(Component ep, int id, float spawnPath)>();
            for (int i = 0; i < candidates.Count; i++)
            {
                var ep = candidates[i];
                if (!TryGetSpawnPathLength(ep, spawnPos, _spawnPathCache, _spawnPathInvalid, out var d))
                    continue;
                reachable.Add((ep, ep.GetInstanceID(), d));
            }

            if (reachable.Count == 0)
            {
                if (LogReady)
                    DebugLog?.Invoke("[PLAN][FAIL] no spawn-reachable extraction points");
                return result;
            }

            (Component ep, int id, float spawnPath) PickByMinSpawn(List<(Component ep, int id, float spawnPath)> list)
            {
                var best = list[0];
                for (int i = 1; i < list.Count; i++)
                {
                    var cur = list[i];
                    if (cur.spawnPath + tieEpsilon < best.spawnPath
                        || (Mathf.Abs(cur.spawnPath - best.spawnPath) <= tieEpsilon && cur.id < best.id))
                    {
                        best = cur;
                    }
                }
                return best;
            }

            int FindIndexById(List<(Component ep, int id, float spawnPath)> list, int id)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].id == id) return i;
                }
                return -1;
            }

            var firstList = new List<(Component ep, int id, float spawnPath)>(reachable);
            int firstIdx = -1;
            if (_firstAnchorId.HasValue)
                firstIdx = FindIndexById(firstList, _firstAnchorId.Value);

            var firstPick = firstIdx >= 0 ? firstList[firstIdx] : PickByMinSpawn(firstList);
            var first = firstPick.ep;
            var firstId = firstPick.id;
            var firstSpawnPath = firstPick.spawnPath;
            if (!_firstAnchorId.HasValue)
                _firstAnchorId = firstId;

            var restForLast = new List<(Component ep, int id, float spawnPath)>();
            for (int i = 0; i < reachable.Count; i++)
            {
                if (reachable[i].id != firstId)
                    restForLast.Add(reachable[i]);
            }

            result.Add(first);
            if (restForLast.Count == 0)
            {
                if (LogReady)
                {
                    var dt1 = Time.realtimeSinceStartup - t0;
                    DebugLog?.Invoke($"[PLAN] all={allPoints.Count} eligible=1 first={first.gameObject.name} firstId={firstId} firstSpawnPath={firstSpawnPath:0.00} dt={dt1:0.000}s");
                    DebugLogPlanList(result, spawnPos, _spawnPathCache, _edgePathCache);
                }
                return result;
            }

            int lastIdx = -1;
            if (_lastAnchorId.HasValue)
                lastIdx = FindIndexById(restForLast, _lastAnchorId.Value);

            var lastPick = lastIdx >= 0 ? restForLast[lastIdx] : PickByMinSpawn(restForLast);
            var last = lastPick.ep;
            var lastId = lastPick.id;
            var lastSpawnPath = lastPick.spawnPath;
            if (!_lastAnchorId.HasValue)
                _lastAnchorId = lastId;

            var middleCandidates = new List<Component>();
            for (int i = 0; i < restForLast.Count; i++)
            {
                if (restForLast[i].id != lastId)
                    middleCandidates.Add(restForLast[i].ep);
            }

            List<Component>? bestMiddle = null;
            float bestTotal = float.MaxValue;

            bool IsBetterSameCostOrder(List<Component> current, List<Component>? best)
            {
                if (best == null) return true;
                int n = Math.Min(current.Count, best.Count);
                for (int i = 0; i < n; i++)
                {
                    int a = current[i].GetInstanceID();
                    int b = best[i].GetInstanceID();
                    if (a != b) return a < b;
                }
                return current.Count < best.Count;
            }

            if (middleCandidates.Count == 0)
            {
                if (TryGetEdgePathLength(first, last, _edgePathCache, _edgePathInvalid, out var directFl))
                {
                    bestTotal = directFl;
                    bestMiddle = new List<Component>();
                }
            }
            else
            {
                var used = new bool[middleCandidates.Count];
                var curOrder = new List<Component>(middleCandidates.Count);

                void Dfs(Component prev, float costSoFar)
                {
                    if (costSoFar > bestTotal + tieEpsilon) return;

                    if (curOrder.Count == middleCandidates.Count)
                    {
                        if (!TryGetEdgePathLength(prev, last, _edgePathCache, _edgePathInvalid, out var tailCost))
                            return;

                        var total = costSoFar + tailCost;
                        if (total + tieEpsilon < bestTotal
                            || (Mathf.Abs(total - bestTotal) <= tieEpsilon && IsBetterSameCostOrder(curOrder, bestMiddle)))
                        {
                            bestTotal = total;
                            bestMiddle = new List<Component>(curOrder);
                        }
                        return;
                    }

                    for (int i = 0; i < middleCandidates.Count; i++)
                    {
                        if (used[i]) continue;
                        var next = middleCandidates[i];

                        if (!TryGetEdgePathLength(prev, next, _edgePathCache, _edgePathInvalid, out var edgeCost))
                            continue;

                        used[i] = true;
                        curOrder.Add(next);
                        Dfs(next, costSoFar + edgeCost);
                        curOrder.RemoveAt(curOrder.Count - 1);
                        used[i] = false;
                    }
                }

                Dfs(first, 0f);
            }

            if (bestMiddle == null)
            {
                if (LogReady)
                    DebugLog?.Invoke("[PLAN][FAIL] no valid NavMesh permutation between first and last");
                return new List<Component>();
            }

            for (int i = 0; i < bestMiddle.Count; i++)
                result.Add(bestMiddle[i]);
            result.Add(last);

            var dt = Time.realtimeSinceStartup - t0;
            if (LogReady)
            {
                DebugLog?.Invoke($"[PLAN] all={allPoints.Count} eligible={result.Count} first={first.gameObject.name} firstId={firstId} last={last.gameObject.name} lastId={lastId} firstSpawnPath={firstSpawnPath:0.00} lastSpawnPath={lastSpawnPath:0.00} bestTotal={bestTotal:0.00} dt={dt:0.000}s");
                DebugLogPlanList(result, spawnPos, _spawnPathCache, _edgePathCache);
            }
            return result;
        }

        public List<Component> BuildPlanDiscoverAllFixedAnchors(
            List<Component> allPoints,
            Vector3 spawnPos,
            bool skipActivated)
        {
            const float tieEpsilon = 0.0001f;

            var t0 = Time.realtimeSinceStartup;
            var result = new List<Component>();
            if (allPoints == null || allPoints.Count == 0) return result;
            if (spawnPos == Vector3.zero)
            {
                if (LogReady)
                    DebugLog?.Invoke("[PLAN][ALL][FAIL] spawnPos is zero; planning skipped");
                return result;
            }

            if (!TryGetGlobalAnchorsNoCache(allPoints, spawnPos, out var globalFirst, out var globalTail) || globalFirst == null)
            {
                if (LogReady)
                    DebugLog?.Invoke("[PLAN][ALL][FAIL] no global anchors from all points");
                return result;
            }

            var candidates = new List<Component>(allPoints.Count);
            for (int i = 0; i < allPoints.Count; i++)
            {
                var ep = allPoints[i];
                if (!ep) continue;

                var st = ReadStateName(ep);
                if (IsCompletedLikeState(st))
                {
                    if (LogReady)
                        DebugLog?.Invoke($"[PLAN][SKIP] completed name={ep.gameObject.name} id={ep.GetInstanceID()} state={st}");
                    continue;
                }

                if (skipActivated && IsMarkedActivated(ep))
                {
                    if (LogReady)
                        DebugLog?.Invoke($"[PLAN][SKIP] activated name={ep.gameObject.name} id={ep.GetInstanceID()}");
                    continue;
                }

                candidates.Add(ep);
            }

            if (candidates.Count == 0) return result;

            var reachable = new List<(Component ep, int id, float spawnPath)>();
            for (int i = 0; i < candidates.Count; i++)
            {
                var ep = candidates[i];
                if (!TryGetSpawnPathLength(ep, spawnPos, _spawnPathCache, _spawnPathInvalid, out var d))
                    continue;
                reachable.Add((ep, ep.GetInstanceID(), d));
            }

            if (reachable.Count == 0)
            {
                if (LogReady)
                    DebugLog?.Invoke("[PLAN][ALL][FAIL] no spawn-reachable extraction points");
                return result;
            }

            (Component ep, int id, float spawnPath) PickByMinSpawn(List<(Component ep, int id, float spawnPath)> list)
            {
                var best = list[0];
                for (int i = 1; i < list.Count; i++)
                {
                    var cur = list[i];
                    if (cur.spawnPath + tieEpsilon < best.spawnPath
                        || (Mathf.Abs(cur.spawnPath - best.spawnPath) <= tieEpsilon && cur.id < best.id))
                    {
                        best = cur;
                    }
                }
                return best;
            }

            int globalFirstId = globalFirst.GetInstanceID();
            int globalTailId = globalTail != null ? globalTail.GetInstanceID() : int.MinValue;

            bool hasGlobalFirst = reachable.Any(x => x.id == globalFirstId);
            bool hasGlobalTail = globalTail != null && globalTailId != globalFirstId && reachable.Any(x => x.id == globalTailId);

            (Component ep, int id, float spawnPath)? firstEntry = null;
            if (hasGlobalFirst)
            {
                firstEntry = reachable.First(x => x.id == globalFirstId);
            }
            else
            {
                var pool = hasGlobalTail
                    ? reachable.Where(x => x.id != globalTailId).ToList()
                    : reachable;
                if (pool.Count == 0) pool = reachable;
                firstEntry = PickByMinSpawn(pool);
            }

            var first = firstEntry.Value.ep;
            int firstId = firstEntry.Value.id;
            result.Add(first);

            bool tailUsable = hasGlobalTail && globalTailId != firstId;
            var middleCandidates = new List<Component>();
            for (int i = 0; i < reachable.Count; i++)
            {
                var cur = reachable[i];
                if (cur.id == firstId) continue;
                if (tailUsable && cur.id == globalTailId) continue;
                middleCandidates.Add(cur.ep);
            }

            Component? tail = tailUsable ? reachable.First(x => x.id == globalTailId).ep : null;

            List<Component>? bestMiddle = null;
            float bestTotal = float.MaxValue;

            bool IsBetterSameCostOrder(List<Component> current, List<Component>? best)
            {
                if (best == null) return true;
                int n = Math.Min(current.Count, best.Count);
                for (int i = 0; i < n; i++)
                {
                    int a = current[i].GetInstanceID();
                    int b = best[i].GetInstanceID();
                    if (a != b) return a < b;
                }
                return current.Count < best.Count;
            }

            if (middleCandidates.Count == 0)
            {
                if (tail == null)
                {
                    bestTotal = 0f;
                    bestMiddle = new List<Component>();
                }
                else if (TryGetEdgePathLength(first, tail, _edgePathCache, _edgePathInvalid, out var directFl))
                {
                    bestTotal = directFl;
                    bestMiddle = new List<Component>();
                }
            }
            else
            {
                var used = new bool[middleCandidates.Count];
                var curOrder = new List<Component>(middleCandidates.Count);

                void Dfs(Component prev, float costSoFar)
                {
                    if (costSoFar > bestTotal + tieEpsilon) return;

                    if (curOrder.Count == middleCandidates.Count)
                    {
                        float total = costSoFar;
                        if (tail != null)
                        {
                            if (!TryGetEdgePathLength(prev, tail, _edgePathCache, _edgePathInvalid, out var tailCost))
                                return;
                            total += tailCost;
                        }

                        if (total + tieEpsilon < bestTotal
                            || (Mathf.Abs(total - bestTotal) <= tieEpsilon && IsBetterSameCostOrder(curOrder, bestMiddle)))
                        {
                            bestTotal = total;
                            bestMiddle = new List<Component>(curOrder);
                        }
                        return;
                    }

                    for (int i = 0; i < middleCandidates.Count; i++)
                    {
                        if (used[i]) continue;
                        var next = middleCandidates[i];

                        if (!TryGetEdgePathLength(prev, next, _edgePathCache, _edgePathInvalid, out var edgeCost))
                            continue;

                        used[i] = true;
                        curOrder.Add(next);
                        Dfs(next, costSoFar + edgeCost);
                        curOrder.RemoveAt(curOrder.Count - 1);
                        used[i] = false;
                    }
                }

                Dfs(first, 0f);
            }

            if (bestMiddle == null)
            {
                if (LogReady)
                    DebugLog?.Invoke("[PLAN][ALL][FAIL] no valid NavMesh permutation between fixed anchors");
                return new List<Component>();
            }

            for (int i = 0; i < bestMiddle.Count; i++)
                result.Add(bestMiddle[i]);
            if (tail != null)
                result.Add(tail);

            if (LogReady)
            {
                var ordered = string.Join(" -> ", result.Where(x => x != null).Select(x => x.gameObject.name));
                var tailName = tail != null ? tail.gameObject.name : "none";
                var dt = Time.realtimeSinceStartup - t0;
                DebugLog?.Invoke($"[PLAN][ALL] globalFirst={globalFirst.gameObject.name} globalTail={(globalTail != null ? globalTail.gameObject.name : "none")} first={first.gameObject.name} tail={tailName} ordered={ordered} bestTotal={bestTotal:0.00} dt={dt:0.000}s");
                DebugLogPlanList(result, spawnPos, _spawnPathCache, _edgePathCache);
            }

            return result;
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
                // Some map points are slightly off NavMesh; sample both ends first to avoid false unreachable.
                if (!TrySampleNavPoint(from, out var fromOnNav)) return false;
                if (!TrySampleNavPoint(to, out var toOnNav)) return false;

                var path = new UnityEngine.AI.NavMeshPath();
                if (!UnityEngine.AI.NavMesh.CalculatePath(fromOnNav, toOnNav, UnityEngine.AI.NavMesh.AllAreas, path))
                    return false;
                if (path.status != UnityEngine.AI.NavMeshPathStatus.PathComplete)
                    return false;

                var corners = path.corners;
                if (corners == null || corners.Length == 0)
                    return false;
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
            UnityEngine.AI.NavMeshHit hit;

            // Fast near sample, then broader fallback for uneven terrain / spawn offsets.
            if (UnityEngine.AI.NavMesh.SamplePosition(pos, out hit, 3f, UnityEngine.AI.NavMesh.AllAreas))
            {
                sampled = hit.position;
                return true;
            }

            if (UnityEngine.AI.NavMesh.SamplePosition(pos, out hit, 10f, UnityEngine.AI.NavMesh.AllAreas))
            {
                sampled = hit.position;
                return true;
            }

            return false;
        }

        private bool TryGetSpawnPathLength(
            Component ep,
            Vector3 spawnPos,
            Dictionary<int, float> cache,
            HashSet<int> invalid,
            out float length)
        {
            length = 0f;
            if (!ep) return false;

            int id = ep.GetInstanceID();
            if (cache.TryGetValue(id, out length)) return true;
            if (invalid.Contains(id)) return false;

            if (TryGetPathLength(spawnPos, ep.transform.position, out length))
            {
                cache[id] = length;
                return true;
            }

            invalid.Add(id);
            return false;
        }

        private bool TryGetEdgePathLength(
            Component from,
            Component to,
            Dictionary<long, float> cache,
            HashSet<long> invalid,
            out float length)
        {
            length = 0f;
            if (!from || !to) return false;

            int fromId = from.GetInstanceID();
            int toId = to.GetInstanceID();
            if (fromId == toId)
            {
                length = 0f;
                return true;
            }

            long key = MakeDirectedKey(fromId, toId);
            if (cache.TryGetValue(key, out length)) return true;
            if (invalid.Contains(key)) return false;

            if (TryGetPathLength(from.transform.position, to.transform.position, out length))
            {
                cache[key] = length;
                return true;
            }

            invalid.Add(key);
            return false;
        }

        private void DebugLogPlanList(
            List<Component> plan,
            Vector3 spawnPos,
            Dictionary<int, float> spawnPathCache,
            Dictionary<long, float> edgePathCache)
        {
            if (plan == null || plan.Count == 0) return;

            for (int i = 0; i < plan.Count; i++)
            {
                var ep = plan[i];
                if (!ep) continue;

                var id = ep.GetInstanceID();
                var st = ReadStateName(ep);
                var act = IsMarkedActivated(ep);
                var disc = _discovered.Contains(id);

                string legFrom;
                float legPath;

                if (i == 0)
                {
                    legFrom = "spawn";
                    if (!spawnPathCache.TryGetValue(id, out legPath))
                    {
                        if (!TryGetPathLength(spawnPos, ep.transform.position, out legPath)) legPath = -1f;
                    }
                }
                else
                {
                    var prev = plan[i - 1];
                    legFrom = prev ? prev.gameObject.name : "unknown";
                    long key = prev ? MakeDirectedKey(prev.GetInstanceID(), id) : 0;
                    if (!edgePathCache.TryGetValue(key, out legPath))
                    {
                        if (!(prev && TryGetPathLength(prev.transform.position, ep.transform.position, out legPath)))
                            legPath = -1f;
                    }
                }

                DebugLog?.Invoke($"[PLAN][{i}] name={ep.gameObject.name} legFrom={legFrom} legPath={legPath:0.00} discovered={disc} activated={act} state={st}");
            }
        }
    }
}











