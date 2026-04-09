#!/usr/bin/env python3
"""
Upload an algorithm script and run it via leaderboard_run, then print results.

Usage:
  python3 scripts/run_algorithm.py <script_path> <game_filename> <leaderboard_enum> [speedup]

Example:
  python3 scripts/run_algorithm.py iterations/iteration_1_boustrophedon.py iteration_1_boustrophedon Dinosaur 5000
"""

import sys
import os
sys.path.insert(0, os.path.dirname(__file__))
import api

if len(sys.argv) < 4:
    print("Usage: run_algorithm.py <script_path> <game_filename> <leaderboard_enum> [speedup]")
    sys.exit(1)

script_path = sys.argv[1]
game_filename = sys.argv[2]
leaderboard = sys.argv[3]
speedup = int(sys.argv[4]) if len(sys.argv) > 4 else 5000

# Check game readiness
status = api.status()
print(f"Game status: {status}")
if not status.get("ready"):
    print("ERROR: Game not ready")
    sys.exit(1)

# Step 1: Upload algorithm script (sanity check)
print(f"\nStep 1: Uploading {script_path} as '{game_filename}'...")
result = api.submit_file(script_path, game_filename)
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
import time
time.sleep(1)

# Step 2: Submit runner
runner_code = f'leaderboard_run(Leaderboards.{leaderboard}, "{game_filename}", {speedup})\nquick_print("---DONE---")'
print(f"\nStep 2: Submitting runner for Leaderboards.{leaderboard} at speedup={speedup}...")
result = api.submit(runner_code, "runner")
print(f"  Result: {result}")

# Poll until done (leaderboard runs can take many minutes)
print("Polling (this may take several minutes)...")
data = api.poll_until_done(interval=10, timeout=1800, verbose=True)

if not data:
    print("ERROR: Timed out waiting for leaderboard_run to finish")
    sys.exit(1)

out = data.get("output", "")
print("\n=== FULL OUTPUT ===")
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
