# Flowseal compatibility contract

Status: normative. The compatibility layer is implemented **according to this document**;
when reality and this document disagree, this document is corrected first and code follows.

Reference build inspected: `Flowseal/zapret-discord-youtube` **1.10.1**, published
2026-08-09. Everything below marked *observed* is a fact about that build, not a promise
about future builds.

---

## 1. Purpose

Запрет by Grubeer must keep working with future upstream releases whenever those releases
stay structurally compatible. It must therefore:

* treat the engine as a black box discovered at runtime, never as a compiled-in snapshot;
* derive strategies, capabilities and version from the files actually present;
* validate an unknown release instead of rejecting or blindly trusting it;
* degrade a single feature rather than fail as a whole;
* never destroy a working engine.

A new upstream release should normally require download → inspection → validation →
dynamic discovery. Only a genuine architectural break should require a manager release.

---

## 2. Observed upstream layout (1.10.1)

```text
<root>\
    general.bat                        21 strategy files at root in 1.10.1, see §4
    general (ALT).bat ... (ALT12).bat
    general (EXP).bat
    general (FAKE TLS AUTO*).bat
    general (SIMPLE FAKE*).bat
    service.bat                        menu + external command dispatcher
    bin\
        winws.exe                      the engine (cygwin build)
        cygwin1.dll                    required by winws.exe
        WinDivert.dll, WinDivert64.sys  packet interception driver, x64 only
        ACTIVE_DISCORD_UDP.bin         active fake, replaceable
        ACTIVE_GAME_UDP.bin            active fake, replaceable
        quic_initial_*.bin             fake candidates
        tls_clienthello_*.bin          fake / seqovl patterns
        stun.bin, stun2.bin
    lists\
        list-general.txt               shipped
        list-google.txt               shipped
        list-exclude.txt              shipped
        ipset-exclude.txt             shipped
        ipset-all.txt                 3-state file, see §5.2
        ipset-all.txt.backup          full IPSet list
        list-general-user.txt         created at runtime, user-owned
        list-exclude-user.txt         created at runtime, user-owned
        ipset-exclude-user.txt        created at runtime, user-owned
    utils\
        test zapret.ps1               strategy test utility
        targets.txt                   test targets
        check_updates.enabled         flag file, presence = enabled
        game_filter.enabled           flag file, content = all|tcp|udp
    .service\
        version.txt                   engine version string
        hosts                         hosts payload for the hosts updater
        ipset-service.txt             full IPSet payload
    LICENSE.txt, README.md
```

### 2.1 Stability classification

| Element | Class | How the manager treats it |
| --- | --- | --- |
| `bin\winws.exe` | **required** | absence = invalid build, refuse activation |
| `bin\WinDivert.dll`, `bin\WinDivert64.sys` | **required** | absence = invalid build |
| `bin\cygwin1.dll` | **expected** | absence logged as a warning, not fatal (upstream may switch toolchain) |
| root `*.bat` except `service*` | **discovered** | the strategy catalog, count and names are never assumed |
| `service.bat` | **expected** | source of `LOCAL_VERSION`; its *menu* is never driven by the manager |
| `lists\` shipped files | **discovered** | referenced only through strategy arguments |
| `lists\*-user.txt` | **manager-owned** | created and preserved by the manager, see §5.4 |
| `utils\test zapret.ps1` | **capability** | enables strategy testing |
| `utils\*.enabled` flag files | **capability** | toggles, see §5 |
| `.service\version.txt` | **capability** | version source |
| `.service\hosts` | **capability** | enables the hosts feature |
| `.service\ipset-service.txt` | **capability** | enables IPSet list update |
| service name `zapret` | **observed constant** | interop contract, see §7 |
| registry marker `HKLM\SYSTEM\CurrentControlSet\Services\zapret\zapret-discord-youtube` | **observed constant** | interop contract |

Anything not in this table is treated as opaque payload: copied, preserved, never parsed.

---

## 3. Version detection

Two independent sources, checked in order, first win recorded with its provenance:

1. `.service\version.txt` — trimmed content (observed: `1.10.1`).
2. `service.bat` — first match of `^\s*set\s+"?LOCAL_VERSION=([^"\r\n]+)"?` (observed line 2).
3. The GitHub release tag the build was downloaded from, if the manager installed it.

