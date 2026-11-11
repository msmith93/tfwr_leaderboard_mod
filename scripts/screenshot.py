#!/usr/bin/env python3
"""
Save a screenshot of the game window (including the overlay).

Usage: python3 scripts/screenshot.py [output.png]     (default: game_screenshot.png)

The mod writes the image into the game's data folder; under Proton that is a Windows path
inside the Steam compat prefix, which is translated here. Set TFWR_COMPAT_PREFIX to the
prefix's drive_c directory if yours is somewhere else.
"""
import os
import shutil
import sys
import time

sys.path.insert(0, os.path.dirname(__file__))
import api

DEFAULT_DRIVE_C = os.path.expanduser(
    "~/.local/share/Steam/steamapps/compatdata/2060160/pfx/drive_c")


def local_path(game_path):
    """Translate the path the game reports into one this machine can open."""
    if os.path.exists(os.path.dirname(game_path)):
        return game_path  # native build
    if len(game_path) > 2 and game_path[1] == ":":
        drive_c = os.environ.get("TFWR_COMPAT_PREFIX", DEFAULT_DRIVE_C)
        return os.path.join(drive_c, game_path[3:].replace("\\", "/"))
    return game_path


def capture(output, timeout=10):
    started = time.time()
    result = api.get("/screenshot")
    if result.get("error"):
        return result
    src = local_path(result["path"])
    # Unity writes the file at the end of the frame; wait for a fresh, complete one.
    last_size = -1
    while time.time() - started < timeout:
        time.sleep(0.2)
        if os.path.exists(src) and os.path.getmtime(src) >= started - 1:
            size = os.path.getsize(src)
            if size > 0 and size == last_size:
                shutil.copyfile(src, output)
                return {"path": output, "width": result.get("width"), "height": result.get("height")}
            last_size = size
    return {"error": f"Screenshot did not appear at {src}"}


if __name__ == "__main__":
    out = sys.argv[1] if len(sys.argv) > 1 else "game_screenshot.png"
    res = capture(out)
    if res.get("error"):
        print(f"ERROR: {res['error']}")
        sys.exit(1)
    print(f"Saved {res['path']} ({res['width']}x{res['height']})")
