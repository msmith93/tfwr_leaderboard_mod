---
name: invoke
description: How to run scripts in The Farmer Was Replaced via the REST API. Use when you need to submit code to the game, run simulations, capture output, or clean up scripts.
user-invocable: false
---

# Running Scripts in The Farmer Was Replaced

All interaction with the game goes through the REST API at `localhost:8787`. The game must be running with the TFWRRemoteAPI mod loaded. See `mod/README.md` for full endpoint docs.

## Prerequisites

Before any API call, verify the game is ready:

```bash
curl -s http://localhost:8787/status
```

Expected: `"ready": true`. If connection is refused, the game isn't running or the mod isn't loaded.

## Core Principle: execute mode + in-game simulation functions

Always submit scripts with **`mode: "execute"`**. To run leaderboard-style simulations, the script itself calls the game's built-in `leaderboard_run()` or `simulate()` function.

Do NOT use `mode: "simulate"` in the API.

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

Testing a leaderboard algorithm requires two API submissions:

### Step 1: Upload the algorithm script

POST the algorithm script with a descriptive filename. This writes it into the game's code workspace AND executes it once as a sanity check.

```bash
curl -s -X POST http://localhost:8787/run-script -d '{
  "code": "<algorithm_code_here>",
  "filename": "iteration_5_spiral",
  "mode": "execute"
}'
```

Poll until it finishes:

```bash
while true; do
  result=$(curl -s http://localhost:8787/poll)
  echo "$result" | python3 -m json.tool
  finished=$(echo "$result" | python3 -c "import sys,json; print(json.load(sys.stdin).get('finished',False))")
  if [ "$finished" = "True" ]; then break; fi
  sleep 3
done
```

Check the `output` field for errors. Fix the script before proceeding.

### Step 2: Upload and run the runner script

POST a runner script that calls `leaderboard_run()` (or `simulate()`) on the algorithm script.

```bash
curl -s -X POST http://localhost:8787/run-script -d '{
  "code": "<runner_code_here>",
  "filename": "runner",
  "mode": "execute"
}'
```

Poll until the runner finishes, then capture the `output` field from the poll response.

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

For `leaderboard_run` output, parse all `FINAL_TIME: <value>` lines:

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
| Mod not loaded | Connection refused or no response | Rebuild mod (`cd mod && bash build.sh`), restart game |
| Script parse error | `output` contains error text after step 1 | Fix the algorithm script syntax (check interpreter restrictions in CLAUDE.md) |
| Run hung/timeout | `/poll` never returns `finished: true` | `POST /stop`, then retry |
| Main thread busy | "Main thread call timed out" in response | `POST /stop` first, wait a moment, then retry |
| No output captured | `output` is empty | Ensure script uses `quick_print()`, not `print()` |

## Stopping a Run

```bash
curl -s -X POST http://localhost:8787/stop
```