Discrepancy between sources is not an error: the manager records all of them, uses (1) as
the display version, and logs a warning if (1) and (2) differ. A build with **no**
detectable version is still usable; it is labelled `unknown (installed <date>)` and
excluded from "newer than installed" comparisons except by release tag.

Version comparison uses a lenient dotted-numeric comparison (`1.10.2 > 1.10.1 > 1.9.9`),
falling back to ordinal comparison for non-numeric tags. Tags are matched case-insensitively
with an optional leading `v`.

---

## 4. Strategy discovery and argument extraction

### 4.1 Discovery

* Enumerate `*.bat` in the runtime root, non-recursive.
* Exclude any file whose name starts with `service` (case-insensitive) — the same rule
  upstream uses.
* Sort with natural/numeric ordering so `ALT2` precedes `ALT10`, matching upstream's
  `[Regex]::Replace($_.Name, '(\d+)', PadLeft(8,'0'))` sort. The key is the file name
  **including** the extension, exactly as upstream sorts it, which is why plain
  `general.bat` lands last rather than first. The expected order in the test suite was taken
  by running upstream's own command against the fixtures, not from reasoning about it.
* The strategy **id** is the file name without extension (`general (ALT11)`), exactly what
  upstream writes into its registry marker. The **display name** is derived from the id
  (`ALT11`, `SIMPLE FAKE ALT2`, `general`) for readability only; the id is what is stored.
* Any count is acceptable: 1, 21, or 40. The count is never asserted against a constant —
  the `main` branch already carries a different set than tag `1.10.1`, which is precisely the
  kind of drift the manager must absorb. Newly added upstream strategies appear in the UI
  with no manager release.

### 4.2 Extraction rules

Observed shape of every strategy file: a preamble that calls `service.bat` helpers, then a
single logical command line

```bat
start "zapret: %~n0" /min "%BIN%winws.exe" --wf-tcp=... ^
--filter-udp=443 --hostlist="%LISTS%list-general.txt" ... --new ^
...
```

The manager **parses** this; it never executes the `.bat`.

1. Locate the first line containing `winws.exe` (case-insensitive).
2. Join logical lines: while a line's trimmed end is `^`, drop the `^` and append the next
   physical line with a single space.
3. Discard everything up to and including the `winws.exe` token (with its closing quote).
4. Tokenize the remainder the way `cmd.exe` would hand `argv` to the process: double quotes
   group, quotes are removed from the value, whitespace outside quotes separates.
   Commas are **not** separators — upstream's comma/`mergeargs` handling exists only to work
   around `for %%i in (…)` tokenization in batch and must not be reproduced. The test in
   §9 asserts the resulting `argv` equals what `cmd.exe` produces for the same file.
