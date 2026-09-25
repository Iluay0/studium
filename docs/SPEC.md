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
- The official Dalamud repo, which bans damage parsers. Distribution: the custom repo (see Post-MVP 5).
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
- **End:** the whole party is out of combat **and** the fight's main enemy is no longer engaged (dead, reset, or not targeting anyone), or on a duty wipe / completion. So with your party dead in a hunt or FATE, the fight continues while others fight the mob, and a raise resumes the same fight. Dead time counts. Then a 1 s hold (timer paused; re-entering resumes). Fallback: 30 s with no damage when the combat flag never came up.
- **Who counts:** you, your party and (in alliance content) your alliance are allies: they start fights, keep them going, and count for wipes. With "Show all players" (Settings → Meter, on by default), every other player is also recorded, but only against enemies your party / alliance is already fighting; they keep a running fight going but never start one and don't count for wipes. In the meter, your party is full brightness; alliance members and other players are muted. The table scrolls when rows overflow.
- **Enemies** are recorded when your party damages them or they damage your party; the fight is named after the one that took the most damage.
- **Outcome:** duties report wipe / clear (and always win). Outside that, a fight is a Clear when its main enemy (the one it's named after) died (death packet, or the game's dead flag at fight end), and a Wipe when at some death every party member in the fight (or you, solo) was dead at once (checked at each death, so respawning or a raise before the fight ends doesn't undo it). Otherwise unknown ("—"), e.g. striking dummies or running away.
- **Name:** the main enemy's name (the highest-HP hostile target engaged), plus the zone.

### Meter window (kagerou-style)
- **Header:** large timer on the left with the Clear / Wipe chip under it (smaller font); beside it, fight name (the fight dropdown), and the zone on a second line, both cut to fit; history 🕘 and settings ⚙ buttons on the right. Collapse is the native title-bar arrow.
- **Fight dropdown:**
  - Lists the fights of the current **play session**. A new session starts after an idle gap of 4 h or more (configurable), so a session can cross midnight.
  - Only the current session (changed 2026-09-26): with no fight this session it's empty ("No fights yet this session."). Earlier sessions are only in the history browser.
  - Capped at 30 entries.
  - Each line: "04:31 [Clear] Magitek Gobwidow G-IX" (duration right-aligned, outcome chip in its colour, name; no start time). The live fight shows a "Live" chip; an unknown outcome shows "—" centred in the chip's slot. Duration and chip slots have fixed widths so chips and names line up.
  - Shows the current character only.
  - Hides fights shorter than the hide threshold.
  - Has **no** "browse history" entry; that's the header button.
  - Opened by clicking the fight name in the header. Picking a past fight shows it in the meter; when a new fight starts, the meter switches back to live.
- **Tabs:**
  - **DPS:** Name, DPS, D%, Total, Crit%, DH%, Max hit, Deaths.
  - **Tank:** Name, Taken, T%, Parry%, Block%, Healed-on, Deaths.
  - **Heal:** Name, HPS, H%, Total, Heal, Shield, Overheal%, Crit%, Deaths. Heal = heals only (overheal included), Shield = damage absorbed by the player's shields, Total = Heal + Shield. HPS / H% use Total (like ACT); Overheal% is measured against Heal only.
  - When the window is too narrow, columns drop from the right; Name and the tab's main number always stay.
- **Rows:**
  - Game job icon (from game textures).
  - Your row: a 2 px accent edge on the left and a brighter name (no background highlight).
  - A gauge in job color, sized relative to the tab's top value. Style setting: **thin underline** (default) or **full-row background bar**.
  - Rows are sorted by the tab's main metric.
- **Footer:** tab switcher; party totals as "Total DPS: X · HPS: Y" (the same number as kagerou's "rdps"), shortening as the window narrows.
- **Click a row** to open the drill-down window.

### Drill-down window
- **Title:** the player's name, what's shown (Damage / Damage Taken / Healing / Deaths), fight and duration.
- **Tabs** (2026-09-26): DPS | Tank | Heal | Deaths ("Deaths (2)" with a count). Clicking a meter row opens the tab matching the meter tab it was clicked from; clicking its Deaths number opens Deaths. Switching tabs keeps the same player, so their damage and healing are a click apart.
- **Summary** under the tab, for that tab: DPS → DPS, total, crit%, DH%; Tank → taken, parry%, block%, healed-on; Heal → HPS, total, heal, shield, overheal%, crit%. Deaths has none.
- **Per-ability table:** Ability, Total, %, Hits, Crit%, DH% (Overheal% on Heal), Avg, Max.
- DoTs, auto-attacks and pets appear as their own ability rows.
- DoT / HoT ticks: see **DoT split** under Post-MVP. When the game gives no status list (nothing readable on the target), ticks still use the old rule (one row per combination of the named source's statuses). Superseded: the old rule showed one player's combined ticks as "DoT ticks (A + B)", but a tick sums every player's DoTs, so this credited everyone's DoTs to one player.
- The table follows the selected tab: damage dealt (DPS), healing with overheal (Heal), or damage taken by enemy ability (Tank).

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
  - Lock position/size, click-through when locked. While click-through, the meter's title says "Studium (Click-through - Ctrl to interact)", shortened to "Studium (Ctrl to interact)" then "Studium" when the window is too narrow.
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

1. **Shield credit** (built, replaces the IINACT-style estimate): shields have no event, so Studium watches each party member's shield gauge. Gains go into a per-player ledger (status, caster, amount). Drops are matched to hits: a drop within 2 s of a hit on that player (either order: the gauge can move before the hit's packet arrives) is damage absorbed, taken from the ledger oldest shield first (assumption when shields overlap) and credited to the caster as healing, with a "Galvanize (shield)" ability row; a drop with no hit within 2 s is an expiry and is just removed. Accurate to ~1% of max HP per change (the gauge is whole percents).
2. **Death recap** (built): breakdown window gets a Deaths tab; clicking the meter's Deaths number opens Deaths. Each death shows "mm:ss · killed by <ability> (<source>)" and the last 30 s newest first: time before death, ability (icon), source, amount (red damage / green heal / miss, with crit/DH/parry/block), HP after with a bar. Party members' HP is read with each hit/heal (before it applies). Saved with the fight. HP and shield are read as each event arrives (before it applies); the recap shows them after it: damage hits the shield first and only the rest comes off HP (shield is whole % of max HP, so ~1% accuracy). The remaining shield is a teal segment after the HP fill in the HP bar, with the % on hover. Shields have no event of their own, so Studium watches each party member's shield gauge every frame; when it goes up, a teal "+8,000" line is added, named after the status that appeared with it (e.g. Brutal Shell) and its source, with exact HP/shield after. An **On enemy** column shows the party's debuffs on the attacker, then a **Buffs** column (the table fits the window like the meter: Event and Source shrink and cut their text; when that isn't enough, columns drop from the right, Buffs first) the player's statuses (buffs and debuffs like Vulnerability Up, with stack icons). On enemy = the party's debuffs on the attacker (harmful, PartyListPriority 50: Reprisal, Addle, Feint, Dismantle...). The game has no mitigation flag, so the player's statuses are everything except noise: permanent (stances), FC buffs, Well Fed, Medicated, and anything with more than 5 min left. Hover an icon for its name and who applied it.
3. ~~DPS-over-time graph~~ dropped: FFLogs + xivanalysis cover it from the uploaded logs (and it would need per-fight time series storage).
4. **Per-tab column editor** (built): Settings → Meter → Columns. Each tab offers only its own columns (DPS: DPS, D%, Total, Crit, DH, Max hit, Hits, Misses, Deaths; Tank: Taken, T%, Parry, Block, Healed-on, Deaths; Heal: HPS, H%, Total, Overheal, Heal crit, Deaths); show/hide and reorder with arrows, reset per tab. No moving columns between tabs. Hits/Misses start hidden. Name is always first; the first column after it is bright and never drops when narrow.
5. **Custom plugin repo** (`repo.json`) for sharing with friends.
6. **DoT split** (agreed 2026-09-25, built; awaiting in-game check). The game sends one DoT tick per target every 3 s summing **every** DoT on it from **every** player, naming one source (cactbot LogGuide, line 24; confirmed in-game: one Bard absorbed the whole party's DoTs). Ground DoTs have their own ticks with the status ID and stay exact. Studium splits the rest, an estimate like ACT's:
   - **Potency** comes from the tooltip text (`ActionTransient` descriptions, read in English whatever the client language), not a hand-written table, so patches update it. Read once per session at login; a Debug window tab lists potency per skill with its icon for checking.
   - **Damage per potency** per player: the median of their non-crit, non-DH hits divided by the action's potency, only for actions with one fixed potency (no combo / positional / conditional potencies). The median dampens self-buff windows.
   - **Split:** each tick is shared between the DoTs on the target, weighted by DoT potency × its owner's damage per potency. A player with no usable hit yet is weighted by potency alone.
   - The tick's DoTs are every DoT status on the target (with its source) when it lands; each is matched to its action by English name (Stormbite's DoT is "Stormbite") for its potency at the owner's job and level. Unknown potency counts as the tick's average DoT.
   - Ticks are kept raw (amount + the DoTs and owners present) and every tick of the fight is re-split whenever a new tick lands (and once more at fight end), so earlier ticks always use the latest medians. Raw ticks live in memory only; saved fights keep the final split.
   - Owners who don't count (other players with "Show all players" off) still weigh in the split, but their share is dropped.
   - Drill-down: the skill's row, then an indented "DoT" row under it (status icon, "~" before the total, tooltip explaining the estimate), then one gauge for both combined; the pair sorts by the combined total. A DoT whose skill isn't in the fight stays alone as "Stormbite (DoT)". Ticks don't count toward hits, crit or DH.
   - **HoTs** (added 2026-09-26) are split the same way: every HoT on the player, weighted by HoT potency × its owner's healing per potency (median of fixed-potency, non-crit heals). Heal potencies come from "Cure Potency" lines: a direct heal opens with "Restores…"; the regen's is the line after "…Effect: Regen"; healing-over-time actions (Regen, Physis II, Asylum) have one. A pet's HoT (faerie) uses its owner's job, level and rate, and merges into the owner. Overheal is shared in proportion. The drill-down shows "HoT" under the skill.
   - **DoTs on party members** (enemy DoTs, damage taken) aren't split: enemy actions have no tooltips, so there's nothing to weigh them by. Their tick row names every DoT on the player, whoever applied it ("DoT ticks (Bleeding + Burns)"), exact.

## Accuracy expectations

- Direct damage and heals come as exact values from the game, so they should match ACT.
- These can differ slightly from ACT/FFLogs:
  - shields
  - DoT damage per player (the game only sends the combined tick; Studium estimates the split)
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
10. Visual polish (done; see **Look**).
