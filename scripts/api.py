"""
Shared TFWR REST API helpers. Import from other scripts.
"""
import json
import time
import urllib.error
import urllib.request

BASE = "http://localhost:8787"


def _request(req):
    """
    Send a request and return the decoded JSON body.

    The mod answers failures with a 4xx/5xx status and a JSON body like {"error": "..."}.
    Those are returned as a dict (with "http_status" added) instead of raised, so callers
    can keep checking result.get("error"). Connection failures still raise.
    """
    try:
        with urllib.request.urlopen(req) as r:
            return json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        raw = e.read().decode("utf-8", errors="replace")
        try:
            data = json.loads(raw)
        except ValueError:
            data = {"error": raw or str(e)}
        if not isinstance(data, dict):
            data = {"error": str(data)}
        data.setdefault("error", f"HTTP {e.code}")
        data["http_status"] = e.code
        return data


def post(path, data=None):
    body = (json.dumps(data) if data else "").encode()
    headers = {"Content-Type": "application/json", "Content-Length": str(len(body))}
    return _request(urllib.request.Request(BASE + path, data=body, headers=headers))


def get(path):
    return _request(urllib.request.Request(BASE + path))


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


def overlay(caption=None, caption_seconds=0, scoreboard=None, clear=False):
    """
    Update the on-screen overlay. Only the arguments given are changed.
      caption: text for the caption strip ("" removes it); caption_seconds > 0 auto-hides it
      scoreboard: dict with any of title, subtitle, status, best_seconds, best_label, history
                  (False removes the panel)
      clear: remove everything first
    Never raises: the overlay is decoration and must not break a run.
    """
    payload = {}
    if clear:
        payload["clear"] = True
    if caption is not None:
        payload["caption"] = caption
        payload["caption_seconds"] = caption_seconds
    if scoreboard is False:
        payload["scoreboard"] = None
    elif scoreboard is not None:
        payload["scoreboard"] = scoreboard
    try:
        return post("/overlay", payload)
    except (urllib.error.URLError, OSError) as e:
        return {"error": str(e)}


def camera_fit(seconds=1.0, region=None):
    """Glide the view so the whole farm is framed (leaving room for the overlay). Never raises."""
    payload = {"fit": True, "seconds": seconds}
    if region:
        payload["region"] = region
    try:
        return post("/camera", payload)
    except (urllib.error.URLError, OSError) as e:
        return {"error": str(e)}


def camera_release():
    """Give the view back to the player (restores the game's normal scroll limits)."""
    try:
        return post("/camera", {"release": True})
    except (urllib.error.URLError, OSError) as e:
        return {"error": str(e)}


def leaderboard(name, time_seconds=None, top=10):
    """
    Read-only look at the Steam leaderboard `name` (e.g. "Carrots_Single"): entry_count, top
    times, times at fixed percentiles, the player's own entry, and, with `time_seconds`, where
    that time would rank. Returns a dict with "error" if it cannot be read. Never raises.
    """
    path = f"/leaderboard?name={name}&top={int(top)}"
    if isinstance(time_seconds, (list, tuple)):
        if time_seconds:
            # Several times at once; results come back in "time_queries", in the same order.
            path += "&times=" + ",".join(f"{float(t):.3f}" for t in time_seconds)
    elif time_seconds:
        path += f"&time={float(time_seconds):.3f}"
    try:
        return get(path)
    except (urllib.error.URLError, OSError) as e:
        return {"error": str(e)}


def ordinal(n):
    """1 -> '1st', 2 -> '2nd', 11 -> '11th', 85 -> '85th'."""
    n = int(n)
    if 10 <= n % 100 <= 20:
        suffix = "th"
    else:
        suffix = {1: "st", 2: "nd", 3: "rd"}.get(n % 10, "th")
    return f"{n:,}{suffix}"


def placed_text(rank, entry_count):
    """Scoreboard wording, e.g. 'placed 85th of 330'."""
    if not rank or not entry_count:
        return ""
    return f"placed {ordinal(rank)} of {entry_count:,}"


def standing_text(rank, entry_count):
    """One line for the scoreboard, e.g. 'world #1,204 of 38,112 (top 3.2%)'."""
    if not rank or not entry_count:
        return ""
    pct = min(100.0, rank * 100.0 / entry_count)
    shown = f"{pct:.1f}" if pct >= 0.1 else "0.1"
    return f"world #{rank:,} of {entry_count:,} (top {shown}%)"