5. Expand variables against a context, before tokenization of quoted paths is finalised:

   | Variable | Value |
   | --- | --- |
   | `%~dp0` | runtime root, trailing `\` |
   | `%BIN%` | `<root>\bin\` |
   | `%LISTS%` | `<root>\lists\` |
   | `%GameFilter%`, `%GameFilterTCP%`, `%GameFilterUDP%` | from game filter state, §5.1 |
   | any `set "X=…"` assignment earlier in the same file | its literal value |

   An unresolvable variable makes the strategy **unsupported**, with the reason surfaced in
   the UI and logged. It never yields a half-expanded argument and never guesses a value.
6. Relative file references inside quotes are made absolute against the runtime root.
7. Sentinel values are preserved verbatim. With the game filter disabled,
   `--wf-tcp=80,443,…,%GameFilterTCP%` becomes `--wf-tcp=80,443,…,12`; port `12` is
   upstream's deliberate no-op placeholder and is **not** stripped.

### 4.3 Referenced-file validation

After extraction the manager collects every path-valued argument
(`--hostlist`, `--hostlist-exclude`, `--ipset`, `--ipset-exclude`, `--dpi-desync-fake-*`,
`--dpi-desync-split-seqovl-pattern`, and any future `--…="<path>"`) and checks existence.

* Missing **user list** files are created with upstream's placeholder content (§5.4).
* Any other missing file marks the strategy unsupported with the missing path named.
  This is per-strategy: one broken strategy never disables the rest.

### 4.4 Argument allow-listing

Arguments are passed through opaquely — the manager does not maintain a list of known
`winws` flags, because upstream adds flags freely. Only two safety rules apply:

* the executable is always the manager-resolved `bin\winws.exe` inside the active runtime,
  never a path taken from the `.bat`;
* no argument may resolve to a path outside the active runtime directory, except the
  documented system paths upstream itself uses. A violation marks the strategy unsupported
  and is logged as a compatibility issue rather than executed.

---

## 5. Capability detection

Capabilities are computed per installed runtime, from files that are actually present.
Nothing is assumed from the version number.

```csharp
public sealed class UpstreamCapabilities
{
    public bool SupportsGameFilter { get; init; }      // utils\game_filter.enabled writable
    public bool SupportsIpSetFilter { get; init; }      // lists\ipset-all.txt (+ .backup)
    public bool SupportsIpSetUpdate { get; init; }      // .service\ipset-service.txt
    public bool SupportsHostsUpdater { get; init; }     // .service\hosts
    public bool SupportsUserDomainLists { get; init; }  // lists\ writable
    public bool SupportsStrategyTests { get; init; }    // utils\test zapret.ps1 (+ targets.txt)
    public bool SupportsFakeReplacement { get; init; }  // bin\ACTIVE_*.bin + candidate .bin
    public bool SupportsUpdateCheckToggle { get; init; }// utils\check_updates.enabled path
    public bool SupportsDiagnostics { get; init; }      // manager-side; always true
    public bool SupportsUpstreamServiceMode { get; init; } // bin\winws.exe present
}
```

Rules:

* A capability that is false disables its UI control, shows an inline explanation naming the
  expected upstream component, and writes one log line. It never throws.
* A capability that appears in a future build (new flag file, new `.service` payload) is
  additive: unknown files are ignored, not treated as errors.
* Detection is re-run after every engine activation and on manual refresh.

### 5.1 Game filter

Flag file `utils\game_filter.enabled`. Absent = disabled. Present, first line
(case-insensitive): `all` → TCP+UDP, `tcp` → TCP only, anything else → UDP only.
Port expansion, exactly as upstream: enabled → `1024-65535`, disabled side → `12`.
Changing the game filter requires restarting the engine; the manager restarts it itself
instead of printing "Restart the zapret to apply the changes".

### 5.2 IPSet filter

`lists\ipset-all.txt` is a three-state file, detected by content, never by a stored setting:

| State | Detection | Meaning |
| --- | --- | --- |
| `any` | file empty (0 lines) | no IPSet restriction |
| `none` | contains `203.0.113.113/32` | IPSet effectively disabled by sentinel |
| `loaded` | non-empty without the sentinel | real list active |

Switching mirrors upstream's rename dance with `ipset-all.txt.backup`, and refuses to enter
`loaded` when no backup exists, pointing the user at the IPSet update action.

### 5.3 IPSet and hosts updates

* IPSet update fetches `.service/ipset-service.txt` from upstream `main` into
  `lists\ipset-all.txt`.
* Upstream's hosts flow only *compares* first/last line and then asks the user to copy the
  file by hand (Notepad + Explorer). The manager improves on this without changing intent:
  it writes a **managed section** delimited by the deliberately ASCII markers
  `# BEGIN ZapretByGrubeer` / `# END ZapretByGrubeer` (the hosts file is parsed by the DNS
  client resolver, so nothing non-ASCII goes into it) into
  `%SystemRoot%\System32\drivers\etc\hosts`, after backing the file up to
  `data\backups\hosts\hosts-<utc>.bak`. Only that section is ever modified or removed;
  entries outside it are never touched. This is what makes the uninstall promise in
  SPEC.md §10.1 possible.

