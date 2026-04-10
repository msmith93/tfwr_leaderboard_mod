---
name: invoke
description: How to run scripts in The Farmer Was Replaced via the REST API. Use when you need to submit code to the game, run simulations, capture output, or clean up scripts.
user-invocable: false
---

# Running Scripts in The Farmer Was Replaced

All interaction with the game goes through the REST API at `localhost:8787`. The game must be running with the TFWRRemoteAPI mod loaded. See `mod/README.md` for full endpoint docs.

## Helper Scripts

All API interaction should go through the pre-approved Python helper scripts in `scripts/`. These are auto-approved because `Bash(python3:*)` is whitelisted. **Do not use raw curl pipelines or shell loops** — they require approval prompts.

| Script | Purpose |
|---|---|
| `python3 scripts/run_algorithm.py <path> <filename> <leaderboard> [speedup] [--demo-seconds N]` | Full flow: waits for `graphics_ready` → upload → sanity check → stop → optional **1×** `leaderboard_run` at speedup **1** for `N` wall-clock seconds (then stop) → high-speed `leaderboard_run` → poll → parse |
| `python3 scripts/poll.py` | Show current poll status and output |
| `python3 scripts/stop.py` | Stop any active execution |

For one-off API calls, use `curl` directly (also whitelisted):

```bash
curl -s http://localhost:8787/status
curl -s -X POST http://localhost:8787/stop -H "Content-Length: 0"
curl -s -X POST http://localhost:8787/delete-script -H "Content-Type: application/json" -d '{"filename": "runner"}'
```

## Prerequisites

Before **`POST /run-script`** (or any execution that opens or uses a `CodeWindow` for parsing), the GPU must be initialized. The mod exposes this on **`GET /status`** as **`graphics_ready`**.

**Required before uploads / runs:** both `"ready": true` and `"graphics_ready": true`.

```bash
curl -s http://localhost:8787/status
```

If connection is refused, the game isn't running or the mod isn't loaded. If `ready` is true but `graphics_ready` is false, wait and poll (e.g. every 0.5–1s) until the graphics device comes up—common right after launch or when the window hasn't fully initialized under Proton.

**Python helpers:** `scripts/api.py` provides `wait_until_ready_for_run()`; `scripts/run_algorithm.py` calls it automatically before any `submit`. For custom scripts, import `api` and call `api.wait_until_ready_for_run()` before `submit` / `submit_file`.

## Core Principle: execute mode + in-game simulation functions

Always submit scripts with **`mode: "execute"`**. To run leaderboard-style simulations, the script itself calls the game's built-in `leaderboard_run()` or `simulate()` function.

Do NOT use `mode: "simulate"` in the API.

Default **`open_editor`** is **`true`** in the mod and in `scripts/api.submit` / `submit_file`: the code window opens for parsing (reliable with Core’s `Parser.Parse`). Under Proton/Wine, **wait for `graphics_ready` on `GET /status`** (and use `api.wait_until_ready_for_run()` before submits) so the GPU is initialized first. Set **`open_editor`: false** only if you explicitly want to avoid opening the editor and accept the scratch/existing-window parse path.

## Two Methods for Simulation

### Primary: leaderboard_run() (recommended)

Uses the exact fixed starting conditions for each leaderboard category. No risk of misconfiguring items or unlocks. Internally repeats the simulation for 2 hours of game time and averages the results, which is how the real leaderboard works.

```
leaderboard_run(leaderboard, filename, speedup)
```

- `leaderboard`: e.g. `Leaderboards.Hay_Single`, `Leaderboards.Dinosaur`
- `filename`: name of a code file already in the game workspace
- `speedup`: speed multiplier (5000 recommended)
- Returns: `None` (the algorithm script must self-report timing via `quick_print`)

Since `leaderboard_run` returns None, the **algorithm script must include timing output** at the end:

```python
# At the very end of the algorithm script, after the win condition is met:
quick_print("FINAL_TIME: ", get_time())
```

