#!/usr/bin/env python3
"""
Upload a script to the game, run it in execute mode, and print its output.

Usage: python3 scripts/submit.py <script_file> <filename_in_game> [timeout_seconds] [--keep]

When the script has finished, its file and code window are removed from the game again, so
diagnostic windows do not pile up over the farm. Pass --keep to leave it in the game (needed
if another script will import it or run it through simulate()/leaderboard_run()).

For leaderboard benchmarks use scripts/run_algorithm.py instead.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(__file__))
import api

keep = "--keep" in sys.argv
argv = [a for a in sys.argv[1:] if a != "--keep"]
if len(argv) < 2:
    print("Usage: submit.py <script_file> <game_filename> [timeout_seconds] [--keep]")
    sys.exit(1)

script_file = argv[0]
game_filename = argv[1]
timeout = int(argv[2]) if len(argv) > 2 else 600

status = api.wait_until_ready_for_run()
print(f"Game status: {status}")

# Tell whoever is watching the game what is running.
api.overlay(scoreboard={"title": "Diagnostic", "subtitle": game_filename, "status": "running on the save's farm",
                        "best_seconds": -1, "best_label": "", "history": []})
result = api.submit_file(script_file, game_filename)
if result.get("error"):
    where = f" (line {result['line']})" if result.get("line") else ""
    print(f"ERROR{where}: {result['error']}")
    sys.exit(1)
print(f"Submitted: {result}")

print("Polling...")
final = api.poll_until_done(interval=2, timeout=timeout)
timed_out = final is None
if timed_out:
    print(f"Timed out after {timeout}s; the script is still running (python3 scripts/stop.py to stop it)")
    final = api.poll()

print("=== OUTPUT ===")
print(final.get("output", ""))
print("=== END ===")
api.overlay(scoreboard={"status": "finished" if final.get("state") == "finished" else str(final.get("state"))})
# Take the window off the farm again, unless it is still running or was asked to stay.
if not keep and not timed_out:
    api.delete_script(game_filename)
    print(f"Removed '{game_filename}' from the game (use --keep to leave it).")
if final.get("state") == "error":
    print(f"RUNTIME ERROR:\n{final.get('error')}")
    sys.exit(1)
