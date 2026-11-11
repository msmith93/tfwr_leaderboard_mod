---
name: invoke
description: How to run scripts in The Farmer Was Replaced via the REST API. Use when you need to submit code to the game, run simulations, capture output, or clean up scripts.
user-invocable: false
---

# Running Scripts in The Farmer Was Replaced

All interaction with the game goes through the REST API at `localhost:8787`. The game must be running with the TFWRRemoteAPI mod loaded. See `mod/README.md` for full endpoint docs.

## Helper Scripts

Use the Python helpers in `scripts/` for anything that waits, loops or parses. They are auto-approved because `Bash(python3:*)` is whitelisted; shell loops and curl pipelines trigger approval prompts.

| Script | Purpose |
|---|---|
| `python3 scripts/run_algorithm.py <path> <filename> <leaderboard> [speedup] [--demo-seconds N] [--save NAME]` | Full benchmark flow: wait for readiness → check save/menu → upload → sanity check → stop → optional **1×** preview for `N` wall-clock seconds → high-speed `leaderboard_run` → poll → print results |
| `python3 scripts/submit.py <path> <filename> [timeout]` | Upload one script, run it in execute mode, print its output (diagnostics) |
| `python3 scripts/run_sim_script.py <path> <filename> [--items ...] [--seed N]` | Run a script inside `simulate()` with chosen items and seed |
| `python3 scripts/poll.py` | Current run state, results and recent output |
| `python3 scripts/stop.py` | Stop any active execution |
| `python3 scripts/load_save.py [name]` | List saves, or load one and close the menu |
| `python3 scripts/session_recap.py [--since N]` | Build the recap page for the tracked leaderboard (`iterations/recap_<leaderboard>.html`) |
| `python3 scripts/screenshot.py [out.png]` | Save what the game window shows right now (then Read the image to look at it) |

Single `curl` calls are fine for one-off checks:

```bash
curl -s http://localhost:8787/status
curl -s -X POST http://localhost:8787/stop
curl -s -X POST http://localhost:8787/delete-script -d '{"filename": "runner"}'
```

## Preflight

Check `GET /status` once before the first run of a session:

```bash
curl -s http://localhost:8787/status
```

| Field | Needed | If not |
|---|---|---|
| (connection) | answers | Game not running, or BepInEx disabled (`winhttp.dll` missing in the game directory). Ask the user to start the game. |
| `ready`, `graphics_ready` | both `true` | Wait and poll again. `api.wait_until_ready_for_run()` does this; `run_algorithm.py` calls it automatically. Submitting before `graphics_ready` can crash the game under Proton. |
| `compat_warnings` | `[]` | The game updated and the mod no longer matches it. Stop and tell the user; results cannot be trusted until the mod is fixed (see "After a game update" in `mod/README.md`). |
| `active_save` | the save the user works in | The game often starts on a blank `Save0`. `python3 scripts/load_save.py` lists saves; load the right one (ask the user which if it is not obvious). A blank save shows up as "Unlocks.X must be unlocked" errors. |
| `menu_open` | `false` | `python3 scripts/load_save.py --close-menu`. Runs work with the menu open, but an observer sees only the menu. |

## Core Principle: execute mode + in-game simulation functions

Submit scripts with **`mode: "execute"`** and have the script call the game's built-in `leaderboard_run()` or `simulate()`. This is the path `run_algorithm.py` uses and the one all recorded results came from.

The mod also accepts `mode: "simulate"` (with `seed`, `items`, `unlocks`, `globals`, `speedup`) and `mode: "leaderboard"` (with `leaderboard`, `speedup`), which run a file directly without a runner script. They are handy for a quick seeded diagnostic; see `mod/README.md`. Keep benchmarks on the helper so results stay comparable.

`open_editor` defaults to `true`: the code window opens and the script is parsed through it. Leave it on; parse errors only carry a line number on that path.

## Two Methods for Simulation

### Primary: leaderboard_run() (recommended)

Uses the exact fixed starting conditions for each leaderboard category. No risk of misconfiguring items or unlocks. Internally repeats the simulation for 2 hours of game time and averages the results, which is how the real leaderboard works. A run that reaches the goal uploads its average to the Steam leaderboard, exactly as when started by hand.

