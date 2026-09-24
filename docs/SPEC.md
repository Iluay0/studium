# Echo — Spec

In-game DPS meter and fight history for FFXIV, as a standalone Dalamud plugin.
Scope agreed on 2026-09-24 through a grilling session. Anything not listed under **MVP** is out of scope until promoted.

## Goals

- See live DPS/tank/heal stats in-game with no overlay, browser or websocket.
- Keep a browsable, persistent history of fights.
- The meter must not depend on IINACT/ACT. It depends only on Dalamud (and the FFXIVClientStructs bundled with it).

## Non-goals

- Uploading to FFLogs from in-game. There is no public upload API.
- Writing our own ACT-format network logs. IINACT keeps doing that.
- FFLogs-style rDPS/aDPS (buff redistribution).
- The official Dalamud repo, which bans damage parsers. Distribution: dev plugin now, custom repo later.
- Talking to IINACT over IPC or websocket.

## Architecture decisions

| Decision | Choice | Why |
|---|---|---|
| Combat data source | Own hooks via FFXIVClientStructs | No IINACT dependency. ClientStructs is fixed by the community after patches, whereas opcodes change every patch. |
| Hook points | `ActionEffectHandler.Receive` for damage and heals (crit/DH, action ID). `PacketDispatcher.HandleActorControlPacket` for DoT/HoT ticks (category 0x17, source in arg), deaths and other actor control events. `PacketDispatcher.HandleActorCastPacket` for casts. | All three are resolved by ClientStructs. Avoid hand-rolled signatures. |
| Buffs/statuses (for shields later) | Diff each actor's `StatusManager` per frame rather than hooking EffectResult | ClientStructs has no EffectResult entry point. A private signature would be ours alone to maintain after every patch. |
| Pets | Merge into the owner via `GameObject.OwnerId` (toggle) | |
| FFLogs logs | IINACT writes them. Echo only detects IINACT. | Writing the ACT format ourselves is 3–6 weeks, and the Uploader might still reject it. |
| Storage | Per-fight summary files plus an index. The index loads at startup; a fight's full data loads when opened. | Instant startup with a month of history. |
| Parser layer | Behind an interface (`ICombatEventSource`) that emits plain event records | Aggregation, storage and UI can be tested without the game. |

Reference implementations (read only, never copy verbatim):
- DeathRecap `Events/CombatEventCapture.cs`
- linusfr/ffxiv-minimal-meter `src/CombatTracker.cs` and `docs/DOT_ATTRIBUTION.md`
- cactbot `docs/LogGuide.md` for field semantics

## MVP

### Commands
- `/echo` and `/dps` (aliases): toggle the meter.
- `/echo config`: open the settings window.
- `/echo history`: open the history browser.

### Fight lifecycle
- **Start:** the first damage event involving a party member (or you, when solo).
- **End:** the whole party is out of combat, or on a wipe or duty completion. Fallback: N seconds with no damage (for open-world content such as FATEs).
- **Outcome:** clear or wipe, when the game tells us; otherwise unknown.
- **Name:** the main enemy's name (the highest-HP hostile target engaged), plus the zone.

### Meter window (kagerou-style)
- **Header:** timer, encounter name, outcome, zone, fight dropdown, then buttons for history 🕘, settings ⚙ and collapse.
- **Fight dropdown:**
  - Lists the fights of the current **play session**. A new session starts after an idle gap of 4 h or more (configurable), so a session can cross midnight.
  - If the session has fewer than N fights (default 15), it fills up with the most recent earlier fights.
  - Capped at 30 entries.
  - Shows the current character only.
  - Hides fights shorter than the hide threshold.
  - Has **no** "browse history" entry; that's the header button.
- **Tabs:**
  - **DPS:** Name, D%, DPS, Total, Crit%, DH%, Max hit, Deaths.
  - **Tank:** Name, Taken, Taken%, Parry%, Block%, Healed-on, Deaths.
  - **Heal:** Name, H%, HPS, Total, Overheal%, Crit%, Deaths. Labeled "excl. shields" until shield estimation ships.