### 5.4 User lists

The manager owns `lists\list-general-user.txt`, `lists\list-exclude-user.txt`,
`lists\ipset-exclude-user.txt`. It creates them with upstream's exact placeholder content
when absent, because upstream's own strategies reference them unconditionally and an empty
or missing file breaks `winws`:

* `list-general-user.txt` → `# Never leave this file empty` + `domain.example.abc`
* `list-exclude-user.txt` → `domain.example.abc`
* `ipset-exclude-user.txt` → `203.0.113.113/32`

The authoritative copies live in `%ProgramData%\ZapretByGrubeer\data\lists\` and are copied
into each activated runtime. That is what "your custom lists are preserved" means across
engine updates, reinstalls and rollbacks.

### 5.5 Active fake replacement

Candidates are `bin\*.bin` whose base name does not start with `ACTIVE_`. The currently
active fake is identified by SHA-256 equality with `ACTIVE_DISCORD_UDP.bin` /
`ACTIVE_GAME_UDP.bin`, exactly as upstream does; replacement is a file copy. If no candidate
matches, the UI reports `(not found)` rather than inventing a selection.

### 5.6 Strategy tests

`utils\test zapret.ps1` is launched as an external PowerShell process with
`-NoProfile -ExecutionPolicy Bypass -File`, its output streamed into the Diagnostics page.
The script is upstream's; the manager neither parses its internals nor depends on its
output format beyond exit code, and reports "completed" plus captured text.

### 5.7 System side effects upstream performs

`service.bat status_zapret` runs `netsh interface tcp set global timestamps=enabled` when
timestamps are disabled. The manager performs the same check itself before starting the
engine, records in `data\engine.json` whether **it** was the one that changed the setting,
and offers to restore the original value on uninstall. A setting it did not change is left
alone.

---

## 6. Installing and updating an engine (transaction)

Never in place. Never destructive before success.

```text
1  resolve release        stable by default; prereleases only if explicitly enabled
2  download asset         prefer the .zip asset; to runtime\staging\<tag>.part
3  verify                 size, zip integrity, no path traversal in entries
4  extract                to runtime\staging\<tag>\, flattening a single wrapper folder
5  inspect                required files (§2.1), version (§3)
6  discover               strategies (§4) and capabilities (§5)
7  validate               compatibility report (§7); abort on Incompatible
8  seed                   copy user lists and toggles from data\ into the candidate
9  stop                   current engine (process or service), wait for exit
10 activate               move candidate to runtime\versions\<tag>, update current.json
11 reapply                selected strategy (or the mapped replacement, §8.3)
12 health check           engine starts, stays up, WinDivert loads
13 verify targets         quick reachability probe for Discord and YouTube
14 commit                 report success, apply retention policy
```

Any failure at 9–13 triggers rollback (§8). Steps 1–8 leave the running engine untouched,
so failure there is a no-op with a report.

`current.json`:

```json
{
  "current": "1.10.2",
  "previous": "1.10.1",
  "activatedUtc": "2026-08-14T10:12:33Z",
  "activatedBy": "update",
  "versionSource": "service-version-file",
  "capabilities": { "...": true },
  "strategyCount": 27
}
```

Retention: the current version plus one previous working version. Older version directories
are removed after a successful commit. `staging\` is cleaned on both success and failure.

---

## 7. Validating an unknown release

A release newer than anything tested is never auto-rejected. Validation produces a report
with three outcomes:

| Outcome | Condition | Behaviour |
| --- | --- | --- |
| **Compatible** | all required files present, ≥1 strategy parsed, version detected | proceed |
| **Compatible with limitations** | required files present, ≥1 strategy parsed, but some capability or some strategies unavailable | proceed, list what is reduced |
| **Incompatible** | no `winws.exe`/WinDivert, or zero parsable strategies | abort, keep current engine |

Presented as:

```text
Flowseal 1.11.0 detected
This engine version is newer than versions tested during the release of Запрет by Grubeer.