```
leaderboard_run(leaderboard, filename, speedup)
```

- `leaderboard`: e.g. `Leaderboards.Hay_Single`, `Leaderboards.Dinosaur`
- `filename`: name of a code file already in the game workspace
- `speedup`: speed multiplier (5000 recommended)
- Returns: `None`, and nothing after it in the runner executes: the game stops the runner when the leaderboard run starts

End the algorithm script with a timing line so each repetition reports its time:

```python
# At the very end of the algorithm script, after the win condition is met:
quick_print("FINAL_TIME: ", get_time())
```

The mod records the same information independently of what the script prints, in `GET /poll`:

- `run_times`: sim time of each repetition
- `leaderboard`: the game's own verdict once the run ends: `completed` (goal reached), `average_time_seconds`, `runs`

Use `leaderboard.completed` for the win condition. A script that ends without reaching the goal still prints `FINAL_TIME`, but the game marks the run as not completed and stops after that one repetition.

See `documentation/docs/unlocks/leaderboard.md` for all leaderboard names and win conditions.

### Fallback: simulate() (for seed control)

Use when you need reproducible runs with specific seeds, or when testing with custom starting conditions.

```
run_time = simulate(filename, sim_unlocks, sim_items, sim_globals, seed, speedup)
```

- Returns: simulation time in seconds
- See `documentation/docs/unlocks/simulation.md` for full details
- See `documentation/docs/unlocks/leaderboard.md` for equivalent simulation parameters per leaderboard

## Two-File Pattern

The algorithm lives in one game file; a tiny runner file calls `leaderboard_run` on it. The helper does all of it:

```bash
python3 scripts/run_algorithm.py iterations/iteration_5_spiral.py iteration_5_spiral Dinosaur 5000
```

Observer preview (real-time, for humans watching): add `--demo-seconds 15` (or another `N`). The script runs `leaderboard_run` at **1×** for `N` wall-clock seconds, stops, then runs the benchmark at `[speedup]` (default 5000).

```bash
python3 scripts/run_algorithm.py iterations/iteration_5_spiral.py iteration_5_spiral Dinosaur 5000 --demo-seconds 15
```

