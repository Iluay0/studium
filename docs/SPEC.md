# Studium — Spec

(Named after Sharlayan's Studium, where scholars observe and record. Originally "Echo"; renamed because `/echo` is a game command.)

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
| Hook points | `ActionEffectHandler.Receive` for damage and heals (crit/DH, action ID). `PacketDispatcher.HandleActorControlPacket` for DoT/HoT ticks (category 0x605 DoT / 0x604 HoT: arg2 = amount, arg3 = source; verified in-game), deaths and other actor control events. `PacketDispatcher.HandleActorCastPacket` for casts. | All three are resolved by ClientStructs. Avoid hand-rolled signatures. |
| Buffs/statuses (for shields later) | Diff each actor's `StatusManager` per frame rather than hooking EffectResult | ClientStructs has no EffectResult entry point. A private signature would be ours alone to maintain after every patch. |
| Pets | Merge into the owner via `GameObject.OwnerId` (toggle) | |
| FFLogs logs | IINACT writes them. Studium only detects IINACT. | Writing the ACT format ourselves is 3–6 weeks, and the Uploader might still reject it. |
| Storage | Per-fight summary files plus an index. The index loads at startup; a fight's full data loads when opened. | Instant startup with a month of history. |
| Parser layer | Behind an interface (`ICombatEventSource`) that emits plain event records | Aggregation, storage and UI can be tested without the game. |

Reference implementations (read only, never copy verbatim):
- DeathRecap `Events/CombatEventCapture.cs`
- linusfr/ffxiv-minimal-meter `src/CombatTracker.cs` and `docs/DOT_ATTRIBUTION.md`
- cactbot `docs/LogGuide.md` for field semantics

## MVP

### Commands
- `/studium` and `/dps` (aliases): toggle the meter.
- `/studium config`: open the settings window.
- `/studium history`: open the history browser.

### Fight lifecycle
- **Start:** the first damage event involving a party member (or you, when solo).
- **End:** the whole party is out of combat **and** the fight's main enemy is no longer engaged (dead, reset, or not targeting anyone), or on a duty wipe / completion. So with your party dead in a hunt or FATE, the fight continues while others fight the mob, and a raise resumes the same fight. Dead time counts. Then a 10 s hold (timer paused; re-entering resumes). Fallback: 30 s with no damage when the combat flag never came up.
- **Enemies** are recorded when your party damages them or they damage your party; the fight is named after the one that took the most damage.
- **Outcome:** duties report wipe / clear (and always win). Outside that, a fight is a Clear when its main enemy (the one it's named after) died (death packet, or the game's dead flag at fight end), and a Wipe when at some death every party member in the fight (or you, solo) was dead at once (checked at each death, so respawning or a raise before the fight ends doesn't undo it). Otherwise unknown ("—"), e.g. striking dummies or running away.
- **Name:** the main enemy's name (the highest-HP hostile target engaged), plus the zone.

### Meter window (kagerou-style)
- **Header:** large timer on the left with the Clear / Wipe chip under it (smaller font); beside it, fight name (the fight dropdown), and the zone on a second line, both cut to fit; history 🕘 and settings ⚙ buttons on the right. Collapse is the native title-bar arrow.
- **Fight dropdown:**
  - Lists the fights of the current **play session**. A new session starts after an idle gap of 4 h or more (configurable), so a session can cross midnight.
  - If the session has fewer than N fights (default 15), it fills up with the most recent earlier fights.
  - Capped at 30 entries.
  - Shows the current character only.
  - Hides fights shorter than the hide threshold.
  - Has **no** "browse history" entry; that's the header button.
  - Opened by clicking the fight name in the header. Picking a past fight shows it in the meter; when a new fight starts, the meter switches back to live.
- **Tabs:**
  - **DPS:** Name, DPS, D%, Total, Crit%, DH%, Max hit, Deaths.
  - **Tank:** Name, Taken, T%, Parry%, Block%, Healed-on, Deaths.
  - **Heal:** Name, HPS, H%, Total, Overheal%, Crit%, Deaths. Labeled "excl. shields" until shield estimation ships.
  - When the window is too narrow, columns drop from the right; Name and the tab's main number always stay.
- **Rows:**
  - Game job icon (from game textures).
  - Your row: a 2 px accent edge on the left and a brighter name (no background highlight).
  - A gauge in job color, sized relative to the tab's top value. Style setting: **thin underline** (default) or **full-row background bar**.
  - Rows are sorted by the tab's main metric.
- **Footer:** tab switcher; party totals as "Total DPS: X · HPS: Y" (the same number as kagerou's "rdps"), shortening as the window narrows.
- **Click a row** to open the drill-down window.

### Drill-down window
- **Header:** the player's job, name, fight and duration, plus a summary: DPS, total, crit%, DH%, deaths.
- **Per-ability table:** Ability, Total, %, Hits, Crit%, DH%, Avg, Max.
- DoTs, auto-attacks and pets appear as their own ability rows.
- DoT / HoT ticks: the game sends one combined tick per source and target, with no status ID. Studium reads the source's DoTs (or HoTs) on the target at that moment (DoT = harmful status with PartyListPriority 10, HoT = helpful with 5). Tick rows sit in the main ability list, sorted by total:
  - one status → "Dia (DoT)" with the status icon (exact);
  - several → one row per combination, "DoT ticks (Caustic Bite + Stormbite)", exact total, never split (the user rejected estimated splits);
  - none identified → "DoT ticks".
- The breakdown follows the tab it was opened from: damage dealt (DPS), healing with overheal (Heal), or damage taken by enemy ability (Tank).

### History browser
- Fights grouped by play session, newest first. Sessions can be collapsed.
- **Row:** time, zone/encounter, duration, outcome, job, character, DPS. Pinned fights show 📌.
- **Filters:** zone, job, character, clears only, minimum duration.
- **Actions:** open in meter, pin/unpin (pinned fights are exempt from retention), delete.
- History is **shared across characters**.

### Look (approved mockup, 2026-09-24)
- Studium's own fixed dark palette on the meter, breakdown and history windows, so the user's Dalamud theme doesn't leak in. The settings window keeps the player's own Dalamud theme. Dalamud's title bars keep their colours; the meter's title bar follows the meter opacity. Palette lives in `Studium/Ui/Theme.cs`.
- Job colours are the only strong colours; a pale teal accent (#7FB8C2) marks the active tab, your row and pins. Clear green, Wipe red.
- Numbers right-aligned; each tab's main number bright, the rest muted. Uppercase dim column headers with a hairline under them.
- Flat icon buttons, flat text tabs with an accent underline, job-tinted row hover.

### Settings window
All options live here, never in the meter.
- **Meter:**
  - Visibility: always / in combat / in duty. Separate toggle: hide in cutscenes.
  - Lock position/size, click-through when locked.
  - Background opacity, 0–100%. The meter's title bar follows it, and meter text has a dark shadow so it stays readable at any opacity.
  - Name display: full (Iluay Dory) / short surname (Iluay D.) / initials (I. D.), plus a separate "show YOU for me" toggle.
  - Merge pets into owner.
  - Gauge style.
- **History:**
  - **Retention slider:** 0–720 h with an hours/days unit toggle. Default 168 h (7 days).
    - `0` = this session only: new fights are kept in memory and never written to disk. Fights saved earlier are left alone (not wiped), so an accidental slide to 0 loses nothing.
  - **Automatically delete saved fights:** on by default, shown above the slider. When off, fights are kept forever and the slider is greyed out.
  - **Skip fights shorter than X s:** on by default, X = 30. Skipped fights are not saved at all.
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
2. **Death recap** (built): breakdown window gets Abilities | Deaths tabs; clicking the meter's Deaths number opens Deaths. Each death shows "mm:ss · killed by <ability> (<source>)" and the last 30 s newest first: time before death, ability (icon), source, amount (red damage / green heal / miss, with crit/DH/parry/block), HP after with a bar. Party members' HP is read with each hit/heal (before it applies). Saved with the fight. The shield (the game's shield gauge, % of max HP) is a teal segment after the HP fill in the HP bar, with the % on hover. An **On enemy** column shows the party's debuffs on the attacker, then a **Buffs** column (the table fits the window like the meter: Event and Source shrink and cut their text; when that isn't enough, columns drop from the right, Buffs first) the player's statuses (buffs and debuffs like Vulnerability Up, with stack icons). On enemy = the party's debuffs on the attacker (harmful, PartyListPriority 50: Reprisal, Addle, Feint, Dismantle...). The game has no mitigation flag, so the player's statuses are everything except noise: permanent (stances), FC buffs, Well Fed, Medicated, and anything with more than 5 min left. Hover an icon for its name and who applied it.
3. ~~DPS-over-time graph~~ dropped: FFLogs + xivanalysis cover it from the uploaded logs (and it would need per-fight time series storage).
4. **Per-tab column editor** (built): Settings → Meter → Columns. Each tab offers only its own columns (DPS: DPS, D%, Total, Crit, DH, Max hit, Hits, Misses, Deaths; Tank: Taken, T%, Parry, Block, Healed-on, Deaths; Heal: HPS, H%, Total, Overheal, Heal crit, Deaths); show/hide and reorder with arrows, reset per tab. No moving columns between tabs. Hits/Misses start hidden. Name is always first; the first column after it is bright and never drops when narrow.
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

1. Plugin skeleton: `/studium` and `/dps`, empty meter window, settings window, config persistence.
2. Hooks produce combat events, printed to a debug window.
3. Aggregation and fight lifecycle, shown as a live DPS tab against a target dummy.
4. Tank/Heal tabs, job icons, gauges, name display, pet merge.
5. Drill-down window.
6. Persistence: index, lazy load, retention, skip-short.
7. Fight dropdown (play sessions) and history browser (filters, pin, delete).
8. FFLogs section: IINACT detection, Uploader launch, FFLogs page link.
9. Visibility rules, lock, click-through, opacity.
10. Visual polish: custom styling for header buttons, tabs, rows and gauges (the user expects this pass; until then use stock ImGui widgets). Known items: meter row hover colour (theme purple looks ugly), own-row highlight, header overlap at narrow widths, drill-down window (skill icons, per-ability gauges).
