---
name: leaderboard
description: End-to-end TFWR leaderboard script development from a user prompt until stop criteria are met. Orchestrates multiple single passes using the iterate skill; use invoke for REST API details.
argument-hint: "<leaderboard name, e.g. Hay_Single> [optional goals or constraints] [observer demo: yes/no or omit to be asked]"
---

# Leaderboard: Full Script Development Flow

Target and context: $ARGUMENTS

Drive the project from the user's prompt toward the **best script you can produce within the iteration limit** (and other stop rules below), by running **one full pass of the iterate skill at a time** and deciding after each pass whether to continue.

This skill owns **prioritization**, **stopping rules**, and the **outer loop**. It does **not** replace the iterate or invoke skills: load and follow `.claude/skills/iterate/SKILL.md` for each iteration, and `.claude/skills/invoke/SKILL.md` for every HTTP/API step (curl, poll, `leaderboard_run`, cleanup).

## Parse the prompt

From `$ARGUMENTS` and the conversation, extract:

- **Leaderboard**: enum name as in `documentation/docs/unlocks/leaderboard.md` (e.g. `Hay_Single`, `Dinosaur`).
- **Goal**: e.g. lower mean time, reach win rate 1.0, explore a named strategy, match a constraint from the user.
- **Observer demo** (real-time preview before each benchmark):
  - **On** if the user clearly opts in (e.g. “with demo”, “show 1× preview”, “observer mode”, “live demo”). Default preview length is **15** wall-clock seconds unless they give another positive integer (e.g. “demo 30 seconds” → `demo_seconds: 30`).
  - **Off** if they clearly opt out (e.g. “no demo”, “fast only”, “skip preview”) → `demo_seconds: 0`.
  - If **not specified** in `$ARGUMENTS` or the conversation, **ask the user once** before the first iteration: whether to run an approximately **15s** real-time `leaderboard_run` preview before each benchmark, or skip. Do **not** assume on/off without asking.
  - **Session state:** store the resolved integer **`demo_seconds`** (`0` = off, or `N` > 0 for preview length). Reuse it for **every** iterate pass in this session unless the user later asks to change it.
- **Limits** (defaults if unstated):
  - **Max iterations** — primary control: how many complete **iterate** passes to allow this session (suggest default **8**; stop sooner if criteria below fire). This is concrete and enforceable.
    - If the user says **“N more iterations”** or **“run N iterations”**, treat **N** as **N sequential outer-loop cycles** — **not** permission to draft N scripts or run N benchmarks in one burst before the first cycle’s evaluate step (see **Sequencing** below).
  - **Wall-clock deadlines** — the model does **not** automatically know elapsed time; only honor these if the user asks and you explicitly check the clock (e.g. via a tool). Simulation length is unpredictable, so time limits are **soft** and easy to overshoot. Prefer **max iterations** for a reliable cap.
  - Optional user hints such as “stop after N extra `leaderboard_run` batches” inside one iteration are handled inside **iterate**, not as a substitute for the iteration cap.

## Startup: high-level state

Before the first iteration:

1. Read `iterations/iteration_tracker.json` for this leaderboard (or confirm none).
2. Skim the latest `iterations/iteration_<n>_notes.md` for that line of work, if any.
3. Confirm win condition and starting inventory in `documentation/docs/unlocks/leaderboard.md`.
4. Ensure **`demo_seconds`** is resolved per **Observer demo** above (parse from prompt or **ask once** if still unspecified). You need this value before the first iteration’s Step 2.
5. Run the **Preflight** from `.claude/skills/invoke/SKILL.md` once: the game answers on `/status`, `compat_warnings` is empty, the intended save is active (the game often starts on a blank `Save0`; ask the user which save to use if it is not obvious), and the menu is closed. If the game was updated since the last session, also refresh `documentation/` from the game install (see CLAUDE.md) and re-read `leaderboard.md`, since win conditions and available leaderboards can change. Do not start iterating against a game the mod does not match.

The **iterate** skill repeats its own “Before Starting” reads each time you run it; you do not need to duplicate every file on every loop, but re-check the tracker after each iteration completes.

**With observer preview** (`demo_seconds` > 0):

**Interview mode:** TTS is your only voice during demo sessions. Open the session with narration — introduce the challenge, state the current best time (if any), and describe what the first experiment will try. Be specific and energetic. Spoken lines appear as captions in the game, and the game shows a scoreboard and frames the farm during each run (see "What the Viewer Sees" in the invoke skill).

Before the first narration, check that speech works: if `./scripts/tts.py` fails (for example a missing Python environment), tell the user once and continue with captions only by calling `python3 -c "import sys; sys.path.insert(0,'scripts'); import api; api.overlay(caption='<line>', caption_seconds=8)"` in place of each TTS call.

