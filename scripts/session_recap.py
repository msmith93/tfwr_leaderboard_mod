#!/usr/bin/env python3
"""
Build a recap page for a leaderboard from the iteration tracker, results and notes.

Usage:
  python3 scripts/session_recap.py [--since N] [--out PATH]

  --since N   first iteration of the current session; those iterations are highlighted
              and summarised separately (default: no session highlight)
  --out PATH  where to write the page (default: iterations/recap_<leaderboard>.html)

The output is a single self-contained HTML file. It is written as a page body (no
<html>/<head> wrapper) so it can be published as an Artifact as-is; browsers also open
it directly from disk.
"""
import argparse
import datetime
import html
import json
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def first_sentence(text, limit=170):
    text = " ".join((text or "").split())
    m = re.search(r"(?<=[.!?])\s", text)
    if m and m.start() <= limit:
        return text[:m.start()]
    if len(text) <= limit:
        return text
    return text[:limit].rsplit(" ", 1)[0] + "…"


def next_steps(notes_path, limit=4):
    """Bullet lines under '## Next Steps' in a notes file."""
    try:
        with open(notes_path, encoding="utf-8") as f:
            text = f.read()
    except OSError:
        return []
    m = re.search(r"^##\s*Next Steps\s*$(.*?)(?=^##\s|\Z)", text, re.S | re.M | re.I)
    if not m:
        return []
    items = []
    for line in m.group(1).splitlines():
        line = line.strip()
        if re.match(r"^([-*]|\d+\.)\s+", line):
            item = re.sub(r"^([-*]|\d+\.)\s+", "", line)
            item = re.sub(r"[*_`]", "", item)
            items.append(first_sentence(item, 220))
    return items[:limit]


def load(since):
    tracker_path = os.path.join(REPO, "iterations", "iteration_tracker.json")
    with open(tracker_path, encoding="utf-8") as f:
        tracker = json.load(f)

    rows = []
    best_so_far = None
    for it in tracker.get("iterations", []):
        mean = it.get("mean_time_seconds")
        if not isinstance(mean, (int, float)):
            continue
        result = {}
        try:
            with open(os.path.join(REPO, it.get("results", "")), encoding="utf-8") as f:
                result = json.load(f)
        except (OSError, ValueError):
            pass
        runs = result.get("n_runs")
        if runs is None and isinstance(result.get("runs"), list):
            runs = len(result["runs"])
        win_rate = it.get("win_rate", result.get("win_rate"))
        valid = win_rate is None or win_rate >= 1.0
        is_new_best = valid and (best_so_far is None or mean < best_so_far)
        rows.append({
            "id": it.get("id"),
            "name": it.get("name", ""),
            "mean": mean,
            "std": result.get("std_time_seconds"),
            "runs": runs,
            "winRate": win_rate,
            "strategy": first_sentence(result.get("strategy_summary", "")),
            "prevBest": best_so_far,
            "newBest": is_new_best,
            "session": bool(since is not None and it.get("id", 0) >= since),
        })
        if is_new_best:
            best_so_far = mean

    last_notes = ""
    if tracker.get("iterations"):
        last_notes = os.path.join(REPO, tracker["iterations"][-1].get("notes", ""))
    return {
        "leaderboard": tracker.get("leaderboard", "Leaderboard"),
        "rows": rows,
        "since": since,
        "ideas": next_steps(last_notes),
        "generated": datetime.date.today().isoformat(),
    }