✓ winws found
✓ strategies detected (27)
✓ service management detected
✓ user lists detected
✓ test utility detected

Result: Compatible
```

and on failure:

```text
Compatibility issue detected
The new Flowseal version changed components required by Запрет by Grubeer.
Your existing engine will remain installed.
[Details]  [Keep current version]
```

Interop constants (§2.1) are checked but are not fatal: if upstream renames its service or
registry marker, upstream-service mode is marked unavailable and managed-process mode
continues to work. The manager reports this as a compatibility issue instead of breaking.

---

## 8. Rollback

### 8.1 Trigger

Failure of stop, activation, or strategy reapplication; the engine failing to start or
exiting within 15 seconds of a post-update start; or an explicit user *Roll back* action on
the Updates page.

Target verification (step 13) deliberately does **not** roll back on its own. An unreachable
Discord or YouTube can just as easily be an ISP outage as an engine regression, and throwing
away a healthy new build over it would be wrong. The probe result is shown in the
post-update report, and when it fails the report leads with a one-click *Roll back* — the
decision stays with the user.

### 8.2 Procedure

1. Stop whatever was started from the candidate.
2. Repoint `current.json` at the previous version directory.
3. Restore the previously selected strategy (its id was saved before step 9).
4. Start the engine and confirm it stays up.
5. Keep the failed candidate under `runtime\staging\failed-<tag>\` for one session so
   diagnostics can inspect it, then delete it.
6. Report: what failed, at which step, and that the previous engine is running.
7. Mark the failed tag so the same release is not offered again automatically; a manual
   retry stays available.

The previous version directory is never deleted before a successful commit, which is what
makes this possible. A first-ever install has no previous version: failure there leaves no
engine installed and says so plainly.

### 8.3 Strategy continuity

The selected strategy is stored by upstream id (`general (ALT11)`). After an update:

* exact id present → reapplied silently;
* id absent → the manager proposes the nearest match (same family and, where present, the
  numerically closest variant), tells the user the previous strategy is gone, and does not
  apply anything until the user confirms or runs tests.

---

## 9. Test fixtures and the definition of a break

`Zapret.Tests` keeps the real upstream `.bat` files from at least one reference build under
`Fixtures/upstream/<version>/`, and asserts:

* all 21 reference strategies of 1.10.1 parse into a non-empty `argv` with no unexpanded
  `%VAR%`, no stray `^`, and every path-valued argument absolute;
* `general.bat` parses into a hand-reviewed golden `argv`, asserted token by token;
* `service.bat` is excluded from the catalog by the `service*` rule;
* natural sort order matches upstream's ordering;
* version detection from both sources;
* capability detection with files selectively removed;
* three-state IPSet detection;
* game-filter port expansion for all four modes;
* rollback restores the previous version after an injected failure at each of steps 9–13;
* an artificial "future" build (extra strategies, extra flag file, renamed non-essential
  file) validates as Compatible.

### 9.1 What counts as a breaking upstream change

A manager release is required only when upstream:

* stops shipping `winws.exe` or the WinDivert driver, or changes their location inside the
  archive;
* stops expressing strategies as root-level `.bat` files invoking `winws.exe`;
* changes how a strategy's arguments are formed such that §4.2 cannot recover a valid
  `argv` (for example, moving arguments into an external config format);
* changes the release-asset layout so the archive can no longer be identified or extracted.

Everything else — new strategies, renamed strategies, new flags, new lists, new flag files,
new `.service` payloads, a new version number, removed optional utilities — is absorbed by
discovery and capability detection, with degradation reported to the user.
