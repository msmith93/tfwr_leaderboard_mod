using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace TFWRRemoteAPI
{
    /// <summary>
    /// Every private game member the plugin reaches by reflection, resolved once at startup.
    /// Anything missing is logged and reported in GET /status "compat_warnings" so a game
    /// update that breaks the mod is visible immediately instead of failing silently.
    /// </summary>
    public static class Compat
    {
        public static readonly List<string> Warnings = new List<string>();

        public static FieldInfo MainSimSim;
        public static FieldInfo MainSimStartLeaderboardNow;
        public static FieldInfo MainSimLeaderboardCancelled;
        public static FieldInfo CodeWindowErrorString;
        public static FieldInfo CodeWindowErrorStartIndex;
        public static FieldInfo BuiltinResetUnlocks;
        public static MethodInfo BuiltinGetUnlockStrings;

        private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private const BindingFlags Stat = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

        public static void Init()
        {
            MainSimSim = Field(typeof(MainSim), "sim", Inst,
                "inventory, sim time and stop/cleanup are unavailable");
            MainSimStartLeaderboardNow = Field(typeof(MainSim), "startLeaderboardNow", Inst,
                "a simulate/leaderboard run that fails to start is reported late or not at all");
            MainSimLeaderboardCancelled = Field(typeof(MainSim), "leaderboardCancelled", Inst,
                "a stopped leaderboard run may cancel the first attempt of the next one");
            CodeWindowErrorString = Field(typeof(CodeWindow), "errorString", Inst,
                "parse errors are reported without a message");
            CodeWindowErrorStartIndex = Field(typeof(CodeWindow), "errorStartIndex", Inst,
                "parse errors are reported without a line number");
            BuiltinResetUnlocks = Field(typeof(BuiltinFunctions), "resetUnlocks", Stat,
                "simulate mode with explicit unlocks omits the base unlock set");

            try
            {
                BuiltinGetUnlockStrings = typeof(BuiltinFunctions).GetMethod("GetUnlockStrings", Stat);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Compat: lookup of BuiltinFunctions.GetUnlockStrings threw: {ex.Message}");
            }
            if (BuiltinGetUnlockStrings == null)
                Warn("BuiltinFunctions.GetUnlockStrings not found: leaderboard mode is unavailable and simulate mode falls back to a hand-built unlock list");
        }

        private static FieldInfo Field(Type type, string name, BindingFlags flags, string consequence)
        {
            FieldInfo field = null;
            try
            {
                field = type.GetField(name, flags);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Compat: lookup of {type.Name}.{name} threw: {ex.Message}");
            }
            if (field == null)
                Warn($"{type.Name}.{name} not found: {consequence}");
            return field;
        }

        public static void Warn(string message)
        {
            Warnings.Add(message);
            Plugin.Log.LogError("Compat: " + message);
        }
    }

    /// <summary>
    /// What the current API run is doing. Written from the Unity main thread and from the
    /// game's simulation thread (via the Harmony hooks), read from HTTP threads, so all access
    /// goes through <see cref="Lock"/>.
    /// </summary>
    public static class RunState
    {
        public const string Idle = "idle";
        public const string Starting = "starting";
        public const string Running = "running";
        public const string Finished = "finished";
        public const string Stopped = "stopped";
        public const string Error = "error";

        public static readonly object Lock = new object();

        public static string RunId;
        public static string Mode;
        public static string State = Idle;

        /// <summary>First runtime error logged by the game during this run (message + trace).</summary>
        public static string ErrorText;

        /// <summary>Sim-clock duration of the last simulate-type run.</summary>
        public static double? SimTimeSeconds;

        /// <summary>Sim-clock duration of each completed attempt of a leaderboard run.</summary>
        public static readonly List<double> RunTimes = new List<double>();

        public static bool HasLeaderboardResult;
        public static bool LeaderboardCompleted;
        public static string LeaderboardName;
        public static double LeaderboardAverageSeconds;
        public static int LeaderboardRuns;

        public static bool IsActive
        {
            get { lock (Lock) { return State == Starting || State == Running; } }
        }

        public static void Begin(string runId, string mode, string state)
        {
            lock (Lock)
            {
                RunId = runId;
                Mode = mode;
                State = state;
                ErrorText = null;
                SimTimeSeconds = null;
                RunTimes.Clear();
                HasLeaderboardResult = false;
                LeaderboardCompleted = false;
                LeaderboardName = null;
                LeaderboardAverageSeconds = 0;
                LeaderboardRuns = 0;
            }
        }
    }

    /// <summary>
    /// Harmony hooks that capture run outcomes the game otherwise only shows on screen:
    /// runtime errors, the sim time of simulate() runs, and leaderboard results.
    /// Each hook is applied independently; one that no longer matches the game is reported
    /// as a compat warning and the rest keep working.
    /// </summary>
    public static class GameHooks
    {
        public static void Install(string harmonyId)
        {
            var harmony = new Harmony(harmonyId);

            Patch(harmony, typeof(global::Logger), "LogError", null, nameof(LogErrorPostfix),
                "runtime errors are not reported by /poll (a failed script looks like it is still running)");
            Patch(harmony, typeof(LeaderboardManager), "StopLeaderboardRun", nameof(StopLeaderboardRunPrefix), null,
                "/poll does not report the final leaderboard result");
            Patch(harmony, typeof(MainSim), "LeaderboardExecutionFinished", nameof(LeaderboardExecutionFinishedPrefix), null,
                "/poll does not report sim_time_seconds or run_times");
        }

        private static void Patch(Harmony harmony, Type type, string method, string prefix, string postfix, string consequence)
        {
            try
            {
                var original = AccessTools.Method(type, method);
                if (original == null)
                {
                    Compat.Warn($"{type.Name}.{method} not found: {consequence}");
                    return;
                }
                harmony.Patch(original,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(GameHooks), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(GameHooks), postfix));
            }
            catch (Exception ex)
            {
                Compat.Warn($"Could not hook {type.Name}.{method} ({ex.Message}): {consequence}");
            }
        }

        // Runs on the simulation thread.
        private static void LogErrorPostfix(global::Logger __instance, string errorMessage, ProgramState state)
        {
            try
            {
                var mainSim = MainSim.Inst;
                if (mainSim == null || !ReferenceEquals(__instance, mainSim.Logger)) return;

                string trace = "";
                try { trace = state != null ? state.GetTrace() : ""; } catch { }
                string text = string.IsNullOrEmpty(trace) ? errorMessage : errorMessage + "\nIn: " + trace;
                // The game's messages carry rich-text markup for its own UI.
                try { text = CodeUtilities.RemoveSyntaxTags(text); } catch { }
                text = text.Replace("\r\n", "\n");

                lock (RunState.Lock)
                {
                    if (RunState.ErrorText == null && (RunState.State == RunState.Starting || RunState.State == RunState.Running))
                        RunState.ErrorText = text;
                }
            }
            catch { }
        }

        private static void StopLeaderboardRunPrefix(bool finished, string leaderboardName, TimeSpan timeSpan)
        {
            try
            {
                int runs = MainSim.Inst != null ? MainSim.Inst.numLeaderboardRuns : 0;
                lock (RunState.Lock)
                {
                    RunState.HasLeaderboardResult = true;
                    RunState.LeaderboardCompleted = finished;
                    RunState.LeaderboardName = leaderboardName;
                    RunState.LeaderboardAverageSeconds = timeSpan.TotalSeconds;
                    RunState.LeaderboardRuns = runs;
                }
            }
            catch { }
        }

        // The game calls this every frame while a simulation's program is not executing, but
        // returns early unless the run overlay is still up, so the same guard is applied here.
        private static void LeaderboardExecutionFinishedPrefix(MainSim __instance)
        {
            try
            {
                if (__instance.leaderboardManager == null || !__instance.leaderboardManager.IsRunning) return;
                var sim = GameBridge.GetActiveSim(__instance);
                if (sim == null) return;

                double seconds = sim.CurrentTime.Seconds;
                if (sim.leaderboardType == LeaderboardType.simulation)
                {
                    lock (RunState.Lock) { RunState.SimTimeSeconds = seconds; }
                    return;
                }

                bool cancelled = false;
                if (Compat.MainSimLeaderboardCancelled != null)
                    cancelled = (bool)Compat.MainSimLeaderboardCancelled.GetValue(__instance);
                if (!cancelled)
                {
                    lock (RunState.Lock) { RunState.RunTimes.Add(seconds); }
                }
            }
            catch { }
        }
    }
}
