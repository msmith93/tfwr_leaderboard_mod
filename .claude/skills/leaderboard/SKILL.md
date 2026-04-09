---
name: leaderboard
description: End-to-end TFWR leaderboard script development from a user prompt until stop criteria are met. Orchestrates multiple single passes using the iterate skill; use invoke for REST API details.
argument-hint: "<leaderboard name, e.g. Hay_Single> [optional goals or constraints]"
---

# Leaderboard: Full Script Development Flow

Target and context: $ARGUMENTS

Drive the project from the user's prompt toward the **best script you can produce within the iteration limit** (and other stop rules below), by running **one full pass of the iterate skill at a time** and deciding after each pass whether to continue.

This skill owns **prioritization**, **stopping rules**, and the **outer loop**. It does **not** replace the iterate or invoke skills: load and follow `.claude/skills/iterate/SKILL.md` for each iteration, and `.claude/skills/invoke/SKILL.md` for every HTTP/API step (curl, poll, `leaderboard_run`, cleanup).

## Parse the prompt

From `$ARGUMENTS` and the conversation, extract:

- **Leaderboard**: enum name as in `documentation/docs/unlocks/leaderboard.md` (e.g. `Hay_Single`, `Dinosaur`).
- **Goal**: e.g. lower mean time, reach win rate 1.0, explore a named strategy, match a constraint from the user.
- **Limits** (defaults if unstated):
  - **Max iterations** — primary control: how many complete **iterate** passes to allow this session (suggest default **8**; stop sooner if criteria below fire). This is concrete and enforceable.
  - **Wall-clock deadlines** — the model does **not** automatically know elapsed time; only honor these if the user asks and you explicitly check the clock (e.g. via a tool). Simulation length is unpredictable, so time limits are **soft** and easy to overshoot. Prefer **max iterations** for a reliable cap.
  - Optional user hints such as “stop after N extra `leaderboard_run` batches” inside one iteration are handled inside **iterate**, not as a substitute for the iteration cap.

## Startup: high-level state

Before the first iteration:

1. Read `iterations/iteration_tracker.json` for this leaderboard (or confirm none).
2. Skim the latest `iterations/iteration_<n>_notes.md` for that line of work, if any.
3. Confirm win condition and starting inventory in `documentation/docs/unlocks/leaderboard.md`.

The **iterate** skill repeats its own “Before Starting” reads each time you run it; you do not need to duplicate every file on every loop, but re-check the tracker after each iteration completes.

## Outer loop

Repeat until **stop** (see below):

1. **Read and execute** `.claude/skills/iterate/SKILL.md` **once**, passing the same leaderboard (and any short directive for this iteration, e.g. “try column snake after notes suggest it”) in the invocation context so Steps 0–6 of iterate run exactly **one** numbered iteration.

2. After iterate finishes (tracker updated, cleanup done), **evaluate**:
   - Did mean time (or the user’s metric) set a **new best** in `iteration_tracker.json`?
   - Is progress **meaningful**? Treat a change under **1%** in mean time vs the previous best as flat unless the user cares about smaller gains.
   - **Diminishing returns**: if **3** consecutive iterations fail to improve the best mean by at least 1%, strongly consider stopping unless the user asked for more exploration.
   - **Hard cap**: do not exceed the session **max iterations** unless the user raises the limit.
   - **User stop**: if the user says to stop or change direction, stop or replan.

3. If not stopping, choose the **next focus** from the latest notes’ “Next Steps” and the tracker, then go to step 1.

## Stop: deliverables

When you exit the loop, summarize for the user:

- **Best script**: path and iteration id from the tracker (`best_iteration`, `best_mean_time`).
- **What changed** across the session (brief bullet list).
- **Open ideas** left in the last notes, if any.

## Relationship to other skills

| Skill | Role |
| --- | --- |
| **leaderboard** (this file) | Prompt → plan → loop → stop; no direct API recipes. |
| **iterate** | Single iteration: diagnostics → script → simulate → results → notes → Discord → tracker → cleanup. |
| **invoke** | REST API at `localhost:8787`, two-file upload, `leaderboard_run` / `simulate`, parsing output, `/delete-script`. |

There is no programmatic “sub-skill call.” **Calling iterate** means reading `.claude/skills/iterate/SKILL.md` and performing that workflow end-to-end once.