PAGE = r"""<title>__TITLE__</title>
<meta name="claude:viewport-fit-sides" content="cover">
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600&family=Space+Grotesk:wght@500;700&family=JetBrains+Mono:wght@400;600&display=swap">
<style>
/* Layout: one reading column; headline time first, then the run of iterations as a chart, then the log. */
:root {
  --bg: #f6f7f3; --surface: #ffffff; --ink: #14170f; --ink-2: #4c5244; --ink-3: #7a8071;
  --rule: #dfe3d6; --series: #1baf7a; --series-soft: rgba(27,175,122,.13); --ref: #7a8071;
  --good: #0a7d4f; --bad: #b3401f; --band: rgba(20,23,15,.045);
  --display: "Space Grotesk", "Segoe UI", system-ui, sans-serif;
  --body: "Inter", system-ui, -apple-system, "Segoe UI", sans-serif;
  --mono: "JetBrains Mono", ui-monospace, "SF Mono", Menlo, monospace;
}
@media (prefers-color-scheme: dark) {
  :root:not([data-theme="light"]) {
    --bg: #12140f; --surface: #1a1d16; --ink: #eef0e8; --ink-2: #b9beae; --ink-3: #8a9080;
    --rule: #2c3126; --series: #199e70; --series-soft: rgba(25,158,112,.2); --ref: #8a9080;
    --good: #4fd09a; --bad: #f08a66; --band: rgba(238,240,232,.06); color-scheme: dark;
  }
}
:root[data-theme="dark"] {
  --bg: #12140f; --surface: #1a1d16; --ink: #eef0e8; --ink-2: #b9beae; --ink-3: #8a9080;
  --rule: #2c3126; --series: #199e70; --series-soft: rgba(25,158,112,.2); --ref: #8a9080;
  --good: #4fd09a; --bad: #f08a66; --band: rgba(238,240,232,.06); color-scheme: dark;
}
body { background: var(--bg); color: var(--ink); font-family: var(--body); font-size: 15px; line-height: 1.55; }
.wrap { max-width: 980px; margin: 0 auto; padding-block: 40px 64px;
  padding-left: max(16px, env(safe-area-inset-left, 0px)); padding-right: max(16px, env(safe-area-inset-right, 0px));
  display: flex; flex-direction: column; gap: 36px; }
.eyebrow { font-size: 12px; letter-spacing: .09em; text-transform: uppercase; color: var(--ink-3); font-weight: 600; }
h1 { font-family: var(--display); font-size: clamp(34px, 6vw, 54px); line-height: 1.02; margin: 6px 0 0; font-weight: 700; text-wrap: balance; }
h2 { font-family: var(--display); font-size: 20px; margin: 0 0 4px; font-weight: 700; }
.lede { color: var(--ink-2); max-width: 65ch; margin: 12px 0 0; }
.note { color: var(--ink-3); font-size: 13px; margin: 0; }
.stats { display: grid; grid-template-columns: repeat(auto-fit, minmax(170px, 1fr)); gap: 0; border-top: 1px solid var(--rule); border-bottom: 1px solid var(--rule); }
.stat { padding: 16px 16px 16px 0; min-width: 0; }
.stat + .stat { padding-left: 16px; border-left: 1px solid var(--rule); }
.stat .k { font-size: 12px; color: var(--ink-3); letter-spacing: .04em; text-transform: uppercase; font-weight: 600; }
.stat .v { font-family: var(--display); font-size: 30px; font-weight: 700; font-variant-numeric: tabular-nums; line-height: 1.15; }
.stat .d { font-size: 13px; color: var(--ink-2); overflow-wrap: anywhere; }
@media (max-width: 560px) { .stat + .stat { border-left: 0; padding-left: 0; border-top: 1px solid var(--rule); } }
section { display: flex; flex-direction: column; gap: 12px; min-width: 0; }
.chart { background: var(--surface); border: 1px solid var(--rule); border-radius: 6px; padding: 14px 10px 8px; position: relative; }
.chart svg { display: block; width: 100%; height: auto; }
.legend { display: flex; flex-wrap: wrap; gap: 6px 18px; font-size: 13px; color: var(--ink-2); padding: 0 6px 6px; }
.legend span { display: inline-flex; align-items: center; gap: 7px; }
.tip { position: absolute; pointer-events: none; background: var(--ink); color: var(--bg); font-size: 12.5px; line-height: 1.4;
  padding: 7px 10px; border-radius: 4px; max-width: 250px; z-index: 2; }
.tip b { font-family: var(--mono); font-weight: 600; }
.scroll { overflow-x: auto; border: 1px solid var(--rule); border-radius: 6px; background: var(--surface); }
table { border-collapse: collapse; width: 100%; min-width: 640px; font-size: 14px; }
th { text-align: left; font-size: 12px; letter-spacing: .04em; text-transform: uppercase; color: var(--ink-3); font-weight: 600; padding: 10px 12px; border-bottom: 1px solid var(--rule); white-space: nowrap; }
td { padding: 10px 12px; border-bottom: 1px solid var(--rule); vertical-align: top; }
tr:last-child td { border-bottom: 0; }
td.num, th.num { text-align: right; font-family: var(--mono); font-variant-numeric: tabular-nums; white-space: nowrap; }
td .name { font-family: var(--mono); font-size: 13px; font-weight: 600; overflow-wrap: anywhere; }
td .why { color: var(--ink-2); font-size: 13px; margin-top: 2px; max-width: 58ch; }
tr.session td { background: var(--band); }
.tag { display: inline-block; font-size: 11px; font-weight: 600; letter-spacing: .04em; text-transform: uppercase; padding: 1px 6px; border-radius: 3px; margin-left: 6px; vertical-align: 1px; white-space: nowrap; }
.tag.best { background: var(--series-soft); color: var(--good); }
.tag.fail { border: 1px solid var(--bad); color: var(--bad); }
.up { color: var(--bad); } .down { color: var(--good); }
ul.ideas { margin: 0; padding-left: 20px; color: var(--ink-2); max-width: 70ch; display: flex; flex-direction: column; gap: 6px; }
</style>

<div class="wrap">
  <header>
    <div class="eyebrow">The Farmer Was Replaced · leaderboard recap</div>
    <h1 id="title"></h1>
    <p class="lede" id="lede"></p>
  </header>

  <div class="stats" id="stats"></div>

  <section>
    <h2>Mean time by iteration</h2>
    <p class="note">Each point is one iteration's average over a full leaderboard run. Lower is faster.</p>
    <div class="chart" id="chart">
      <div class="legend">
        <span><svg width="26" height="12" aria-hidden="true"><line x1="1" y1="6" x2="25" y2="6" stroke="var(--series)" stroke-width="2"/><circle cx="13" cy="6" r="4.5" fill="var(--series)" stroke="var(--surface)" stroke-width="2"/></svg>new best</span>
        <span><svg width="26" height="12" aria-hidden="true"><line x1="1" y1="6" x2="25" y2="6" stroke="var(--series)" stroke-width="2"/><circle cx="13" cy="6" r="4" fill="var(--surface)" stroke="var(--series)" stroke-width="2"/></svg>did not beat the best</span>
        <span><svg width="26" height="12" aria-hidden="true"><line x1="1" y1="6" x2="25" y2="6" stroke="var(--ref)" stroke-width="1.5" stroke-dasharray="4 3"/></svg>best so far</span>
        <span id="legend-session" hidden><svg width="18" height="12" aria-hidden="true"><rect x="0" y="0" width="18" height="12" fill="var(--band)" stroke="var(--rule)"/></svg>this session</span>
      </div>
      <div id="plot"></div>
      <div class="tip" id="tip" hidden></div>
    </div>
  </section>

  <section>
    <h2>What was tried</h2>
    <div class="scroll"><table id="log"></table></div>
  </section>

  <section id="ideas-section" hidden>
    <h2>Ideas left open</h2>
    <p class="note">From the notes of the most recent iteration.</p>
    <ul class="ideas" id="ideas"></ul>
  </section>

  <p class="note" id="footer"></p>
</div>

<script>
const DATA = __DATA__;

function fmt(s) {
  if (s == null || !isFinite(s)) return "–";
  if (s < 60) return s.toFixed(2) + "s";
  const m = Math.floor(s / 60), r = s - m * 60;
  return m + ":" + (r < 10 ? "0" : "") + r.toFixed(1);
}
function pct(a, b) { return ((a - b) / b * 100); }
function esc(t) { const d = document.createElement("div"); d.textContent = t == null ? "" : String(t); return d.innerHTML; }

const rows = DATA.rows;
const best = rows.filter(r => r.newBest).slice(-1)[0];
const first = rows[0];
const session = rows.filter(r => r.session);
const prettyName = DATA.leaderboard.replace(/_/g, " ");

document.getElementById("title").textContent = prettyName;

// Lede: one or two plain sentences on where things stand.
(function () {
  let text = "";
  if (!rows.length) text = "No iterations recorded yet.";
  else {
    text = rows.length + " iteration" + (rows.length === 1 ? "" : "s") + " so far. The fastest is iteration " + best.id +
      " (" + best.name + ") at " + fmt(best.mean) + ".";
    if (session.length) {
      const sBest = session.filter(r => r.newBest).slice(-1)[0];
      const before = session[0].prevBest;
      text += " This session ran iteration" + (session.length === 1 ? " " : "s ") + session[0].id +
        (session.length > 1 ? "–" + session[session.length - 1].id : "") + ". ";
      if (sBest && before != null) text += "It took the best time from " + fmt(before) + " down to " + fmt(sBest.mean) + ".";
      else if (sBest) text += "It set the first recorded time.";
      else text += "None of them beat the standing best.";
    }
  }
  document.getElementById("lede").textContent = text;
})();

// Stats
(function () {
  if (!rows.length) return;
  const cells = [];
  cells.push(["Best time", fmt(best.mean), "iteration " + best.id + " · " + best.name]);
  if (first && first.id !== best.id)
    cells.push(["Since iteration " + first.id, Math.abs(pct(best.mean, first.mean)).toFixed(0) + "% faster", fmt(first.mean) + " → " + fmt(best.mean)]);
  cells.push(["Iterations", String(rows.length), rows.filter(r => r.newBest).length + " set a new best"]);
  if (session.length) {
    const sBest = session.filter(r => r.newBest).slice(-1)[0];
    const before = session[0].prevBest;
    cells.push(["This session", sBest && before != null ? "−" + fmt(before - sBest.mean) : (sBest ? fmt(sBest.mean) : "no change"),
      session.length + " iteration" + (session.length === 1 ? "" : "s") + (sBest ? "" : ", best unchanged")]);
  }
  document.getElementById("stats").innerHTML = cells.map(c =>
    '<div class="stat"><div class="k">' + esc(c[0]) + '</div><div class="v">' + esc(c[1]) + '</div><div class="d">' + esc(c[2]) + "</div></div>").join("");
})();

// Chart
(function () {
  const host = document.getElementById("plot");
  if (rows.length < 1) { host.textContent = "No data yet."; return; }
  const W = 940, H = 340, L = 58, R = 18, T = 14, B = 40;
  const n = rows.length;
  const maxV = Math.max(...rows.map(r => r.mean));
  // Round the axis top to a tidy step in seconds.
  const steps = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600];
  const step = steps.find(s => maxV / s <= 6) || 7200;
  const top = Math.ceil(maxV / step) * step;
  const x = i => n === 1 ? (L + (W - L - R) / 2) : L + (W - L - R) * i / (n - 1);
  const y = v => T + (H - T - B) * (1 - v / top);
  const ns = "http://www.w3.org/2000/svg";
  const svg = document.createElementNS(ns, "svg");
  svg.setAttribute("viewBox", "0 0 " + W + " " + H);
  svg.setAttribute("role", "img");
  svg.setAttribute("aria-label", "Mean time per iteration, " + n + " iterations; best " + fmt(best.mean));
  const add = (tag, attrs, text) => {
    const el = document.createElementNS(ns, tag);
    for (const k in attrs) el.setAttribute(k, attrs[k]);
    if (text != null) el.textContent = text;
    svg.appendChild(el); return el;
  };

  // Session band
  const sIdx = rows.map((r, i) => r.session ? i : -1).filter(i => i >= 0);
  if (sIdx.length) {
    const half = n > 1 ? (W - L - R) / (n - 1) / 2 : 40;
    const x0 = Math.max(L, x(sIdx[0]) - half), x1 = Math.min(W - R, x(sIdx[sIdx.length - 1]) + half);
    add("rect", { x: x0, y: T, width: x1 - x0, height: H - T - B, fill: "var(--band)" });
    document.getElementById("legend-session").hidden = false;
  }

  // Grid + y labels
  for (let v = 0; v <= top + 1e-9; v += step) {
    add("line", { x1: L, x2: W - R, y1: y(v), y2: y(v), stroke: "var(--rule)", "stroke-width": 1 });
    add("text", { x: L - 8, y: y(v) + 4, "text-anchor": "end", "font-size": 12, fill: "var(--ink-3)", "font-family": "var(--mono)" }, fmt(v).replace(/\.0$/, "").replace(/\.00s$/, "s"));
  }
  // X labels: every iteration when few, thinned when many
  const every = n <= 20 ? 1 : Math.ceil(n / 20);
  rows.forEach((r, i) => {
    if (i % every === 0 || i === n - 1)
      add("text", { x: x(i), y: H - B + 18, "text-anchor": "middle", "font-size": 12, fill: "var(--ink-3)", "font-family": "var(--mono)" }, r.id);
  });
  add("text", { x: (L + W - R) / 2, y: H - 4, "text-anchor": "middle", "font-size": 12, fill: "var(--ink-3)" }, "iteration");

  // Best-so-far step line (reference, not a series)
  let d = "", cur = null;
  rows.forEach((r, i) => {
    const b = r.newBest ? r.mean : cur;
    if (b == null) return;
    d += (d === "" ? "M" : "L") + x(i) + " " + y(cur == null ? b : cur) + " ";
    if (r.newBest) d += "L" + x(i) + " " + y(b) + " ";
    cur = b;
  });
  if (d) add("path", { d: d + "L" + (W - R) + " " + y(cur), fill: "none", stroke: "var(--ref)", "stroke-width": 1.5, "stroke-dasharray": "4 3" });

  // Series line
  add("path", { d: rows.map((r, i) => (i ? "L" : "M") + x(i) + " " + y(r.mean)).join(" "), fill: "none", stroke: "var(--series)", "stroke-width": 2, "stroke-linejoin": "round" });

  // Crosshair + points
  const cross = add("line", { x1: 0, x2: 0, y1: T, y2: H - B, stroke: "var(--ink-3)", "stroke-width": 1, visibility: "hidden" });
  const dots = rows.map((r, i) => add("circle", { cx: x(i), cy: y(r.mean), r: r.newBest ? 5 : 4.5,
    fill: r.newBest ? "var(--series)" : "var(--surface)", stroke: r.newBest ? "var(--surface)" : "var(--series)", "stroke-width": 2 }));

  // Direct label on the best point only
  const bi = rows.indexOf(best);
  const anchor = x(bi) > W - 150 ? "end" : (x(bi) < L + 90 ? "start" : "middle");
  add("text", { x: x(bi) + (anchor === "end" ? -9 : anchor === "start" ? 9 : 0), y: Math.min(H - B - 6, y(best.mean) + 20),
    "text-anchor": anchor, "font-size": 12.5, "font-weight": 600, fill: "var(--ink)", "font-family": "var(--mono)" }, fmt(best.mean));

  host.appendChild(svg);

  // Hover: nearest iteration by x
  const tip = document.getElementById("tip"), chart = document.getElementById("chart");
  function show(i, evt) {
    const r = rows[i];
    cross.setAttribute("x1", x(i)); cross.setAttribute("x2", x(i)); cross.setAttribute("visibility", "visible");
    dots.forEach((c, j) => c.setAttribute("r", j === i ? 6.5 : (rows[j].newBest ? 5 : 4.5)));
    let delta = "";
    if (r.prevBest != null) {
      const p = pct(r.mean, r.prevBest);
      delta = "<br>" + (p <= 0 ? "" : "+") + p.toFixed(1) + "% vs best before";
    }
    tip.innerHTML = "Iteration " + esc(r.id) + " · " + esc(r.name) + "<br><b>" + fmt(r.mean) + "</b>" + (r.newBest ? " · new best" : "") + delta;
    tip.hidden = false;
    const box = chart.getBoundingClientRect(), sb = svg.getBoundingClientRect();
    const px = sb.left - box.left + x(i) / W * sb.width, py = sb.top - box.top + y(r.mean) / H * sb.height;
    const tw = tip.offsetWidth, th = tip.offsetHeight;
    tip.style.left = Math.max(4, Math.min(box.width - tw - 4, px + (px > box.width / 2 ? -tw - 12 : 12))) + "px";
    tip.style.top = Math.max(4, py - th - 10) + "px";
  }
  function hide() { tip.hidden = true; cross.setAttribute("visibility", "hidden"); dots.forEach((c, j) => c.setAttribute("r", rows[j].newBest ? 5 : 4.5)); }
  function nearest(evt) {
    const sb = svg.getBoundingClientRect();
    const vx = (evt.clientX - sb.left) / sb.width * W;
    let bestI = 0, bestD = Infinity;
    rows.forEach((r, i) => { const dd = Math.abs(x(i) - vx); if (dd < bestD) { bestD = dd; bestI = i; } });
    return bestI;
  }
  svg.addEventListener("pointermove", e => show(nearest(e), e));
  svg.addEventListener("pointerdown", e => show(nearest(e), e));
  svg.addEventListener("pointerleave", hide);
})();

// Log table (also the table view of the chart)
(function () {
  let h = "<thead><tr><th class='num'>#</th><th>Iteration</th><th class='num'>Mean</th><th class='num'>vs best before</th><th class='num'>Runs</th></tr></thead><tbody>";
  rows.forEach(r => {
    let delta = "–", cls = "";
    if (r.prevBest != null) {
      const p = pct(r.mean, r.prevBest);
      delta = (p <= 0 ? "−" : "+") + Math.abs(p).toFixed(1) + "%";
      cls = p <= 0 ? "down" : "up";
    }
    const failed = r.winRate != null && r.winRate < 1;
    h += "<tr" + (r.session ? " class='session'" : "") + "><td class='num'>" + esc(r.id) + "</td><td><span class='name'>" + esc(r.name) + "</span>" +
      (r.id === best.id ? "<span class='tag best'>fastest</span>" : (r.newBest ? "<span class='tag best'>new best</span>" : "")) +
      (failed ? "<span class='tag fail'>goal missed</span>" : "") +
      (r.strategy ? "<div class='why'>" + esc(r.strategy) + "</div>" : "") +
      "</td><td class='num'>" + fmt(r.mean) + "</td><td class='num " + cls + "'>" + delta + "</td><td class='num'>" + (r.runs == null ? "–" : esc(r.runs)) + "</td></tr>";
  });
  document.getElementById("log").innerHTML = h + "</tbody>";
})();

if (DATA.ideas && DATA.ideas.length) {
  document.getElementById("ideas-section").hidden = false;
  document.getElementById("ideas").innerHTML = DATA.ideas.map(t => "<li>" + esc(t) + "</li>").join("");
}
document.getElementById("footer").textContent = "Generated " + DATA.generated + " from iterations/iteration_tracker.json." +
  (DATA.since != null ? " Shaded rows are this session (iteration " + DATA.since + " onward)." : "");
</script>
"""


