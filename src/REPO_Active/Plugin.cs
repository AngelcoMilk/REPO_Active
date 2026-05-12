using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using REPO_Active.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace REPO_Active
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "angelcomilk.repo_active";
        public const string PluginName = "REPO_Active";
        public const string PluginVersion = "5.2.1";

        private const float RescanCooldown = 0.6f;
        private const bool SkipActivated = true;
        private const float AutoInterval = 5.0f;
        private const float DiscoverIntervalBase = 0.5f;
        private const float DiscoverInterval4To6 = 1.0f;
        private const float DiscoverInterval7To9 = 1.5f;
        private const float DiscoverInterval10To12 = 2.0f;
        private const float DiscoverIntervalMax = 3.0f;
        private const float DiscoverRadius = 20f;
        private const float AutoReadyBuffer = 30f;

        private ConfigEntry<bool> _autoActivate = null!;
        private ConfigEntry<KeyCode> _keyActivateNearest = null!;
        private ConfigEntry<bool> _discoverAllPoints = null!;

        private readonly HashSet<int> _deferredMarking = new HashSet<int>();
        private readonly List<PlayerAvatar> _playerCache = new List<PlayerAvatar>();
        private readonly List<Vector3> _playerLastPos = new List<Vector3>();

        private ExtractionPointScanner _scanner = null!;
        private Harmony? _harmony;

        private float _autoTimer;
        private float _discoverTimer;
        private float _autoReadyTime = -1f;
        private float _discoverReadyTime = -1f;
        private bool _autoPrimed;
        private int _playerCacheHash;
        private int _playerListEmptyStreak;
        private int _lastPlayerCount = -1;
        private float _lastBaseInterval = DiscoverIntervalBase;
        private int _lastHostPosCount = -1;
        private string _lastBusyInfo = "";
        private int _lastBusyCount = -1;
        private float _lastBusyLogTime = -1f;
        private bool? _lastTailHoldState;

        internal static Plugin? Instance { get; private set; }

        internal ExtractionPointStateTracker StateTracker { get; } = new ExtractionPointStateTracker();

        private void Awake()
        {
            _autoActivate = Config.Bind("Auto", "AutoActivate", false, "Auto activate when idle.");
            _keyActivateNearest = Config.Bind("Keybinds", "ActivateNearest", KeyCode.F3, "Press to activate next extraction point.");
            _discoverAllPoints = Config.Bind("Discovery", "DiscoverAllPoints", false, "If true, treat all extraction points as discovered.");

            Instance = this;
            _scanner = new ExtractionPointScanner(RescanCooldown, StateTracker);

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            SceneManager.sceneLoaded += OnSceneLoaded;
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded for the strong-typed REPO build.");
        }

        private void OnDestroy()
        {
            try
            {
                SceneManager.sceneLoaded -= OnSceneLoaded;
            }
            catch
            {
            }

            try
            {
                _harmony?.UnpatchSelf();
                _harmony = null;
            }
            catch
            {
            }

            if (ReferenceEquals(Instance, this))
            {
                Instance = null;
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(_keyActivateNearest.Value) && !_autoActivate.Value)
            {
                ActivateNearest();
            }

            _discoverTimer += Time.deltaTime;
            float interval = GetDiscoveryInterval();
            if (_discoverTimer >= interval)
            {
                _discoverTimer = 0f;
                DiscoveryTick();
            }

            if (!_autoActivate.Value)
            {
                return;
            }

            if (_autoReadyTime < 0f)
            {
                var all = _scanner.ScanAndGetAllPoints();
                var refPos = _scanner.GetReferencePos();
                if (all.Count > 0 && refPos != Vector3.zero)
                {
                    _scanner.CaptureSpawnPosIfNeeded(refPos);
                    _autoReadyTime = Time.realtimeSinceStartup;
                    _autoTimer = 0f;
                }

                return;
            }

            if ((Time.realtimeSinceStartup - _autoReadyTime) < AutoReadyBuffer)
            {
                return;
            }

            if (!_autoPrimed)
            {
                PrimeFirstPointIfAlreadyActivated();
                _autoPrimed = true;
            }

            _autoTimer += Time.deltaTime;
            if (_autoTimer >= AutoInterval)
            {
                _autoTimer = 0f;
                ActivateNearest();
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            _autoReadyTime = -1f;
            _discoverReadyTime = Time.realtimeSinceStartup;
            _autoPrimed = false;
            _autoTimer = 0f;
            _discoverTimer = 0f;
            _lastTailHoldState = null;

            StateTracker.ResetForNewRound();
            _scanner.ResetForNewRound();
            _deferredMarking.Clear();

            _lastPlayerCount = -1;
            _lastBaseInterval = DiscoverIntervalBase;
            RefreshDiscoveryIntervalFromCurrentPlayerCount("scene init", invalidateCache: true);
        }

        private void DiscoveryTick()
        {
            var all = _scanner.ScanAndGetAllPoints();
            var refPos = _scanner.GetReferencePos();
            _scanner.CaptureSpawnPosIfNeeded(refPos);

            if (all.Count == 0)
            {
                return;
            }

            if (_discoverAllPoints.Value)
            {
                _scanner.MarkAllDiscovered(all);
                return;
            }

            if (_discoverReadyTime < 0f)
            {
                _discoverReadyTime = Time.realtimeSinceStartup;
            }

            if ((Time.realtimeSinceStartup - _discoverReadyTime) < AutoReadyBuffer)
            {
                return;
            }

            if (_scanner.DiscoveredCount >= all.Count)
            {
                return;
            }

            var allPositions = TryGetAllPlayerPositionsHost();
            if (allPositions.Count == 0)
            {
                _playerListEmptyStreak++;
                _scanner.UpdateDiscovered(refPos, DiscoverRadius);
                return;
            }

            _playerListEmptyStreak = 0;
            for (int i = 0; i < allPositions.Count; i++)
            {
                _scanner.UpdateDiscovered(allPositions[i], DiscoverRadius);
            }

            if (allPositions.Count != _lastHostPosCount)
            {
                _lastHostPosCount = allPositions.Count;
            }
        }

        private void ActivateNearest()
        {
            var allPoints = _scanner.ScanAndGetAllPoints();
            if (allPoints.Count == 0)
            {
                return;
            }

            if (_scanner.TryGetActivatingInfo(allPoints, out var busyInfo, out var busyCount))
            {
                float now = Time.realtimeSinceStartup;
                if (busyInfo != _lastBusyInfo || busyCount != _lastBusyCount || (now - _lastBusyLogTime) > 2f)
                {
                    _lastBusyInfo = busyInfo;
                    _lastBusyCount = busyCount;
                    _lastBusyLogTime = now;
                }

                return;
            }

            var startPos = _scanner.GetReferencePos();
            _scanner.CaptureSpawnPosIfNeeded(startPos);
            var spawnPos = _scanner.GetSpawnPos();

            if (_discoverAllPoints.Value)
            {
                _scanner.MarkAllDiscovered(allPoints);
            }
            else
            {
                UpdateDiscoveredFromCachedPlayers(startPos);
            }

            var eligible = _discoverAllPoints.Value ? allPoints : _scanner.FilterDiscovered(allPoints);
            var planInput = BuildPlanInputWithoutEarlyTail(allPoints, eligible, spawnPos);
            var plan = _discoverAllPoints.Value
                ? _scanner.BuildPlanDiscoverAllFixedAnchors(allPoints, spawnPos, SkipActivated)
                : _scanner.BuildStage1PlannedList(planInput, spawnPos, SkipActivated);

            if (plan.Count == 0)
            {
                return;
            }

            var next = plan[0];
            if (ShouldHoldTailPointActivation(allPoints, spawnPos, plan, next, out _))
            {
                return;
            }

            try
            {
                next.OnClick();
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"ExtractionPoint.OnClick failed: {ex.GetType().Name}");
                return;
            }

            var state = _scanner.ReadState(next);
            if (state.HasValue &&
                !ExtractionPointScanner.IsIdleLikeState(state) &&
                !ExtractionPointScanner.IsCompletedLikeState(state))
            {
                _scanner.MarkActivated(next);
            }
            else
            {
                TryDeferredMarkActivated(next);
            }
        }

        private List<ExtractionPoint> BuildPlanInputWithoutEarlyTail(
            List<ExtractionPoint> allPoints,
            List<ExtractionPoint> eligible,
            Vector3 spawnPos)
        {
            if (_discoverAllPoints.Value ||
                eligible.Count <= 1 ||
                !_scanner.TryGetGlobalAnchorsNoCache(allPoints, spawnPos, out _, out var globalTail) ||
                globalTail == null ||
                !globalTail)
            {
                return eligible;
            }

            int tailId = globalTail.GetInstanceID();
            bool hasTail = eligible.Any(point => point && point.GetInstanceID() == tailId);
            if (!hasTail)
            {
                return eligible;
            }

            var filtered = new List<ExtractionPoint>();
            int unfinishedNonTail = 0;
            for (int i = 0; i < eligible.Count; i++)
            {
                var point = eligible[i];
                if (!point || point.GetInstanceID() == tailId)
                {
                    continue;
                }

                filtered.Add(point);
                if (!ExtractionPointScanner.IsCompletedLikeState(_scanner.ReadState(point)))
                {
                    unfinishedNonTail++;
                }
            }

            return filtered.Count > 0 && unfinishedNonTail > 0 ? filtered : eligible;
        }

        private void TryDeferredMarkActivated(ExtractionPoint point)
        {
            if (!point)
            {
                return;
            }

            int id = point.GetInstanceID();
            if (_deferredMarking.Add(id))
            {
                StartCoroutine(DeferredMarkActivated(point, id));
            }
        }

        private IEnumerator DeferredMarkActivated(ExtractionPoint point, int id)
        {
            try
            {
                if (!point)
                {
                    yield break;
                }

                float[] waits = { 0.15f, 0.25f, 0.35f, 0.35f, 0.40f };
                for (int i = 0; i < waits.Length; i++)
                {
                    yield return new WaitForSeconds(waits[i]);
                    if (!point)
                    {
                        yield break;
                    }

                    var state = _scanner.ReadState(point);
                    if (state.HasValue &&
                        !ExtractionPointScanner.IsIdleLikeState(state) &&
                        !ExtractionPointScanner.IsCompletedLikeState(state))
                    {
                        _scanner.MarkActivated(point);
                        yield break;
                    }

                    if (i == 0)
                    {
                        yield return null;
                        if (!point)
                        {
                            yield break;
                        }

                        state = _scanner.ReadState(point);
                        if (state.HasValue &&
                            !ExtractionPointScanner.IsIdleLikeState(state) &&
                            !ExtractionPointScanner.IsCompletedLikeState(state))
                        {
                            _scanner.MarkActivated(point);
                            yield break;
                        }
                    }
                }
            }
            finally
            {
                _deferredMarking.Remove(id);
            }
        }

        private void PrimeFirstPointIfAlreadyActivated()
        {
            var all = _scanner.ScanAndGetAllPoints();
            if (all.Count == 0)
            {
                return;
            }

            var refPos = _scanner.GetReferencePos();
            _scanner.CaptureSpawnPosIfNeeded(refPos);

            if (_discoverAllPoints.Value)
            {
                _scanner.MarkAllDiscovered(all);
            }
            else
            {
                _scanner.UpdateDiscovered(refPos, DiscoverRadius);
            }

            var eligible = _discoverAllPoints.Value ? all : _scanner.FilterDiscovered(all);
            var spawnPos = _scanner.GetSpawnPos();
            var plan = _discoverAllPoints.Value
                ? _scanner.BuildPlanDiscoverAllFixedAnchors(all, spawnPos, SkipActivated)
                : _scanner.BuildStage1PlannedList(eligible, spawnPos, SkipActivated);

            if (plan.Count == 0)
            {
                return;
            }

            var state = _scanner.ReadState(plan[0]);
            if (state.HasValue && !ExtractionPointScanner.IsIdleLikeState(state))
            {
                _scanner.MarkActivated(plan[0]);
            }
        }

        private List<Vector3> TryGetAllPlayerPositionsHost()
        {
            var positions = new List<Vector3>();

            try
            {
                if (!SemiFunc.IsMasterClientOrSingleplayer())
                {
                    return positions;
                }

                var players = SemiFunc.PlayerGetList();
                if (players == null || players.Count == 0)
                {
                    return positions;
                }

                int hash = 17;
                var livePlayers = new List<PlayerAvatar>(players.Count);
                for (int i = 0; i < players.Count; i++)
                {
                    var player = players[i];
                    if (!player || !player.transform)
                    {
                        continue;
                    }

                    livePlayers.Add(player);
                    hash = unchecked(hash * 31 + player.GetInstanceID());
                }

                if (livePlayers.Count == 0)
                {
                    return positions;
                }

                bool listChanged = hash != _playerCacheHash || _playerCache.Count != livePlayers.Count;
                if (listChanged)
                {
                    _playerCacheHash = hash;
                    _playerCache.Clear();
                    _playerCache.AddRange(livePlayers);
                    _playerLastPos.Clear();
                    for (int i = 0; i < _playerCache.Count; i++)
                    {
                        _playerLastPos.Add(_playerCache[i].transform.position);
                    }
                }

                for (int i = 0; i < _playerCache.Count; i++)
                {
                    var player = _playerCache[i];
                    if (!player || !player.transform)
                    {
                        ClearPlayerCache();
                        return positions;
                    }

                    var pos = player.transform.position;
                    positions.Add(pos);
                    _playerLastPos[i] = pos;
                }
            }
            catch
            {
                return positions;
            }

            return positions;
        }

        private void UpdateDiscoveredFromCachedPlayers(Vector3 fallbackPos)
        {
            if (_playerCache.Count > 0 && _playerLastPos.Count == _playerCache.Count)
            {
                for (int i = 0; i < _playerLastPos.Count; i++)
                {
                    _scanner.UpdateDiscovered(_playerLastPos[i], DiscoverRadius);
                }

                return;
            }

            _scanner.UpdateDiscovered(fallbackPos, DiscoverRadius);
        }

        private bool ShouldHoldTailPointActivation(
            List<ExtractionPoint> allPoints,
            Vector3 spawnPos,
            List<ExtractionPoint> plan,
            ExtractionPoint next,
            out string reason)
        {
            reason = "";

            if (_discoverAllPoints.Value || !next)
            {
                return false;
            }

            if (!_scanner.TryGetGlobalAnchorsNoCache(allPoints, spawnPos, out var firstAnchor, out var tailAnchor) ||
                tailAnchor == null ||
                !tailAnchor)
            {
                return false;
            }

            int firstId = firstAnchor != null && firstAnchor ? firstAnchor.GetInstanceID() : int.MinValue;
            int tailId = tailAnchor.GetInstanceID();
            bool isTailTarget = next.GetInstanceID() == tailId;

            var pendingNames = new List<string>();
            for (int i = 0; i < allPoints.Count; i++)
            {
                var point = allPoints[i];
                if (!point)
                {
                    continue;
                }

                int id = point.GetInstanceID();
                if (id == firstId || id == tailId)
                {
                    continue;
                }

                var state = _scanner.ReadState(point);
                if (ExtractionPointScanner.IsCompletedLikeState(state))
                {
                    continue;
                }

                pendingNames.Add($"{point.gameObject.name}#{point.GetInstanceID()}({_scanner.ReadStateName(point)})");
            }

            bool shouldHold = isTailTarget && pendingNames.Count > 0;
            _lastTailHoldState = shouldHold;

            if (!shouldHold)
            {
                return false;
            }

            string queue = plan.Count == 0
                ? "-"
                : string.Join(" -> ", plan.Where(point => point).Select(point => $"{point.gameObject.name}#{point.GetInstanceID()}"));

            reason = $"tail point locked, pendingOthers={pendingNames.Count}, pending=[{string.Join(", ", pendingNames)}], queue={queue}";
            return true;
        }

        private void HandleRoomPopulationChangedEvent(string reason)
        {
            RefreshDiscoveryIntervalFromCurrentPlayerCount(reason, invalidateCache: true);
        }

        private void RefreshDiscoveryIntervalFromCurrentPlayerCount(string reason, bool invalidateCache)
        {
            int players = GetPlayerCountHost();
            if (players < 1)
            {
                players = 1;
            }

            if (players != _lastPlayerCount)
            {
                _lastPlayerCount = players;
                _lastBaseInterval = ComputeDiscoveryInterval(players);
            }

            if (invalidateCache)
            {
                ClearPlayerCache();
            }
        }

        private void ClearPlayerCache()
        {
            _playerCache.Clear();
            _playerLastPos.Clear();
            _playerCacheHash = 0;
        }

        private float GetDiscoveryInterval()
        {
            if (_discoverAllPoints.Value)
            {
                return DiscoverIntervalBase;
            }

            if (_lastPlayerCount < 1)
            {
                RefreshDiscoveryIntervalFromCurrentPlayerCount("lazy init", invalidateCache: false);
            }

            float baseInterval = _lastBaseInterval;
            if (_playerListEmptyStreak <= 0)
            {
                return baseInterval;
            }

            float multiplier = 1f + Math.Min(_playerListEmptyStreak, 10) * 0.2f;
            float interval = baseInterval * multiplier;
            return interval > DiscoverIntervalMax ? DiscoverIntervalMax : interval;
        }

        private static float ComputeDiscoveryInterval(int players)
        {
            if (players <= 3)
            {
                return DiscoverIntervalBase;
            }

            if (players <= 6)
            {
                return DiscoverInterval4To6;
            }

            if (players <= 9)
            {
                return DiscoverInterval7To9;
            }

            return DiscoverInterval10To12;
        }

        private static int GetPlayerCountHost()
        {
            try
            {
                if (!SemiFunc.IsMasterClientOrSingleplayer())
                {
                    return 1;
                }

                var players = SemiFunc.PlayerGetList();
                return players != null && players.Count > 0 ? players.Count : 1;
            }
            catch
            {
                return 1;
            }
        }

        internal static void NotifyRoomPopulationChanged(string reason)
        {
            Instance?.HandleRoomPopulationChangedEvent(reason);
        }

        internal static void NotifyExtractionPointState(ExtractionPoint point, ExtractionPoint.State state)
        {
            Instance?.StateTracker.Record(point, state);
        }
    }

    [HarmonyPatch(typeof(NetworkManager), nameof(NetworkManager.OnPlayerEnteredRoom))]
    internal static class NetworkManagerOnPlayerEnteredRoomPatch
    {
        private static void Postfix()
        {
            Plugin.NotifyRoomPopulationChanged("player entered");
        }
    }

    [HarmonyPatch(typeof(NetworkManager), nameof(NetworkManager.OnPlayerLeftRoom))]
    internal static class NetworkManagerOnPlayerLeftRoomPatch
    {
        private static void Postfix()
        {
            Plugin.NotifyRoomPopulationChanged("player left");
        }
    }

    [HarmonyPatch(typeof(ExtractionPoint), nameof(ExtractionPoint.StateSetRPC))]
    internal static class ExtractionPointStateSetRpcPatch
    {
        private static void Postfix(ExtractionPoint __instance, ExtractionPoint.State state)
        {
            Plugin.NotifyExtractionPointState(__instance, state);
        }
    }
}
