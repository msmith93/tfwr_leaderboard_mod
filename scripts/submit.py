#!/usr/bin/env python3
"""
Upload a script to the game, run it in execute mode, and print its output.

Usage: python3 scripts/submit.py <script_file> <filename_in_game> [timeout_seconds]

For leaderboard benchmarks use scripts/run_algorithm.py instead.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(__file__))
import api

if len(sys.argv) < 3:
    print("Usage: submit.py <script_file> <game_filename> [timeout_seconds]")
    sys.exit(1)

script_file = sys.argv[1]
game_filename = sys.argv[2]
timeout = int(sys.argv[3]) if len(sys.argv) > 3 else 600

status = api.wait_until_ready_for_run()
print(f"Game status: {status}")

result = api.submit_file(script_file, game_filename)
if result.get("error"):
    where = f" (line {result['line']})" if result.get("line") else ""
    print(f"ERROR{where}: {result['error']}")
    sys.exit(1)
print(f"Submitted: {result}")

print("Polling...")
final = api.poll_until_done(interval=2, timeout=timeout)
if final is None:
    print(f"Timed out after {timeout}s; the script is still running (python3 scripts/stop.py to stop it)")
    final = api.poll()

print("=== OUTPUT ===")
print(final.get("output", ""))
print("=== END ===")
if final.get("state") == "error":
    print(f"RUNTIME ERROR:\n{final.get('error')}")
    sys.exit(1)
