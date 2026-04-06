# TFWR Leaderboard Automation

This project automates leaderboard script development for **The Farmer Was Replaced** (TFWR). A BepInEx mod exposes a REST API on `localhost:8787` that lets you submit scripts, run in-game simulations, and capture results programmatically.

## Project Structure

```
mod/                    BepInEx plugin source + API docs (see mod/README.md)
documentation/
  builtins.py           Game API stubs (all available functions, types, enums)
  docs/                 Game mechanics, scripting reference, unlock guides
  docs/unlocks/
    leaderboard.md      Leaderboard win conditions and starting inventories
    simulation.md       simulate() function reference
iterations/             Iteration scripts, results, notes, and tracker
  iteration_tracker.json  Master tracker for all iterations
```

## Game Interpreter Restrictions

TFWR uses a custom Python-like interpreter, NOT standard CPython. The following patterns are **unsupported** and will cause parse or runtime errors:

- **No triple-quoted strings** (`"""..."""` or `'''...'''`) -- use `# comments` instead
- **No ternary expressions** (`a if cond else b`) -- use explicit `if`/`else` blocks
- **No list multiplication** (`[0] * N`) -- use `append` in a loop
- **No `.reverse()` method** -- build a reversed copy with an index loop
- **No `sum()` builtin** -- use an explicit accumulator loop
- **No `print()`** for captured output -- use `quick_print()` exclusively

### Safe patterns

```python
# Comments (not docstrings)
# This function does X

# Conditional assignment
result = 0
if condition:
    result = a
else:
    result = b

# List initialization
lst = []
for i in range(N):
    lst.append(0)

# Reversing a list
reversed_lst = []
for i in range(len(lst) - 1, -1, -1):
    reversed_lst.append(lst[i])

# Summing a list (sum() is not available)
total = 0
for val in lst:
    total = total + val
```

## REST API Quick Reference

The game must be running with the TFWRRemoteAPI mod loaded. Full docs: `mod/README.md`.

| Endpoint | Method | Purpose |
|---|---|---|
| `/status` | GET | Check if game is ready |
| `/run-script` | POST | Submit and execute a script |
| `/poll` | GET | Check run status, get output and inventory |
| `/output` | GET | Get `quick_print()` output buffer only |
| `/inventory` | GET | Get current farm inventory |
| `/stop` | POST | Stop any active execution |
| `/dismiss` | GET/POST | Dismiss leaderboard result screen (also auto-dismissed after 3s) |
| `/delete-script` | POST | Delete a script file and close its code window |

### Submitting scripts

Always use `mode: "execute"`. To run leaderboard-style simulations, call `leaderboard_run()` or `simulate()` from within the script itself.

```bash
curl -s -X POST http://localhost:8787/run-script \
  -d '{"code": "<script_code>", "filename": "<name>", "mode": "execute"}'
```

### leaderboard_run() (primary -- recommended)

Uses exact fixed starting conditions for each leaderboard. Internally repeats the script for 2 hours of game time and averages results.

```
leaderboard_run(leaderboard, filename, speedup)
```

- `leaderboard`: e.g. `Leaderboards.Hay_Single`, `Leaderboards.Dinosaur`
- `filename`: name of a code file already in the game workspace
- `speedup`: speed multiplier (5000 recommended)
- Returns: `None` -- the algorithm script must self-report timing with `quick_print("FINAL_TIME: ", get_time())`

See `documentation/docs/unlocks/leaderboard.md` for all leaderboard names and win conditions.

### simulate() (fallback -- for seed control)

Use when you need reproducible runs with specific seeds or custom starting conditions.

```
run_time = simulate(filename, sim_unlocks, sim_items, sim_globals, seed, speedup)
```

- Returns: simulation time in seconds
- See `documentation/docs/unlocks/simulation.md` for full details
- See `documentation/docs/unlocks/leaderboard.md` for equivalent simulation parameters per leaderboard

## Iteration Conventions

### File naming

| File | Pattern |
|---|---|
| Algorithm script | `iterations/iteration_<n>_<name>.py` |
| Results JSON | `iterations/iteration_<n>_results.json` |
| Analysis notes | `iterations/iteration_<n>_notes.md` |
| Diagnostic script | `iterations/diag_<leaderboard>_<what>.py` |
| Calibration data | `iterations/calibration_<leaderboard>.json` |
| Master tracker | `iterations/iteration_tracker.json` |

### Results JSON schema

```json
{
  "iteration": 1,
  "name": "descriptive_name",
  "leaderboard": "Hay_Single",
  "timestamp": "2026-04-06T12:00:00Z",
  "strategy_summary": "1-2 sentence description of approach",
  "method": "leaderboard_run",
  "n_runs": 10,
  "win_rate": 1.0,
  "mean_time_seconds": 123.456,
  "std_time_seconds": 2.345,
  "best_time_seconds": 120.1,
  "worst_time_seconds": 128.9,
  "runs": [
    {"run": 0, "time_seconds": 121.5},
    {"run": 1, "time_seconds": 124.2}
  ]
}
```

### Notes markdown template

```markdown
# Iteration <n>: <name>

## Strategy
What this iteration attempts and why.

## Results
Key metrics: win rate, mean/std/best/worst times, comparison to previous iteration.

## Analysis
What worked, what didn't, bottlenecks identified.

## Next Steps
Concrete ideas for the next iteration.
```

### Tracker JSON schema

```json
{
  "leaderboard": "Hay_Single",
  "best_iteration": 3,
  "best_mean_time": 98.7,
  "iterations": [
    {
      "id": 1,
      "name": "basic_sweep",
      "script": "iterations/iteration_1_basic_sweep.py",
      "results": "iterations/iteration_1_results.json",
      "notes": "iterations/iteration_1_notes.md",
      "mean_time_seconds": 123.456,
      "win_rate": 1.0,
      "timestamp": "2026-04-06T12:00:00Z"
    }
  ]
}
```

## Skills

- **invoke** -- Reference knowledge for submitting and running scripts via the REST API (loaded automatically when relevant)
- `/iterate <leaderboard>` -- Full iteration loop: diagnose, write, simulate, capture, analyze, report, repeat (invoke manually)

## Key Documentation References

- `documentation/builtins.py` -- All game functions, types, and enums (read this first for any new leaderboard)
- `documentation/docs/unlocks/leaderboard.md` -- Win conditions and starting inventories for every leaderboard
- `documentation/docs/unlocks/simulation.md` -- How `simulate()` works
- `documentation/docs/scripting/` -- Language reference (variables, loops, functions, lists, dicts, etc.)
- `documentation/docs/unlocks/` -- Mechanics for each game feature (plants, mazes, dinosaurs, etc.)
