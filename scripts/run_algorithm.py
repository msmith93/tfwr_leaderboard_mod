#!/usr/bin/env python3
"""
Upload an algorithm script and run it via leaderboard_run, then print results.

Usage:
  python3 scripts/run_algorithm.py <script_path> <game_filename> <leaderboard_enum> [speedup] [--demo-seconds N] [--save NAME]

Exits non-zero if the script fails to parse, hits a runtime error, or the benchmark times out.

While it runs, the game shows a scoreboard (leaderboard, iteration, live average, best so far and
past iterations from iterations/iteration_tracker.json) and the camera frames the farm.
--no-overlay turns both off; --narrate adds one spoken progress line halfway through the benchmark.

Examples:
  python3 scripts/run_algorithm.py iterations/iteration_1_boustrophedon.py iteration_1_boustrophedon Dinosaur 5000
  python3 scripts/run_algorithm.py iterations/iteration_1_boustrophedon.py iteration_1_boustrophedon Dinosaur 5000 --demo-seconds 15
"""

import argparse
import json
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(__file__))
import api


REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def scoreboard_from_tracker(leaderboard, game_filename):
    """Scoreboard contents for this run: title, what is running, and past results if the tracker covers this leaderboard."""
    board = {"title": leaderboard.replace("_", " "), "subtitle": game_filename,
             "best_seconds": -1, "best_label": "", "history": []}
    m = re.match(r"iteration_(\d+)_(.+)", game_filename)
    if m:
        board["subtitle"] = f"iteration {m.group(1)}: {m.group(2)}"
    try:
        with open(os.path.join(REPO, "iterations", "iteration_tracker.json")) as f:
            tracker = json.load(f)
        if tracker.get("leaderboard", "").lower() == leaderboard.lower():
            board["history"] = [it["mean_time_seconds"] for it in tracker.get("iterations", [])
                                if isinstance(it.get("mean_time_seconds"), (int, float))]
            if tracker.get("best_mean_time"):
                board["best_seconds"] = tracker["best_mean_time"]
                board["best_label"] = f"iteration {tracker.get('best_iteration')}"
    except (OSError, ValueError, KeyError):
        pass
    return board


