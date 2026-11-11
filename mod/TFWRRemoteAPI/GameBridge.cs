using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Platforms;
using UnityEngine;

namespace TFWRRemoteAPI
{
    /// <summary>An error that maps to a specific HTTP status code.</summary>
    public sealed class ApiException : Exception
    {
        public readonly int Status;

        public ApiException(int status, string message) : base(message)
        {
            Status = status;
        }
    }

    public static class GameBridge
    {
        private static int _mainThreadId;

        /// <summary>
        /// Unity main thread id. Must be set via <see cref="EnsureMainThread"/> from the game
        /// main thread (e.g. Plugin.Awake) before any HTTP handler runs — otherwise the first
        /// REST call would capture a thread-pool id and RunOnMainThreadSync would execute Unity
        /// API on the wrong thread (native crash).
        /// </summary>
        public static int MainThreadId => _mainThreadId;

        /// <summary>Call once from Unity's main thread (e.g. start of Plugin.Awake).</summary>
        public static void EnsureMainThread()
        {
            if (_mainThreadId == 0)
                _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>Path to the active save directory (where the game keeps its .py files).</summary>
        public static string GetActiveSavePath()
        {
            string persistentPath = Application.persistentDataPath;
            string activeSave = OptionHolder.GetString("activeSave", "Save0");
            string savePath = Path.Combine(persistentPath, "Saves", activeSave);

            if (!Directory.Exists(savePath))
                Directory.CreateDirectory(savePath);

            return savePath;
        }

        public static string GetActiveSaveName()
        {
            try { return OptionHolder.GetString("activeSave", "Save0"); } catch { return ""; }
        }

        /// <summary>Names of the saves on disk (directories under Saves/ that hold a save.json).</summary>
        public static List<string> ListSaves()
        {
            var result = new List<string>();
            string root = Path.Combine(Application.persistentDataPath, "Saves");
            if (!Directory.Exists(root)) return result;
            foreach (var dir in Directory.GetDirectories(root))
            {
                if (File.Exists(Path.Combine(dir, "save.json")))
                    result.Add(Path.GetFileName(dir));
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        /// <summary>
        /// True while the title/pause menu covers the farm. The menu hides the workspace; so does
        /// the leaderboard result screen, which is therefore excluded.
        /// </summary>
        public static bool IsMenuOpen(MainSim mainSim)
        {
            try
            {
                if (mainSim.workspace == null || mainSim.workspace.gameObject.activeSelf) return false;
                var lm = mainSim.leaderboardManager;
                return lm == null || !lm.IsLeaderBoardScreenOpen;
            }
            catch
            {
                return false;
            }
        }

        // ── Camera ──────────────────────────────────────────────
        // The game has no free camera: the view is the workspace's scroll position plus a
        // zoom factor, and the 3D camera follows those. Driving both moves the farm on screen.
        // Framing is closed-loop: each frame the farm's corners are projected to the screen
        // and pan/zoom are nudged until its outline sits in the requested region.

        private static bool _camActive, _camFit, _camCenter;
        private static float _camTargetZoom, _camStartTime, _camDuration;
        private static Rect _camRegion;   // target region as screen fractions, y up

        // The workspace scroll view only lets the view travel a limited distance from the code
        // windows and springs back otherwise, which makes a large farm impossible to centre.
        // While the API holds the camera that limit is lifted; ReleaseCamera puts it back.
        private static UnityEngine.UI.ScrollRect _camScroll;
        private static UnityEngine.UI.ScrollRect.MovementType _camOriginalMovement;

        private static void HoldScroll(Workspace workspace)
        {
            var scroll = workspace.workspaceContainer.GetComponentInParent<UnityEngine.UI.ScrollRect>();
            if (scroll == null) return;
            if (_camScroll != scroll)
            {
                _camScroll = scroll;
                _camOriginalMovement = scroll.movementType;
            }
            scroll.movementType = UnityEngine.UI.ScrollRect.MovementType.Unrestricted;
            scroll.velocity = Vector2.zero;
        }

        /// <summary>Stop any camera move and restore the game's normal scroll limits.</summary>
        public static bool ReleaseCamera()
        {
            _camActive = false;
            if (_camScroll == null) return false;
            _camScroll.movementType = _camOriginalMovement;
            _camScroll = null;
            return true;
        }

        public static float GetZoom(MainSim mainSim)
        {
            return mainSim.workspace.cameraController.zoom;
        }

        private static float ZoomLimit(Workspace workspace, string field, float fallback)
        {
            try
            {
                var f = typeof(Workspace).GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (f != null) return (float)f.GetValue(workspace);
            }
            catch { }
            return fallback;
        }

        /// <summary>Screen-space bounding box (pixels, y up) of the farm's surface outline.</summary>
        public static bool TryGetFarmScreenRect(MainSim mainSim, out Rect rect)
        {
            rect = default(Rect);
            var cam = mainSim.mainCamera;
            var scaler = mainSim.SceneScaler;
            if (cam == null || scaler == null) return false;

            int n = Math.Max(1, mainSim.GetWorldSize().x);
            // Cell (x, y) is centred at local (-x, y, 0) and is one unit wide.
            float[] xs = { 0.5f, -(n - 0.5f) };
            float[] ys = { -0.5f, n - 0.5f };
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (float lx in xs)
            {
                foreach (float ly in ys)
                {
                    Vector3 screen = cam.WorldToScreenPoint(scaler.TransformPoint(new Vector3(lx, ly, 0f)));
                    if (screen.z <= 0f) return false; // behind the camera
                    minX = Mathf.Min(minX, screen.x); maxX = Mathf.Max(maxX, screen.x);
                    minY = Mathf.Min(minY, screen.y); maxY = Mathf.Max(maxY, screen.y);
                }
            }
            rect = Rect.MinMaxRect(minX, minY, maxX, maxY);
            return rect.width > 1f && rect.height > 1f;
        }

        /// <summary>
        /// Start a camera move. <paramref name="fit"/> scales and pans until the farm fills
        /// <paramref name="region"/> (screen fractions, origin bottom-left); <paramref name="center"/>
        /// alone only pans the farm to the region's centre; <paramref name="zoom"/> sets an
        /// explicit zoom instead of fitting.
        /// </summary>
        public static void StartCameraMove(MainSim mainSim, float? zoom, bool fit, bool center, Rect region, float seconds)
        {
            _camFit = fit && !zoom.HasValue;
            _camCenter = center || fit;
            _camTargetZoom = zoom ?? -1f;
            _camRegion = region;
            _camStartTime = Time.unscaledTime;
            _camDuration = Mathf.Max(0.05f, seconds);
            _camActive = true;
            HoldScroll(mainSim.workspace);
        }

        private static void SetZoom(Workspace workspace, float zoom)
        {
            float min = ZoomLimit(workspace, "minCanvasScaleFactor", 0.1f);
            float max = ZoomLimit(workspace, "maxCanvasScaleFactor", 5f);
            zoom = Mathf.Clamp(zoom, min, max);
            if (Mathf.Approximately(zoom, workspace.cameraController.zoom)) return;

            // Same steps as the game's own Zoom(), anchored at the screen centre instead of the mouse.
            var container = workspace.workspaceContainer;
            var parent = container.parent;
            var screenCentre = new Vector3(Screen.width / 2f, Screen.height / 2f, 0f);
            Vector3 before = parent.InverseTransformPoint(screenCentre);
            workspace.cameraController.zoom = zoom;
            workspace.zoomContainer.localScale = Vector3.one * zoom;
            var scaler = container.GetComponent<ContainerScaler>();
            if (scaler != null) scaler.UpdateMarginSize();
            Vector3 after = parent.InverseTransformPoint(screenCentre);
            container.anchoredPosition -= (Vector2)(before - after);
        }

        /// <summary>Advance a camera move; call every frame from the main thread.</summary>
        public static void UpdateCamera(MainSim mainSim)
        {
            if (!_camActive) return;
            var workspace = mainSim.workspace;
            if (workspace == null || workspace.cameraController == null) { _camActive = false; return; }

            if (_camScroll != null) _camScroll.velocity = Vector2.zero;
            float elapsed = Time.unscaledTime - _camStartTime;
            // Fraction of the remaining error removed per frame; smaller = slower glide.
            float gain = Mathf.Clamp(Time.unscaledDeltaTime * 4f / _camDuration, 0.02f, 0.35f);
            bool settled = true;

            if (_camTargetZoom > 0f)
            {
                float current = workspace.cameraController.zoom;
                float next = Mathf.Lerp(current, _camTargetZoom, gain);
                if (Mathf.Abs(_camTargetZoom - current) < 0.002f) next = _camTargetZoom; else settled = false;
                SetZoom(workspace, next);
            }

            Rect farm;
            if ((_camFit || _camCenter) && TryGetFarmScreenRect(mainSim, out farm))
            {
                var target = new Rect(_camRegion.x * Screen.width, _camRegion.y * Screen.height,
                    _camRegion.width * Screen.width, _camRegion.height * Screen.height);

                if (_camFit)
                {
                    float scale = Mathf.Min(target.width / farm.width, target.height / farm.height);
                    if (Mathf.Abs(scale - 1f) > 0.01f)
                    {
                        settled = false;
                        SetZoom(workspace, workspace.cameraController.zoom * Mathf.Lerp(1f, scale, gain));
                    }
                }

                Vector2 error = target.center - farm.center;
                if (error.magnitude > 2f)
                {
                    settled = false;
                    var parent = workspace.workspaceContainer.parent;
                    Vector3 local = parent.InverseTransformVector(new Vector3(error.x, error.y, 0f));
                    workspace.workspaceContainer.anchoredPosition += (Vector2)local * gain;
                }
            }

            // Stop once in place, or give up after a generous time so the player keeps control.
            if (settled || elapsed > _camDuration * 4f + 2f) _camActive = false;
        }

        // ── Code windows ────────────────────────────────────────

        /// <summary>
        /// Make sure a code window named <paramref name="filename"/> exists, is visible and
        /// holds <paramref name="code"/> (pass null to leave an existing window's text alone).
        /// Returns null if the window could not be created.
        /// </summary>
        public static CodeWindow EnsureCodeWindow(MainSim mainSim, string filename, string code)
        {
            var workspace = mainSim.workspace;
            CodeWindow cw;
            if (!workspace.codeWindows.TryGetValue(filename, out cw) || cw == null)
            {
                try
                {
                    workspace.OpenCodeWindow(filename, code ?? "", new Vector2(100f, -100f));
                    Plugin.Log.LogInfo($"Opened code window '{filename}'");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"Failed to open code window '{filename}': {ex}");
                }
                if (!workspace.codeWindows.TryGetValue(filename, out cw) || cw == null)
                    return null;
            }

            // Windows hidden by plugin versions before 1.1 are still in the workspace.
            if (cw.gameObject != null && !cw.gameObject.activeSelf)
                cw.gameObject.SetActive(true);

            if (code != null && cw.CodeInput.text != code)
                cw.CodeInput.text = code;
            return cw;
        }

        /// <summary>
        /// Close a code window the way the in-game delete button does: it is removed from the
        /// workspace and destroyed, so it does not pile up or get re-saved by the game.
        /// </summary>
        public static bool CloseCodeWindow(string filename, CodeWindow cw)
        {
            try
            {
                if (cw == null) return false;
                var window = cw.GetComponent<Window>();
                if (window != null)
                {
                    window.Close();
                    Plugin.Log.LogInfo($"Closed code window '{filename}'");
                    return true;
                }
                // No Window component: fall back to hiding it.
                cw.CodeInput.text = "";
                cw.gameObject.SetActive(false);
                Plugin.Log.LogWarning($"Code window '{filename}' has no Window component; hid it instead");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Failed to close code window '{filename}': {ex}");
                return false;
            }
        }

        /// <summary>
        /// Parse through the window (the game's own path). On failure returns null and fills in
        /// the parser's message and the 1-based line it points at (0 if unknown).
        /// </summary>
        public static Node ParseWindow(CodeWindow cw, out string error, out int line)
        {
            error = null;
            line = 0;
            var node = cw.Parse();
            if (node != null) return node;

            error = "Parse error in script";
            try
            {
                if (Compat.CodeWindowErrorString != null)
                {
                    var raw = Compat.CodeWindowErrorString.GetValue(cw) as string;
                    if (!string.IsNullOrEmpty(raw))
                    {
                        string localized = raw;
                        try { localized = Localizer.Localize(raw); } catch { }
                        error = "Parse error: " + StripTags(string.IsNullOrEmpty(localized) ? raw : localized);
                    }
                }
                if (Compat.CodeWindowErrorStartIndex != null)
                {
                    int index = (int)Compat.CodeWindowErrorStartIndex.GetValue(cw);
                    string text = cw.CodeInput.text ?? "";
                    if (index >= 0 && index <= text.Length)
                    {
                        line = 1;
                        for (int i = 0; i < index; i++)
                            if (text[i] == '\n') line++;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Could not read parse error details: {ex.Message}");
            }
            return null;
        }

        private static string StripTags(string s)
        {
            try { return CodeUtilities.RemoveSyntaxTags(s); } catch { return s; }
        }

        /// <summary>
        /// Parse code without showing it: Core's Parser.Parse(TokenStream, CodeWindow) needs some
        /// CodeWindow, so the scratch window's text is swapped to <paramref name="code"/> for the
        /// parse and restored afterwards. On failure returns null and sets <paramref name="error"/>.
        /// </summary>
        public static Node ParseCodeDirect(string code, CodeWindow scratchWindow, out string error)
        {
            error = null;
            if (scratchWindow == null)
            {
                error = "No CodeWindow available for parsing";
                return null;
            }
            string previous = scratchWindow.CodeInput.text;
            try
            {
                scratchWindow.CodeInput.text = code;
                // Tokenize's return value means "saw an unknown token", not success; the game
                // ignores it and lets the parser report the problem, so do the same.
                TokenStream stream;
                Tokenizer.Tokenize(code, out stream);
                var program = Parser.Parse(stream, scratchWindow);
                if (program?.syntaxTree == null) error = "Parse error in script";
                return program?.syntaxTree;
            }
            catch (Exception ex)
            {
                string message = ex.Message;
                try { message = Localizer.Localize(ex.Message); } catch { }
                error = "Parse error: " + StripTags(string.IsNullOrEmpty(message) ? ex.Message : message);
                return null;
            }
            finally
            {
                scratchWindow.CodeInput.text = previous;
            }
        }

        // ── Simulation state ────────────────────────────────────

        /// <summary>The Simulation the game is currently advancing (the nested one during simulate()).</summary>
        public static Simulation GetActiveSim(MainSim mainSim)
        {
            if (Compat.MainSimSim == null || mainSim == null) return null;
            try
            {
                return (Simulation)Compat.MainSimSim.GetValue(mainSim);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>True while a ScheduleLeaderboardStart request has not been picked up by MainSim.Update yet.</summary>
        public static bool IsLeaderboardStartPending(MainSim mainSim)
        {
            if (Compat.MainSimStartLeaderboardNow == null) return false;
            try
            {
                return Compat.MainSimStartLeaderboardNow.GetValue(mainSim) != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Stop whatever is running and return the game to an idle main farm: ends a nested
        /// simulation or leaderboard run, closes the result screen, and clears the state the
        /// game carries from one leaderboard attempt to the next.
        /// </summary>
        public static void StopEverything(MainSim mainSim)
        {
            var lm = mainSim.leaderboardManager;

            if (mainSim.MightBeSimulating())
            {
                // OkPressed restores the main sim itself; calling RestoreMainSim as well would
                // leave the game with no simulation at all.
                if (lm != null && lm.IsLeaderBoardScreenOpen)
                {
                    lm.OkPressed();
                }
                else
                {
                    mainSim.RestoreMainSim();
                    if (lm != null) lm.RemoveOverlay();
                }
            }

            if (mainSim.IsExecuting())
                mainSim.StopMainExecution();

            mainSim.TimeFactor = 1.0;
            mainSim.totalLeaderboardTime = new Duration(0L);
            mainSim.numLeaderboardRuns = 0;
            try
            {
                if (Compat.MainSimLeaderboardCancelled != null)
                    Compat.MainSimLeaderboardCancelled.SetValue(mainSim, false);
            }
            catch { }

            // simulate() pauses the main sim while the nested one runs; undo that.
            var sim = GetActiveSim(mainSim);
            if (sim != null) sim.Paused = false;
        }

        /// <summary>Dismiss the leaderboard result screen if it's showing.</summary>
        public static bool DismissLeaderboardScreen(MainSim mainSim)
        {
            var lm = mainSim.leaderboardManager;
            if (lm == null || !lm.IsLeaderBoardScreenOpen) return false;
            if (!mainSim.MightBeSimulating())
            {
                // OkPressed would restore a simulation that is no longer stored and break the game.
                Plugin.Log.LogWarning("Leaderboard screen is open but no simulation is stored; not dismissing");
                return false;
            }
            lm.OkPressed();
            return true;
        }

        // ── Request parsing for simulate / leaderboard modes ────

        private static string Normalize(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name)
            {
                if (c != '_' && c != ' ' && c != '-') sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>Strips an enum-style prefix so "Items.Cactus" and "cactus" both work.</summary>
        private static string StripPrefix(string name, string prefix)
        {
            name = name.Trim();
            if (name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase))
                return name.Substring(prefix.Length + 1);
            return name;
        }

        public static LeaderboardSO FindLeaderboard(string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new ApiException(400, "'leaderboard' is required for mode \"leaderboard\" (e.g. \"Hay_Single\")");

            string wanted = Normalize(StripPrefix(name, "Leaderboards"));
            var known = new List<string>();
            foreach (var lb in ResourceManager.GetAllLeaderboards())
            {
                if (lb == null || string.IsNullOrEmpty(lb.leaderboardName)) continue;
                if (Normalize(lb.leaderboardName) == wanted) return lb;
                known.Add(lb.leaderboardName);
            }
            throw new ApiException(400, $"Unknown leaderboard '{name}'. Known: {string.Join(", ", known)}");
        }

        /// <summary>The unlock list the game's own leaderboard_run() builds for a leaderboard.</summary>
        public static List<string> LeaderboardUnlocks(LeaderboardSO lb)
        {
            if (!lb.everythingUnlocked)
            {
                var reset = ResetUnlocks();
                if (reset == null)
                    throw new ApiException(501, "Cannot build the unlock list for this leaderboard on this game version (see compat_warnings in /status)");
                return reset;
            }
            var all = AllUnlocksViaGame(lb.singleDrone);
            if (all == null)
                throw new ApiException(501, "Leaderboard mode is unavailable on this game version (see compat_warnings in /status)");
            return all;
        }

        private static List<string> ResetUnlocks()
        {
            if (Compat.BuiltinResetUnlocks == null) return null;
            try
            {
                var list = Compat.BuiltinResetUnlocks.GetValue(null) as List<string>;
                return list == null ? null : new List<string>(list);
            }
            catch
            {
                return null;
            }
        }

        private static List<string> AllUnlocksViaGame(bool singleDrone)
        {
            if (Compat.BuiltinGetUnlockStrings == null) return null;
            try
            {
                IEnumerable<IPyObject> all = ResourceManager.GetAllUnlocks();
                return Compat.BuiltinGetUnlockStrings.Invoke(null, new object[] { all, singleDrone }) as List<string>;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"GetUnlockStrings failed: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Unlocks for simulate mode. Absent or null: everything at max level. Otherwise either
        /// an object of name -> level ({"speed": 3, "expand": 1}) or an array of names (max level),
        /// on top of the base set the game always grants a simulation.
        /// </summary>
        public static List<string> ParseUnlocks(object unlocksValue)
        {
            if (unlocksValue == null)
            {
                var all = AllUnlocksViaGame(false);
                if (all != null) return all;

                // Fallback if the game helper is gone.
                all = new List<string>();
                foreach (var u in ResourceManager.GetAllUnlocks())
                {
                    all.Add(u.unlockName);
                    if (u.IsMultiUnlock) all.Add($"{u.unlockName}_{u.maxUnlockLevel}");
                }
                return all;
            }

            var byName = new Dictionary<string, UnlockSO>(StringComparer.OrdinalIgnoreCase);
            foreach (var u in ResourceManager.GetAllUnlocks())
                byName[u.unlockName] = u;

            var requested = new List<KeyValuePair<string, double>>();
            var dict = unlocksValue as Dictionary<string, object>;
            var array = unlocksValue as List<object>;
            if (dict != null)
            {
                foreach (var kv in dict)
                {
                    if (!(kv.Value is double))
                        throw new ApiException(400, $"Unlock level for '{kv.Key}' must be a number");
                    requested.Add(new KeyValuePair<string, double>(kv.Key, (double)kv.Value));
                }
            }
            else if (array != null)
            {
                foreach (var entry in array)
                {
                    var name = entry as string;
                    if (name == null) throw new ApiException(400, "'unlocks' array entries must be strings");
                    requested.Add(new KeyValuePair<string, double>(name, -1));
                }
            }
            else
            {
                throw new ApiException(400, "'unlocks' must be an object of name -> level or an array of names");
            }

            var result = ResetUnlocks() ?? new List<string>();
            foreach (var kv in requested)
            {
                string key = StripPrefix(kv.Key, "Unlocks");
                UnlockSO unlock;
                if (!byName.TryGetValue(key, out unlock))
                    throw new ApiException(400, $"Unknown unlock '{kv.Key}'. Known: {string.Join(", ", byName.Keys)}");

                // Same rules as the game's simulate(): out of range means max, below 1 means locked.
                double level = kv.Value;
                if (level < 0 || level > unlock.maxUnlockLevel) level = unlock.maxUnlockLevel;
                else if (level < 1) continue;

                result.Add(unlock.unlockName);
                if (unlock.IsMultiUnlock) result.Add($"{unlock.unlockName}_{(int)level}");
            }
            return result;
        }

        /// <summary>Starting items for simulate mode: {"cactus": 1000000000, "Items.Power": 5}.</summary>
        public static ItemBlock ParseItems(Dictionary<string, object> itemsValue)
        {
            var items = ItemBlock.CreateEmpty();
            if (itemsValue == null) return items;

            var nameToId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in ResourceManager.GetAllItems())
            {
                if (item != null && !string.IsNullOrEmpty(item.itemName))
                    nameToId[item.itemName] = item.itemId;
            }

            foreach (var kv in itemsValue)
            {
                if (!(kv.Value is double))
                    throw new ApiException(400, $"Item amount for '{kv.Key}' must be a number");

                string key = StripPrefix(kv.Key, "Items");
                int id;
                if (!nameToId.TryGetValue(key, out id) || id < 0 || id >= items.items.Length)
                    throw new ApiException(400, $"Unknown item '{kv.Key}'. Known: {string.Join(", ", nameToId.Keys)}");

                items.items[id] = Math.Max(0.0, (double)kv.Value);
            }
            return items;
        }

        /// <summary>Global variables for simulate mode. Numbers, strings and booleans are supported.</summary>
        public static List<KeyValuePair<string, IPyObject>> ParseGlobals(Dictionary<string, object> globalsValue)
        {
            var result = new List<KeyValuePair<string, IPyObject>>();
            if (globalsValue == null) return result;

            foreach (var kv in globalsValue)
            {
                IPyObject value;
                if (kv.Value is double) value = new PyNumber((double)kv.Value);
                else if (kv.Value is string) value = new PyString((string)kv.Value);
                else if (kv.Value is bool) value = new PyBool((bool)kv.Value);
                else throw new ApiException(400, $"Global '{kv.Key}' must be a number, string or boolean");
                result.Add(new KeyValuePair<string, IPyObject>(kv.Key, value));
            }
            return result;
        }

        // ── Inventory ───────────────────────────────────────────

        /// <summary>JSON object of every item and its amount in the given simulation's farm.</summary>
        public static string GetInventoryJson(Simulation sim)
        {
            try
            {
                if (sim?.farm == null) return "{}";

                var itemBlock = sim.farm.Items;
                if (itemBlock == null) return "{}";

                var writer = new MiniJson.ObjectWriter();
                foreach (var itemSO in ResourceManager.GetAllItems())
                {
                    if (itemSO == null || string.IsNullOrEmpty(itemSO.itemName)) continue;
                    double amount = 0;
                    if (itemSO.itemId >= 0 && itemSO.itemId < itemBlock.items.Length)
                        amount = itemBlock.items[itemSO.itemId];
                    writer.Num(itemSO.itemName, amount);
                }
                return writer.ToString();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"GetInventoryJson error: {ex.Message}");
                return "{}";
            }
        }
    }
}
