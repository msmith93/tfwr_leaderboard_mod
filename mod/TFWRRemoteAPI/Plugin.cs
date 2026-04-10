using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;

namespace TFWRRemoteAPI
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.claudecode.tfwrapi";
        public const string PluginName = "TFWR Remote API";
        public const string PluginVersion = "1.0.0";
        public const int Port = 8787;

        public static Plugin Instance { get; private set; }
        public static ManualLogSource Log { get; private set; }

        private HttpListener _listener;
        private Thread _listenerThread;
        private volatile bool _running;

        // Main-thread dispatch queue
        private readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();

        // For synchronous main-thread calls from HTTP threads
        private readonly ConcurrentDictionary<string, ManualResetEventSlim> _pendingEvents
            = new ConcurrentDictionary<string, ManualResetEventSlim>();
        private readonly ConcurrentDictionary<string, string> _pendingResults
            = new ConcurrentDictionary<string, string>();

        // Track active run
        private volatile string _activeRunId = null;
        private volatile bool _runFinished = false;
        private volatile string _runMode = null;

        // Auto-dismiss leaderboard screen
        private float _leaderboardScreenOpenTime = -1f;
        private const float LeaderboardDismissDelay = 3f;

        private void Awake()
        {
            GameBridge.EnsureMainThread();

            Instance = this;
            Log = Logger;
            Log.LogInfo("TFWR Remote API plugin loaded!");

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{Port}/");
                _listener.Start();
                _running = true;

                _listenerThread = new Thread(ListenLoop)
                {
                    IsBackground = true,
                    Name = "TFWRRemoteAPI-HTTP"
                };
                _listenerThread.Start();

                Log.LogInfo($"HTTP API listening on http://localhost:{Port}/");
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to start HTTP listener: {ex.Message}");
            }
        }

        private void OnDestroy()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
        }

        private void Update()
        {
            GameBridge.EnsureMainThread();

            // Drain main-thread queue
            while (_mainThreadQueue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.LogError($"Main thread action error: {ex}");
                }
            }

            // Check if a run has finished
            if (_activeRunId != null && !_runFinished)
            {
                try
                {
                    var mainSim = MainSim.Inst;
                    if (mainSim != null)
                    {
                        bool finished;
                        if (_runMode == "simulate")
                        {
                            finished = !mainSim.MightBeSimulating();
                        }
                        else
                        {
                            finished = !mainSim.IsExecuting();
                        }
                        if (finished)
                        {
                            _runFinished = true;
                        }
                    }
                }
                catch { }
            }

            // Auto-dismiss leaderboard result screen after delay
            try
            {
                var mainSim = MainSim.Inst;
                if (mainSim != null && mainSim.leaderboardManager != null
                    && mainSim.leaderboardManager.IsLeaderBoardScreenOpen)
                {
                    if (_leaderboardScreenOpenTime < 0f)
                    {
                        _leaderboardScreenOpenTime = Time.time;
                        Log.LogInfo("Leaderboard screen detected, will auto-dismiss in 3s");
                    }
                    else if (Time.time - _leaderboardScreenOpenTime >= LeaderboardDismissDelay)
                    {
                        mainSim.leaderboardManager.OkPressed();
                        _leaderboardScreenOpenTime = -1f;
                        Log.LogInfo("Auto-dismissed leaderboard screen");
                    }
                }
                else
                {
                    _leaderboardScreenOpenTime = -1f;
                }
            }
            catch { }
        }

        // ── HTTP Listener Loop ──────────────────────────────────

        private void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    var ctx = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => HandleRequest(ctx));
                }
                catch (HttpListenerException) when (!_running)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_running) Log.LogError($"Listener error: {ex.Message}");
                }
            }
        }

        private void HandleRequest(HttpListenerContext ctx)
        {
            try
            {
                var path = ctx.Request.Url.AbsolutePath.TrimEnd('/');
                var method = ctx.Request.HttpMethod;

                string responseJson;
                switch (path)
                {
                    case "/status":
                        responseJson = HandleStatus();
                        break;
                    case "/run-script":
                        if (method != "POST")
                        {
                            responseJson = JsonError("POST required");
                            break;
                        }
                        responseJson = HandleRunScript(ctx.Request);
                        break;
                    case "/poll":
                        responseJson = HandlePoll();
                        break;
                    case "/output":
                        responseJson = HandleOutput();
                        break;
                    case "/inventory":
                        responseJson = HandleInventory();
                        break;
                    case "/stop":
                        responseJson = HandleStop();
                        break;
                    case "/delete-script":
                        if (method != "POST")
                        {
                            responseJson = JsonError("POST required");
                            break;
                        }
                        responseJson = HandleDeleteScript(ctx.Request);
                        break;
                    case "/dismiss":
                        responseJson = HandleDismiss();
                        break;
                    default:
                        responseJson = JsonError($"Unknown endpoint: {path}");
                        break;
                }

                SendResponse(ctx.Response, responseJson);
            }
            catch (Exception ex)
            {
                try
                {
                    SendResponse(ctx.Response, JsonError(ex.Message), 500);
                }
                catch { }
            }
        }

        // ── Endpoint Handlers ───────��───────────────────────────

        private string HandleStatus()
        {
            string result = RunOnMainThreadSync(() =>
            {
                bool graphicsReady = IsGraphicsReady();
                var mainSim = MainSim.Inst;
                if (mainSim == null)
                {
                    return $"{{\"ready\":false,\"graphics_ready\":{Bool(graphicsReady)}}}";
                }

                bool executing = mainSim.IsExecuting();
                bool simulating = mainSim.MightBeSimulating();
                string activeRun = _activeRunId ?? "";
                bool runDone = _runFinished;

                return $"{{\"ready\":true,\"graphics_ready\":{Bool(graphicsReady)},\"executing\":{Bool(executing)},\"simulating\":{Bool(simulating)},"
                     + $"\"active_run_id\":\"{Escape(activeRun)}\",\"run_finished\":{Bool(runDone)}}}";
            });
            return result;
        }

        /// <summary>
        /// True when Unity reports a real GPU (not Null). Opening the code editor / TMP under Proton
        /// crashes if this is false ("Graphics device is null" in player logs).
        /// </summary>
        private static bool IsGraphicsReady()
        {
            try
            {
                if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                    return false;
                string name = SystemInfo.graphicsDeviceName;
                if (string.IsNullOrEmpty(name))
                    return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private string HandleRunScript(HttpListenerRequest request)
        {
            string body;
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            {
                body = reader.ReadToEnd();
            }

            // Simple JSON parsing (no external deps in .NET 4.6 BepInEx)
            var code = ExtractJsonString(body, "code");
            var filename = ExtractJsonString(body, "filename") ?? "claude_diag";
            var mode = ExtractJsonString(body, "mode") ?? "execute";
            var seedStr = ExtractJsonString(body, "seed");
            int seed = -1;
            if (seedStr != null) int.TryParse(seedStr, out seed);

            if (string.IsNullOrEmpty(code))
                return JsonError("'code' field is required");

            // Default true: Parser.Parse is reliable with a real code window. GET /status graphics_ready
            // and the run-script guard ensure the GPU is up before OpenCodeFile/TMP run under Proton.
            bool openEditor = ExtractJsonBool(body, "open_editor", true);

            string runId = Guid.NewGuid().ToString("N").Substring(0, 8);

            string result = RunOnMainThreadSync(() =>
            {
                try
                {
                    var mainSim = MainSim.Inst;
                    if (mainSim == null)
                        return JsonError("MainSim not ready");

                    // Stop any existing execution
                    if (mainSim.IsExecuting())
                        mainSim.StopMainExecution();
                    if (mainSim.MightBeSimulating())
                        mainSim.RestoreMainSim();

                    // Write code to save file
                    string savePath = GameBridge.GetActiveSavePath();
                    string filePath = Path.Combine(savePath, filename + ".py");
                    File.WriteAllText(filePath, code);

                    // Sync workspace from files on disk
                    Saver.ApplyCodeChanges(mainSim.workspace);

                    if (!IsGraphicsReady())
                        return JsonError("Graphics device not ready; poll GET /status until graphics_ready is true before run-script.");

                    CodeWindow scratchForParse = null;

                    if (openEditor)
                    {
                        if (mainSim.workspace.codeWindows.TryGetValue(filename, out var existingCw))
                        {
                            GameBridge.ReactivateCodeWindow(existingCw, code);
                        }
                        else
                        {
                            try
                            {
                                GameBridge.OpenCodeFile(mainSim, filename, code);
                            }
                            catch (Exception openEx)
                            {
                                Log.LogWarning($"OpenCodeFile failed for '{filename}': {openEx.Message}");
                            }
                            if (mainSim.workspace.codeWindows.TryGetValue(filename, out var newCw))
                            {
                                newCw.CodeInput.text = code;
                            }
                            else
                            {
                                Log.LogWarning($"Code window not available for '{filename}', will use direct parse for execution");
                            }
                        }
                    }
                    else
                    {
                        Log.LogInfo($"open_editor=false: UI hidden for '{filename}'; reusing any CodeWindow for Parser.Parse (Core requires CodeWindow)");
                        foreach (var kv in mainSim.workspace.codeWindows)
                        {
                            scratchForParse = kv.Value;
                            break;
                        }
                        if (scratchForParse == null)
                        {
                            try
                            {
                                GameBridge.OpenCodeFile(mainSim, "__api_scratch", "# api scratch\n");
                            }
                            catch (Exception openEx)
                            {
                                return JsonError($"open_editor=false needs a CodeWindow for parsing; could not create scratch: {openEx.Message}");
                            }
                            if (!mainSim.workspace.codeWindows.TryGetValue("__api_scratch", out scratchForParse)
                                || scratchForParse == null)
                            {
                                return JsonError("open_editor=false: scratch CodeWindow missing after OpenCodeFile");
                            }
                        }
                    }

                    // Clear output
                    global::Logger.Clear();

                    _activeRunId = runId;
                    _runFinished = false;
                    _runMode = mode;

                    if (mode == "simulate")
                    {
                        var unlockList = GameBridge.ParseUnlocks(body);
                        var items = GameBridge.ParseItems(body);

                        var args = new MainSim.LeaderboardStartArgs(
                            filename,
                            unlockList,
                            items,
                            new List<KeyValuePair<string, IPyObject>>(),
                            "",  // leaderboardName
                            "",  // steamLeaderboardName
                            LeaderboardType.simulation,
                            seed
                        );
                        Log.LogInfo($"Scheduling leaderboard start for '{filename}' with {unlockList.Count} unlocks, seed={seed}");
                        Log.LogInfo($"Items: cactus={items.items[4]}, power={items.items[10]}");
                        mainSim.ScheduleLeaderboardStart(args);
                        Log.LogInfo("ScheduleLeaderboardStart called successfully");
                    }
                    else
                    {
                        // Direct execution: Parser.Parse requires a CodeWindow instance in Core.dll.
                        if (openEditor && mainSim.workspace.codeWindows.TryGetValue(filename, out var codeWindow))
                        {
                            var node = codeWindow.Parse();
                            if (node != null)
                            {
                                mainSim.StartMainExecution(codeWindow, node);
                            }
                            else
                            {
                                return JsonError("Parse error in script");
                            }
                        }
                        else
                        {
                            var parseCw = scratchForParse;
                            if (parseCw == null && mainSim.workspace.codeWindows.TryGetValue(filename, out var namedCw))
                                parseCw = namedCw;
                            if (parseCw == null)
                                return JsonError("No CodeWindow available for parse (open_editor=false with empty workspace?)");

                            var node = GameBridge.ParseCodeDirect(code, parseCw);
                            if (node != null)
                            {
                                mainSim.StartMainExecution(null, node);
                                Log.LogInfo($"Executing '{filename}' via direct parse (scratch CodeWindow)");
                            }
                            else
                            {
                                return JsonError("Parse error in script (direct parse)");
                            }
                        }
                    }

                    return $"{{\"started\":true,\"run_id\":\"{runId}\",\"mode\":\"{Escape(mode)}\",\"open_editor\":{Bool(openEditor)}}}";
                }
                catch (Exception ex)
                {
                    return JsonError($"Run failed: {ex.Message}\n{ex.StackTrace}");
                }
            });

            return result;
        }

        private string HandlePoll()
        {
            string result = RunOnMainThreadSync(() =>
            {
                try
                {
                    string runId = _activeRunId ?? "";
                    bool finished = _runFinished;
                    string output = global::Logger.GetOutputString() ?? "";

                    var mainSim = MainSim.Inst;
                    double timeSeconds = 0;
                    string itemsJson = "{}";

                    if (mainSim != null)
                    {
                        // Try to read sim time from current or stored sim
                        var sim = GameBridge.GetActiveSim(mainSim);
                        if (sim != null)
                        {
                            timeSeconds = sim.CurrentTime.Seconds;
                            itemsJson = GameBridge.GetInventoryJson(sim);
                        }
                    }

                    return $"{{\"run_id\":\"{Escape(runId)}\",\"finished\":{Bool(finished)},"
                         + $"\"time_seconds\":{timeSeconds:F6},"
                         + $"\"output\":\"{Escape(output)}\","
                         + $"\"items\":{itemsJson}}}";
                }
                catch (Exception ex)
                {
                    return JsonError($"Poll error: {ex.Message}");
                }
            });
            return result;
        }

        private string HandleOutput()
        {
            string result = RunOnMainThreadSync(() =>
            {
                string output = global::Logger.GetOutputString() ?? "";
                return $"{{\"output\":\"{Escape(output)}\"}}";
            });
            return result;
        }

        private string HandleInventory()
        {
            string result = RunOnMainThreadSync(() =>
            {
                try
                {
                    var mainSim = MainSim.Inst;
                    if (mainSim == null)
                        return JsonError("MainSim not ready");

                    var sim = GameBridge.GetActiveSim(mainSim);
                    if (sim == null)
                        return JsonError("No active simulation");

                    return $"{{\"items\":{GameBridge.GetInventoryJson(sim)}}}";
                }
                catch (Exception ex)
                {
                    return JsonError($"Inventory error: {ex.Message}");
                }
            });
            return result;
        }

        private string HandleStop()
        {
            string result = RunOnMainThreadSync(() =>
            {
                try
                {
                    var mainSim = MainSim.Inst;
                    if (mainSim == null)
                        return JsonError("MainSim not ready");

                    if (mainSim.MightBeSimulating())
                        mainSim.RestoreMainSim();
                    if (mainSim.IsExecuting())
                        mainSim.StopMainExecution();

                    _activeRunId = null;
                    _runFinished = true;
                    return "{\"stopped\":true}";
                }
                catch (Exception ex)
                {
                    return JsonError($"Stop error: {ex.Message}");
                }
            });
            return result;
        }

        private string HandleDismiss()
        {
            string result = RunOnMainThreadSync(() =>
            {
                try
                {
                    var mainSim = MainSim.Inst;
                    if (mainSim == null)
                        return JsonError("MainSim not ready");

                    bool dismissed = GameBridge.DismissLeaderboardScreen(mainSim);
                    return $"{{\"dismissed\":{Bool(dismissed)}}}";
                }
                catch (Exception ex)
                {
                    return JsonError($"Dismiss error: {ex.Message}");
                }
            });
            return result;
        }

        private string HandleDeleteScript(HttpListenerRequest request)
        {
            string body;
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            {
                body = reader.ReadToEnd();
            }

            var filename = ExtractJsonString(body, "filename");
            if (string.IsNullOrEmpty(filename))
                return JsonError("'filename' field is required");

            string result = RunOnMainThreadSync(() =>
            {
                try
                {
                    var mainSim = MainSim.Inst;
                    if (mainSim == null)
                        return JsonError("MainSim not ready");

                    string savePath = GameBridge.GetActiveSavePath();
                    string filePath = Path.Combine(savePath, filename + ".py");

                    bool fileDeleted = false;
                    bool windowClosed = false;

                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                        fileDeleted = true;
                        Log.LogInfo($"Deleted file: {filePath}");
                    }

                    // Hide the code window (keeps it in the dict for potential reuse)
                    if (mainSim.workspace.codeWindows.TryGetValue(filename, out var cw))
                    {
                        GameBridge.CloseCodeWindow(mainSim, filename, cw);
                        windowClosed = true;
                    }

                    return $"{{\"deleted\":true,\"filename\":\"{Escape(filename)}\","
                         + $"\"file_deleted\":{Bool(fileDeleted)},\"window_closed\":{Bool(windowClosed)}}}";
                }
                catch (Exception ex)
                {
                    return JsonError($"Delete failed: {ex.Message}");
                }
            });
            return result;
        }

        // ── Main Thread Sync ────────────────────────────────────

        private string RunOnMainThreadSync(Func<string> func, int timeoutMs = 10000)
        {
            int mainId = GameBridge.MainThreadId;
            if (mainId != 0 && Thread.CurrentThread.ManagedThreadId == mainId)
            {
                return func();
            }

            string callId = Guid.NewGuid().ToString("N");
            var evt = new ManualResetEventSlim(false);
            _pendingEvents[callId] = evt;

            _mainThreadQueue.Enqueue(() =>
            {
                try
                {
                    _pendingResults[callId] = func();
                }
                catch (Exception ex)
                {
                    _pendingResults[callId] = JsonError(ex.Message);
                }
                finally
                {
                    evt.Set();
                }
            });

            if (!evt.Wait(timeoutMs))
            {
                _pendingEvents.TryRemove(callId, out _);
                return JsonError("Main thread call timed out");
            }

            _pendingEvents.TryRemove(callId, out _);
            _pendingResults.TryRemove(callId, out var result);
            evt.Dispose();
            return result ?? JsonError("No result");
        }

        // ── JSON Helpers ───────────��────────────────────────────

        private static string JsonError(string msg)
        {
            return $"{{\"error\":\"{Escape(msg)}\"}}";
        }

        private static string Bool(bool v) => v ? "true" : "false";

        private static string Escape(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\n", "\\n")
                    .Replace("\r", "\\r")
                    .Replace("\t", "\\t");
        }

        /// <summary>
        /// Reads a JSON boolean for the given key. Returns defaultValue if the key is missing or not true/false.
        /// </summary>
        internal static bool ExtractJsonBool(string json, string key, bool defaultValue)
        {
            string pattern = "\"" + key + "\"";
            int idx = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return defaultValue;
            idx += pattern.Length;
            while (idx < json.Length && (json[idx] == ' ' || json[idx] == ':' || json[idx] == '\t'))
                idx++;

            if (idx + 4 <= json.Length &&
                string.Compare(json, idx, "true", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
                return true;
            if (idx + 5 <= json.Length &&
                string.Compare(json, idx, "false", 0, 5, StringComparison.OrdinalIgnoreCase) == 0)
                return false;
            return defaultValue;
        }

        // Minimal JSON string extractor (no external JSON lib in .NET 4.6)
        internal static string ExtractJsonString(string json, string key)
        {
            string pattern = "\"" + key + "\"";
            int idx = json.IndexOf(pattern);
            if (idx < 0) return null;
            idx += pattern.Length;

            // Skip whitespace and colon
            while (idx < json.Length && (json[idx] == ' ' || json[idx] == ':' || json[idx] == '\t'))
                idx++;

            if (idx >= json.Length) return null;

            if (json[idx] == '"')
            {
                // String value
                idx++;
                var sb = new StringBuilder();
                while (idx < json.Length && json[idx] != '"')
                {
                    if (json[idx] == '\\' && idx + 1 < json.Length)
                    {
                        idx++;
                        switch (json[idx])
                        {
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case '\\': sb.Append('\\'); break;
                            case '"': sb.Append('"'); break;
                            default: sb.Append(json[idx]); break;
                        }
                    }
                    else
                    {
                        sb.Append(json[idx]);
                    }
                    idx++;
                }
                return sb.ToString();
            }
            else
            {
                // Number or literal
                var sb = new StringBuilder();
                while (idx < json.Length && json[idx] != ',' && json[idx] != '}' && json[idx] != ' ')
                {
                    sb.Append(json[idx]);
                    idx++;
                }
                return sb.ToString();
            }
        }

        private static void SendResponse(HttpListenerResponse response, string json, int statusCode = 200)
        {
            response.StatusCode = statusCode;
            response.ContentType = "application/json";
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            var buffer = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }
    }
}