- **Rows:**
  - Game job icon (from game textures).
  - Your own row highlighted.
  - A gauge in job color, sized relative to the tab's top value. Style setting: **thin underline** (default) or **full-row background bar**.
  - Rows are sorted by the tab's main metric.
- **Footer:** tab switcher; raid DPS and raid HPS (party totals, the same as kagerou's "rdps").
- **Click a row** to open the drill-down window.

### Drill-down window
- **Header:** the player's job, name, fight and duration, plus a summary: DPS, total, crit%, DH%, deaths.
- **Per-ability table:** Ability, Total, %, Hits, Crit%, DH%, Avg, Max.
- DoTs, auto-attacks and pets appear as their own ability rows.

### History browser
- Fights grouped by play session, newest first. Sessions can be collapsed.
- **Row:** time, zone/encounter, duration, outcome, job, character, DPS. Pinned fights show 📌.
- **Filters:** zone, job, character, clears only, minimum duration.
- **Actions:** open in meter, pin/unpin (pinned fights are exempt from retention), delete.
- History is **shared across characters**.

### Settings window
All options live here, never in the meter.
- **Meter:**
  - Visibility: always / in combat / in duty. Separate toggle: hide in cutscenes.
  - Lock position/size, click-through when locked.
  - Background opacity.
  - Name display: full / initials / "YOU" for self.
  - Merge pets into owner.
  - Gauge style.
- **History:**
  - **Retention slider:** 0–720 h with an hours/days unit toggle. Default 168 h (7 days).
    - `0` = this session only: fights are kept in memory and never written to disk.
  - **Never delete saved fights:** off by default. When on, the slider is greyed out.
  - **Skip fights shorter than X s:** on by default, X = 10. Skipped fights are not saved at all.
  - **Hide fights shorter than Y s in lists:** default 15.
  - **Session split idle gap:** default 4 h.
  - Retention cleanup runs at startup and hourly. Pinned fights are never deleted.
- **FFLogs:**
  - Line: "IINACT detected: yes/no", plus an **Open IINACT settings** button (runs `/iinact`).
  - Text: "IINACT is required for writing FFLogs network logs."
  - **Uploader path** (file picker). The **Launch Uploader** button stays disabled until the path is set. Supports the Windows `.exe` and the Linux AppImage (via `start /unix`).
  - **Open my FFLogs page:** `https://www.fflogs.com/character/{region}/{world}/{name}`. The region comes from Lumina: `World → DataCenter → WorldDCGroupType.Region`. Disabled when logged out.

## Post-MVP (in order)

1. **Shield estimation**, like IINACT/ACT: on by default, toggle to disable. Tracks shield-granting statuses and estimates absorbed damage per caster. About 3–5 days.
2. **Death recap:** the last ~10 s of damage and healing before each death. Needs a rolling event buffer.
3. **DPS-over-time graph** per fight and pull comparison.
4. **Per-tab column editor.**
5. **Custom plugin repo** (`repo.json`) for sharing with friends.

## Accuracy expectations

- Direct damage and heals come as exact values from the game, so they should match ACT.
- These can differ slightly from ACT/FFLogs:
  - shields
  - DoT attribution edge cases
  - overkill damage
  - fight start/end (the DPS divisor)

  FFXIV_ACT_Plugin is closed-source, so its heuristics can't be copied.

## Verification per slice

- **Unit tests** (no game) for: aggregation, fight lifecycle, session grouping, retention, and the FFLogs URL/region mapping. Tests feed synthetic event records through the `ICombatEventSource` interface.
- **In-game check:** a target dummy (solo), then a real duty. Compare the numbers against IINACT for the same pull where possible.

## Build order (vertical slices)

1. Plugin skeleton: `/echo` and `/dps`, empty meter window, settings window, config persistence.
2. Hooks produce combat events, printed to a debug window.
3. Aggregation and fight lifecycle, shown as a live DPS tab against a target dummy.
4. Tank/Heal tabs, job icons, gauges, name display, pet merge.
5. Drill-down window.
6. Persistence: index, lazy load, retention, skip-short.
7. Fight dropdown (play sessions) and history browser (filters, pin, delete).
8. FFLogs section: IINACT detection, Uploader launch, FFLogs page link.
9. Visibility rules, lock, click-through, opacity.
