# TFWR Leaderboard Automation

**Demo:** [Watch Claude iterate on a Dinosaur leaderboard script](https://youtu.be/cgPKENI8BcY?si=iDeD07lb7u2fi_Ye)

Automated leaderboard script development for **The Farmer Was Replaced** (TFWR). This repo contains two main components:

1. **A BepInEx mod** (`mod/`) that exposes a REST API on `localhost:8787`, letting external tools submit scripts, trigger simulations, and read results from a running game instance.
2. **Claude agent skills** (`.claude/skills/`) that drive an AI-powered iteration loop — diagnosing game mechanics, writing algorithm scripts, benchmarking them, and improving toward the best leaderboard time.

---

## Repository Structure

```
mod/                          BepInEx plugin source
  TFWRRemoteAPI/
    Plugin.cs                 HTTP listener, endpoint routing
    GameBridge.cs             Safe wrappers for game APIs
  build.sh                    Build and install the plugin
  README.md                   Full mod / API reference

documentation/
  builtins.py                 All game functions, types, and enums
  docs/
    unlocks/
      leaderboard.md          Win conditions and starting inventories per leaderboard
      simulation.md           simulate() reference
    scripting/                Language reference (variables, loops, functions, etc.)

scripts/
  api.py                      Python wrapper around the REST API
  run_algorithm.py            Full benchmark flow: upload → sanity check → run → parse
  poll.py                     Show current output and FINAL_TIME count
  stop.py                     Stop any active execution

iterations/                   Algorithm scripts, results JSON, analysis notes
  iteration_tracker.json      Master tracker: best iteration, mean times, all entries

diagnostics/                  One-off measurement scripts and calibration data

.claude/skills/
  leaderboard/SKILL.md        Outer loop: prompt → iterate → stop
  iterate/SKILL.md            Single iteration: write → run → results → notes → tracker
  invoke/SKILL.md             API reference for scripts and helpers
```

---

## The Mod

The BepInEx plugin patches TFWR to listen for HTTP requests. The game must be running with the mod loaded before any API calls will work.

### Prerequisites

- **The Farmer Was Replaced** (Steam, Unity 6)
- **BepInEx 5.4.23** installed in the game directory
- **.NET SDK** (any version targeting `net46`)

### Building and Installing

```bash
cd mod/
bash build.sh
```

This compiles `TFWRRemoteAPI.dll` and copies it to `BepInEx/plugins/`. Restart the game to load the mod.

### Verifying

After launching and loading a save:

```bash
curl http://localhost:8787/status
```

Expected:

```json
{ "ready": true, "graphics_ready": true, "executing": false, "simulating": false }
```

### Key API Endpoints

| Endpoint | Method | Purpose |
|---|---|---|
| `/status` | GET | Check `ready` + `graphics_ready` before any submission |
| `/run-script` | POST | Upload and execute a script |
| `/poll` | GET | Check run status, get `quick_print()` output, inventory |
| `/output` | GET | Output buffer only |
| `/inventory` | GET | Current farm inventory snapshot |
| `/stop` | POST | Stop any active execution |
| `/dismiss` | GET/POST | Dismiss the leaderboard result screen |
| `/delete-script` | POST | Delete a script file and close its code window |

See `mod/README.md` for full parameter docs and examples.

---

## The Agent Skills

Three Claude Code skills orchestrate the full development loop. Invoke them in Claude Code with `/leaderboard`, `/iterate`, or reference `/invoke` for API details.

### `/leaderboard` — Full Development Loop

Drives the project from a prompt to the best script found within an iteration budget. Handles strategy planning, the outer loop, stopping criteria (diminishing returns, max iterations), and optional TTS narration for live demos.

```
/leaderboard Dinosaur [goals or constraints] [observer demo: yes/no]
```

### `/iterate` — Single Iteration

Runs exactly one numbered iteration end-to-end:

1. Read tracker + notes + game docs
2. (Optional) Run diagnostic scripts to calibrate unknown constants
3. Write `iterations/iteration_<n>_<name>.py`
4. Benchmark with `run_algorithm.py` (optional 1× real-time observer preview, then high-speed)
5. Parse `FINAL_TIME:` output → `iterations/iteration_<n>_results.json`
6. Write `iterations/iteration_<n>_notes.md` with analysis and next steps
7. Post Discord summary
8. Update `iterations/iteration_tracker.json`
9. Clean up temporary runner and diagnostic scripts from the game

### `/invoke` — API Reference

Not user-invocable directly; loaded automatically when any skill needs to submit code or manage runs. Documents the two-file upload pattern, `leaderboard_run` vs `simulate`, output parsing, and error handling.

---

## Running Benchmarks Manually

```bash
# Upload and run a leaderboard benchmark (5000× speedup)
python3 scripts/run_algorithm.py \
  iterations/iteration_1_hamiltonian_baseline.py \
  iteration_1_hamiltonian_baseline \
  Dinosaur \
  5000

# With a 30-second real-time observer preview before the benchmark
python3 scripts/run_algorithm.py \
  iterations/iteration_1_hamiltonian_baseline.py \
  iteration_1_hamiltonian_baseline \
  Dinosaur \
  5000 \
  --demo-seconds 30
```

The helper waits for `graphics_ready`, uploads the script, does a quick sanity check, runs `leaderboard_run` at the requested speedup for 2 hours of in-game time, and prints parsed `FINAL_TIME` statistics.

---

## Game Interpreter Restrictions

TFWR uses a custom Python-like interpreter. These standard Python patterns will cause errors:

| Unsupported | Use instead |
|---|---|
| `"""docstrings"""` | `# comments` |
| `a if cond else b` | Explicit `if`/`else` block |
| `[0] * N` | `append` in a loop |
| `.reverse()` | Build a reversed copy with an index loop |
| `sum(lst)` | Explicit accumulator loop |
| `print()` | `quick_print()` |

Algorithm scripts must end with `quick_print("FINAL_TIME: ", get_time())` so `leaderboard_run` can collect per-repetition timing.

---

## Current Progress

The active leaderboard is **Dinosaur** (collect bones while managing dinosaur spawns). Best result across 14 iterations: **1000.6s mean** (iteration 14: `lean_warmup`), down from a 1721s baseline.

See `iterations/iteration_tracker.json` for the full history and `iterations/iteration_14_notes.md` for the latest analysis.