The helper exits non-zero, with the reason, if the script fails to parse (message and line), hits a runtime error during the sanity check or the benchmark, or the benchmark times out. On success it prints the benchmark output, a `=== GAME RESULT ===` block (the game's verdict) and a `=== PARSED RESULTS ===` block (from `FINAL_TIME` lines).

### Waiting for long benchmarks

A benchmark can outlast a single foreground command. Run the helper in the background, redirect to a log, and append an exit marker so completion is unambiguous:

```bash
python3 scripts/run_algorithm.py <path> <filename> <Leaderboard> 5000 > /tmp/run.log 2>&1; echo "RUNNER_EXIT:$?" >> /tmp/run.log
```

Wait for the `RUNNER_EXIT:` line before reading results. Do not treat a background-task notification alone as proof the benchmark finished.

## What the Viewer Sees

For people watching the game window, the helpers put three things on screen without any extra work from you:

- **Captions**: every `./scripts/tts.py "<line>"` call shows the same text in a strip at the bottom of the game for as long as the audio plays. The caption is exactly what is spoken, so keep spoken lines short and self-contained. (`--no-caption` turns it off.)
- **Scoreboard**: `run_algorithm.py` shows a panel with the leaderboard, the iteration, the live clock and running average, the best time so far, and a bar per past iteration. It reads the best time and history from `iterations/iteration_tracker.json`, which is one more reason to keep the tracker current.
- **Camera**: `run_algorithm.py` frames the whole farm at the start of the preview and the benchmark, and again when the run is over.

`run_algorithm.py --narrate` adds one spoken (and captioned) progress line halfway through the benchmark, comparing the running average with the best so far. `--no-overlay` disables the scoreboard and camera moves.

To set these yourself (for example during diagnostics), use `api.overlay(...)`, `api.camera_fit()` and `api.camera_release()` from `scripts/api.py`, or the `/overlay` and `/camera` endpoints in `mod/README.md`. To check what is actually on screen, take a screenshot and look at it.

## Runner Script Templates

### Using leaderboard_run (primary)

```python
leaderboard_run(Leaderboards.Hay_Single, "iteration_1_basic", 5000)
```

### Using simulate (fallback, for seed control)

```python
NUM_SEEDS = 10
SPEEDUP = 5000
SCRIPT_NAME = "iteration_5_spiral"
SIM_ITEMS = {Items.Cactus: 1000000000, Items.Power: 1000000000}

times = []
for seed in range(NUM_SEEDS):
    t = simulate(SCRIPT_NAME, Unlocks, SIM_ITEMS, {}, seed, SPEEDUP)
    quick_print("seed ", seed, " time: ", t)
    times.append(t)

total = 0
for t in times:
    total = total + t
mean = total / len(times)

quick_print("---RESULTS---")
quick_print("seeds: ", NUM_SEEDS)
quick_print("mean: ", mean)
quick_print("best: ", min(times))
quick_print("worst: ", max(times))
```

Note: `sum()` is not available in the game interpreter. Use an explicit accumulator loop. `len()`, `min()`, and `max()` are available.

## Reading Results

`GET /poll` fields that matter:

| Field | Meaning |
|---|---|
| `state` | `starting`, `running`, `finished`, `stopped`, `error` (or `idle` before any run) |
| `finished` | `true` for `finished`, `stopped` and `error` |
| `error` | The game's runtime error and trace when `state` is `error` |
| `output` | Everything printed since this run started. It is complete (no line limit) and is cleared when the next run starts, so it never contains a previous run's lines. |
| `next_offset` | Pass as `/poll?since=<n>` to get only new output (`api.poll_until_done` does this) |
| `run_times`, `leaderboard`, `sim_time_seconds` | Results recorded by the mod (see above) |

When `--demo-seconds` is used, take statistics from the **benchmark** phase only: the helper's final `FULL OUTPUT`, `GAME RESULT` and `PARSED RESULTS` blocks all belong to the benchmark. Ignore anything printed during the preview.

To parse `FINAL_TIME` yourself:

```python
import re
times = [float(m.group(1)) for m in re.finditer(r"FINAL_TIME:\s*([\d.]+)", output_text)]
```

For `simulate` output, parse `seed N time: T` lines (see the runner template above for the format).

## Cleaning Up Scripts

After capturing results, delete temporary scripts from the game:

```bash
curl -s -X POST http://localhost:8787/delete-script -d '{"filename": "runner"}'
```

This closes the code window and removes the `.py` file from the save. Always clean up runner and diagnostic scripts after use. Never delete files you did not create: the save also holds the user's own scripts.

## Error Handling

API failures are HTTP errors with a JSON body `{"error": "..."}`. `scripts/api.py` returns them as a dict with `error` and `http_status` instead of raising, so check `result.get("error")`.

| Problem | Symptom | Fix |
|---|---|---|
| Game not running / mods off | Connection refused | Ask the user to start the game; see Preflight |
| Mod out of date for the game | `compat_warnings` not empty, or errors naming missing methods | Stop; the mod needs updating (`mod/README.md`, "After a game update") |
| GPU not ready | 503 from `run-script`, or `graphics_ready: false` | `api.wait_until_ready_for_run()` |
| Script parse error | `run-script` returns 400 with `error` and `line` | Fix the syntax (check interpreter restrictions in CLAUDE.md) |
| Script runtime error | `/poll` shows `state: "error"` with the message in `error` | Fix the script. `POST /stop` clears the paused run. |
| Wrong save loaded | Errors like "Unlocks.X must be unlocked", or an empty inventory | `python3 scripts/load_save.py <name>` |
| Run never finishes | `state` stays `running` | The script has not terminated (win condition never met?). `POST /stop`, then investigate at low speedup. |
| Game busy | 504 "Timed out waiting for the game's main thread" | The request was not applied. Wait a moment and retry. |
| No output captured | `output` is empty | Use `quick_print()`, not `print()` |

## Stopping a Run

```bash
curl -s -X POST http://localhost:8787/stop
```

This ends any execution, simulation or leaderboard run, closes the result screen and resets speed to 1×.
