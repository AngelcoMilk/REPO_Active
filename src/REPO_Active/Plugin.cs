using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
        public const string PluginVersion = "5.2.3";

        private const bool SkipActivated = true;
        private const float DiscoverRadius = 20f;
        private const float AutoReadyBuffer = 30f;
        private const float AutoEventDelay = 0.35f;

        private static readonly string[] KeyOptions = BuildKeyOptions();

        private ConfigEntry<bool> _autoActivate = null!;
        private ConfigEntry<string> _keyActivateNearest = null!;
        private ConfigEntry<bool> _discoverAllPoints = null!;

        private readonly HashSet<int> _deferredMarking = new HashSet<int>();
        private readonly List<PlayerAvatar> _playerCache = new List<PlayerAvatar>();
        private readonly List<Vector3> _playerLastPos = new List<Vector3>();

        private ExtractionPointScanner _scanner = null!;
        private Harmony? _harmony;
        private Coroutine? _autoCoroutine;

        private KeyCode _activateKey = KeyCode.F3;
        private float _autoReadyAt = -1f;
        private bool _levelReady;
        private bool _autoPrimed;
        private int _playerCacheHash;

        internal static Plugin? Instance { get; private set; }

        internal ExtractionPointStateTracker StateTracker { get; } = new ExtractionPointStateTracker();

        private void Awake()
        {
            MigrateLegacyConfigIfNeeded(
                out var defaultAutoActivate,
                out var defaultDiscoverAll,
                out var defaultKey);

            _autoActivate = Config.Bind("Auto", "AutoActivate", defaultAutoActivate, "Auto activate when idle.");
            _keyActivateNearest = Config.Bind(
                "Keybinds",
                "ActivateNearestKey",
                SanitizeKeyName(defaultKey),
                new ConfigDescription(
                    "Press to activate next extraction point.",
                    new AcceptableValueList<string>(KeyOptions)));
            _discoverAllPoints = Config.Bind("Discovery", "DiscoverAllPoints", defaultDiscoverAll, "If true, treat all extraction points as discovered.");
            _keyActivateNearest.SettingChanged += (_, _) => RefreshActivateKey();
            RefreshActivateKey();
            Config.Save();

            Instance = this;
            _scanner = new ExtractionPointScanner(StateTracker);

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            SceneManager.sceneLoaded += OnSceneLoaded;
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
            if (_activateKey != KeyCode.None && Input.GetKeyDown(_activateKey))
            {
                ActivateNearest(manual: true);
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            ResetRoundState();
        }

        private void ResetRoundState()
        {
            _levelReady = false;
            _autoReadyAt = -1f;
            _autoPrimed = false;
            if (_autoCoroutine != null)
            {
                StopCoroutine(_autoCoroutine);
                _autoCoroutine = null;
            }

            StateTracker.ResetForNewRound();
            _scanner.ResetForNewRound();
            _deferredMarking.Clear();
            ClearPlayerCache();
        }

        private void HandleLevelGenerated()
        {
            if (_levelReady)
            {
                return;
            }

            _levelReady = true;
            _autoReadyAt = Time.realtimeSinceStartup + AutoReadyBuffer;
            RefreshCachedPlayers();
            CaptureReferenceAndDiscovery();
            ScheduleAutoActivation(AutoReadyBuffer);
        }

        private void HandleExtractionPointStarted(ExtractionPoint point)
        {
            if (!point)
            {
                return;
            }

            _scanner.RegisterPoint(point);
            CaptureReferenceAndDiscovery();
            if (_levelReady)
            {
                ScheduleAutoActivation(GetAutoReadyDelay());
            }
        }

        private void HandleExtractionPointStateChanged(ExtractionPoint point, ExtractionPoint.State state)
        {
            if (!point)
            {
                return;
            }

            _scanner.RegisterPoint(point);
            StateTracker.Record(point, state);

            if (ExtractionPointScanner.IsBlockingState(state))
            {
                _scanner.MarkActivated(point);
                return;
            }

            if (ExtractionPointScanner.IsCompletedLikeState(state))
            {
                _scanner.MarkActivated(point);
            }

            if (_levelReady && _autoActivate.Value && IsAutoContinueState(state))
            {
                ScheduleAutoActivation(Math.Max(GetAutoReadyDelay(), AutoEventDelay));
            }
        }

        private void HandleRoomPopulationChangedEvent()
        {
            ClearPlayerCache();
            RefreshCachedPlayers();
        }

        private void ScheduleAutoActivation(float delay)
        {
            if (!_autoActivate.Value || !_levelReady || _autoCoroutine != null)
            {
                return;
            }

            float effectiveDelay = Math.Max(delay, GetAutoReadyDelay());
            _autoCoroutine = StartCoroutine(AutoActivationAfterDelay(effectiveDelay));
        }

        private IEnumerator AutoActivationAfterDelay(float delay)
        {
            if (delay > 0f)
            {
                yield return new WaitForSeconds(delay);
            }

            _autoCoroutine = null;
            if (!_autoActivate.Value || !_levelReady)
            {
                yield break;
            }

            CaptureReferenceAndDiscovery();
            if (!_autoPrimed)
            {
                PrimeFirstPointIfAlreadyActivated();
                _autoPrimed = true;
            }

            ActivateNearest(manual: false);
        }

        private float GetAutoReadyDelay()
        {
            if (_autoReadyAt < 0f)
            {
                return 0f;
            }

            float remaining = _autoReadyAt - Time.realtimeSinceStartup;
            return remaining > 0f ? remaining : 0f;
        }

        private void ActivateNearest(bool manual)
        {
            var allPoints = _scanner.GetAllPoints();
            if (allPoints.Count == 0)
            {
                return;
            }

            if (_scanner.TryGetActivatingInfo(allPoints, out _, out _))
            {
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
            if (ShouldHoldTailPointActivation(allPoints, spawnPos, plan, next))
            {
                return;
            }

            try
            {
                next.OnClick();
            }
            catch
            {
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
                }
            }
            finally
            {
                _deferredMarking.Remove(id);
            }
        }

        private void PrimeFirstPointIfAlreadyActivated()
        {
            var all = _scanner.GetAllPoints();
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

        private void CaptureReferenceAndDiscovery()
        {
            var refPos = _scanner.GetReferencePos();
            _scanner.CaptureSpawnPosIfNeeded(refPos);

            var all = _scanner.GetAllPoints();
            if (_discoverAllPoints.Value)
            {
                _scanner.MarkAllDiscovered(all);
                return;
            }

            RefreshCachedPlayers();
            UpdateDiscoveredFromCachedPlayers(refPos);
        }

        private void RefreshCachedPlayers()
        {
            if (_discoverAllPoints.Value)
            {
                return;
            }

            var positions = TryGetAllPlayerPositionsHost();
            if (positions.Count == 0)
            {
                return;
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
            ExtractionPoint next)
        {
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

            int pendingNonTail = 0;
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

                if (!ExtractionPointScanner.IsCompletedLikeState(_scanner.ReadState(point)))
                {
                    pendingNonTail++;
                }
            }

            return isTailTarget && pendingNonTail > 0;
        }

        private void ClearPlayerCache()
        {
            _playerCache.Clear();
            _playerLastPos.Clear();
            _playerCacheHash = 0;
        }

        private void RefreshActivateKey()
        {
            _activateKey = ParseKeyCode(_keyActivateNearest.Value);
        }

        private void MigrateLegacyConfigIfNeeded(
            out bool defaultAutoActivate,
            out bool defaultDiscoverAll,
            out string defaultKey)
        {
            defaultAutoActivate = false;
            defaultDiscoverAll = false;
            defaultKey = "F3";

            string path = Config.ConfigFilePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return;
            }

            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch
            {
                return;
            }

            if (TryReadConfigValue(text, "AutoActivate", out var autoText) &&
                bool.TryParse(autoText, out var autoValue))
            {
                defaultAutoActivate = autoValue;
            }

            if (TryReadConfigValue(text, "DiscoverAllPoints", out var discoverText) &&
                bool.TryParse(discoverText, out var discoverValue))
            {
                defaultDiscoverAll = discoverValue;
            }

            if (TryReadConfigValue(text, "ActivateNearestKey", out var keyText) ||
                TryReadConfigValue(text, "ActivateNearestShortcut", out keyText) ||
                TryReadConfigValue(text, "ActivateNearest", out keyText))
            {
                defaultKey = SanitizeKeyName(ExtractPrimaryKeyName(keyText));
            }

            bool needsCleanRewrite =
                text.IndexOf("ActivateNearestShortcut", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("Setting type: KeyCode", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("Setting type: KeyboardShortcut", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("BuildQueueAndRun", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("EnforceHostAuthority", StringComparison.OrdinalIgnoreCase) >= 0;

            if (!needsCleanRewrite)
            {
                return;
            }

            try
            {
                string backup = path + ".bak." + DateTime.Now.ToString("yyyyMMddHHmmss");
                File.Copy(path, backup, overwrite: false);
            }
            catch
            {
            }

            try
            {
                Config.Clear();
                File.Delete(path);
                Config.Reload();
            }
            catch
            {
            }
        }

        private static bool TryReadConfigValue(string text, string key, out string value)
        {
            value = "";
            using (var reader = new StringReader(text))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed[0] == '#')
                    {
                        continue;
                    }

                    int equals = trimmed.IndexOf('=');
                    if (equals <= 0)
                    {
                        continue;
                    }

                    string candidateKey = trimmed.Substring(0, equals).Trim();
                    if (!string.Equals(candidateKey, key, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    value = trimmed.Substring(equals + 1).Trim();
                    return true;
                }
            }

            return false;
        }

        private static bool IsAutoContinueState(ExtractionPoint.State state)
        {
            return state == ExtractionPoint.State.None ||
                   state == ExtractionPoint.State.Idle ||
                   state == ExtractionPoint.State.Success ||
                   state == ExtractionPoint.State.Complete ||
                   state == ExtractionPoint.State.Cancel;
        }

        private static KeyCode ParseKeyCode(string value)
        {
            if (Enum.TryParse(value, ignoreCase: true, out KeyCode key))
            {
                return key;
            }

            return KeyCode.F3;
        }

        private static string SanitizeKeyName(string value)
        {
            string primary = ExtractPrimaryKeyName(value);
            for (int i = 0; i < KeyOptions.Length; i++)
            {
                if (string.Equals(KeyOptions[i], primary, StringComparison.OrdinalIgnoreCase))
                {
                    return KeyOptions[i];
                }
            }

            return "F3";
        }

        private static string ExtractPrimaryKeyName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "F3";
            }

            string trimmed = value.Trim();
            int plus = trimmed.IndexOf('+');
            if (plus >= 0)
            {
                trimmed = trimmed.Substring(0, plus).Trim();
            }

            int comma = trimmed.IndexOf(',');
            if (comma >= 0)
            {
                trimmed = trimmed.Substring(0, comma).Trim();
            }

            return trimmed.Length == 0 ? "F3" : trimmed;
        }

        private static string[] BuildKeyOptions()
        {
            var values = new List<string> { "None" };
            for (int i = 1; i <= 12; i++)
            {
                values.Add("F" + i);
            }

            for (char c = 'A'; c <= 'Z'; c++)
            {
                values.Add(c.ToString());
            }

            for (int i = 0; i <= 9; i++)
            {
                values.Add("Alpha" + i);
            }

            values.Add("Mouse3");
            values.Add("Mouse4");
            values.Add("Mouse5");
            return values.ToArray();
        }

        internal static void NotifyRoomPopulationChanged()
        {
            Instance?.HandleRoomPopulationChangedEvent();
        }

        internal static void NotifyLevelGenerated()
        {
            Instance?.HandleLevelGenerated();
        }

        internal static void NotifyExtractionPointStarted(ExtractionPoint point)
        {
            Instance?.HandleExtractionPointStarted(point);
        }

        internal static void NotifyExtractionPointState(ExtractionPoint point, ExtractionPoint.State state)
        {
            Instance?.HandleExtractionPointStateChanged(point, state);
        }
    }

    [HarmonyPatch(typeof(NetworkManager), nameof(NetworkManager.OnPlayerEnteredRoom))]
    internal static class NetworkManagerOnPlayerEnteredRoomPatch
    {
        private static void Postfix()
        {
            Plugin.NotifyRoomPopulationChanged();
        }
    }

    [HarmonyPatch(typeof(NetworkManager), nameof(NetworkManager.OnPlayerLeftRoom))]
    internal static class NetworkManagerOnPlayerLeftRoomPatch
    {
        private static void Postfix()
        {
            Plugin.NotifyRoomPopulationChanged();
        }
    }

    [HarmonyPatch(typeof(LevelGenerator), "GenerateDone")]
    internal static class LevelGeneratorGenerateDonePatch
    {
        private static void Postfix()
        {
            Plugin.NotifyLevelGenerated();
        }
    }

    [HarmonyPatch(typeof(SemiFunc), nameof(SemiFunc.OnLevelGenDone))]
    internal static class SemiFuncOnLevelGenDonePatch
    {
        private static void Postfix()
        {
            Plugin.NotifyLevelGenerated();
        }
    }

    [HarmonyPatch(typeof(ExtractionPoint), "Start")]
    internal static class ExtractionPointStartPatch
    {
        private static void Postfix(ExtractionPoint __instance)
        {
            Plugin.NotifyExtractionPointStarted(__instance);
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
