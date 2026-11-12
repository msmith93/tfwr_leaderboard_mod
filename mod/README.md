# TFWR Remote API - BepInEx Mod

A BepInEx plugin for **The Farmer Was Replaced** that exposes an HTTP API on `localhost:8787`, allowing external tools (like Claude Code) to run scripts, trigger simulations, and read results from the game in real time.

Last verified against the game build of 2026-11-05 (Steam build 26462009, Unity 6000.0.43, includes the mining DLC code) under Proton on Linux.

## Prerequisites

- **The Farmer Was Replaced** (Steam, Unity 6) — runs natively on both Windows and Linux (no Proton required on Windows)
- **BepInEx 5.4.23** installed in the game directory (Windows and Linux builds are both available)
- **.NET SDK** (for building; any version that supports the `net46` target — available on Windows and Linux)

## Building

The build resolves the game install directory automatically per-OS. It picks, in order of precedence:

1. An explicit `GameDir` passed to the build (`-p:GameDir=...` / `-GameDir`)
2. The `TFWR_GAME_DIR` environment variable
3. The default Steam location for your OS:
   - **Windows:** `C:\Program Files (x86)\Steam\steamapps\common\The Farmer Was Replaced`
   - **Linux:** `~/.local/share/Steam/steamapps/common/The Farmer Was Replaced`

> **⚠️ Windows support is untested.** The Windows build tooling (`build.ps1`, the OS-aware `TFWRRemoteAPI.csproj`, and the Windows instructions below) was written by Claude and has **not** been verified on a real Windows machine. Expect to adjust the game path for your setup; please report anything that doesn't work.

If Steam installed the game to a non-default library (e.g. a second drive), override the path as shown below.

### Windows (PowerShell)

```powershell
cd mod
.\build.ps1
# Non-default Steam library:
.\build.ps1 -GameDir "D:\SteamLibrary\steamapps\common\The Farmer Was Replaced"
```

If PowerShell blocks the script due to execution policy, run it explicitly:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

### Linux

```bash
cd mod/
bash build.sh
# Non-default Steam library:
TFWR_GAME_DIR="/mnt/games/SteamLibrary/steamapps/common/The Farmer Was Replaced" bash build.sh
```

### Manual (any OS)

```bash
cd mod/TFWRRemoteAPI
dotnet build -c Release -p:GameDir="<path to The Farmer Was Replaced>"
# Then copy bin/Release/TFWRRemoteAPI.dll into <game>/BepInEx/plugins/
```

All three flows compile the plugin and copy `TFWRRemoteAPI.dll` to `BepInEx/plugins/`. Restart the game to load the mod.

### Tests

The JSON reader/writer is plain C# with no game dependencies and has its own test runner (no NuGet packages, no game install needed):

```bash
cd mod/TFWRRemoteAPI.Tests
dotnet run
```

The project targets `net10.0`; change `TargetFramework` in `TFWRRemoteAPI.Tests.csproj` if you have a different SDK.

## Verifying It Works

After launching the game, run:

```bash
curl http://localhost:8787/status
```

```json
{
  "ready": true,
  "graphics_ready": true,
  "executing": false,
  "simulating": false,
  "menu_open": false,
  "active_save": "MySave",
  "active_run_id": "",
  "state": "idle",
  "run_finished": false,
  "plugin_version": "1.1.0",
  "game_version": "0.1",
  "unity_version": "6000.0.43f1",
  "compat_warnings": []
}
```

**Check `compat_warnings` after every game update.** At startup the plugin looks up every private game member it depends on. Anything it cannot find is listed here (and logged to `BepInEx/LogOutput.log`) together with what stops working. An empty list means the plugin matches the installed game.

## Conventions

- Responses are JSON. Failures use an HTTP error status with a body of `{"error": "..."}`:

  | Status | Meaning |
  | ------ | ------- |
  | 400 | Bad request: invalid JSON, missing/unknown field, or a script **parse error** (body also has `line`) |
  | 404 | Unknown endpoint, or unknown save |
  | 405 | Wrong HTTP method |
  | 409 | Not allowed in the current game state (e.g. leaderboard not unlocked, unsaved changes) |
  | 503 | Game not ready yet (`MainSim` or graphics device) |
  | 504 | The game's main thread did not answer in time. The request was **not** applied. |

  `curl -s` still prints the body on errors; add `-f` or `-w '%{http_code}'` if you need the status.
