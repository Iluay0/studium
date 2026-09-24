# Studium (DPSMeter)

Previously named "Echo"; renamed because `/echo` is a built-in game chat command. Never register commands that shadow game commands.

Standalone Dalamud plugin: in-game DPS meter and fight history. Commands: `/studium`, `/dps`.
**Read `docs/SPEC.md` before any work.** It holds the agreed scope, non-goals and build order. Anything not listed in the spec is out of scope unless the user promotes it.

## Hard rules
- No dependency on IINACT, ACT, FFXIV_ACT_Plugin, websockets or IINACT IPC. The only IINACT touchpoint is detecting that it is installed and running `/iinact`.
- Combat data comes only from FFXIVClientStructs-resolved hooks. Do not add hand-written signatures or network opcodes without asking the user first.
- Keep the game-facing hook layer thin, behind `ICombatEventSource`. Aggregation, fight lifecycle, sessions and retention must be testable without the game.
- All user options live in the settings window, never in the meter window.
- Reference plugins (DeathRecap, ffxiv-minimal-meter) are for reading only. Don't copy code verbatim; check licenses first.

## Environment
- Linux, XIVLauncher.Core, Dalamud 15.0.3.5 (stg). Dev hooks at `~/.xlcore/dalamud/Hooks/dev`.
- `DALAMUD_HOME` is unset. Dalamud.NET.Sdk falls back to the path above on Linux.
- .NET SDK 10 (also 8). Target `net10.0-windows` with `Dalamud.NET.Sdk/15.x`, matching IINACT and WrathCombo in `~/Code/FFXIVPlugins/`.
- The game runs under Wine. Paths the plugin sees are Wine paths. Launch Linux binaries (e.g. the FFLogs Uploader AppImage) through `start /unix`.
- IINACT logs (for reference/testing only): `~/.xlcore/wineprefix/drive_c/users/dory/Documents/IINACT/Network_*.log`.

## Build & verify
- Build: `dotnet build`.
- Tests: `dotnet test` (unit tests for non-game logic).
- In-game: add the build output folder as a dev plugin location in Dalamud settings, then `/xlplugins` → load. The user runs the in-game checks. For each slice, tell them exactly what to test (target dummy first, then a duty).

## Layout
- `Studium/`: the Dalamud plugin (hooks, windows, config). Output: `Studium/bin/Debug/Studium.dll`.
- `Studium.Core/`: game-free logic (plain `net10.0`, no Dalamud references). Anything testable goes here.
- `Studium.Tests/`: xunit tests for `Studium.Core`.

## Releasing
- GitHub: https://github.com/Iluay0/studium (remote `origin`, branch `master`).
- Bump `<Version>` in `Studium/Studium.csproj` and push. CI (`.github/workflows/build.yml`) tests, builds, creates a GitHub Release with `latest.zip`, and commits the updated `repo.json` to `master` itself.
- Because CI commits to `master`, run `git pull --ff-only` before committing new work after a release.
- Custom repo URL for Dalamud: `https://raw.githubusercontent.com/Iluay0/studium/master/repo.json` (only works while the GitHub repo is public).
