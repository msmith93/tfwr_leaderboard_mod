#!/usr/bin/env python3
"""
Show the Steam leaderboard for a category: how many players, the times at the top and at
fixed percentiles, your own entry, and where a given time would rank. Read-only.

Usage:
  python3 scripts/leaderboard.py <Leaderboard> [time_seconds] [--top N] [--json]

Examples:
  python3 scripts/leaderboard.py Carrots_Single
  python3 scripts/leaderboard.py Carrots_Single 399.2      # where would 399.2s rank?
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(__file__))
import api


def fmt(seconds):
    if seconds < 60:
        return f"{seconds:.2f}s"
    m = int(seconds // 60)
    return f"{m}:{seconds - m * 60:04.1f} ({seconds:.1f}s)"


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    if not args:
        print(__doc__)
        sys.exit(1)
    top = 10
    if "--top" in sys.argv:
        top = int(sys.argv[sys.argv.index("--top") + 1])
        args = [a for a in args if a != str(top)] or args
    name = args[0]
    time_seconds = float(args[1]) if len(args) > 1 else None

    data = api.leaderboard(name, time_seconds, top)
    if data.get("error"):
        print(f"ERROR: {data['error']}")
        sys.exit(1)
    if "--json" in sys.argv:
        print(json.dumps(data, indent=2))
        return

    count = data.get("entry_count", 0)
    print(f"Leaderboard: {data.get('leaderboard')}   players with a time: {count:,}")
    print("Top times:")
    for e in data.get("top", []):
        print(f"  #{e['rank']:<4} {fmt(e['time_seconds'])}")
    print("Time needed to be in the:")
    for p in data.get("percentiles", []):
        print(f"  top {p['top_percent']:g}%  (rank {p['rank']:,})  {fmt(p['time_seconds'])}")
    me = data.get("player")
    if me:
        print(f"Your recorded best: {fmt(me['time_seconds'])}  {api.standing_text(me['rank'], count)}")
    else:
        print("Your recorded best: none yet on this leaderboard")
    q = data.get("time_query")
    if q:
        print(f"A time of {fmt(q['time_seconds'])} would be {api.standing_text(q['rank'], count)}"
              + (f", faster than {q['beats_percent']:.0f}% of players" if "beats_percent" in q else ""))


if __name__ == "__main__":
    main()