def main():
    parser = argparse.ArgumentParser(description="Build a leaderboard recap page from the iteration tracker.")
    parser.add_argument("--since", type=int, default=None, help="First iteration id of the current session")
    parser.add_argument("--out", default=None, help="Output path")
    args = parser.parse_args()

    try:
        data = load(args.since)
    except (OSError, ValueError) as e:
        print(f"ERROR: could not read the tracker: {e}")
        sys.exit(1)
    if not data["rows"]:
        print("ERROR: the tracker has no iterations with a mean time yet")
        sys.exit(1)

    title = f"{data['leaderboard'].replace('_', ' ')} Leaderboard Recap"
    # "</" must not appear inside the inline script.
    payload = json.dumps(data, ensure_ascii=False).replace("</", "<\\/")
    page = PAGE.replace("__TITLE__", html.escape(title)).replace("__DATA__", payload)

    out = args.out or os.path.join(REPO, "iterations", f"recap_{data['leaderboard'].lower()}.html")
    with open(out, "w", encoding="utf-8") as f:
        f.write(page)
    best = [r for r in data["rows"] if r["newBest"]][-1]
    print(f"Wrote {out}")
    print(f"Title: {title}")
    print(f"{len(data['rows'])} iterations; best iteration {best['id']} ({best['name']}) at {best['mean']:.2f}s")


if __name__ == "__main__":
    main()
