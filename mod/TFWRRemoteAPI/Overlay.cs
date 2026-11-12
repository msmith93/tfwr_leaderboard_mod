using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace TFWRRemoteAPI
{
    /// <summary>
    /// On-screen presentation for people watching the game: a caption strip (what the agent
    /// is saying) and a scoreboard panel. Drawn with IMGUI on top of the game, so it needs no
    /// game assets and shows up in screen recordings. All state is set through POST /overlay
    /// and only touched on the main thread.
    /// </summary>
    public static class Overlay
    {
        // Caption
        public static string Caption;
        public static float CaptionUntil;      // Time.unscaledTime; <= 0 means until replaced

        // Scoreboard (null title = hidden)
        public static string Title;
        public static string Subtitle;
        public static string Status;
        public static string Standing;         // e.g. "Best: placed 85th of 330"
        // Percentile bar under the standing line: share of players beaten, 0-100.
        public static double? BarBest;         // the best time so far
        public static double? BarCurrent;      // the run just finished, if different
        public static readonly List<double> BarMarks = new List<double>();   // every iteration
        public static double? BestSeconds;
        public static string BestLabel;
        public static readonly List<double> History = new List<double>();

        private static Texture2D _white;
        private static GUIStyle _captionStyle, _titleStyle, _rowStyle, _bigStyle, _smallStyle;
        private static int _styledForHeight;

        public static void ClearScoreboard()
        {
            Title = null;
            Subtitle = null;
            Status = null;
            Standing = null;
            BarBest = null;
            BarCurrent = null;
            BarMarks.Clear();
            BestSeconds = null;
            BestLabel = null;
            History.Clear();
        }

        public static string FormatTime(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return "-";
            if (seconds < 60) return seconds.ToString("0.00", CultureInfo.InvariantCulture) + "s";
            int minutes = (int)(seconds / 60);
            double rest = seconds - minutes * 60;
            return minutes + ":" + rest.ToString("00.0", CultureInfo.InvariantCulture);
        }

        private static void EnsureStyles()
        {
            if (_white == null)
            {
                _white = new Texture2D(1, 1);
                _white.SetPixel(0, 0, Color.white);
                _white.Apply();
            }
            if (_captionStyle != null && _styledForHeight == Screen.height) return;
            _styledForHeight = Screen.height;
            float u = Screen.height / 1080f;

            _captionStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(30 * u), wordWrap = true, alignment = TextAnchor.MiddleCenter, richText = false
            };
            _captionStyle.normal.textColor = Color.white;
            _titleStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(26 * u), fontStyle = FontStyle.Bold, wordWrap = false };
            _titleStyle.normal.textColor = Color.white;
            _rowStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(21 * u), wordWrap = false, richText = true };
            _rowStyle.normal.textColor = new Color(0.9f, 0.9f, 0.9f);
            _bigStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(40 * u), fontStyle = FontStyle.Bold, richText = true };
            _bigStyle.normal.textColor = Color.white;
            _smallStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(17 * u), wordWrap = true };
            _smallStyle.normal.textColor = new Color(0.75f, 0.75f, 0.75f);
        }

        private static void Fill(Rect r, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _white);
            GUI.color = old;
        }

        public static void Draw()
        {
            bool captionOn = !string.IsNullOrEmpty(Caption) && (CaptionUntil <= 0f || Time.unscaledTime <= CaptionUntil);
            if (!captionOn && Title == null) return;

            EnsureStyles();
            float u = Screen.height / 1080f;
            if (captionOn) DrawCaption(u);
            if (Title != null) DrawScoreboard(u);
        }

        private static void DrawCaption(float u)
        {
            float width = Mathf.Min(Screen.width * 0.7f, 1500 * u);
            float pad = 18 * u;
            var content = new GUIContent(Caption);
            float textHeight = _captionStyle.CalcHeight(content, width - 2 * pad);
            var box = new Rect((Screen.width - width) / 2f, Screen.height - textHeight - 2 * pad - 40 * u, width, textHeight + 2 * pad);
            Fill(box, new Color(0f, 0f, 0f, 0.78f));
            GUI.Label(new Rect(box.x + pad, box.y + pad, box.width - 2 * pad, textHeight), content, _captionStyle);
        }

        private static void DrawScoreboard(float u)
        {
            float width = 400 * u, pad = 16 * u, line = 30 * u;
            float x = Screen.width - width - 24 * u, y = 24 * u;

            // Live numbers straight from the game.
            double? clock = null, average = null;
            int runs = 0;
            bool simulating = false;
            try
            {
                var mainSim = MainSim.Inst;
                if (mainSim != null)
                {
                    simulating = mainSim.MightBeSimulating();
                    runs = mainSim.numLeaderboardRuns;
                    if (simulating)
                    {
                        var sim = GameBridge.GetActiveSim(mainSim);
                        if (sim != null) clock = sim.CurrentTime.Seconds;
                    }
                    if (runs > 0) average = mainSim.totalLeaderboardTime.Seconds / runs;
                }
            }
            catch { }

            // After the run ends the game resets its counters; fall back to the recorded result.
            // A run that ended without reaching the goal has no comparable time.
            bool goalMissed = false;
            lock (RunState.Lock)
            {
                if (!average.HasValue && RunState.HasLeaderboardResult && RunState.LeaderboardRuns > 0)
                {
                    average = RunState.LeaderboardAverageSeconds;
                    runs = RunState.LeaderboardRuns;
                    goalMissed = !RunState.LeaderboardCompleted;
                }
            }

            bool hasHistory = History.Count > 1;
            float height = pad * 2 + line * 1.3f
                + (string.IsNullOrEmpty(Subtitle) ? 0 : line)
                + line * 1.9f                       // big clock / average
                + line                              // runs row
                + (BestSeconds.HasValue ? line : 0)
                + (hasHistory ? 90 * u : 0)
                + (string.IsNullOrEmpty(Standing) ? 0 : line)
                + (BarBest.HasValue ? 74 * u : 0)
                + (string.IsNullOrEmpty(Status) ? 0 : line);

            Fill(new Rect(x, y, width, height), new Color(0.05f, 0.07f, 0.1f, 0.85f));
            Fill(new Rect(x, y, 5 * u, height), new Color(0.35f, 0.75f, 0.45f, 1f));

            float cx = x + pad + 5 * u, cy = y + pad, cw = width - 2 * pad - 5 * u;
            GUI.Label(new Rect(cx, cy, cw, line * 1.3f), Title, _titleStyle);
            cy += line * 1.3f;
            if (!string.IsNullOrEmpty(Subtitle))
            {
                GUI.Label(new Rect(cx, cy, cw, line), Subtitle, _rowStyle);
                cy += line;
            }

            // Headline number: the running average once there is one, else the live clock.
            double? headline = average ?? clock;
            string headlineText = headline.HasValue ? FormatTime(headline.Value) : "-";
            string delta = "";
            if (goalMissed)
            {
                delta = $"  <size={Mathf.RoundToInt(22 * u)}><color=#ff8389>goal missed</color></size>";
            }
            else if (average.HasValue && BestSeconds.HasValue && BestSeconds.Value > 0)
            {
                double d = average.Value - BestSeconds.Value;
                string color = d <= 0 ? "#6fdc8c" : "#ff8389";
                delta = $"  <size={Mathf.RoundToInt(22 * u)}><color={color}>{(d <= 0 ? "-" : "+")}{FormatTime(Math.Abs(d))}</color></size>";
            }
            GUI.Label(new Rect(cx, cy, cw, line * 1.9f), headlineText + delta, _bigStyle);
            cy += line * 1.9f;

            string runsRow = average.HasValue
                ? $"average of {runs} run{(runs == 1 ? "" : "s")}" + (simulating && clock.HasValue ? $"   now {FormatTime(clock.Value)}" : "")
                : (simulating ? "first run in progress" : "waiting for a run");
            GUI.Label(new Rect(cx, cy, cw, line), runsRow, _rowStyle);
            cy += line;

            if (BestSeconds.HasValue)
            {
                string label = string.IsNullOrEmpty(BestLabel) ? "" : $"  ({BestLabel})";
                GUI.Label(new Rect(cx, cy, cw, line), $"best  <b>{FormatTime(BestSeconds.Value)}</b>{label}", _rowStyle);
                cy += line;
            }

            if (hasHistory)
            {
                DrawHistory(new Rect(cx, cy + 8 * u, cw, 60 * u), u);
                GUI.Label(new Rect(cx, cy + 68 * u, cw, 22 * u), $"{History.Count} iterations, lower is better", _smallStyle);
                cy += 90 * u;
            }

            if (!string.IsNullOrEmpty(Standing))
            {
                GUI.Label(new Rect(cx, cy, cw, line), Standing, _rowStyle);
                cy += line;
            }

            if (BarBest.HasValue)
            {
                DrawRankBar(new Rect(cx, cy + 14 * u, cw, 14 * u), u);
                cy += 74 * u;
            }

            if (!string.IsNullOrEmpty(Status))
                GUI.Label(new Rect(cx, cy, cw, line), Status, _rowStyle);
        }

        // Where the best time sits among all players: slowest at the left, fastest at the right.
        // Thin ticks are the individual iterations; the tall marker is the best.
        private static void DrawRankBar(Rect track, float u)
        {
            Func<double, float> xOf = pct => track.x + track.width * Mathf.Clamp01((float)pct / 100f);
            float best = xOf(BarBest.Value);

            Fill(track, new Color(1f, 1f, 1f, 0.14f));
            Fill(new Rect(track.x, track.y, best - track.x, track.height), new Color(0.44f, 0.86f, 0.55f, 0.55f));

            foreach (double mark in BarMarks)
                Fill(new Rect(xOf(mark) - 1 * u, track.y - 4 * u, 2 * u, track.height + 8 * u), new Color(0.82f, 0.86f, 0.93f, 0.85f));

            if (BarCurrent.HasValue)
                Fill(new Rect(xOf(BarCurrent.Value) - 1.5f * u, track.y - 7 * u, 3 * u, track.height + 14 * u), new Color(1f, 0.78f, 0.3f, 1f));

            Fill(new Rect(best - 2.5f * u, track.y - 9 * u, 5 * u, track.height + 18 * u), new Color(0.44f, 0.86f, 0.55f, 1f));

            float labelY = track.yMax + 10 * u;
            GUI.Label(new Rect(track.x, labelY, track.width, 28 * u),
                "faster than " + BarBest.Value.ToString("0", CultureInfo.InvariantCulture) + "% of players", _smallStyle);
            var right = new GUIStyle(_smallStyle) { alignment = TextAnchor.UpperRight };
            GUI.Label(new Rect(track.x, labelY, track.width, 28 * u), "#1", right);
        }

        // One bar per past iteration, scaled to the slowest; the fastest is highlighted.
        private static void DrawHistory(Rect area, float u)
        {
            double max = 0, min = double.MaxValue;
            foreach (double v in History)
            {
                if (v > max) max = v;
                if (v > 0 && v < min) min = v;
            }
            if (max <= 0) return;

            int n = History.Count;
            float gap = n > 40 ? 1 * u : 3 * u;
            float barWidth = Mathf.Max(1f, (area.width - gap * (n - 1)) / n);
            for (int i = 0; i < n; i++)
            {
                float h = Mathf.Max(2f, (float)(History[i] / max) * area.height);
                var bar = new Rect(area.x + i * (barWidth + gap), area.yMax - h, barWidth, h);
                bool isBest = History[i] == min;
                Fill(bar, isBest ? new Color(0.44f, 0.86f, 0.55f, 1f) : new Color(0.45f, 0.55f, 0.7f, 0.9f));
            }
        }
    }
}
