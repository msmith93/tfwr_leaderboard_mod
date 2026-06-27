#!/usr/bin/env python3
"""
Upload a script and run it inside simulate() with configurable starting conditions.
Used for diagnostics that need leaderboard-equivalent fixed conditions.

Usage:
  python3 scripts/run_sim_script.py <script_path> <game_filename> \
      [--items "Cactus:1000000000,Power:1000000000"] [--seed 1] [--speedup 5000] [--timeout 600]
"""
import argparse
import os
import sys
import time

sys.path.insert(0, os.path.dirname(__file__))
import api


def main():
    p = argparse.ArgumentParser()
    p.add_argument("script_path")
    p.add_argument("game_filename")
    p.add_argument("--items", default="Cactus:1000000000,Power:1000000000")
    p.add_argument("--seed", type=int, default=1)
    p.add_argument("--speedup", type=int, default=5000)
    p.add_argument("--timeout", type=int, default=600)
    args = p.parse_args()

    api.wait_until_ready_for_run()
    print(f"Uploading {args.script_path} as '{args.game_filename}'...")
    r = api.submit_file(args.script_path, args.game_filename)
    print(f"  {r}")
    if r.get("error"):
        sys.exit(1)
    # The upload sanity-executes on the real farm; stop it quickly.
    time.sleep(2)
    api.stop()
    time.sleep(1)

    items = ""
    for pair in args.items.split(","):
        if pair:
            k, v = pair.split(":")
            items += f"Items.{k}: {v}, "
    runner = (
        f't = simulate("{args.game_filename}", Unlocks, {{{items}}}, {{}}, {args.seed}, {args.speedup})\n'
        'quick_print("SIMTIME:", t)\n'
        'quick_print("---DONE---")'
    )
    print("Runner:\n" + runner)
    r = api.submit(runner, "runner")
    print(f"  {r}")
    if r.get("error"):
        sys.exit(1)
    data = api.poll_until_done(interval=3, timeout=args.timeout, verbose=True)
    if not data:
        print("TIMEOUT; stopping...")
        api.stop()
        time.sleep(1)
        data = api.poll()
    print("=== OUTPUT ===")
    print(data.get("output", ""))
    print("=== END ===")


if __name__ == "__main__":
    main()