- Request bodies are read as UTF-8 JSON. `\uXXXX` escapes (what Python's `json.dumps` emits for non-ASCII text) are decoded.
- `filename` is the code file's name in the game, with or without `.py`.

## API Endpoints

### `GET /status`

Shown above.

- `ready` - whether `MainSim` is initialized
- `graphics_ready` - whether Unity reports a real GPU. Wait for this before `POST /run-script`; opening a code window before the device is up crashes the game under Proton/Wine.
- `executing` / `simulating` - the game's own view: code is executing / a nested simulation (`simulate()`, `leaderboard_run()`, or simulate/leaderboard mode) is active
- `menu_open` - the title/pause menu is covering the farm. Scripts still run, but nothing is visible; `POST /load-save` closes it.
- `active_save` - the save the game currently has loaded
- `active_run_id`, `state`, `run_finished` - the last run started through the API (see `/poll`)
- `plugin_version`, `game_version`, `unity_version`, `compat_warnings` - version and compatibility info

### `POST /run-script`

Writes a script into the game and runs it.

```json
{
  "code": "quick_print(\"hello world\")",
  "filename": "my_script",
  "mode": "execute"
}
```

| Field | Default | Used by | Description |
| ----- | ------- | ------- | ----------- |
| `code` | - | all | Script text. Required for `execute`. For the other modes it may be omitted to run a file that is already open in the game. |
| `filename` | `claude_diag` | all | Name of the code file |
| `mode` | `execute` | all | `execute`, `simulate` or `leaderboard` (below) |
| `open_editor` | `true` | execute | `true` opens/refreshes the file's code window and parses through it. `false` keeps the editor closed and parses through another open window (or a hidden `__api_scratch` one); parse errors then come without a line number. |
| `speedup` | `1` | simulate, leaderboard | Simulation speed multiplier |
| `seed` | `-1` (random) | simulate | RNG seed |
| `items` | none | simulate | Starting items, e.g. `{"cactus": 1000000000, "Items.Power": 5}` |
| `unlocks` | all at max | simulate | `{"speed": 3, "expand": 1}` (name to level) or `["speed", "plant"]` (max level), added to the base set every simulation gets. Level rules match `simulate()`: out of range means max, below 1 means locked. |
| `globals` | none | simulate | Global variables injected into the script. Numbers, strings and booleans. |
| `leaderboard` | - | leaderboard | Leaderboard name, e.g. `"Hay_Single"` or `"Leaderboards.Hay_Single"` |

**Modes**

- **`execute`** - Runs the script on the save's live farm. To benchmark, have the script call the game's own `leaderboard_run()` or `simulate()`.
- **`simulate`** - Runs the file in a fresh simulation, like the `simulate()` builtin, without needing a runner script. `/poll` reports the result as `sim_time_seconds`.
- **`leaderboard`** - Runs the file as a leaderboard run, like `leaderboard_run()`, with that leaderboard's fixed start conditions. As in the game, the run repeats until 2 hours of simulated time have passed and **a completed run uploads its score to the Steam leaderboard**. Requires the leaderboard unlock in the loaded save.

Any previous run is stopped first. Requests that fail validation (bad JSON, unknown item/unlock/leaderboard, game not ready) are rejected before that happens; a parse error is detected after the previous run was stopped.

**Response**

```json
{ "started": true, "run_id": "a1b2c3d4", "mode": "execute", "filename": "my_script", "open_editor": true }
```

Parse error (status 400):

```json
{ "error": "Parse error: This is not a valid expression.", "line": 3 }
```

### `GET /poll`

Status, results, output and inventory of the current run.

```json
{
  "run_id": "a1b2c3d4",
  "mode": "execute",
  "state": "finished",
  "finished": true,
  "error": null,
  "sim_time_seconds": null,
  "run_times": [61.2, 60.8],
  "leaderboard": { "name": "hay_single", "completed": true, "average_time_seconds": 61.0, "runs": 118 },
  "time_seconds": 42.5,
  "leaderboard_runs_so_far": 0,
  "items": { "hay": 100000512, "power": 999 },
  "output": "FINAL_TIME:  61.2\nFINAL_TIME:  60.8\n",
  "next_offset": 38,
  "output_source": "file",
  "output_truncated": false
}
```

- `state` - `idle`, `starting`, `running`, `finished`, `stopped` or `error`
- `finished` - `true` for `finished`, `stopped` and `error`
- `error` - the game's runtime error message and trace when `state` is `error`. The game pauses a script that hits a runtime error instead of ending it, so without this a failed script would look like it is still running.
- `sim_time_seconds` - sim-clock duration of the last `simulate`-type run (simulate mode, or the most recent `simulate()` call a script made)
- `run_times` - sim-clock duration of each attempt of a leaderboard run, whether or not the script prints anything
- `leaderboard` - the game's final result for a leaderboard run: whether it completed (goal reached), the average time and the number of attempts. `null` until the run ends.
- `time_seconds` - sim clock of the simulation currently active
- `leaderboard_runs_so_far`, `leaderboard_total_time_seconds` - attempts completed in the leaderboard run in progress and their combined sim time (the run ends at 7200)
- `items` - inventory of the simulation currently active
- `output` - everything `quick_print()` and the game logged since the run started
- `next_offset` - pass as `?since=` on the next call to receive only new output

**Output is complete.** The game's in-memory log keeps only the last ~100 messages; the plugin reads the game's `output.txt` instead, so long runs do not lose lines. With `GET /poll?since=<next_offset>` only output after that byte offset is returned (if the log was cleared by a new run, it starts from the beginning again). Without `since`, the whole output is returned, capped at the last 8 MB (`output_truncated: true`). `output_source` is `memory` only if the file could not be read, in which case the ~100-message limit applies.

### `GET /output`

Only the output fields of `/poll` (`output`, `next_offset`, `output_source`, `output_truncated`). Accepts `?since=` as well.

### `GET /inventory`

Inventory of the simulation currently active (all items).

```json
{ "items": { "hay": 1024, "wood": 0, "cactus": 999999000, "power": 999998000 } }
```

### `POST /stop` (GET also accepted)

Stops any execution, simulation or leaderboard run and returns the game to an idle farm: closes the result screen, resets speed to 1x, and clears the leaderboard attempt counters so a stopped run cannot leak into the next one.

```json
{ "stopped": true, "was_running": true }
```

### `POST /dismiss` (GET also accepted)

Closes the leaderboard result screen if it is showing.

```json
{ "dismissed": true }
```

The plugin closes the screen by itself a few seconds after runs **started through the API**. Runs you start by hand in the game are left alone.

### `POST /delete-script`

Closes a file's code window (the same way the in-game delete button does) and removes its `.py` file from the save.

```json
{ "filename": "runner" }
```

```json
{ "deleted": true, "filename": "runner", "file_deleted": true, "window_closed": true, "stopped_run": false }
```

If that file is the one executing, the run is stopped first (`stopped_run: true`).

### `GET /saves`

```json
{ "active_save": "Save0", "saves": ["MySave", "Save0"] }
```

### `POST /load-save`

Switches to another save, the same way the in-game save chooser does, and closes the menu so the farm is visible.

```json
{ "save": "MySave" }
```

| Field | Default | Description |
| ----- | ------- | ----------- |
| `save` | active save | Save to load. If it is already active (or omitted), nothing is reloaded and only the menu is closed. |
| `close_menu` | `true` | Close the title menu once the save is loaded |
| `discard_unsaved` | `false` | The current save's unsaved changes are lost on a switch. Without this flag the request is refused with 409 when there are any. |

```json
{ "loading": true, "save": "MySave", "close_menu": true }
```

Loading takes a moment: poll `/status` until `active_save` matches and `menu_open` is `false`.

> The game starts on the save named in its options file. A save whose name looks like a number (for example `1.0`) is misread by the game at startup and it opens `Save0` instead, so check `active_save` and load the save you want.

### `POST /overlay`

Draws on top of the game for people watching: a caption strip at the bottom and a scoreboard panel at the top right. Both show up in screen recordings and screenshots.

```json
{
  "caption": "Trying a six by six pumpkin block with watering.",
  "caption_seconds": 8,
  "scoreboard": {
    "title": "Pumpkins",
    "subtitle": "iteration 17: watered_6x6",
    "best_seconds": 947.83,
    "best_label": "iteration 16",
    "history": [2356, 1800, 1500, 1210, 990, 947.83],
    "status": "benchmark at 5000x"
  }
}
```

| Field | Description |
| ----- | ----------- |
| `caption` | Text for the caption strip. `""` removes it. |
| `caption_seconds` | Hide the caption after this long (`0` or omitted: until replaced) |
| `scoreboard` | Any subset of the keys above; keys that are left out keep their current value, so `{"scoreboard": {"status": "..."}}` updates one line. `null` removes the panel. `history` draws one bar per past result with the fastest highlighted. |
| `clear` | `true` removes caption and scoreboard first |

The scoreboard's large number is filled in by the plugin from the game: the live sim clock during the first attempt of a run, then the running average of the leaderboard run, with the difference to `best_seconds` in green (faster) or red (slower).

`scripts/tts.py` sends each spoken line as a caption for the length of the audio, and `scripts/run_algorithm.py` fills in the scoreboard from `iterations/iteration_tracker.json`.

### `GET /camera`, `POST /camera`

The game has no free camera; the view is a scroll position and a zoom. `POST /camera` moves both smoothly.

```json
{ "fit": true }
```

| Field | Default | Description |
| ----- | ------- | ----------- |
| `fit` | `false` | Zoom and pan until the whole farm fills `region` |
| `center` | `false` | Pan only: move the farm to the middle of `region` at the current zoom |
| `zoom` | - | Explicit zoom factor (1 is the game's default) instead of fitting |
| `region` | `{"x":0.03,"y":0.16,"width":0.72,"height":0.80}` | Where the farm should sit, in fractions of the screen with the origin at the bottom-left. The default leaves room for the scoreboard and captions. |
| `seconds` | `1.0` | Roughly how long the glide takes |
| `release` | `false` | Hand the view back to the player |

The farm's size changes when a simulation or leaderboard run starts (for example 8x8 for single-drone leaderboards), so fit again about a second after starting a run.

While the API holds the camera, the game's scroll limits are lifted (they normally spring the view back toward the code windows, which makes a large farm impossible to centre). `{"release": true}` restores them; the view then drifts back inside the normal limits.

`GET /camera` reports `zoom`, `world_size`, the screen size and `farm_rect` (where the farm currently is, in screen fractions).

### `GET /leaderboard`

Read-only look at a Steam leaderboard through the game's own Steam session. Nothing is uploaded.

```
GET /leaderboard?name=Carrots_Single&time=382.8&top=10
```

| Parameter | Description |
| --------- | ----------- |
| `name` | Leaderboard name, as for `leaderboard_run()` (`Carrots_Single`, `Leaderboards.Hay`, ...) |
| `time` | Optional. A time in seconds to place on the leaderboard |
| `top` | Optional. How many of the fastest entries to return (default 10, at most 100) |

```json
{
  "leaderboard": "carrots_single",
  "entry_count": 330,
  "top": [{ "rank": 1, "time_seconds": 227.0 }],
  "percentiles": [{ "top_percent": 10, "rank": 33, "time_seconds": 268.5 }],
  "player": { "rank": 84, "time_seconds": 382.8, "top_percent": 25.45 },
  "time_query": { "time_seconds": 382.8, "rank": 85, "top_percent": 25.76 }
}
```

- `percentiles` - the time at the top 1, 5, 10, 25, 50 and 75 percent
- `player` - the Steam user's own recorded best, or `null` if they have none
- `time_query` - the rank `time` would get (only when `time` is given). Finding it takes about log2(`entry_count`) round trips to Steam, so allow a few seconds.

Player names are not returned. Steam must be running; 502 means Steam refused or does not know the leaderboard, 504 that it did not answer in time. `python3 scripts/leaderboard.py <Leaderboard> [time]` prints this as a table.

The scoreboard (`/overlay`) has a `standing` line for this, e.g. `"standing": "world #84 of 330 (top 25.5%)"`.

### `GET /screenshot`

Asks Unity to save the current frame, overlay included, as `api_screenshot.png` in the game's data folder and returns its path (`{"path": "...", "width": 1920, "height": 1080}`). The file is written at the end of the frame. Under Proton the path is a Windows path inside the compat prefix; `scripts/screenshot.py [out.png]` waits for the file and copies it to a normal location.

## Configuration

Settings live in `BepInEx/config/com.claudecode.tfwrapi.cfg` (created on first run):

| Setting | Default | Description |
| ------- | ------- | ----------- |
| `General.Port` | `8787` | Port of the HTTP API |
| `General.MainThreadTimeoutMs` | `10000` | How long a request waits for the game's main thread |
| `Leaderboard.AutoDismissResultScreen` | `true` | Close the result screen after API-started runs |
| `Leaderboard.DismissDelaySeconds` | `3` | How long the result screen stays up first |

## Usage Examples

### Run a simple script

```bash
curl -s -X POST http://localhost:8787/run-script \
  -d '{"code":"quick_print(\"hello\")", "filename":"hello"}'
curl -s http://localhost:8787/poll
```

### Benchmark with the game's own `leaderboard_run()`

```bash
# 1. put the algorithm in the game
curl -s -X POST http://localhost:8787/run-script -d @algo_payload.json   # {"code": "...", "filename": "my_algo"}
curl -s -X POST http://localhost:8787/stop

# 2. run it as a leaderboard run at high speed
curl -s -X POST http://localhost:8787/run-script \
  -d '{"code":"leaderboard_run(Leaderboards.Hay_Single, \"my_algo\", 5000)", "filename":"runner"}'

# 3. poll until "finished": true, then read "leaderboard" and "run_times"
curl -s http://localhost:8787/poll
```

`scripts/run_algorithm.py` in this repo does exactly this, with waiting and parsing.

### Run a simulation directly

```bash
curl -s -X POST http://localhost:8787/run-script -d '{
  "code": "for i in range(31):\n    move(North)\nquick_print(\"hay: \", num_items(Items.Hay))",
  "filename": "sim_test",
  "mode": "simulate",
  "seed": 1,
  "speedup": 1000,
  "items": {"cactus": 1000000000, "power": 1000000000},
  "unlocks": {"speed": 3, "expand": 2},
  "globals": {"water_threshold": 0.5}
}'
curl -s http://localhost:8787/poll      # sim_time_seconds holds the result once finished
```

### Run a leaderboard directly

```bash
curl -s -X POST http://localhost:8787/run-script \
  -d '{"filename": "my_algo", "mode": "leaderboard", "leaderboard": "Hay_Single", "speedup": 5000}'
```

## Architecture

```
Plugin.cs      BepInEx entry point: HTTP listener, routing, endpoint handlers, run tracking
GameBridge.cs  Wrappers around game APIs: code windows, parsing, stop/cleanup, saves, inventory
GameHooks.cs   Startup compatibility check, run state, Harmony hooks that capture run outcomes
Overlay.cs     Caption strip and scoreboard panel drawn over the game (IMGUI)
SteamBoard.cs  Read-only Steam leaderboard queries (entry count, top times, percentiles, rank of a time)
MiniJson.cs    Dependency-free JSON reader/writer (unit tested in ../TFWRRemoteAPI.Tests)
```

- The HTTP listener runs on a background thread. Game state is only touched on Unity's main thread, through a queue drained in `Update()`. A request that is not picked up within the timeout is cancelled, so a 504 never takes effect later.
- Three Harmony hooks record what the game otherwise only shows on screen: `Logger.LogError` (runtime errors), `MainSim.LeaderboardExecutionFinished` (per-attempt and simulation times) and `LeaderboardManager.StopLeaderboardRun` (final leaderboard result).
- `simulate` and `leaderboard` modes build the same start arguments as the game's `simulate()` and `leaderboard_run()` builtins and hand them to `MainSim.ScheduleLeaderboardStart()`.

### After a game update

1. Rebuild (`bash build.sh`). Compile errors point at public game APIs that changed.
2. Start the game and check `compat_warnings` in `/status` for private members that moved.
3. Decompile `TheFarmerWasReplaced_Data/Managed/Core.dll` (e.g. with `ilspycmd`) to see the new shape of anything that broke.

## Notes

- The API is available as soon as the game window is up, including on the title menu, and acts on whichever save is active (`active_save`).
- Each `/run-script` call stops any previous run before starting a new one.
- The mod listens on `localhost` only. It has no authentication and allows cross-origin requests, so any local program (or web page open in a browser on the same machine) can drive it. Do not expose the port.

## Troubleshooting

- **Connection refused** - The game is not running, or BepInEx is not loading. Check that `winhttp.dll` exists in the game directory (BepInEx's loader; renaming it disables all mods) and, under Proton, that the `winhttp` DLL override is set (`WINEDLLOVERRIDES="winhttp=n,b" %command%` in the Steam launch options, or via `winecfg`).
- **Errors mentioning missing methods, or `compat_warnings` is not empty** - The game updated. See "After a game update".
- **"Unlocks.X must be unlocked before this can be used"** - The wrong save is loaded (often a blank `Save0`). Check `active_save` and use `/load-save`.
- **504 "Timed out waiting for the game's main thread"** - The game is still loading or frozen. Retry; the request was not applied.
- **No output** - Use `quick_print()`. `print()` is slow and writes into the air.
- **Build errors about missing types** - The game reference DLLs couldn't be found. `TFWRRemoteAPI.csproj` resolves them from `GameDir`, which defaults to the standard Steam location for your OS. If your game lives elsewhere, pass `-p:GameDir="<path>"` (or `-GameDir` to `build.ps1`), or set the `TFWR_GAME_DIR` environment variable.
