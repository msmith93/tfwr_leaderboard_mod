"""
Shared TFWR REST API helpers. Import from other scripts.
"""
import json
import time
import urllib.request

BASE = "http://localhost:8787"


def post(path, data=None):
    body = (json.dumps(data) if data else "").encode()
    headers = {"Content-Type": "application/json", "Content-Length": str(len(body))}
    req = urllib.request.Request(BASE + path, data=body, headers=headers)
    with urllib.request.urlopen(req) as r:
        return json.loads(r.read())


def get(path):
    with urllib.request.urlopen(BASE + path) as r:
        return json.loads(r.read())


def status():
    return get("/status")


def stop():
    return post("/stop")


def delete_script(filename):
    return post("/delete-script", {"filename": filename})


def submit(code, filename, mode="execute"):
    return post("/run-script", {"code": code, "filename": filename, "mode": mode})


def submit_file(path, filename, mode="execute"):
    with open(path) as f:
        code = f.read()
    return submit(code, filename, mode)


def poll():
    return get("/poll")


def poll_until_done(interval=5, timeout=600, verbose=True):
    """Poll every `interval` seconds until finished or timeout. Returns final poll data."""
    elapsed = 0
    while elapsed < timeout:
        time.sleep(interval)
        elapsed += interval
        data = poll()
        if verbose:
            out = data.get("output", "")
            ft = sum(1 for l in out.split("\n") if "FINAL_TIME" in l)
            print(f"  t={elapsed}s finished={data.get('finished')} FINAL_TIME_count={ft}", flush=True)
        if data.get("finished"):
            return data
    return None
