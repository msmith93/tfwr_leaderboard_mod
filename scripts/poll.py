#!/usr/bin/env python3
"""Poll the game API once and print current status and output."""
import sys, os
sys.path.insert(0, os.path.dirname(__file__))
import api

data = api.poll()
out = data.get("output", "")
lines = out.strip().split("\n") if out.strip() else []
ft_lines = [l for l in lines if "FINAL_TIME" in l]

print(f"finished: {data.get('finished')}")
print(f"Total output lines: {len(lines)}")
print(f"FINAL_TIME count: {len(ft_lines)}")
if ft_lines:
    print("FINAL_TIME lines:", ft_lines[-5:])
print("Last 10 output lines:")
for l in lines[-10:]:
    print(f"  {l}")