Since `leaderboard_run` internally repeats the script for 2 hours of game time, this produces multiple `FINAL_TIME` lines -- one per repetition -- giving many data points automatically.

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

Use the pre-approved helper script — it handles the full flow without shell loops or approval prompts:

```bash
python3 scripts/run_algorithm.py iterations/iteration_5_spiral.py iteration_5_spiral Dinosaur 5000
```

Observer preview (real-time, for humans watching): add `--demo-seconds 15` (or another `N`). The script runs `leaderboard_run` at **1×** for `N` wall-clock seconds, stops, then runs the benchmark at `[speedup]` (default 5000).

```bash
python3 scripts/run_algorithm.py iterations/iteration_5_spiral.py iteration_5_spiral Dinosaur 5000 --demo-seconds 15
```

This does: upload → sanity check → stop → optional demo at 1× → stop → benchmark `leaderboard_run` → poll → parse FINAL_TIME lines. For iteration results JSON, parse `FINAL_TIME` only from the **benchmark** phase (see iterate skill).

Other helpers (all `python3:*`, auto-approved):
```bash
python3 scripts/poll.py          # show current output and FINAL_TIME count
python3 scripts/stop.py          # stop active execution
```

### Manual curl approach (for one-off checks)

```bash
curl -s http://localhost:8787/status
curl -s -X POST http://localhost:8787/stop -H "Content-Length: 0"
curl -s -X POST http://localhost:8787/delete-script \
  -H "Content-Type: application/json" -d '{"filename": "runner"}'
```

## Runner Script Templates

### Using leaderboard_run (primary)

The runner is simple because `leaderboard_run` handles repetition internally:

```python
leaderboard_run(Leaderboards.Hay_Single, "iteration_1_basic", 5000)
quick_print("---DONE---")
```

The algorithm script must end with `quick_print("FINAL_TIME: ", get_time())` so each repetition reports its time. Parse all `FINAL_TIME` lines from the output to compute mean/std/best/worst.

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

## Parsing Output

The `output` field from `/poll` contains all `quick_print()` output as a single string with `\n` separators.

For `leaderboard_run` output, parse all `FINAL_TIME: <value>` lines from the **benchmark** run only. If you used `--demo-seconds`, ignore any `FINAL_TIME` lines produced during the short 1× preview (the helper prints `=== BENCHMARK ===` before the timed run; only that phase’s output should feed statistics).

```python
import re
times = []
for line in output_text.split("\n"):
    if "FINAL_TIME: " in line:
        match = re.search(r"FINAL_TIME:\s*([\d.]+)", line)
        if match:
            times.append(float(match.group(1)))
```

For `simulate` output, parse `seed N time: T` lines (see the runner template above for the format).

## Cleaning Up Scripts

After capturing results, delete temporary scripts from the game:

```bash
curl -s -X POST http://localhost:8787/delete-script -d '{"filename": "runner"}'
```

This removes the `.py` file from the game's save directory and closes the code window. Always clean up runner scripts after use.

## Error Handling

| Problem | Symptom | Fix |
|---|---|---|
| Game not running | Connection refused on curl | Start the game, load a save, verify with `GET /status` |
| GPU not ready | `graphics_ready: false` or run-script error about graphics | Poll `GET /status` until `graphics_ready` is true; use `api.wait_until_ready_for_run()` |
| Mod not loaded | Connection refused or no response | Rebuild mod (`cd mod && bash build.sh`), restart game |
| Script parse error | `output` contains error text after step 1 | Fix the algorithm script syntax (check interpreter restrictions in CLAUDE.md) |
| Run hung/timeout | `/poll` never returns `finished: true` | `POST /stop`, then retry |
| Main thread busy | "Main thread call timed out" in response | `POST /stop` first, wait a moment, then retry |
| No output captured | `output` is empty | Ensure script uses `quick_print()`, not `print()` |

## Stopping a Run

```bash
curl -s -X POST http://localhost:8787/stop
```
