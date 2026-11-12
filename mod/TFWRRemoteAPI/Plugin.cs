using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace TFWRRemoteAPI
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.claudecode.tfwrapi";
        public const string PluginName = "TFWR Remote API";
        public const string PluginVersion = "1.1.0";
        public const int DefaultPort = 8787;

        public static Plugin Instance { get; private set; }
        public static ManualLogSource Log { get; private set; }

        // Settings (BepInEx/config/com.claudecode.tfwrapi.cfg)
        private ConfigEntry<int> _port;
        private ConfigEntry<bool> _autoDismiss;
        private ConfigEntry<float> _dismissDelay;
        private ConfigEntry<int> _mainThreadTimeoutMs;

        private HttpListener _listener;
        private Thread _listenerThread;
        private volatile bool _running;

        private readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();

        // Captured on the main thread at startup; read from HTTP threads.
        private string _gameVersion = "";
        private string _unityVersion = "";
        private string _outputPath = "";

        // Main-thread only.
        private int _startingFrames;
        private float _leaderboardScreenOpenTime = -1f;
        // The result screen is only auto-dismissed for runs the API started: armed while such
        // a run is active and for a short grace period after it ends or is stopped.
        private float _dismissArmedUntil = -1f;
        // Set by /load-save: close the title menu once the reloaded scene has settled.
        private int _closeMenuCountdown = -1;
        private const float DismissGraceSeconds = 15f;

        // Without ?since=, /poll and /output return the whole output file; cap that.
        private const long MaxFullOutputBytes = 8L * 1024 * 1024;

        private struct ApiResponse
        {
            public int Status;
            public string Json;

            public static ApiResponse Ok(string json)
            {
                return new ApiResponse { Status = 200, Json = json };
            }

            public static ApiResponse Error(int status, string message)
            {
                return new ApiResponse
                {
                    Status = status,
                    Json = new MiniJson.ObjectWriter().Str("error", message).ToString()
                };
            }
        }

        private void Awake()
        {
            GameBridge.EnsureMainThread();

            Instance = this;
            Log = Logger;

            _port = Config.Bind("General", "Port", DefaultPort, "Port for the HTTP API on localhost.");
            _autoDismiss = Config.Bind("Leaderboard", "AutoDismissResultScreen", true,
                "Close the leaderboard result screen automatically after runs started through the API. Runs you start by hand are never auto-dismissed.");
            _dismissDelay = Config.Bind("Leaderboard", "DismissDelaySeconds", 3f,
                "How long the result screen stays up before it is auto-dismissed.");
            _mainThreadTimeoutMs = Config.Bind("General", "MainThreadTimeoutMs", 10000,
                "How long an API request waits for the game's main thread before giving up.");

            _gameVersion = Application.version ?? "";
            _unityVersion = Application.unityVersion ?? "";
            _outputPath = Path.Combine(Application.persistentDataPath, "output.txt");

            Log.LogInfo($"{PluginName} {PluginVersion} loaded (game {_gameVersion}, Unity {_unityVersion})");

            Compat.Init();
            GameHooks.Install(PluginGuid);
            if (Compat.Warnings.Count == 0)
                Log.LogInfo("Compatibility check passed: all game internals found");
            else
                Log.LogError($"Compatibility check found {Compat.Warnings.Count} problem(s); see GET /status compat_warnings");

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{_port.Value}/");
                _listener.Start();
                _running = true;

                _listenerThread = new Thread(ListenLoop)
                {
                    IsBackground = true,
                    Name = "TFWRRemoteAPI-HTTP"
                };
                _listenerThread.Start();

                Log.LogInfo($"HTTP API listening on http://localhost:{_port.Value}/");
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

            try
            {
                var mainSim = MainSim.Inst;
                if (mainSim != null)
                {
                    TrackRun(mainSim);
                    AutoDismiss(mainSim);
                    CloseMenuWhenLoaded(mainSim);
                    GameBridge.UpdateCamera(mainSim);
                }
            }
            catch (Exception ex)
            {
                Log.LogError($"Update error: {ex}");
            }
        }

        private void OnGUI()
        {
            try
            {
                Overlay.Draw();
            }
            catch (Exception ex)
            {
                // Never let a drawing problem spam every frame.
                Overlay.Caption = null;
                Overlay.ClearScoreboard();
                Log.LogError($"Overlay disabled after error: {ex}");
            }
        }

        /// <summary>Advance the run state machine: starting -> running -> finished | error.</summary>
        private void TrackRun(MainSim mainSim)
        {
            string state, mode, errorText;
            lock (RunState.Lock)
            {
                state = RunState.State;
                mode = RunState.Mode;
                errorText = RunState.ErrorText;
            }

            if (state == RunState.Starting)
            {
                // simulate/leaderboard modes are only *scheduled* by the request; MainSim.Update
                // starts them on a later frame, so "not simulating yet" does not mean finished.
                if (mainSim.MightBeSimulating())
                {
                    state = RunState.Running;
                    SetState(state);
                }
                else if (++_startingFrames > 2 && !GameBridge.IsLeaderboardStartPending(mainSim))
                {
                    lock (RunState.Lock)
                    {
                        if (RunState.ErrorText == null)
                            RunState.ErrorText = "The game did not start the run (the file may have failed to parse)";
                    }
                    EndRun(RunState.Error);
                    return;
                }
            }

            if (state != RunState.Running) return;

            if (errorText != null)
            {
                // A runtime error pauses the simulation without ending the execution, so the
                // game still reports "executing". Report it as a finished, failed run.
                EndRun(RunState.Error);
                return;
            }

            bool finished = mode == "execute" ? !mainSim.IsExecuting() : !mainSim.MightBeSimulating();
            if (finished) EndRun(RunState.Finished);
        }

        private static void SetState(string state)
        {
            lock (RunState.Lock) { RunState.State = state; }
        }

        private void EndRun(string state)
        {
            SetState(state);
            _dismissArmedUntil = Time.time + DismissGraceSeconds;
        }

        private void CloseMenuWhenLoaded(MainSim mainSim)
        {
            if (_closeMenuCountdown < 0) return;
            // Menu.Start (which opens the title menu and loads the save) runs on the first
            // frames of the new scene; give it time before closing the menu.
            if (_closeMenuCountdown-- > 0) return;
            if (GameBridge.IsMenuOpen(mainSim) && mainSim.menu != null)
            {
                mainSim.menu.Play();
                Log.LogInfo("Closed the title menu after loading the save");
            }
        }

        private void AutoDismiss(MainSim mainSim)
        {
            var lm = mainSim.leaderboardManager;
            bool armed = _autoDismiss.Value && (RunState.IsActive || Time.time <= _dismissArmedUntil);
            if (!armed || lm == null || !lm.IsLeaderBoardScreenOpen)
            {
                _leaderboardScreenOpenTime = -1f;
                return;
            }

            if (_leaderboardScreenOpenTime < 0f)
            {
                _leaderboardScreenOpenTime = Time.time;
                Log.LogInfo($"Leaderboard screen detected, will auto-dismiss in {_dismissDelay.Value}s");
            }
            else if (Time.time - _leaderboardScreenOpenTime >= _dismissDelay.Value)
            {
                _leaderboardScreenOpenTime = -1f;
                if (GameBridge.DismissLeaderboardScreen(mainSim))
                    Log.LogInfo("Auto-dismissed leaderboard screen");
            }
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
                catch (Exception ex)
                {
                    if (!_running) break;
                    Log.LogError($"Listener error: {ex.Message}");
                    Thread.Sleep(250); // do not spin if the listener is broken
                }
            }
        }

        private void HandleRequest(HttpListenerContext ctx)
        {
            ApiResponse response;
            try
            {
                response = Route(ctx.Request);
            }
            catch (MiniJson.JsonException ex)
            {
                response = ApiResponse.Error(400, "Invalid JSON: " + ex.Message);
            }
            catch (ApiException ex)
            {
                response = ApiResponse.Error(ex.Status, ex.Message);
            }
            catch (Exception ex)
            {
                Log.LogError($"Request error: {ex}");
                response = ApiResponse.Error(500, ex.Message);
            }

            try
            {
                SendResponse(ctx.Response, response);
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Could not send response: {ex.Message}");
                try { ctx.Response.Abort(); } catch { }
            }
        }

        private ApiResponse Route(HttpListenerRequest request)
        {
            string path = request.Url.AbsolutePath.TrimEnd('/');
            string method = request.HttpMethod;

            switch (path)
            {
                case "/status":
                    return HandleStatus();
                case "/run-script":
                    if (method != "POST") return ApiResponse.Error(405, "POST required");
                    return HandleRunScript(ReadBody(request));
                case "/poll":
                    return HandlePoll(ParseSince(request));
                case "/output":
                    return HandleOutput(ParseSince(request));
                case "/inventory":
                    return HandleInventory();
                case "/stop":
                    return HandleStop();
                case "/delete-script":
                    if (method != "POST") return ApiResponse.Error(405, "POST required");
                    return HandleDeleteScript(ReadBody(request));
                case "/dismiss":
                    return HandleDismiss();
                case "/saves":
                    return HandleSaves();
                case "/overlay":
                    if (method != "POST") return ApiResponse.Error(405, "POST required");
                    return HandleOverlay(ReadBody(request));
                case "/camera":
                    if (method != "POST") return HandleCameraGet();
                    return HandleCamera(ReadBody(request));
                case "/screenshot":
                    return HandleScreenshot();
                case "/leaderboard":
                    return HandleLeaderboard(request);
                case "/load-save":
                    if (method != "POST") return ApiResponse.Error(405, "POST required");
                    return HandleLoadSave(ReadBody(request));
                default:
                    return ApiResponse.Error(404, $"Unknown endpoint: {path}");
            }
        }

        private static string ReadBody(HttpListenerRequest request)
        {
            // Always UTF-8: without a charset in Content-Type, ContentEncoding falls back to
            // the system code page and mangles non-ASCII script text.
            using (var reader = new StreamReader(request.InputStream, new UTF8Encoding(false)))
            {
                return reader.ReadToEnd();
            }
        }

        /// <summary>Returns the ?since= byte offset, or -1 when it was not given.</summary>
        private static long ParseSince(HttpListenerRequest request)
        {
            string raw = request.QueryString["since"];
            if (string.IsNullOrEmpty(raw)) return -1;
            long since;
            if (!long.TryParse(raw, out since) || since < 0)
                throw new ApiException(400, "'since' must be a non-negative integer byte offset");
            return since;
        }

        // ── Endpoint Handlers ───────────────────────────────────

        private ApiResponse HandleStatus()
        {
            return RunOnMainThreadSync(() =>
            {
                var writer = new MiniJson.ObjectWriter();
                var mainSim = MainSim.Inst;
                writer.Bool("ready", mainSim != null);
                writer.Bool("graphics_ready", IsGraphicsReady());
                if (mainSim != null)
                {
                    writer.Bool("executing", mainSim.IsExecuting());
                    writer.Bool("simulating", mainSim.MightBeSimulating());
                    writer.Bool("menu_open", GameBridge.IsMenuOpen(mainSim));
                }
                writer.Str("active_save", GameBridge.GetActiveSaveName());
                lock (RunState.Lock)
                {
                    writer.Str("active_run_id", RunState.RunId ?? "");
                    writer.Str("state", RunState.State);
                    writer.Bool("run_finished", IsTerminal(RunState.State));
                }
                writer.Str("plugin_version", PluginVersion);
                writer.Str("game_version", _gameVersion);
                writer.Str("unity_version", _unityVersion);
                writer.StrArray("compat_warnings", Compat.Warnings);
                return ApiResponse.Ok(writer.ToString());
            });
        }

        private static bool IsTerminal(string state)
        {
            return state == RunState.Finished || state == RunState.Stopped || state == RunState.Error;
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
                return !string.IsNullOrEmpty(SystemInfo.graphicsDeviceName);
            }
            catch
            {
                return false;
            }
        }

        private ApiResponse HandleRunScript(string body)
        {
            var req = MiniJson.ParseObject(body);

            string code = MiniJson.GetString(req, "code");
            string filename = MiniJson.GetString(req, "filename") ?? "claude_diag";
            if (filename.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
                filename = filename.Substring(0, filename.Length - 3);
            string mode = (MiniJson.GetString(req, "mode") ?? "execute").ToLowerInvariant();
            bool openEditor = MiniJson.GetBool(req, "open_editor", true);
            int seed = (int)MiniJson.GetNumber(req, "seed", -1);
            double speedup = MiniJson.GetNumber(req, "speedup", 1);

            if (filename.Length == 0)
                return ApiResponse.Error(400, "'filename' must not be empty");
            if (mode != "execute" && mode != "simulate" && mode != "leaderboard")
                return ApiResponse.Error(400, $"Unknown mode '{mode}'. Use \"execute\", \"simulate\" or \"leaderboard\".");
            if (mode == "execute" && string.IsNullOrEmpty(code))
                return ApiResponse.Error(400, "'code' field is required");

            string runId = Guid.NewGuid().ToString("N").Substring(0, 8);

            return RunOnMainThreadSync(() =>
            {
                var mainSim = MainSim.Inst;
                if (mainSim == null)
                    return ApiResponse.Error(503, "MainSim not ready");
                if (!IsGraphicsReady())
                    return ApiResponse.Error(503, "Graphics device not ready; poll GET /status until graphics_ready is true before run-script.");

                // Everything that can be rejected is checked before the game is touched, so a
                // bad request leaves a running script alone. (A parse error is the exception:
                // parsing needs the code in a window, which replaces what was running.)
                MainSim.LeaderboardStartArgs startArgs = null;
                if (mode == "simulate")
                {
                    object unlocksValue;
                    req.TryGetValue("unlocks", out unlocksValue);
                    startArgs = new MainSim.LeaderboardStartArgs(
                        filename,
                        GameBridge.ParseUnlocks(unlocksValue),
                        GameBridge.ParseItems(MiniJson.GetObject(req, "items")),
                        GameBridge.ParseGlobals(MiniJson.GetObject(req, "globals")),
                        "",  // leaderboardName
                        "",  // steamLeaderboardName
                        LeaderboardType.simulation,
                        true,  // fullyGenerateUnderground, as the simulate() builtin passes
                        Platforms.EDlcType.None,
                        seed);
                }
                else if (mode == "leaderboard")
                {
                    var lb = GameBridge.FindLeaderboard(MiniJson.GetString(req, "leaderboard"));
                    string requiredUnlock = lb.dlc == Platforms.EDlcType.Mining ? "underground_leaderboard" : "leaderboard";
                    if (!mainSim.IsUnlocked(requiredUnlock))
                        return ApiResponse.Error(409, $"The '{requiredUnlock}' unlock is required to run this leaderboard (same rule as leaderboard_run()).");

                    startArgs = new MainSim.LeaderboardStartArgs(
                        filename,
                        GameBridge.LeaderboardUnlocks(lb),
                        new ItemBlock(lb.startItems),
                        new List<KeyValuePair<string, IPyObject>>(),
                        lb.leaderboardName,
                        lb.steamLeaderboardName,
                        lb.leaderboardType,
                        lb.fullyGenerateUnderground,
                        lb.dlc,
                        -1,
                        lb.singleDrone,
                        lb.whitelistedGroundFeatureUnlocks,
                        lb.pretendedFeatureChunks,
                        lb.pretendedChunkDepths);
                }

                if (code == null && !mainSim.workspace.codeWindows.ContainsKey(filename))
                    return ApiResponse.Error(400, $"No 'code' given and no file named '{filename}' is open in the game");

                GameBridge.StopEverything(mainSim);
                if (RunState.IsActive) EndRun(RunState.Stopped);

                if (code != null)
                    File.WriteAllText(Path.Combine(GameBridge.GetActiveSavePath(), filename + ".py"), code);

                // simulate/leaderboard always need the file's own window: the game looks the
                // file up by window name when it starts the run.
                CodeWindow window = null;
                CodeWindow scratch = null;
                if (openEditor || mode != "execute")
                {
                    window = GameBridge.EnsureCodeWindow(mainSim, filename, code);
                    if (window == null)
                        return ApiResponse.Error(500, $"Could not open a code window for '{filename}' (see BepInEx/LogOutput.log)");
                }
                else
                {
                    foreach (var kv in mainSim.workspace.codeWindows)
                    {
                        scratch = kv.Value;
                        break;
                    }
                    if (scratch == null)
                        scratch = GameBridge.EnsureCodeWindow(mainSim, "__api_scratch", "# api scratch\n");
                    if (scratch == null)
                        return ApiResponse.Error(500, "open_editor=false needs some CodeWindow for parsing and none could be created");
                }

                mainSim.Logger.Clear();

                Node node;
                if (window != null)
                {
                    string parseError;
                    int line;
                    node = GameBridge.ParseWindow(window, out parseError, out line);
                    if (node == null)
                    {
                        var err = new MiniJson.ObjectWriter().Str("error", parseError);
                        if (line > 0) err.Num("line", line);
                        return new ApiResponse { Status = 400, Json = err.ToString() };
                    }
                }
                else
                {
                    string parseError;
                    node = GameBridge.ParseCodeDirect(code, scratch, out parseError);
                    if (node == null)
                        return ApiResponse.Error(400, parseError);
                }

                _dismissArmedUntil = float.PositiveInfinity;
                _startingFrames = 0;
                try
                {
                    if (mode == "execute")
                    {
                        RunState.Begin(runId, mode, RunState.Running);
                        mainSim.StartMainExecution(window, node);
                    }
                    else
                    {
                        RunState.Begin(runId, mode, RunState.Starting);
                        mainSim.ScheduleLeaderboardStart(startArgs);
                        mainSim.TimeFactor = speedup;
                    }
                }
                catch
                {
                    EndRun(RunState.Error);
                    throw;
                }
                Log.LogInfo($"Started run {runId}: mode={mode} file='{filename}'");

                return ApiResponse.Ok(new MiniJson.ObjectWriter()
                    .Bool("started", true)
                    .Str("run_id", runId)
                    .Str("mode", mode)
                    .Str("filename", filename)
                    .Bool("open_editor", window != null)
                    .ToString());
            });
        }

        private ApiResponse HandlePoll(long since)
        {
            var response = RunOnMainThreadSync(() =>
            {
                var writer = new MiniJson.ObjectWriter();
                AppendRunState(writer);

                var mainSim = MainSim.Inst;
                double timeSeconds = 0;
                string itemsJson = "{}";
                int runsSoFar = 0;
                if (mainSim != null)
                {
                    runsSoFar = mainSim.numLeaderboardRuns;
                    var sim = GameBridge.GetActiveSim(mainSim);
                    if (sim != null)
                    {
                        timeSeconds = sim.CurrentTime.Seconds;
                        itemsJson = GameBridge.GetInventoryJson(sim);
                    }
                }
                writer.Num("time_seconds", timeSeconds);
                writer.Num("leaderboard_runs_so_far", runsSoFar);
                writer.Num("leaderboard_total_time_seconds", mainSim != null ? mainSim.totalLeaderboardTime.Seconds : 0);
                writer.Raw("items", itemsJson);
                return ApiResponse.Ok(writer.ToString());
            });
            if (response.Status != 200) return response;

            // Output is read on the HTTP thread; splice it into the object built above.
            var output = new MiniJson.ObjectWriter();
            AppendOutput(output, since);
            string head = response.Json.Substring(0, response.Json.Length - 1);
            string tail = output.ToString().Substring(1);
            return ApiResponse.Ok(head + "," + tail);
        }

        private static void AppendRunState(MiniJson.ObjectWriter writer)
        {
            lock (RunState.Lock)
            {
                writer.Str("run_id", RunState.RunId ?? "");
                writer.Str("mode", RunState.Mode ?? "");
                writer.Str("state", RunState.State);
                writer.Bool("finished", IsTerminal(RunState.State));
                writer.Str("error", RunState.ErrorText);
                if (RunState.SimTimeSeconds.HasValue)
                    writer.Num("sim_time_seconds", RunState.SimTimeSeconds.Value);
                else
                    writer.Raw("sim_time_seconds", "null");
                writer.NumArray("run_times", RunState.RunTimes);
                if (RunState.HasLeaderboardResult)
                {
                    writer.Raw("leaderboard", new MiniJson.ObjectWriter()
                        .Str("name", RunState.LeaderboardName)
                        .Bool("completed", RunState.LeaderboardCompleted)
                        .Num("average_time_seconds", RunState.LeaderboardAverageSeconds)
                        .Num("runs", RunState.LeaderboardRuns)
                        .ToString());
                }
                else
                {
                    writer.Raw("leaderboard", "null");
                }
            }
        }

        private ApiResponse HandleOutput(long since)
        {
            var writer = new MiniJson.ObjectWriter();
            AppendOutput(writer, since);
            return ApiResponse.Ok(writer.ToString());
        }

        /// <summary>
        /// Adds "output", "next_offset", "output_source" and "output_truncated".
        ///
        /// The game's in-memory log keeps only the last ~100 messages, which silently drops
        /// lines on long runs, so output is read from the game's own output.txt (complete since
        /// the last run started). <paramref name="since"/> is a byte offset from a previous
        /// "next_offset"; -1 means everything.
        /// </summary>
        private void AppendOutput(MiniJson.ObjectWriter writer, long since)
        {
            try
            {
                if (File.Exists(_outputPath))
                {
                    using (var fs = new FileStream(_outputPath, FileMode.Open, FileAccess.Read,
                               FileShare.ReadWrite | FileShare.Delete))
                    {
                        long length = fs.Length;
                        bool truncated = false;
                        long start = since;
                        if (start < 0)
                        {
                            start = 0;
                            if (length > MaxFullOutputBytes)
                            {
                                start = length - MaxFullOutputBytes;
                                truncated = true;
                            }
                        }
                        else if (start > length)
                        {
                            // The file was cleared by a new run since the caller's offset.
                            start = 0;
                        }

                        fs.Seek(start, SeekOrigin.Begin);
                        var buffer = new byte[length - start];
                        int read = 0;
                        while (read < buffer.Length)
                        {
                            int n = fs.Read(buffer, read, buffer.Length - read);
                            if (n <= 0) break;
                            read += n;
                        }

                        string text = Encoding.UTF8.GetString(buffer, 0, read).Replace("\r\n", "\n");
                        writer.Str("output", text);
                        writer.Num("next_offset", start + read);
                        writer.Str("output_source", "file");
                        writer.Bool("output_truncated", truncated);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Could not read {_outputPath}: {ex.Message}");
            }

            // Fallback: the in-memory buffer (last ~100 messages only; offsets do not apply).
            string memory = "";
            try { memory = MainSim.Inst?.Logger?.GetOutputString() ?? ""; } catch { }
            writer.Str("output", memory);
            writer.Num("next_offset", 0);
            writer.Str("output_source", "memory");
            writer.Bool("output_truncated", true);
        }

        private ApiResponse HandleInventory()
        {
            return RunOnMainThreadSync(() =>
            {
                var mainSim = MainSim.Inst;
                if (mainSim == null)
                    return ApiResponse.Error(503, "MainSim not ready");

                var sim = GameBridge.GetActiveSim(mainSim);
                if (sim == null)
                    return ApiResponse.Error(503, "No active simulation");

                return ApiResponse.Ok(new MiniJson.ObjectWriter()
                    .Raw("items", GameBridge.GetInventoryJson(sim))
                    .ToString());
            });
        }

        private ApiResponse HandleStop()
        {
            return RunOnMainThreadSync(() =>
            {
                var mainSim = MainSim.Inst;
                if (mainSim == null)
                    return ApiResponse.Error(503, "MainSim not ready");

                bool wasRunning = mainSim.IsExecuting() || mainSim.MightBeSimulating();
                GameBridge.StopEverything(mainSim);
                if (RunState.IsActive) EndRun(RunState.Stopped);

                return ApiResponse.Ok(new MiniJson.ObjectWriter()
                    .Bool("stopped", true)
                    .Bool("was_running", wasRunning)
                    .ToString());
            });
        }

        private ApiResponse HandleDismiss()
        {
            return RunOnMainThreadSync(() =>
            {
                var mainSim = MainSim.Inst;
                if (mainSim == null)
                    return ApiResponse.Error(503, "MainSim not ready");

                bool dismissed = GameBridge.DismissLeaderboardScreen(mainSim);
                return ApiResponse.Ok(new MiniJson.ObjectWriter().Bool("dismissed", dismissed).ToString());
            });
        }

        private static Dictionary<string, object> ParseOptionalBody(string body)
        {
            return string.IsNullOrEmpty(body) || body.Trim().Length == 0
                ? new Dictionary<string, object>()
                : MiniJson.ParseObject(body);
        }

        private ApiResponse HandleOverlay(string body)
        {
            var req = ParseOptionalBody(body);
            return RunOnMainThreadSync(() =>
            {
                if (MiniJson.GetBool(req, "clear", false))
                {
                    Overlay.Caption = null;
                    Overlay.ClearScoreboard();
                }

                // "caption": text shown in a strip at the bottom; "" or null removes it.
                object captionValue;
                if (req.TryGetValue("caption", out captionValue))
                {
                    Overlay.Caption = captionValue as string;
                    double seconds = MiniJson.GetNumber(req, "caption_seconds", 0);
                    Overlay.CaptionUntil = seconds > 0 ? Time.unscaledTime + (float)seconds : 0f;
                }

                // "scoreboard": panel at the top right; null removes it. Given keys replace
                // the stored values, so a status-only update keeps the rest.
                object boardValue;
                if (req.TryGetValue("scoreboard", out boardValue))
                {
                    var board = boardValue as Dictionary<string, object>;
                    if (board == null)
                    {
                        Overlay.ClearScoreboard();
                    }
                    else
                    {
                        if (board.ContainsKey("title")) Overlay.Title = MiniJson.GetString(board, "title");
                        if (Overlay.Title == null) Overlay.Title = "";
                        if (board.ContainsKey("subtitle")) Overlay.Subtitle = MiniJson.GetString(board, "subtitle");
                        if (board.ContainsKey("status")) Overlay.Status = MiniJson.GetString(board, "status");
                        if (board.ContainsKey("standing")) Overlay.Standing = MiniJson.GetString(board, "standing");
                        if (board.ContainsKey("best_label")) Overlay.BestLabel = MiniJson.GetString(board, "best_label");
                        if (board.ContainsKey("best_seconds"))
                        {
                            double best = MiniJson.GetNumber(board, "best_seconds", -1);
                            Overlay.BestSeconds = best > 0 ? best : (double?)null;
                        }
                        object historyValue;
                        if (board.TryGetValue("history", out historyValue))
                        {
                            Overlay.History.Clear();
                            var list = historyValue as List<object>;
                            if (list != null)
                                foreach (var entry in list)
                                    if (entry is double) Overlay.History.Add((double)entry);
                        }
                    }
                }

                return ApiResponse.Ok(new MiniJson.ObjectWriter()
                    .Bool("caption_visible", !string.IsNullOrEmpty(Overlay.Caption)
                        && (Overlay.CaptionUntil <= 0f || Time.unscaledTime <= Overlay.CaptionUntil))
                    .Bool("scoreboard_visible", Overlay.Title != null)
                    .ToString());
            });
        }

        private ApiResponse HandleCameraGet()
        {
            return RunOnMainThreadSync(() =>
            {
                var mainSim = MainSim.Inst;
                if (mainSim == null) return ApiResponse.Error(503, "MainSim not ready");
                var writer = new MiniJson.ObjectWriter()
                    .Num("zoom", GameBridge.GetZoom(mainSim))
                    .Num("world_size", mainSim.GetWorldSize().x)
                    .Num("screen_width", Screen.width)
                    .Num("screen_height", Screen.height);
                Rect farm;
                if (GameBridge.TryGetFarmScreenRect(mainSim, out farm))
                {
                    // Where the farm currently is, as fractions of the screen (origin bottom-left).
                    writer.Raw("farm_rect", new MiniJson.ObjectWriter()
                        .Num("x", Math.Round(farm.x / Screen.width, 3))
                        .Num("y", Math.Round(farm.y / Screen.height, 3))
                        .Num("width", Math.Round(farm.width / Screen.width, 3))
                        .Num("height", Math.Round(farm.height / Screen.height, 3))
                        .ToString());
                }
                return ApiResponse.Ok(writer.ToString());
            });
        }

        private ApiResponse HandleCamera(string body)
        {
            var req = ParseOptionalBody(body);
            return RunOnMainThreadSync(() =>
            {
                var mainSim = MainSim.Inst;
                if (mainSim == null) return ApiResponse.Error(503, "MainSim not ready");

                if (MiniJson.GetBool(req, "release", false))
                {
                    bool released = GameBridge.ReleaseCamera();
                    return ApiResponse.Ok(new MiniJson.ObjectWriter().Bool("released", released).ToString());
                }

                bool fit = MiniJson.GetBool(req, "fit", false);
                bool center = MiniJson.GetBool(req, "center", false);
                float? zoom = null;
                if (req.ContainsKey("zoom")) zoom = (float)MiniJson.GetNumber(req, "zoom", 1);
                if (!fit && !center && !zoom.HasValue)
                    return ApiResponse.Error(400, "Give at least one of \"fit\": true, \"center\": true, \"zoom\": <number> or \"release\": true");

                // Region the farm should occupy, as screen fractions with the origin at the
                // bottom-left. The default leaves room for the scoreboard (right) and captions (bottom).
                var region = new Rect(0.03f, 0.16f, 0.72f, 0.80f);
                var regionValue = MiniJson.GetObject(req, "region");
                if (regionValue != null)
                {
                    region = new Rect(
                        (float)MiniJson.GetNumber(regionValue, "x", region.x),
                        (float)MiniJson.GetNumber(regionValue, "y", region.y),
                        (float)MiniJson.GetNumber(regionValue, "width", region.width),
                        (float)MiniJson.GetNumber(regionValue, "height", region.height));
                    if (region.width <= 0.05f || region.height <= 0.05f)
                        return ApiResponse.Error(400, "'region' width and height must be fractions of the screen larger than 0.05");
                }
                float seconds = (float)MiniJson.GetNumber(req, "seconds", 1.0);

                GameBridge.StartCameraMove(mainSim, zoom, fit, center, region, seconds);
                return ApiResponse.Ok(new MiniJson.ObjectWriter()
                    .Bool("moving", true)
                    .Bool("fit", fit && !zoom.HasValue)
                    .Bool("center", center || fit)
                    .Num("seconds", seconds)
                    .ToString());
            });
        }

        /// <summary>
        /// GET /leaderboard?name=Hay_Single[&time=123.4][&top=10]: the Steam leaderboard's size,
        /// top times, times at a few percentiles, the player's own entry and where `time` would rank.
        /// </summary>
        private ApiResponse HandleLeaderboard(HttpListenerRequest request)
        {
            string name = request.QueryString["name"];
            int top = 10;
            if (!string.IsNullOrEmpty(request.QueryString["top"]) && !int.TryParse(request.QueryString["top"], out top))
                return ApiResponse.Error(400, "'top' must be an integer");
            double timeSeconds = 0;
            string rawTime = request.QueryString["time"];
            if (!string.IsNullOrEmpty(rawTime) && (!double.TryParse(rawTime, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out timeSeconds) || timeSeconds <= 0))
                return ApiResponse.Error(400, "'time' must be a positive number of seconds");

            SteamBoardQuery query = null;
            string leaderboardName = null;
            var started = RunOnMainThreadSync(() =>
            {
                var lb = GameBridge.FindLeaderboard(name);
                if (string.IsNullOrEmpty(lb.steamLeaderboardName))
                    return ApiResponse.Error(404, $"Leaderboard '{lb.leaderboardName}' has no Steam leaderboard");
                leaderboardName = lb.leaderboardName;
                query = new SteamBoardQuery(lb.steamLeaderboardName, top, (int)Math.Round(timeSeconds * 1000));
                query.Start();
                return ApiResponse.Ok("{}");
            });
            if (started.Status != 200) return started;

            // Steam answers over several callbacks; placing a time takes ~log2(entries) round trips.
            if (!query.Done.Wait(45000))
                return ApiResponse.Error(504, "Steam did not answer the leaderboard query in time");
            if (query.Error != null)
                return ApiResponse.Error(502, query.Error);

            Func<int, double> percentOf = rank => query.EntryCount > 0 ? Math.Round(rank * 100.0 / query.EntryCount, 2) : 0;
            var writer = new MiniJson.ObjectWriter()
                .Str("leaderboard", leaderboardName)
                .Num("entry_count", query.EntryCount);

            var topJson = new List<string>();
            foreach (var e in query.Top)
                topJson.Add(new MiniJson.ObjectWriter().Num("rank", e.Key).Num("time_seconds", e.Value / 1000.0).ToString());
            writer.Raw("top", "[" + string.Join(",", topJson) + "]");

            var pctJson = new List<string>();
            foreach (var p in query.Percentiles)
                pctJson.Add(new MiniJson.ObjectWriter().Num("top_percent", p.Key).Num("rank", p.Value.Key)
                    .Num("time_seconds", p.Value.Value / 1000.0).ToString());
            writer.Raw("percentiles", "[" + string.Join(",", pctJson) + "]");

            writer.Raw("player", query.HasPlayer
                ? new MiniJson.ObjectWriter().Num("rank", query.PlayerRank).Num("time_seconds", query.PlayerMs / 1000.0)
                    .Num("top_percent", percentOf(query.PlayerRank)).ToString()
                : "null");
            writer.Raw("time_query", query.HasTimeRank
                ? new MiniJson.ObjectWriter().Num("time_seconds", timeSeconds).Num("rank", query.TimeRank)
                    .Num("top_percent", percentOf(Math.Min(query.TimeRank, Math.Max(1, query.EntryCount)))).ToString()
                : "null");
            return ApiResponse.Ok(writer.ToString());
        }

        private ApiResponse HandleScreenshot()
        {
            return RunOnMainThreadSync(() =>
            {
                // Written by Unity at the end of the current frame.
                string path = Path.Combine(Application.persistentDataPath, "api_screenshot.png");
                ScreenCapture.CaptureScreenshot(path);
                return ApiResponse.Ok(new MiniJson.ObjectWriter()
                    .Str("path", path)
                    .Num("width", Screen.width)
                    .Num("height", Screen.height)
                    .ToString());
            });
        }

        private ApiResponse HandleSaves()
        {
            return RunOnMainThreadSync(() => ApiResponse.Ok(new MiniJson.ObjectWriter()
                .Str("active_save", GameBridge.GetActiveSaveName())
                .StrArray("saves", GameBridge.ListSaves())
                .ToString()));
        }

        private ApiResponse HandleLoadSave(string body)
        {
            var req = string.IsNullOrEmpty(body) || body.Trim().Length == 0
                ? new Dictionary<string, object>()
                : MiniJson.ParseObject(body);
            string save = MiniJson.GetString(req, "save");
            bool closeMenu = MiniJson.GetBool(req, "close_menu", true);
            bool discardUnsaved = MiniJson.GetBool(req, "discard_unsaved", false);

            return RunOnMainThreadSync(() =>
            {
                var mainSim = MainSim.Inst;
                if (mainSim == null)
                    return ApiResponse.Error(503, "MainSim not ready");

                string active = GameBridge.GetActiveSaveName();
                if (string.IsNullOrEmpty(save)) save = active;

                bool reload = save != active;
                if (reload)
                {
                    var saves = GameBridge.ListSaves();
                    if (!saves.Contains(save))
                        return ApiResponse.Error(404, $"No save named '{save}'. Saves: {string.Join(", ", saves)}");
                    if (mainSim.dirty && !discardUnsaved)
                        return ApiResponse.Error(409, $"The current save '{active}' has unsaved changes. Save in the game first, or pass \"discard_unsaved\": true.");

                    // Same steps as picking a save in the game's menu without saving first.
                    GameBridge.StopEverything(mainSim);
                    if (RunState.IsActive) EndRun(RunState.Stopped);
                    Saver.StopFileWatcher();
                    OptionHolder.SetOption("activeSave", save);
                    SceneManager.LoadScene(0);
                    Log.LogInfo($"Loading save '{save}'");
                }

                // After a reload the scene needs a moment before the menu can be closed.
                if (closeMenu) _closeMenuCountdown = reload ? 30 : 0;

                return ApiResponse.Ok(new MiniJson.ObjectWriter()
                    .Bool("loading", reload)
                    .Str("save", save)
                    .Bool("close_menu", closeMenu)
                    .ToString());
            });
        }

        private ApiResponse HandleDeleteScript(string body)
        {
            var req = MiniJson.ParseObject(body);
            string filename = MiniJson.GetString(req, "filename");
            if (string.IsNullOrEmpty(filename))
                return ApiResponse.Error(400, "'filename' field is required");
            if (filename.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
                filename = filename.Substring(0, filename.Length - 3);

            return RunOnMainThreadSync(() =>
            {
                var mainSim = MainSim.Inst;
                if (mainSim == null)
                    return ApiResponse.Error(503, "MainSim not ready");

                bool stopped = false;
                bool windowClosed = false;
                CodeWindow cw;
                if (mainSim.workspace.codeWindows.TryGetValue(filename, out cw) && cw != null)
                {
                    // Closing the window that is executing would pull the code out from under
                    // the run, so end the run first.
                    if (cw.isExecuting && (mainSim.IsExecuting() || mainSim.MightBeSimulating()))
                    {
                        GameBridge.StopEverything(mainSim);
                        if (RunState.IsActive) EndRun(RunState.Stopped);
                        stopped = true;
                    }
                    windowClosed = GameBridge.CloseCodeWindow(filename, cw);
                }

                bool fileDeleted = false;
                string filePath = Path.Combine(GameBridge.GetActiveSavePath(), filename + ".py");
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    fileDeleted = true;
                }

                return ApiResponse.Ok(new MiniJson.ObjectWriter()
                    .Bool("deleted", fileDeleted || windowClosed)
                    .Str("filename", filename)
                    .Bool("file_deleted", fileDeleted)
                    .Bool("window_closed", windowClosed)
                    .Bool("stopped_run", stopped)
                    .ToString());
            });
        }

        // ── Main Thread Sync ────────────────────────────────────

        private sealed class PendingCall
        {
            public const int Queued = 0, Running = 1, Cancelled = 2;
            public int State;
            public ApiResponse Result;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        /// <summary>
        /// Run <paramref name="func"/> on Unity's main thread and wait for its result.
        /// If the main thread does not pick the call up in time it is cancelled, so a request
        /// that reported a timeout never takes effect later.
        /// </summary>
        private ApiResponse RunOnMainThreadSync(Func<ApiResponse> func)
        {
            int mainId = GameBridge.MainThreadId;
            if (mainId != 0 && Thread.CurrentThread.ManagedThreadId == mainId)
                return Guarded(func);

            var call = new PendingCall();
            _mainThreadQueue.Enqueue(() =>
            {
                if (Interlocked.CompareExchange(ref call.State, PendingCall.Running, PendingCall.Queued) != PendingCall.Queued)
                    return; // the caller gave up
                call.Result = Guarded(func);
                call.Done.Set();
            });

            int timeoutMs = Math.Max(1000, _mainThreadTimeoutMs.Value);
            if (call.Done.Wait(timeoutMs))
                return call.Result;

            if (Interlocked.CompareExchange(ref call.State, PendingCall.Cancelled, PendingCall.Queued) == PendingCall.Queued)
                return ApiResponse.Error(504, "Timed out waiting for the game's main thread (is the game paused or still loading?). The request was not applied.");

            // Already executing on the main thread: let it finish rather than misreport.
            if (call.Done.Wait(60000))
                return call.Result;
            return ApiResponse.Error(504, "The game's main thread did not finish the request; its outcome is unknown.");
        }

        private static ApiResponse Guarded(Func<ApiResponse> func)
        {
            try
            {
                return func();
            }
            catch (ApiException ex)
            {
                return ApiResponse.Error(ex.Status, ex.Message);
            }
            catch (MiniJson.JsonException ex)
            {
                return ApiResponse.Error(400, ex.Message);
            }
            catch (Exception ex)
            {
                Log.LogError($"Handler error: {ex}");
                return ApiResponse.Error(500, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void SendResponse(HttpListenerResponse response, ApiResponse apiResponse)
        {
            var buffer = Encoding.UTF8.GetBytes(apiResponse.Json);
            response.StatusCode = apiResponse.Status;
            response.ContentType = "application/json; charset=utf-8";
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }
    }
}
