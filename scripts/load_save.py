#!/usr/bin/env python3
"""
List the game's saves, or load one and close the menu.

Usage:
  python3 scripts/load_save.py                 # list saves and show the active one
  python3 scripts/load_save.py <save_name>     # load that save (refused if the current save has unsaved changes)
  python3 scripts/load_save.py <save_name> --discard-unsaved
  python3 scripts/load_save.py --close-menu    # keep the active save, just close the menu
"""
import os
import sys

sys.path.insert(0, os.path.dirname(__file__))
import api

args = [a for a in sys.argv[1:] if not a.startswith("--")]
flags = [a for a in sys.argv[1:] if a.startswith("--")]

api.wait_until_ready_for_run(verbose=False)

if not args and "--close-menu" not in flags:
    info = api.saves()
    st = api.status()
    print(f"active_save: {info.get('active_save')}  (menu_open={st.get('menu_open')})")
    for name in info.get("saves", []):
        print(f"  {name}")
    sys.exit(0)

name = args[0] if args else None
result = api.load_save(name, discard_unsaved="--discard-unsaved" in flags)
if result.get("error"):
    print(f"ERROR: {result['error']}")
    sys.exit(1)
print(f"active_save: {result.get('active_save')}  menu_open={result.get('menu_open')}")