```bash
./scripts/tts.py "<opening: challenge intro, current best or 'no prior work', first plan>" --voice am_adam
```

## Outer loop

Repeat until **stop** (see below):

### Sequencing (do not batch)

- **One iteration at a time:** for each numbered iteration, finish the full **iterate** workflow (new script → run(s) → results JSON → notes → tracker → cleanup) **before** creating the next `iteration_<n+1>_*.py` or starting another `run_algorithm` / benchmark for the *next* id.
- **Why:** the evaluate step (step 2) is supposed to use outcomes and notes to choose the *next* experiment. Writing or running several iterations in one shot skips that feedback and often wastes work (e.g. wrong branch after an unexpected result).
- **Exception:** only batch multiple iterations in a single turn if the user **explicitly** asks for a fixed, predetermined set (e.g. “run these three thresholds as a grid, no evaluation between”) — and even then, prefer reporting after each run so partial results are visible.

1. **Re-read** `.claude/skills/iterate/SKILL.md` **in full** before each iteration — do not rely on memory from prior passes, as context compression can cause steps to be silently skipped. Then execute it end-to-end, passing the same leaderboard, the session **`demo_seconds`** value (`0` or `N`), and any short directive for this iteration (e.g. “try column snake after notes suggest it”) so Steps 0–6 of iterate run exactly **one** numbered iteration.

2. After iterate finishes (tracker updated, cleanup done), **evaluate**:
   - Did mean time (or the user’s metric) set a **new best** in `iteration_tracker.json`?
   - Is progress **meaningful**? Treat a change under **1%** in mean time vs the previous best as flat unless the user cares about smaller gains.
   - **Diminishing returns**: if **3** consecutive iterations fail to improve the best mean by at least 1%, strongly consider stopping unless the user asked for more exploration.
   - **Hard cap**: do not exceed the session **max iterations** unless the user raises the limit.
   - **User stop**: if the user says to stop or change direction, stop or replan.
   - **Invalid result**: an iteration whose `GAME RESULT` shows `Completed: False` did not reach the win condition. It cannot be the best iteration regardless of its times; the next pass should fix that first.

   **With observer preview** (`demo_seconds` > 0): Announce the outcome and your next direction out loud — new best, flat result, or stopping, and why.
   ```bash
   ./scripts/tts.py "<outcome: new best / flat / stopping, and what direction next>" --voice am_adam
   ```

3. If not stopping, choose the **next focus** from the latest notes’ “Next Steps” and the tracker, **then** return to step 1 for the **next** iteration only (increment `n` once; do not pre-create later iteration files in the same turn).

## Stop: deliverables

**With observer preview** (`demo_seconds` > 0): Close the session — state the final best time, what made the winning strategy work, and one idea you'd try with more time.
```bash
./scripts/tts.py "<closing: final best time, why it worked, what you'd try next>" --voice am_adam
```

**Session recap page** (every session, with or without the observer preview): build the recap and publish it so the user has a link to keep or share.

```bash
python3 scripts/session_recap.py --since <id of the first iteration run this session>
```

This writes `iterations/recap_<leaderboard>.html`: the best time, a chart of every iteration with this session's highlighted, one line per iteration on what was tried, and the open ideas from the latest notes. It reads the tracker, each `iteration_<n>_results.json` (`strategy_summary`) and the latest notes (`## Next Steps`), so those need to be complete first.

Publish that file with the Artifact tool. If `iteration_tracker.json` has a `recap_url` for this leaderboard, pass it as `url` so the same link is updated (read the artifact first, as the tool requires); otherwise publish a new one and save its link as `recap_url` in the tracker. If no Artifact tool is available, give the user the file path instead. Note the first iteration id of the session at startup so `--since` is right.

When you exit the loop, summarize for the user:

- **Best script**: path and iteration id from the tracker (`best_iteration`, `best_mean_time`).
- **What changed** across the session (brief bullet list).
- **Open ideas** left in the last notes, if any.
- **Recap link** (or file path).

## Relationship to other skills

| Skill | Role |
| --- | --- |
| **leaderboard** (this file) | Prompt → plan → loop → stop; no direct API recipes. |
| **iterate** | Single iteration: diagnostics → script → simulate → results → notes → Discord → tracker → cleanup. |
| **invoke** | REST API at `localhost:8787`: preflight, helper scripts, `leaderboard_run` / `simulate`, reading results, save loading, `/delete-script`. |

There is no programmatic “sub-skill call.” **Calling iterate** means reading `.claude/skills/iterate/SKILL.md` and performing that workflow end-to-end once.
