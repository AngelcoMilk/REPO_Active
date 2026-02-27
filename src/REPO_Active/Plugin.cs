using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using REPO_Active.Runtime;
using REPO_Active.Reflection;
using REPO_Active.Debug;

namespace REPO_Active
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "angelcomilk.repo_active";
        public const string PluginName = "REPO_Active";
        public const string PluginVersion = "5.1.0";

        // ---- config ----
        private ConfigEntry<bool> _autoActivate = null!;
        private ConfigEntry<KeyCode> _keyActivateNearest = null!;
        private ConfigEntry<bool> _discoverAllPoints = null!;
        private ConfigEntry<bool> _enableDebugLog = null!;

        private ExtractionPointScanner _scanner = null!;
        private ExtractionPointInvoker _invoker = null!;
        private ModLogger? _dbg;
        private Harmony? _harmony;
        private static Plugin? _instance;

        private float _autoTimer = 0f;
        private float _discoverTimer = 0f;
        private float _autoReadyTime = -1f;
        private float _discoverReadyTime = -1f;
        private bool _autoPrimed = false;
        private bool _logReady = false;
        private readonly HashSet<int> _deferredMarking = new HashSet<int>();

        // Player list cache for discovery (host only)
        private readonly List<Component> _playerCache = new List<Component>();
        private readonly List<Vector3> _playerLastPos = new List<Vector3>();
        private int _playerCacheHash = 0;
        private int _playerListEmptyStreak = 0;
        private int _lastPlayerCount = -1;
        private float _lastBaseInterval = DISCOVER_INTERVAL_BASE;
        private int _lastHostPosCount = -1;
        private string _lastDiscoverReason = "";
        private int _lastDiscoverAll = -1;
        private int _lastDiscoverCount = -1;
        private int _lastAutoBufferBucket = int.MinValue;
        private int _lastDiscoverBufferBucket = int.MinValue;
        private string _lastBusyInfo = "";
        private int _lastBusyCount = -1;
        private float _lastBusyLogTime = -1f;
        private bool? _lastTailHoldState = null;

        private const float RESCAN_COOLDOWN = 0.6f;
        private const bool SKIP_ACTIVATED = true;
        private const float AUTO_INTERVAL = 5.0f;
        private const float DISCOVER_INTERVAL_BASE = 0.5f;
        private const float DISCOVER_INTERVAL_4_6 = 1.0f;
        private const float DISCOVER_INTERVAL_7_9 = 1.5f;
        private const float DISCOVER_INTERVAL_10_12 = 2.0f;
        private const float DISCOVER_INTERVAL_MAX = 3.0f;
        private const float DISCOVER_RADIUS = 20f;
        private const float AUTO_READY_BUFFER = 30f;

        private void Awake()
        {
            _autoActivate = Config.Bind("Auto", "AutoActivate", false, "Auto activate when idle.");
            _keyActivateNearest = Config.Bind("Keybinds", "ActivateNearest", KeyCode.F3, "Press to activate next extraction point (uses OnClick via reflection)." );
            _discoverAllPoints = Config.Bind("Discovery", "DiscoverAllPoints", false, "If true, treat all extraction points as discovered.");
            _enableDebugLog = Config.Bind("Debug", "EnableDebugLog", false, "Enable detailed mod logs in BepInEx\\config\\REPO_Active\\logs.");

            _invoker = new ExtractionPointInvoker(Logger);
            _scanner = new ExtractionPointScanner(Logger, RESCAN_COOLDOWN);
            _dbg = new ModLogger(Logger, _enableDebugLog.Value);
            _scanner.DebugLog = _dbg.Log;

            _instance = this;
            InstallEventPatches();

            SceneManager.sceneLoaded += OnSceneLoaded;
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded. (OnClick reflection activation)");
            _dbg.Log($"[INIT] {PluginName} {PluginVersion} Auto={_autoActivate.Value} DiscoverAll={_discoverAllPoints.Value}");
        }

        private void OnDestroy()
        {
            try { SceneManager.sceneLoaded -= OnSceneLoaded; } catch { }
            try { UninstallEventPatches(); } catch { }
            if (ReferenceEquals(_instance, this)) _instance = null;
        }


        private void Update()
        {
            if (_dbg != null) _dbg.Enabled = _enableDebugLog.Value;
            _scanner.LogReady = _logReady;

            // manual
            // [VERIFY] UnityEngine.Input.GetKeyDown (UnityEngine.InputLegacyModule).
            if (Input.GetKeyDown(_keyActivateNearest.Value))
            {
                if (_autoActivate.Value)
                {
                    _dbg?.Log("[INPUT] F3 ignored (auto mode enabled)");
                }
                else
                {
                    _dbg?.Log("[INPUT] F3 pressed");
                    ActivateNearest();
                }
            }


            // discovery polling
            // [VERIFY] UnityEngine.Time.deltaTime (UnityEngine.CoreModule).
            _discoverTimer += Time.deltaTime;
            float interval = GetDiscoveryInterval();
            if (_discoverTimer >= interval)
            {
                _discoverTimer = 0f;
                DiscoveryTick();
            }

            // auto mode
            if (_autoActivate.Value)
            {
                if (_autoReadyTime < 0f)
                {
                    if (_scanner.EnsureReady())
                    {
                        var all = _scanner.ScanAndGetAllPoints();
                        var refPos = _scanner.GetReferencePos();
                        // [VERIFY] UnityEngine.Time.realtimeSinceStartup (UnityEngine.CoreModule).
                        if (all.Count > 0 && refPos != Vector3.zero)
                        {
                            _scanner.CaptureSpawnPosIfNeeded(refPos);
                            _autoReadyTime = Time.realtimeSinceStartup;
                            _autoTimer = 0f;
                            _dbg?.Log($"[AUTO] primed count={all.Count} refPos={refPos}");
                        }
                    }
                }
                else
                {
                    // wait for services-ready buffer before allowing auto activation
                    if ((Time.realtimeSinceStartup - _autoReadyTime) >= AUTO_READY_BUFFER)
                    {
                        _lastAutoBufferBucket = int.MinValue;
                        // Prime: if the first planned point already activated, mark it once
                        if (!_autoPrimed)
                        {
                            PrimeFirstPointIfAlreadyActivated();
                            _autoPrimed = true;
                            _dbg?.Log("[AUTO] primed-first");
                        }

                        _autoTimer += Time.deltaTime;
                        if (_autoTimer >= AUTO_INTERVAL)
                        {
                            _autoTimer = 0f;
                            _dbg?.Log("[AUTO] tick");
                            AutoActivateIfIdle();
                        }
                    }
                    else
                    {
                        var left = AUTO_READY_BUFFER - (Time.realtimeSinceStartup - _autoReadyTime);
                        if (left > 0f)
                        {
                            int bucket = (int)(left / 5f);
                            if (bucket != _lastAutoBufferBucket)
                            {
                                _lastAutoBufferBucket = bucket;
                                _dbg?.Log($"[AUTO] buffer wait {left:0.0}s");
                            }
                        }
                    }
                }
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // Reset per-round timers so auto mode re-enters its buffer on every map load
            // [VERIFY] UnityEngine.SceneManagement.SceneManager/Scene (UnityEngine.CoreModule).
            _autoReadyTime = -1f;
            _discoverReadyTime = Time.realtimeSinceStartup;
            _autoPrimed = false;
            _autoTimer = 0f;
            _discoverTimer = 0f;
            _lastTailHoldState = null;
            _scanner.ResetForNewRound();
            _logReady = false;
            _lastPlayerCount = -1;
            _lastBaseInterval = DISCOVER_INTERVAL_BASE;
            RefreshDiscoveryIntervalFromCurrentPlayerCount("scene init", invalidateCache: true);
            _dbg?.Log("[SCENE] new scene loaded, reset timers");
        }

        private void DiscoveryTick()
        {
            if (!_scanner.EnsureReady()) return;

            // Always rescan on discovery tick to keep cache fresh even while points are activating
            var all = _scanner.ScanAndGetAllPoints();
            var refPos = _scanner.GetReferencePos();
            _scanner.CaptureSpawnPosIfNeeded(refPos);
            if (!_logReady && all.Count > 0)
            {
                _logReady = true;
                _scanner.LogReady = true;
                _dbg?.Log("[LOG] ready: ExtractionPoint detected");
            }
            if (all.Count == 0)
            {
                if (_logReady)
                    LogDiscoverState("skip: no EP", 0, 0);
                return;
            }

            if (_discoverAllPoints.Value)
            {
                _scanner.MarkAllDiscovered(all);
            }
            else
            {
                // Bind discovery to the same services-ready buffer; avoid early empty lists.
                if (_discoverReadyTime < 0f) _discoverReadyTime = Time.realtimeSinceStartup;
                if ((Time.realtimeSinceStartup - _discoverReadyTime) < AUTO_READY_BUFFER)
                {
                    var left = AUTO_READY_BUFFER - (Time.realtimeSinceStartup - _discoverReadyTime);
                    int bucket = (int)(left / 5f);
                    if (bucket != _lastDiscoverBufferBucket)
                    {
                        _lastDiscoverBufferBucket = bucket;
                        LogDiscoverState("buffer wait", all.Count, _scanner.DiscoveredCount);
                    }
                    return;
                }
                _lastDiscoverBufferBucket = int.MinValue;

                if (_scanner.DiscoveredCount >= all.Count)
                {
                    LogDiscoverState("skip: all discovered", all.Count, _scanner.DiscoveredCount);
                    return;
                }
                var allPos = TryGetAllPlayerPositionsHost();
                var spawnPos = _scanner.GetSpawnPos();
                if (allPos.Count == 0)
                {
                    _playerListEmptyStreak++;
                    LogDiscoverState("fallback: PlayerList not ready -> local", all.Count, _scanner.DiscoveredCount);

                    var newlyLocal = new List<Component>();
                    int addedLocal = _scanner.UpdateDiscoveredDetailed(refPos, DISCOVER_RADIUS, newlyLocal);
                    if (addedLocal > 0 && _dbg != null)
                    {
                        _dbg.Log($"[DISCOVER] by local pos={refPos} +{addedLocal}");
                        for (int j = 0; j < newlyLocal.Count; j++)
                        {
                            var ep = newlyLocal[j];
                            if (!ep) continue;
                            var epPos = ep.transform.position;
                            var dp = Vector3.Distance(refPos, epPos);
                            string ds = spawnPos == Vector3.zero ? "na" : Vector3.Distance(spawnPos, epPos).ToString("0.00");
                            _dbg.Log($"[DISCOVER+] ep={ep.gameObject.name} epPos={epPos} distToPlayer={dp:0.00} distToSpawn={ds}");
                        }
                    }
                }
                else
                {
                    _playerListEmptyStreak = 0;
                    for (int i = 0; i < allPos.Count; i++)
                    {
                        var newly = new List<Component>();
                        int added = _scanner.UpdateDiscoveredDetailed(allPos[i], DISCOVER_RADIUS, newly);
                        if (added > 0 && _dbg != null)
                        {
                            _dbg.Log($"[DISCOVER] by player#{i} pos={allPos[i]} +{added}");
                            for (int j = 0; j < newly.Count; j++)
                            {
                                var ep = newly[j];
                                if (!ep) continue;
                                var epPos = ep.transform.position;
                                var dp = Vector3.Distance(allPos[i], epPos);
                                string ds = spawnPos == Vector3.zero ? "na" : Vector3.Distance(spawnPos, epPos).ToString("0.00");
                                _dbg.Log($"[DISCOVER+] ep={ep.gameObject.name} epPos={epPos} distToPlayer={dp:0.00} distToSpawn={ds}");
                            }
                        }
                    }
                    if (allPos.Count != _lastHostPosCount)
                    {
                        _lastHostPosCount = allPos.Count;
                        _dbg?.Log($"[DISCOVER] hostPos count={allPos.Count}");
                    }
                    LogDiscoverState("update: positions", all.Count, _scanner.DiscoveredCount);
                }
            }
        }

        private void AutoActivateIfIdle()
        {
            if (!_scanner.EnsureReady())
            {
                _dbg?.Log("[AUTO] EnsureReady=false");
                return;
            }
            ActivateNearest();
        }

        private void ActivateNearest()
        {
            if (!_scanner.EnsureReady())
            {
                Logger.LogWarning("ExtractionPoint type not found yet.");
                _dbg?.Log("[F3] EnsureReady=false");
                return;
            }

            var allPoints = _scanner.ScanAndGetAllPoints();
            _dbg?.Log($"[F3] allPoints={allPoints.Count}");

            // 1) 必须没有任何提取点处于激活中
            string busyInfo;
            int busyCount;
            if (_scanner.TryGetActivatingInfo(allPoints, out busyInfo, out busyCount))
            {
                Logger.LogWarning("有提取点正在激活中，F3 忽略");
                if (_dbg != null)
                {
                    var now = Time.realtimeSinceStartup;
                    if (busyInfo != _lastBusyInfo || busyCount != _lastBusyCount || (now - _lastBusyLogTime) > 2f)
                    {
                        _lastBusyInfo = busyInfo;
                        _lastBusyCount = busyCount;
                        _lastBusyLogTime = now;
                        _dbg.Log($"[F3] blocked: activating busy={busyCount} {busyInfo}");
                    }
                }
                return;
            }

            // 2) spawnPos + startPos
            var startPos = _scanner.GetReferencePos();
            _scanner.CaptureSpawnPosIfNeeded(startPos);
            var spawnPos = _scanner.GetSpawnPos();
            _dbg?.Log($"[F3] startPos={startPos} spawnPos={spawnPos}");

            // discovery filter
            if (_discoverAllPoints.Value)
                _scanner.MarkAllDiscovered(allPoints);
            else
                UpdateDiscoveredFromCachedPlayers(startPos);

            var eligible = _discoverAllPoints.Value ? allPoints : _scanner.FilterDiscovered(allPoints);
            _dbg?.Log($"[F3] eligible={eligible.Count} discoverAll={_discoverAllPoints.Value}");

            // 4) 构建规划列表
            var planInput = eligible;
            if (!_discoverAllPoints.Value
                && eligible.Count > 1
                && _scanner.TryGetGlobalAnchorsNoCache(allPoints, spawnPos, out _, out var globalTail)
                && globalTail != null)
            {
                int tailId = globalTail.GetInstanceID();
                bool hasTail = false;
                for (int i = 0; i < eligible.Count; i++)
                {
                    if (eligible[i] && eligible[i].GetInstanceID() == tailId)
                    {
                        hasTail = true;
                        break;
                    }
                }

                if (hasTail)
                {
                    var filtered = new List<Component>();
                    int unfinishedNonTail = 0;
                    for (int i = 0; i < eligible.Count; i++)
                    {
                        var ep = eligible[i];
                        if (!ep) continue;
                        if (ep.GetInstanceID() == tailId) continue;

                        filtered.Add(ep);

                        var st = _scanner.ReadStateName(ep);
                        if (!ExtractionPointScanner.IsCompletedLikeState(st))
                            unfinishedNonTail++;
                    }

                    // Only filter out tail when there is at least one unfinished non-tail point.
                    // If all non-tail points are completed, tail must be allowed into the plan.
                    if (filtered.Count > 0 && unfinishedNonTail > 0)
                    {
                        planInput = filtered;
                        _dbg?.Log($"[PLAN][TAIL-FILTER] removed global tail from candidates tail={FormatEpShort(globalTail)} input={eligible.Count} filtered={filtered.Count} unfinishedNonTail={unfinishedNonTail}");
                    }
                    else if (filtered.Count > 0)
                    {
                        _dbg?.Log($"[PLAN][TAIL-FILTER] skipped (all non-tail completed) tail={FormatEpShort(globalTail)} input={eligible.Count} filtered={filtered.Count}");
                    }
                }
            }

            var plan = _discoverAllPoints.Value
                ? _scanner.BuildPlanDiscoverAllFixedAnchors(allPoints, spawnPos, skipActivated: SKIP_ACTIVATED)
                : _scanner.BuildStage1PlannedList(planInput, spawnPos, skipActivated: SKIP_ACTIVATED);
            if (plan.Count == 0)
            {
                Logger.LogWarning("没有可激活的提取点（可能都已激活或未发现）");
                _dbg?.Log("[F3] plan empty");
                return;
            }

            if (!_discoverAllPoints.Value)
                LogDiscoveredPlanSummary(plan, allPoints, spawnPos);

            var next = plan[0];
            if (ShouldHoldTailPointActivation(allPoints, spawnPos, plan, next, out var tailBlockReason))
            {
                _dbg?.Log($"[F3] tail-lock blocked next={FormatEpShort(next)} reason={tailBlockReason}");
                return;
            }

            var nextState = _scanner.ReadStateName(next);
            _dbg?.Log($"[F3] next={next.gameObject.name} state={nextState}");
            var distP = Vector3.Distance(startPos, next.transform.position);
            var distS = spawnPos == Vector3.zero ? "na" : Vector3.Distance(spawnPos, next.transform.position).ToString("0.00");
            _dbg?.Log($"[ACTIVATE] ep={next.gameObject.name} epPos={next.transform.position} distToPlayer={distP:0.00} distToSpawn={distS} state={nextState}");

            Logger.LogInfo($"[F3] Activate planned EP: {next.gameObject.name} pos={next.transform.position} dist={Vector3.Distance(startPos, next.transform.position):0.00}");

            var invokeOk = _invoker.InvokeOnClick(next);
            _dbg?.Log($"[F3] invoke={invokeOk} next={next.gameObject.name}");

            if (invokeOk)
            {
                var st = _scanner.ReadStateName(next);
                if (!string.IsNullOrEmpty(st) &&
                    !ExtractionPointScanner.IsIdleLikeState(st) &&
                    !ExtractionPointScanner.IsCompletedLikeState(st))
                {
                    _scanner.MarkActivated(next);
                    _dbg?.Log($"[F3] marked activated: {next.gameObject.name} state={st}");
                }
                else
                {
                    _dbg?.Log($"[F3] NOT marked (state not active): {next.gameObject.name} state={st}");
                    TryDeferredMarkActivated(next);
                }
            }
        }

        private void TryDeferredMarkActivated(Component ep)
        {
            if (ep == null) return;
            int id = ep.GetInstanceID();
            if (!_deferredMarking.Add(id))
            {
                _dbg?.Log($"[F3] deferred already pending: {ep.gameObject.name}");
                return;
            }

            StartCoroutine(DeferredMarkActivated(ep, id));
        }

        private IEnumerator DeferredMarkActivated(Component ep, int id)
        {
            try
            {
                if (ep == null) yield break;

                float[] waits = { 0.15f, 0.25f, 0.35f, 0.35f, 0.40f };
                string lastState = "";

                for (int i = 0; i < waits.Length; i++)
                {
                    if (waits[i] > 0f) yield return new WaitForSeconds(waits[i]);
                    if (ep == null) yield break;

                    var st = _scanner.ReadStateName(ep);
                    lastState = st;

                    if (!string.IsNullOrEmpty(st) &&
                        !ExtractionPointScanner.IsIdleLikeState(st) &&
                        !ExtractionPointScanner.IsCompletedLikeState(st))
                    {
                        _scanner.MarkActivated(ep);
                        _dbg?.Log($"[F3] deferred marked activated: {ep.gameObject.name} state={st}");
                        yield break;
                    }

                    if (i == 0)
                    {
                        yield return null;
                        if (ep == null) yield break;
                        var st2 = _scanner.ReadStateName(ep);
                        lastState = string.IsNullOrEmpty(st2) ? lastState : st2;
                        if (!string.IsNullOrEmpty(st2) &&
                            !ExtractionPointScanner.IsIdleLikeState(st2) &&
                            !ExtractionPointScanner.IsCompletedLikeState(st2))
                        {
                            _scanner.MarkActivated(ep);
                            _dbg?.Log($"[F3] deferred marked activated: {ep.gameObject.name} state={st2}");
                            yield break;
                        }
                    }
                }

                _dbg?.Log($"[F3] deferred NOT marked: {ep.gameObject.name} state={lastState}");
            }
            finally
            {
                _deferredMarking.Remove(id);
            }
        }

        private void PrimeFirstPointIfAlreadyActivated()
        {
            var all = _scanner.ScanAndGetAllPoints();
            if (all.Count == 0) return;

            var refPos = _scanner.GetReferencePos();
            _scanner.CaptureSpawnPosIfNeeded(refPos);

            if (_discoverAllPoints.Value)
                _scanner.MarkAllDiscovered(all);
            else
                _scanner.UpdateDiscovered(refPos, DISCOVER_RADIUS);

            var eligible = _discoverAllPoints.Value ? all : _scanner.FilterDiscovered(all);
            var spawnPos = _scanner.GetSpawnPos();
            var plan = _discoverAllPoints.Value
                ? _scanner.BuildPlanDiscoverAllFixedAnchors(all, spawnPos, skipActivated: SKIP_ACTIVATED)
                : _scanner.BuildStage1PlannedList(eligible, spawnPos, skipActivated: SKIP_ACTIVATED);
            if (plan.Count == 0) return;

            var first = plan[0];
            var state = _scanner.ReadStateName(first);
            if (!string.IsNullOrEmpty(state) && !ExtractionPointScanner.IsIdleLikeState(state))
            {
                _scanner.MarkActivated(first);
            }
        }

        private List<Vector3> TryGetAllPlayerPositionsHost()
        {
            var list = new List<Vector3>();

            try
            {
                var photonNetworkType = Type.GetType("Photon.Pun.PhotonNetwork, PhotonUnityNetworking");
                if (photonNetworkType == null) return list;

                var inRoomProp = photonNetworkType.GetProperty("InRoom");
                var isMasterProp = photonNetworkType.GetProperty("IsMasterClient");

                bool inRoom = inRoomProp != null && inRoomProp.GetValue(null) is bool b1 && b1;
                bool isMaster = isMasterProp != null && isMasterProp.GetValue(null) is bool b2 && b2;

                if (!inRoom || !isMaster) return list;

                var gdType = Type.GetType("GameDirector, Assembly-CSharp");
                if (gdType == null) return list;

                object? gdInst = null;
                var instProp = gdType.GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (instProp != null) gdInst = instProp.GetValue(null, null);
                if (gdInst == null)
                {
                    var instField = gdType.GetField("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (instField != null) gdInst = instField.GetValue(null);
                }
                if (gdInst == null) return list;

                object? playerListObj = null;
                var plProp = gdType.GetProperty("PlayerList", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (plProp != null) playerListObj = plProp.GetValue(gdInst, null);
                if (playerListObj == null)
                {
                    var plField = gdType.GetField("PlayerList", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (plField != null) playerListObj = plField.GetValue(gdInst);
                }
                if (playerListObj == null) return list;

                var asEnumerable = playerListObj as System.Collections.IEnumerable;
                if (asEnumerable == null) return list;

                var temp = new List<Component>();
                int hash = 17;

                foreach (var p in asEnumerable)
                {
                    if (p == null) continue;
                    var comp = ExtractPlayerComponent(p);
                    if (comp == null || comp.transform == null) continue;

                    temp.Add(comp);
                    hash = unchecked(hash * 31 + comp.GetInstanceID());
                }

                if (temp.Count == 0) return list;

                bool listChanged = (hash != _playerCacheHash) || (_playerCache.Count != temp.Count);
                if (listChanged)
                {
                    _playerCacheHash = hash;
                    _playerCache.Clear();
                    _playerCache.AddRange(temp);
                    _playerLastPos.Clear();
                    for (int i = 0; i < _playerCache.Count; i++)
                    {
                        _playerLastPos.Add(_playerCache[i].transform.position);
                    }
                }

                for (int i = 0; i < _playerCache.Count; i++)
                {
                    var comp = _playerCache[i];
                    if (comp == null || comp.transform == null)
                    {
                        _playerCache.Clear();
                        _playerLastPos.Clear();
                        _playerCacheHash = 0;
                        return list;
                    }

                    var pos = comp.transform.position;
                    list.Add(pos);

                    _playerLastPos[i] = pos;
                }
            }
            catch
            {
                return list;
            }

            return list;
        }

        private Component? ExtractPlayerComponent(object p)
        {
            if (p is Component comp && comp.transform != null) return comp;

            var pt = p.GetType();
            object? avatarObj = null;
            var avatarProp = pt.GetProperty("PlayerAvatar", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (avatarProp != null) avatarObj = avatarProp.GetValue(p, null);
            if (avatarObj == null)
            {
                var avatarField = pt.GetField("PlayerAvatar", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (avatarField != null) avatarObj = avatarField.GetValue(p);
            }
            if (avatarObj == null) return null;

            if (avatarObj is Component comp2 && comp2.transform != null) return comp2;

            var trProp = avatarObj.GetType().GetProperty("transform", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var tr = trProp != null ? trProp.GetValue(avatarObj, null) as Transform : null;
            return tr;
        }

        private void UpdateDiscoveredFromCachedPlayers(Vector3 fallbackPos)
        {
            if (_playerCache.Count > 0 && _playerLastPos.Count == _playerCache.Count)
            {
                var spawnPos = _scanner.GetSpawnPos();
                for (int i = 0; i < _playerLastPos.Count; i++)
                {
                    var newly = new List<Component>();
                    int added = _scanner.UpdateDiscoveredDetailed(_playerLastPos[i], DISCOVER_RADIUS, newly);
                    if (added > 0 && _dbg != null)
                    {
                        _dbg.Log($"[DISCOVER] by player#{i} pos={_playerLastPos[i]} +{added}");
                        for (int j = 0; j < newly.Count; j++)
                        {
                            var ep = newly[j];
                            if (!ep) continue;
                            var epPos = ep.transform.position;
                            var dp = Vector3.Distance(_playerLastPos[i], epPos);
                            string ds = spawnPos == Vector3.zero ? "na" : Vector3.Distance(spawnPos, epPos).ToString("0.00");
                            _dbg.Log($"[DISCOVER+] ep={ep.gameObject.name} epPos={epPos} distToPlayer={dp:0.00} distToSpawn={ds}");
                        }
                    }
                }
                return;
            }

            var newlyFallback = new List<Component>();
            int addedFallback = _scanner.UpdateDiscoveredDetailed(fallbackPos, DISCOVER_RADIUS, newlyFallback);
            if (addedFallback > 0 && _dbg != null)
            {
                var spawnPos = _scanner.GetSpawnPos();
                _dbg.Log($"[DISCOVER] by local pos={fallbackPos} +{addedFallback}");
                for (int j = 0; j < newlyFallback.Count; j++)
                {
                    var ep = newlyFallback[j];
                    if (!ep) continue;
                    var epPos = ep.transform.position;
                    var dp = Vector3.Distance(fallbackPos, epPos);
                    string ds = spawnPos == Vector3.zero ? "na" : Vector3.Distance(spawnPos, epPos).ToString("0.00");
                    _dbg.Log($"[DISCOVER+] ep={ep.gameObject.name} epPos={epPos} distToPlayer={dp:0.00} distToSpawn={ds}");
                }
            }
        }

        private static string FormatEpShort(Component? ep)
        {
            if (ep == null) return "null";
            var pos = ep.transform.position;
            return $"{ep.gameObject.name}#{ep.GetInstanceID()}@({pos.x:0.00},{pos.y:0.00},{pos.z:0.00})";
        }

        private void LogDiscoveredPlanSummary(List<Component> plan, List<Component> allPoints, Vector3 spawnPos)
        {
            if (_dbg == null || plan == null || plan.Count == 0) return;

            string first = FormatEpShort(plan[0]);
            string tail = FormatEpShort(plan[plan.Count - 1]);

            string middle;
            if (plan.Count <= 2)
            {
                middle = "-";
            }
            else
            {
                var names = new List<string>();
                for (int i = 1; i < plan.Count - 1; i++)
                {
                    names.Add(FormatEpShort(plan[i]));
                }
                middle = string.Join(" -> ", names);
            }

            string ordered = string.Join(" -> ", plan.Where(x => x != null).Select(x => FormatEpShort(x)));
            _dbg.Log($"[PLAN][SUMMARY] first={first} middle={middle} tail={tail} ordered={ordered}");

            if (_scanner.TryGetGlobalAnchorsNoCache(allPoints, spawnPos, out var globalFirst, out var globalTail))
            {
                _dbg.Log($"[PLAN][GLOBAL] first={FormatEpShort(globalFirst)} tail={FormatEpShort(globalTail)} total={allPoints.Count}");
            }
        }
        private bool ShouldHoldTailPointActivation(List<Component> allPoints, Vector3 spawnPos, List<Component> plan, Component next, out string reason)
        {
            reason = "";

            if (_discoverAllPoints.Value) return false;
            if (next == null) return false;

            if (!_scanner.TryGetGlobalAnchorsNoCache(allPoints, spawnPos, out var firstAnchor, out var tailAnchor))
                return false;

            if (tailAnchor == null) return false;

            int firstId = firstAnchor != null ? firstAnchor.GetInstanceID() : int.MinValue;
            int tailId = tailAnchor.GetInstanceID();
            bool isTailTarget = next.GetInstanceID() == tailId;

            var pendingNames = new List<string>();
            for (int i = 0; i < allPoints.Count; i++)
            {
                var ep = allPoints[i];
                if (!ep) continue;
                int id = ep.GetInstanceID();
                if (id == firstId || id == tailId) continue;

                var st = _scanner.ReadStateName(ep);
                if (ExtractionPointScanner.IsCompletedLikeState(st)) continue;

                pendingNames.Add($"{FormatEpShort(ep)}({st})");
            }

            bool shouldHold = isTailTarget && pendingNames.Count > 0;
            string queue = plan == null || plan.Count == 0
                ? "-"
                : string.Join(" -> ", plan.Where(x => x != null).Select(x => FormatEpShort(x)));

            _dbg?.Log($"[TAIL][CHECK] globalFirst={FormatEpShort(firstAnchor)} globalTail={FormatEpShort(tailAnchor)} next={FormatEpShort(next)} isTailTarget={isTailTarget} pendingOthers={pendingNames.Count} hold={shouldHold} queue={queue}");

            if (_lastTailHoldState.HasValue && _lastTailHoldState.Value && !shouldHold)
            {
                string releaseReason = !isTailTarget
                    ? "next switched to non-tail target"
                    : "all prerequisite points completed";
                _dbg?.Log($"[TAIL][UNLOCK] reason={releaseReason} next={FormatEpShort(next)} queue={queue}");
            }
            _lastTailHoldState = shouldHold;

            if (!shouldHold) return false;

            reason = $"tail point locked, pendingOthers={pendingNames.Count}, pending=[{string.Join(", ", pendingNames)}], queue={queue}";
            return true;
        }

        private void HandleRoomPopulationChangedEvent(string reason)
        {
            // 人数变化只更新扫描间隔与玩家缓存，不触发任何重排。
            RefreshDiscoveryIntervalFromCurrentPlayerCount(reason, invalidateCache: true);
        }

        private void RefreshDiscoveryIntervalFromCurrentPlayerCount(string reason, bool invalidateCache)
        {
            int players = GetPlayerCountHost();
            if (players < 1) players = 1;

            if (players != _lastPlayerCount)
            {
                _lastPlayerCount = players;
                _lastBaseInterval = ComputeDiscoveryInterval(players);
                _dbg?.Log($"[DISCOVER] interval base={_lastBaseInterval:0.0}s players={players} reason={reason}");
            }

            if (invalidateCache)
            {
                _playerCache.Clear();
                _playerLastPos.Clear();
                _playerCacheHash = 0;
            }
        }
        private float GetDiscoveryInterval()
        {
            // When all points are treated as discovered, discovery polling does not need
            // dynamic player-based interval or empty-list penalties.
            if (_discoverAllPoints.Value) return DISCOVER_INTERVAL_BASE;

            if (_lastPlayerCount < 1)
            {
                RefreshDiscoveryIntervalFromCurrentPlayerCount("lazy init", invalidateCache: false);
            }

            float baseInterval = _lastBaseInterval;
            if (_playerListEmptyStreak <= 0) return baseInterval;

            float mult = 1f + (Math.Min(_playerListEmptyStreak, 10) * 0.2f);
            float interval = baseInterval * mult;
            return interval > DISCOVER_INTERVAL_MAX ? DISCOVER_INTERVAL_MAX : interval;
        }
        private void LogDiscoverState(string reason, int allCount, int discoveredCount)
        {
            if (_dbg == null) return;
            if (reason != _lastDiscoverReason || allCount != _lastDiscoverAll || discoveredCount != _lastDiscoverCount)
            {
                _lastDiscoverReason = reason;
                _lastDiscoverAll = allCount;
                _lastDiscoverCount = discoveredCount;
                _dbg.Log($"[DISCOVER] {reason} all={allCount} discovered={discoveredCount}");
            }
        }

        private float ComputeDiscoveryInterval(int players)
        {
            if (players <= 3) return DISCOVER_INTERVAL_BASE;
            if (players <= 6) return DISCOVER_INTERVAL_4_6;
            if (players <= 9) return DISCOVER_INTERVAL_7_9;
            return DISCOVER_INTERVAL_10_12;
        }

        private static void OnPlayerEnteredRoomPostfix()
        {
            _instance?.HandleRoomPopulationChangedEvent("player entered");
        }

        private static void OnPlayerLeftRoomPostfix()
        {
            _instance?.HandleRoomPopulationChangedEvent("player left");
        }

        private void InstallEventPatches()
        {
            try
            {
                _harmony = new Harmony(PluginGuid + ".population");
                var networkType = AccessTools.TypeByName("NetworkManager");
                var onEntered = networkType != null ? AccessTools.DeclaredMethod(networkType, "OnPlayerEnteredRoom") : null;
                if (onEntered != null)
                {
                    _harmony.Patch(onEntered, postfix: new HarmonyMethod(typeof(Plugin), nameof(OnPlayerEnteredRoomPostfix)));
                }

                var onLeft = networkType != null ? AccessTools.DeclaredMethod(networkType, "OnPlayerLeftRoom") : null;
                if (onLeft != null)
                {
                    _harmony.Patch(onLeft, postfix: new HarmonyMethod(typeof(Plugin), nameof(OnPlayerLeftRoomPostfix)));
                }

                _dbg?.Log("[PATCH] population patches installed");
            }
            catch (Exception e)
            {
                Logger.LogError($"Failed to install population patches: {e}");
            }
        }

        private void UninstallEventPatches()
        {
            try
            {
                _harmony?.UnpatchSelf();
                _harmony = null;
            }
            catch { }
        }
        private int GetPlayerCountHost()
        {
            try
            {
                var photonNetworkType = Type.GetType("Photon.Pun.PhotonNetwork, PhotonUnityNetworking");
                if (photonNetworkType == null) return 1;

                var inRoomProp = photonNetworkType.GetProperty("InRoom");
                var isMasterProp = photonNetworkType.GetProperty("IsMasterClient");
                var playerListProp = photonNetworkType.GetProperty("PlayerList");

                bool inRoom = inRoomProp != null && inRoomProp.GetValue(null) is bool b1 && b1;
                bool isMaster = isMasterProp != null && isMasterProp.GetValue(null) is bool b2 && b2;
                if (!inRoom || !isMaster) return 1;

                if (playerListProp != null)
                {
                    // [VERIFY] PhotonNetwork.PlayerList exists in decompiled PhotonNetwork.cs (returns Player[]).
                    var arr = playerListProp.GetValue(null) as Array;
                    if (arr != null && arr.Length > 0) return arr.Length;
                }
            }
            catch { }

            return 1;
        }

    }
}

















































