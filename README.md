# Studium

In-game DPS meter and fight history for FFXIV, as a Dalamud plugin. Named after Sharlayan's Studium, where scholars observe and record.

- Live DPS / Tank / Heal meter, straight from the game (no ACT, IINACT or overlay needed)
- Per-ability breakdown, DoT/HoT attribution, shield credit for healers
- Death recap: the last 30 s before each death, with buffs, enemy debuffs and shields
- Fight history saved to disk, grouped by play session, with filters and pins
- FFLogs: launch the Uploader and open your FFLogs page (logs are written by IINACT)

Commands: `/dps` or `/studium` (meter), `/studium config`, `/studium history`.

## Install

Studium isn't on the official Dalamud repo (it doesn't allow damage meters). Add it as a custom repo:

1. In game, open `/xlsettings` → **Experimental**.
2. Under **Custom Plugin Repositories**, add:
   ```
   https://raw.githubusercontent.com/Iluay0/studium/master/repo.json
   ```
   Tick **Enabled**, then **Save**.
3. Open `/xlplugins`, search for **Studium** and install it.

Updates then arrive through Dalamud like any other plugin.

## Releasing

Bump `<Version>` in `Studium/Studium.csproj` and push to `master`. CI builds and tests it, creates a GitHub Release with the plugin zip, and updates `repo.json`.
