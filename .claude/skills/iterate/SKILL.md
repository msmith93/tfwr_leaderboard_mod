---
name: iterate
description: Run exactly one leaderboard iteration -- optional diagnostics, write script, simulate (repeat runs within this iteration if needed), results JSON, notes, Discord, tracker update, cleanup. Outer loop is the leaderboard skill.
disable-model-invocation: true
argument-hint: "<leaderboard name, e.g. Hay_Single or Dinosaur>"
---

# Iterate: Single Leaderboard Iteration

Target leaderboard: $ARGUMENTS

Perform **one** numbered iteration cycle: diagnose (if needed), write a script, simulate, capture results, analyze, report to Discord, update tracker, and clean up. **Do not** start another iteration when finished; the **leaderboard** skill (`.claude/skills/leaderboard/SKILL.md`) decides whether to run this workflow again.

Within Step 2, you may run **`leaderboard_run` or `simulate` multiple times** in the same iteration (e.g. fix a bug and re-upload, or extra seeds for variance). That is still a single iteration as long as you publish **one** `iteration_<n>_*.py` and **one** primary `iteration_<n>_results.json` for this id.

## Before Starting

1. Read `iterations/iteration_tracker.json` to understand the current state.
2. Read `documentation/docs/unlocks/leaderboard.md` to confirm the win condition and starting inventory for the target leaderboard.
3. Read `documentation/builtins.py` to understand available game functions.
4. If previous iterations exist, read the most recent `iterations/iteration_<n>_notes.md` to understand what has been tried.

## Step 0 (Conditional): Run Diagnostics

**Run this step when:**
- The tracker has **no entries** for the target leaderboard (first iteration), OR
- The previous iteration's notes flag **unexpectedly bad results** that suggest incorrect assumptions about game mechanics
- There are details about the game that the documentation is not clear about that need to be tested in the game

**Skip this step** if no diagnostics are needed or if this is a subsequent iteration with no anomalies.

### What to diagnose

Write small, focused scripts that each measure one thing. Use `get_tick_count()`, `get_time()`, and `quick_print()` to capture measurements. Submit each via the API with `mode: "execute"` (see the invoke skill for the API flow).

Examples of useful measurements:
- Growth/harvest times and yields for the relevant plant type
- Tick costs for key actions (movement, hat changes, watering, planting, harvesting)
- Resource consumption rates (e.g., cactus cost per apple for Dinosaur)
- Powered vs. unpowered tick rates
- Polyculture multipliers or other yield modifiers
- Whether `use_item(Items.Water)` affects grow time for this plant

### How to record findings

- Save each diagnostic script as `diagnostics/diag_<leaderboard>_<what>.py`
- Save all calibrated constants in `diagnostics/calibration_<leaderboard>.json`:

```json
{
  "leaderboard": "Hay_Single",
  "calibrated_at": "2026-04-06T12:00:00Z",
  "constants": {
    "grass_grow_time_seconds": 0.5,
    "grass_yield_per_harvest": 512,
    "move_ticks": 200
  },
  "notes": "Grass auto-grows on Grassland. No planting or tilling needed."
}
```

- Review any existing calibration file before running diagnostics to avoid re-measuring known values.

## Step 1: Write a New Iteration Script

1. Determine the next iteration number from `iterations/iteration_tracker.json`.
2. If calibration data exists (`diagnostics/calibration_<leaderboard>.json`), read it and use the constants to inform your strategy.
3. Read notes from previous iterations to understand what has been tried and what to improve.
4. Read the relevant game mechanics docs in `documentation/docs/unlocks/` for the target leaderboard.
5. Write the algorithm script and save it as `iterations/iteration_<n>_<name>.py`.

### Script requirements

- Follow all interpreter safety rules from CLAUDE.md (no ternary, no triple quotes, no list multiplication, no `.reverse()`, no `sum()`).
- Use `quick_print()` for any output, never `print()`.
- The script **must terminate** when the win condition is met. The leaderboard timer does not stop until the program ends.
- Test the win condition in a `while` loop (e.g., `while num_items(Items.Hay) < 100000000:`) so the script exits as soon as the goal is reached.
- **Include timing output at the end**: `quick_print("FINAL_TIME: ", get_time())` -- this is required for `leaderboard_run()` to report per-run timing.

## Step 2: Run Simulations

Follow the two-file pattern from the invoke skill:

1. **Check game readiness**: `GET /status` must return `ready: true`.
2. **Submit the algorithm script**: POST with `mode: "execute"` and the iteration filename. Poll until done. Check `output` for errors.
3. **Submit the runner script**: POST a runner that calls `leaderboard_run()` on the algorithm script. Poll until done.
4. **Capture the runner output**: Save the raw `output` text from the poll response.

### Runner using leaderboard_run (primary)

The runner is simple since `leaderboard_run` handles repetition internally (repeats for 2 hours of game time, giving many data points):

```python
leaderboard_run(Leaderboards.<TARGET>, "iteration_<n>_<name>", 5000)
quick_print("---DONE---")
```

Replace `<TARGET>` with the leaderboard enum from `documentation/docs/unlocks/leaderboard.md` (e.g., `Hay_Single`, `Dinosaur`, `Pumpkins_Single`).

The algorithm script's `quick_print("FINAL_TIME: ", get_time())` lines provide per-repetition timing. Parse them from the output.

### Alternative runner using simulate (for seed control)

Use this only when you need reproducible runs with specific seeds:

```python
NUM_SEEDS = 10
SPEEDUP = 5000
SCRIPT_NAME = "iteration_<n>_<name>"
SIM_ITEMS = {}  # set per leaderboard from leaderboard.md

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

## Step 3: Capture Output in Standard Format

Parse the runner's output and save structured results to `iterations/iteration_<n>_results.json`.

For `leaderboard_run` output: parse all `FINAL_TIME: <value>` lines to get per-repetition times. For `simulate` output: parse `seed N time: T` lines.

The results JSON must follow this schema:

```json
{
  "iteration": 1,
  "name": "descriptive_name",
  "leaderboard": "Hay_Single",
  "timestamp": "2026-04-06T12:00:00Z",
  "strategy_summary": "1-2 sentence description of the approach",
  "method": "leaderboard_run",
  "n_runs": 72,
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

Compute `std_time_seconds`: for each run, compute `(t - mean)^2`, sum those with an accumulator loop, divide by the number of runs, and take the square root.

## Step 4: Analyze Results and Save Notes

Create `iterations/iteration_<n>_notes.md` with this structure:

```markdown
# Iteration <n>: <name>

## Strategy
What this iteration attempts and why. Reference specific mechanics or constants.

## Results
| Metric | Value |
|---|---|
| Win rate | 72/72 (100%) |
| Mean time | 123.456s |
| Std dev | 2.345s |
| Best | 120.1s |
| Worst | 128.9s |
| vs. previous best | -5.2s (4.1% improvement) |

## Analysis
What worked, what didn't. Identify bottlenecks.

## Next Steps
Concrete ideas for the next iteration. Be specific about what to change and why.
```

If results are unexpectedly bad, note this explicitly. That signals the **leaderboard** skill (or the next **iterate** pass) to consider Step 0 diagnostics.

## Step 5: Send Discord Message

Use the Discord MCP to send a 2-5 sentence summary to the designated channel.

Format:
> **TFWR Iteration <n>: <name>** (<leaderboard>)
> <strategy summary in 1 sentence>. Mean time: <mean>s across <N> runs (best: <best>s, worst: <worst>s).
> <comparison to previous best, if applicable>.
> Next direction: <1-sentence plan for next iteration>.

If this is the first iteration, skip the comparison line.

## Step 6: Update Tracker and Cleanup

Update `iterations/iteration_tracker.json`:

```json
{
  "leaderboard": "Hay_Single",
  "best_iteration": 1,
  "best_mean_time": 123.456,
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

- Update `best_iteration` and `best_mean_time` if this iteration achieved a new best.
- Append the new iteration entry to the `iterations` array.

### Cleanup: Delete temporary scripts from the game

After updating the tracker, clean up all temporary scripts from the game using `POST /delete-script`:

1. **Runner script**: always delete (`"filename": "runner"`)
2. **Diagnostic scripts**: delete any `diag_*` scripts created in Step 0
3. **Algorithm script**: optionally delete if the game workspace is getting crowded

```bash
curl -s -X POST http://localhost:8787/delete-script -d '{"filename": "runner"}'
```

### Handoff

**Stop here.** This iteration is complete. The **leaderboard** skill decides whether to read this file again for another pass; the user may also invoke **iterate** alone for a single step. Do not loop back to Step 1 on your own.
