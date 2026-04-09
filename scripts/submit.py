#!/usr/bin/env python3
# Helper script for submitting scripts to TFWR REST API
# Usage: python3 scripts/submit.py <script_file> <filename_in_game> [--runner]
#
# --runner: submit as a runner calling leaderboard_run
# Default: submit the file directly as execute mode

import sys
import json
import urllib.request
import urllib.error
import time

BASE = "http://localhost:8787"

def post(path, data):
    body = json.dumps(data).encode()
    req = urllib.request.Request(BASE + path, data=body,
                                  headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req) as r:
        return json.loads(r.read())

def get(path):
    with urllib.request.urlopen(BASE + path) as r:
        return json.loads(r.read())

def poll_until_done(interval=5, max_wait=600):
    for _ in range(max_wait // interval):
        time.sleep(interval)
        data = get("/poll")
        out = data.get("output", "")
        ft = [l for l in out.split("\n") if "FINAL_TIME" in l]
        print(f"  finished={data.get('finished')} FINAL_TIME_count={len(ft)}", flush=True)
        if data.get("finished"):
            return data
    return None

if len(sys.argv) < 3:
    print("Usage: submit.py <script_file> <game_filename> [--runner <leaderboard>]")
    sys.exit(1)

script_file = sys.argv[1]
game_filename = sys.argv[2]
is_runner = "--runner" in sys.argv

with open(script_file) as f:
    code = f.read()

# Check status
status = get("/status")
print(f"Game status: {status}")
if not status.get("ready"):
    print("Game not ready!")
    sys.exit(1)

# Submit
result = post("/run-script", {"code": code, "filename": game_filename, "mode": "execute"})
print(f"Submitted: {result}")

# Poll
print("Polling...")
final = poll_until_done()
if final:
    out = final.get("output", "")
    print("=== OUTPUT ===")
    print(out)
    print("=== END ===")
else:
    print("Timed out waiting for result")
