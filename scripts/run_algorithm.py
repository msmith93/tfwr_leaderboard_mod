#!/usr/bin/env python3
"""
Upload an algorithm script and run it via leaderboard_run, then print results.

Usage:
  python3 scripts/run_algorithm.py <script_path> <game_filename> <leaderboard_enum> [speedup] [--demo-seconds N]

Examples:
  python3 scripts/run_algorithm.py iterations/iteration_1_boustrophedon.py iteration_1_boustrophedon Dinosaur 5000
  python3 scripts/run_algorithm.py iterations/iteration_1_boustrophedon.py iteration_1_boustrophedon Dinosaur 5000 --demo-seconds 15
"""

import argparse
import os
import sys
import time

sys.path.insert(0, os.path.dirname(__file__))
import api


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
    args = parser.parse_args()

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

    # Step 1: Upload algorithm script (sanity check)
    print(f"\nStep 1: Uploading {script_path} as '{game_filename}'...")
    result = api.submit_file(script_path, game_filename)
    if result.get("error"):
        print(f"  ERROR: {result.get('error')}")
        sys.exit(1)
    print(f"  Result: {result}")

    # Poll briefly for syntax errors (up to 15 seconds)
    data = api.poll_until_done(interval=5, timeout=15, verbose=True)
    if data:
        out = data.get("output", "")
        print(f"  Sanity check output:\n{out[:500]}")
        if "error" in out.lower() or "traceback" in out.lower():
            print("ERROR: Script has errors. Fix before proceeding.")
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
        print(f"  Letting demo run for ~{demo_seconds}s wall clock...")
        time.sleep(demo_seconds)
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

    # Poll until done (leaderboard runs can take many minutes)
    print("Polling (this may take several minutes)...")
    data = api.poll_until_done(interval=10, timeout=1800, verbose=True)

    if not data:
        print("ERROR: Timed out waiting for leaderboard_run to finish")
        sys.exit(1)

    out = data.get("output", "")
    print("\n=== FULL OUTPUT (benchmark only; ignore any demo lines above in logs) ===")
    print(out)
    print("=== END OUTPUT ===")

    # Parse FINAL_TIME lines
    times = []
    for line in out.split("\n"):
        if "FINAL_TIME:" in line:
            try:
                val = float(line.split("FINAL_TIME:")[-1].strip())
                times.append(val)
            except ValueError:
                pass

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


if __name__ == "__main__":
    main()