def speak(text):
    """Say a line through scripts/tts.py (which also captions it) without blocking the benchmark."""
    try:
        subprocess.Popen([os.path.join(REPO, "scripts", "tts.py"), text],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    except OSError as e:
        print(f"  (narration unavailable: {e})")


def main():
    parser = argparse.ArgumentParser(
        description="Upload algorithm script and run leaderboard_run (optional 1x demo, then benchmark)."
    )
    parser.add_argument("script_path", help="Path to the .py file to upload")
    parser.add_argument("game_filename", help="Filename in the game workspace (no path)")
    parser.add_argument("leaderboard", help="Leaderboard enum name, e.g. Dinosaur, Hay_Single")
    parser.add_argument(
        "speedup",
        nargs="?",
        type=int,
        default=5000,
        help="Simulation speedup for benchmark (default 5000)",
    )
    parser.add_argument(
        "--demo-seconds",
        type=int,
        default=0,
        metavar="N",
        help="Wall-clock seconds: run leaderboard_run at 1x then stop, before benchmark (0=skip)",
    )
    parser.add_argument(
        "--save",
        default=None,
        metavar="NAME",
        help="Load this game save first (default: keep the active save)",
    )
    parser.add_argument(
        "--no-overlay",
        action="store_true",
        help="Do not show the scoreboard or move the camera",
    )
    parser.add_argument(
        "--narrate",
        action="store_true",
        help="Speak a short progress line halfway through the benchmark",
    )
    args = parser.parse_args()
    show = not args.no_overlay

    script_path = args.script_path
    game_filename = args.game_filename
    leaderboard = args.leaderboard
    speedup = args.speedup
    demo_seconds = args.demo_seconds

    if demo_seconds < 0:
        print("ERROR: --demo-seconds must be >= 0")
        sys.exit(1)

    # Wait until MainSim is up and GPU is initialized before opening the code editor (Proton-safe)
    print("Waiting for game + graphics readiness...")
    status = api.wait_until_ready_for_run(timeout=180, interval=0.5, verbose=True)
    print(f"Game status: {status}")
    if not status.get("ready"):
        print("ERROR: Game not ready after wait")
        sys.exit(1)

    for warning in status.get("compat_warnings") or []:
        print(f"WARNING: mod/game mismatch: {warning}")

    # Make sure the intended save is loaded and the title menu is not covering the farm.
    if args.save or status.get("menu_open"):
        loaded = api.load_save(args.save)
        if loaded.get("error"):
            print(f"ERROR: {loaded['error']}")
            sys.exit(1)
        status = loaded
    print(f"Active save: {status.get('active_save')}")

    board = scoreboard_from_tracker(leaderboard, game_filename)
    best = board["best_seconds"] if board["best_seconds"] > 0 else None

    def stage(status_text):
        if show:
            api.overlay(scoreboard={"status": status_text})

    def frame_farm():
        # The simulated farm can be a different size from the save's farm, so frame it
        # once the run has started.
        if show:
            time.sleep(1.0)
            api.camera_fit()

    if show:
        api.overlay(scoreboard=dict(board, status="checking the script"))
        api.camera_fit()

    # Step 1: Upload algorithm script (sanity check)
    print(f"\nStep 1: Uploading {script_path} as '{game_filename}'...")
    result = api.submit_file(script_path, game_filename)
    if result.get("error"):
        # Parse errors are rejected here with the parser's message and line number.
        where = f" (line {result['line']})" if result.get("line") else ""
        print(f"  ERROR{where}: {result.get('error')}")
        stage("script failed to parse")
        sys.exit(1)
    print(f"  Result: {result}")

    # Poll briefly for runtime errors (up to 15 seconds). The mod reports them as state "error".
    data = api.poll_until_done(interval=5, timeout=15, verbose=True)
    if data is None:
        data = api.poll()
    out = data.get("output", "")
    print(f"  Sanity check output:\n{out[:500]}")
    if data.get("state") == "error" or data.get("error"):
        print(f"ERROR: Script failed at runtime:\n{data.get('error')}")
        print("Fix before proceeding.")
        api.stop()
        stage("script hit a runtime error")
        sys.exit(1)

    # Stop algorithm (it runs indefinitely in execute mode)
    print("\nStopping sanity check run...")
    api.stop()
    time.sleep(1)

    if demo_seconds > 0:
        print(f"\n=== DEMO 1x ({demo_seconds}s wall clock, then stop) ===")
        demo_runner = (
            f'leaderboard_run(Leaderboards.{leaderboard}, "{game_filename}", 1)\n'
            'quick_print("---DEMO_DONE---")'
        )
        print(
            f"Submitting demo runner for Leaderboards.{leaderboard} at speedup=1 "
            f"(observer preview; output not used for benchmark stats)..."
        )
        result = api.submit(demo_runner, "runner")
        if result.get("error"):
            print(f"  ERROR: {result.get('error')}")
            sys.exit(1)
        print(f"  Result: {result}")
        stage("preview at 1x")
        frame_farm()
        print(f"  Letting demo run for ~{demo_seconds}s wall clock...")
        time.sleep(max(0, demo_seconds - (1 if show else 0)))
        print("  Stopping demo run...")
        api.stop()
        time.sleep(1)

    # Benchmark: submit runner at requested speedup
    runner_code = (
        f'leaderboard_run(Leaderboards.{leaderboard}, "{game_filename}", {speedup})\n'
        'quick_print("---DONE---")'
    )
    print(f"\n=== BENCHMARK ===")
    print(f"Submitting runner for Leaderboards.{leaderboard} at speedup={speedup}...")
    result = api.submit(runner_code, "runner")
    if result.get("error"):
        print(f"  ERROR: {result.get('error')}")
        sys.exit(1)
    print(f"  Result: {result}")

    stage(f"benchmark at {speedup}x")
    frame_farm()

    # Halfway through the 2 hours of simulated time, say how it is going.
    narrated = []

    def halfway_line(d):
        if narrated or d.get("leaderboard_total_time_seconds", 0) < 3600:
            return
        runs = d.get("leaderboard_runs_so_far", 0)
        if runs <= 0:
            return
        narrated.append(True)
        avg = d["leaderboard_total_time_seconds"] / runs
        line = f"Halfway through the benchmark. {runs} runs so far, averaging {avg:.0f} seconds."
        if best:
            diff = avg - best
            if abs(diff) < 0.005 * best:
                line += f" That is level with the best so far, {best:.0f}."
            elif diff < 0:
                line += f" That is {-diff:.0f} seconds faster than the best so far."
            else:
                line += f" That is {diff:.0f} seconds slower than the best of {best:.0f}."
        print(f"  NARRATION: {line}", flush=True)
        speak(line)

    # Poll until done (leaderboard runs can take many minutes)
    print("Polling (this may take several minutes)...")
    data = api.poll_until_done(interval=5 if args.narrate else 10, timeout=1800, verbose=True,
                               on_poll=halfway_line if args.narrate else None)

    if not data:
        print("ERROR: Timed out waiting for leaderboard_run to finish")
        stage("benchmark timed out")
        sys.exit(1)

    out = data.get("output", "")
    print("\n=== FULL OUTPUT (benchmark only; ignore any demo lines above in logs) ===")
    print(out)
    print("=== END OUTPUT ===")

    if data.get("state") == "error":
        print(f"\nERROR: Benchmark failed at runtime:\n{data.get('error')}")
        api.stop()
        stage("benchmark hit a runtime error")
        sys.exit(1)

    # Per-repetition times printed by the algorithm script itself.
    times = api.final_times(out)

    # The mod also records each repetition's sim time and the game's own final result,
    # independent of what the script prints.
    mod_times = data.get("run_times") or []
    result = data.get("leaderboard")
    if result:
        print("\n=== GAME RESULT ===")
        print(f"Leaderboard: {result.get('name')}")
        print(f"Completed: {result.get('completed')}")
        print(f"Runs: {result.get('runs')}")
        print(f"Average: {result.get('average_time_seconds'):.3f}s")
        if not result.get("completed"):
            print("WARNING: the game marked this run as NOT completed (goal not reached or run cancelled).")

    if not times and mod_times:
        print("\nNo FINAL_TIME lines in output; using per-run times recorded by the mod.")
        times = mod_times
    elif times and mod_times and len(times) != len(mod_times):
        print(f"\nNOTE: {len(times)} FINAL_TIME lines but the mod recorded {len(mod_times)} runs.")

    if times:
        total = sum(times)
        mean = total / len(times)
        best = min(times)
        worst = max(times)
        print(f"\n=== PARSED RESULTS ===")
        print(f"Runs: {len(times)}")
        print(f"Mean: {mean:.3f}s")
        print(f"Best: {best:.3f}s")
        print(f"Worst: {worst:.3f}s")
    else:
        print("\nNo FINAL_TIME lines found in output.")

    if show:
        completed = (result or {}).get("completed")
        if completed is False:
            stage("finished: goal NOT reached")
        elif times and best and (sum(times) / len(times)) < best:
            stage("finished: new best")
        else:
            stage("finished")
        # Back on the save's own farm, which may be a different size: frame it again.
        time.sleep(1.0)
        api.camera_fit()


if __name__ == "__main__":
    main()
