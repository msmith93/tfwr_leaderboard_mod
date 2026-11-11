#!/usr/bin/env python3
"""Poll the game API once and print the current run's state, results and recent output."""
import sys, os
sys.path.insert(0, os.path.dirname(__file__))
import api

data = api.poll()
if data.get("http_status"):
    print(f"ERROR: {data.get('error')}")
    sys.exit(1)

out = data.get("output", "")
lines = out.strip().split("\n") if out.strip() else []
times = api.final_times(out)

print(f"state: {data.get('state')}")
print(f"finished: {data.get('finished')}")
if data.get("error"):
    print(f"runtime error:\n{data.get('error')}")
if data.get("leaderboard"):
    lb = data["leaderboard"]
    print(f"leaderboard result: {lb.get('name')} completed={lb.get('completed')} "
          f"average={lb.get('average_time_seconds')}s runs={lb.get('runs')}")
if data.get("sim_time_seconds") is not None:
    print(f"sim_time_seconds: {data.get('sim_time_seconds')}")
print(f"runs recorded by mod: {len(data.get('run_times') or [])}")
print(f"Total output lines: {len(lines)}")
print(f"FINAL_TIME count: {len(times)}")
if times:
    print("FINAL_TIME values (last 5):", times[-5:])
print("Last 10 output lines:")
for l in lines[-10:]:
    print(f"  {l}")
