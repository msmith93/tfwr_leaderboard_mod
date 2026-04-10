"""
Shared TFWR REST API helpers. Import from other scripts.
"""
import json
import time
import urllib.error
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


def graphics_ready(data):
    """True if the mod reports a usable GPU. Missing key = legacy mod (treat as ready)."""
    if "graphics_ready" not in data:
        return True
    return bool(data.get("graphics_ready"))


def wait_until_ready_for_run(timeout=180, interval=0.5, verbose=True):
    """
    Block until GET /status shows ready MainSim and graphics_ready (GPU initialized).
    Call this before any POST /run-script to avoid Proton crashes from opening the code editor
    while GraphicsDeviceType is Null.
    """
    elapsed = 0.0
    if verbose:
        print("  Polling GET /status until ready and graphics_ready...", flush=True)
    while elapsed < timeout:
        try:
            st = status()
        except (urllib.error.URLError, OSError) as e:
            if verbose:
                print(f"  wait_until_ready: status failed ({e}), retrying...", flush=True)
            time.sleep(interval)
            elapsed += interval
            continue
        rdy = st.get("ready")
        gfx = graphics_ready(st)
        if verbose and (elapsed == 0 or (elapsed > 0 and int(elapsed) % 5 == 0)):
            if not (rdy and gfx):
                print(f"  t={elapsed:.0f}s ready={rdy} graphics_ready={gfx} (waiting...)", flush=True)
        if rdy and gfx:
            if verbose:
                print(f"  Game ready (graphics_ready={gfx})", flush=True)
            return st
        time.sleep(interval)
        elapsed += interval
    raise TimeoutError(
        f"Timed out after {timeout}s waiting for ready=true and graphics_ready=true; "
        "ensure the game is in focus / GPU initialized, then retry."
    )


def stop():
    return post("/stop")


def delete_script(filename):
    return post("/delete-script", {"filename": filename})


def submit(code, filename, mode="execute", open_editor=True):
    payload = {"code": code, "filename": filename, "mode": mode, "open_editor": open_editor}
    return post("/run-script", payload)


def submit_file(path, filename, mode="execute", open_editor=True):
    with open(path) as f:
        code = f.read()
    return submit(code, filename, mode, open_editor)


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