def saves():
    """{"active_save": ..., "saves": [...]}"""
    return get("/saves")


def load_save(name=None, timeout=60, discard_unsaved=False, verbose=True):
    """
    Load a save (or, with name=None, just close the menu on the active one) and wait until
    it is active with the menu closed. Returns the final /status, or a dict with "error".
    """
    payload = {"discard_unsaved": discard_unsaved}
    if name:
        payload["save"] = name
    result = post("/load-save", payload)
    if result.get("error"):
        return result
    target = result.get("save")
    elapsed = 0.0
    while elapsed < timeout:
        time.sleep(0.5)
        elapsed += 0.5
        try:
            st = status()
        except (urllib.error.URLError, OSError):
            continue
        if st.get("ready") and st.get("active_save") == target and not st.get("menu_open"):
            if verbose:
                print(f"  Save '{target}' active, menu closed", flush=True)
            return st
    return {"error": f"Timed out after {timeout}s waiting for save '{target}' to load"}


def delete_script(filename):
    return post("/delete-script", {"filename": filename})


def submit(code, filename, mode="execute", open_editor=True, **options):
    """
    POST /run-script. Extra keyword arguments are passed through for the simulate and
    leaderboard modes: speedup, seed, items, unlocks, globals, leaderboard.
    A rejected request returns {"error": ..., "http_status": ...}; a parse error also has "line".
    """
    payload = {"code": code, "filename": filename, "mode": mode, "open_editor": open_editor}
    payload.update(options)
    return post("/run-script", payload)


def submit_file(path, filename, mode="execute", open_editor=True, **options):
    with open(path, encoding="utf-8") as f:
        code = f.read()
    return submit(code, filename, mode, open_editor, **options)


def poll(since=None):
    """
    GET /poll. Without `since` the full output of the current run is returned.
    Pass the previous response's "next_offset" as `since` to get only new output.
    """
    if since is None:
        return get("/poll")
    return get(f"/poll?since={int(since)}")


def final_times(output):
    """All FINAL_TIME values in an output string, in order."""
    times = []
    for line in output.split("\n"):
        if "FINAL_TIME:" in line:
            try:
                times.append(float(line.split("FINAL_TIME:")[-1].strip()))
            except ValueError:
                pass
    return times


def poll_until_done(interval=5, timeout=600, verbose=True, on_poll=None, frame=True):
    """
    Poll every `interval` seconds until the run ends or `timeout` elapses.
    `on_poll(data)`, if given, is called with every successful poll (for live commentary).
    With `frame` (default) the camera is fitted to the farm shortly after the run starts and
    again when it ends, so whoever is watching can see it. The farm changes size when a
    simulation starts or stops, which is why it is fitted twice.

    Returns the final poll data, with "output" holding everything printed since the run
    started (fetched incrementally). Check data["state"]: "finished", "stopped" or "error"
    (data["error"] then has the game's runtime error). Returns None on timeout.
    """
    elapsed = 0
    offset = 0
    chunks = []
    if frame:
        time.sleep(min(1.0, interval))
        elapsed += min(1.0, interval)
        camera_fit()
    while elapsed < timeout:
        time.sleep(interval)
        elapsed += interval
        data = poll(since=offset)
        if data.get("http_status"):
            if verbose:
                print(f"  t={elapsed}s poll failed: {data.get('error')}", flush=True)
            continue
        if "next_offset" in data and data.get("output_source") == "file":
            if data["next_offset"] < offset:
                chunks = []  # output was cleared by a new run
            chunks.append(data.get("output", ""))
            offset = data["next_offset"]
            data["output"] = "".join(chunks)
        # else: older mod or in-memory fallback, where "output" is already everything available
        if verbose:
            ft = len(final_times(data.get("output", "")))
            state = data.get("state", "?")
            print(f"  t={elapsed}s state={state} finished={data.get('finished')} FINAL_TIME_count={ft}", flush=True)
        if on_poll is not None:
            try:
                on_poll(data)
            except Exception as e:  # commentary must never break the benchmark
                print(f"  on_poll failed: {e}", flush=True)
        if data.get("finished"):
            if frame:
                camera_fit()
            return data
    return None
